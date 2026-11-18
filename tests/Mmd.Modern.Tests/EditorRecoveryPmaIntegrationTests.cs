using System.Numerics;
using System.Text.Json;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRecoveryPmaIntegrationTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    [Fact]
    public async Task RealRecoveryPackageAndUnregisteredDraftSurviveClosingTheSourceDocument() => await session.Dispatch(async () =>
    {
        var token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-pma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = Model(); string source = Path.Combine(directory, "model.pmx");
            await File.WriteAllBytesAsync(source, PmxWriter.ToBytes(model), token);
            string cache = Path.Combine(directory, "cache");
            using var workspace = new EditorProjectWorkspace { PackageCacheDirectory = cache };
            Guid actorId = workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, token), ModelRole.Prop);
            workspace.CommitMotion(actorId, new() { ModelName = "recovery", BoneKeyframes = [new() { BoneName = "root", Frame = 10 }] });
            var nativeLight = EditorRecoveryNativeLightingTests.Light("native recovery");
            workspace.EditLighting(new(new SceneLightingSnapshot([nativeLight])));
            var actor = Assert.Single(workspace.Motions);
            var animation = new EditorAnimationViewModel { CurrentFrame = 7 };
            var scene = new EditorSceneInspectorViewModel { CurrentFrame = 7 };
            animation.SetActor(actorId, actor.Model, actor.Motion); animation.SelectBone(0);
            animation.PositionX = 12.5m;
            workspace.Session!.Seek(7, token);
            var lightInspector = new EditorLightInspector();
            lightInspector.SetLighting(workspace.Session.Lighting, nativeLight.Id, workspace.DocumentId);
            lightInspector.ViewModel!.Lights[0].PositionX = 19m;
            var state = EditorRecoverySessionState.Capture(animation, scene, workspace.Motions) with
            {
                NativeLighting = lightInspector.CaptureRecoveryState(workspace.Session.Lighting, EditorInspectorPage.Lighting, workspace.DocumentId)
            };
            using var json = JsonDocument.Parse(state.ToJsonBytes());
            using var snapshot = workspace.CaptureRecoverySnapshot(token);
            var store = new EditorRecoveryStore(Path.Combine(directory, "recovery"));
            using var session = await store.CreateSessionAsync(null, "Unnamed recovery", token);
            workspace.New();
            var checkpoint = await store.WriteCheckpointAsync(session, new(null, "Unnamed recovery", 1, json.RootElement),
                (path, cancellation) => Task.Run(() => snapshot.Save(path, cancellation), cancellation), token);
            Assert.Null(workspace.ProjectPath); Assert.False(workspace.IsDirty);
            session.Dispose(); // Simulates an abandoned lease; clean shutdown is deliberately not recorded.
            var reopenedStore = new EditorRecoveryStore(store.RootDirectory);
            var available = Assert.Single(await reopenedStore.ListRecoverableAsync(token));
            Assert.Equal(checkpoint.CheckpointId, Assert.Single(available.Checkpoints).CheckpointId);
            using var restoredWorkspace = new EditorProjectWorkspace { PackageCacheDirectory = cache };
            var restoredState = restoredWorkspace.OpenRecovery(checkpoint, token);
            Assert.Equal(7, restoredWorkspace.Session!.CurrentFrame);
            Assert.Null(restoredWorkspace.ProjectPath);
            Assert.True(restoredWorkspace.IsDirty);
            Assert.Equal(nativeLight, Assert.Single(restoredWorkspace.Session.Lighting.Lights));
            Assert.Equal(19m, Assert.Single(restoredState.NativeLighting!.Lights).PositionX);
            var restoredAnimation = new EditorAnimationViewModel(); var restoredScene = new EditorSceneInspectorViewModel();
            restoredState.Restore(restoredAnimation, restoredScene, restoredWorkspace.Motions);
            Assert.Equal(12.5m, restoredAnimation.PositionX); Assert.Equal(7, restoredAnimation.CurrentFrame);
            Assert.True(restoredAnimation.HasAnyDraft);
            var restoredActor = Assert.Single(restoredWorkspace.Motions);
            Assert.Equal(10u, Assert.Single(restoredActor.Motion.BoneKeyframes).Frame);
            Assert.Equal(Vector3.Zero, restoredActor.Motion.BoneKeyframes[0].Translation);
            // A malformed recovery candidate must not replace the live document or its ownership.
            var priorSession = restoredWorkspace.Session; var priorDocument = restoredWorkspace.DocumentId;
            var inconsistent = restoredState with
            {
                Animation = restoredState.Animation with { CurrentFrame = 8 },
                Scene = restoredState.Scene with { CurrentFrame = 8 }
            };
            using var inconsistentJson = JsonDocument.Parse(inconsistent.ToJsonBytes());
            var invalidCheckpoint = checkpoint with { EditorState = inconsistentJson.RootElement.Clone() };
            Assert.Throws<InvalidDataException>(() => restoredWorkspace.OpenRecovery(invalidCheckpoint, token));
            Assert.Same(priorSession, restoredWorkspace.Session); Assert.Equal(priorDocument, restoredWorkspace.DocumentId);
            Assert.Null(restoredWorkspace.ProjectPath); Assert.True(restoredWorkspace.IsDirty);
            var badLights = restoredState with
            {
                NativeLighting = restoredState.NativeLighting! with { CommittedLights = [nativeLight with { Position = Vector3.One }] }
            };
            using var badLightsJson = JsonDocument.Parse(badLights.ToJsonBytes());
            Assert.Throws<InvalidDataException>(() => restoredWorkspace.OpenRecovery(checkpoint with { EditorState = badLightsJson.RootElement.Clone() }, token));
            Assert.Same(priorSession, restoredWorkspace.Session); Assert.Equal(priorDocument, restoredWorkspace.DocumentId);
            Assert.Throws<InvalidOperationException>(() => restoredWorkspace.PackageCacheDirectory = Path.Combine(directory, "other-cache"));
            Assert.Equal(cache, restoredWorkspace.PackageCacheDirectory);
            // A changed but otherwise valid PMA cannot be trusted through stale checkpoint metadata.
            Assert.Throws<InvalidDataException>(() => restoredWorkspace.OpenRecovery(checkpoint with { Sha256 = new string('0', 64) }, token));
            Assert.Same(priorSession, restoredWorkspace.Session);
            Assert.True(Directory.Exists(cache));
            Assert.Equal(PmxWriter.ToBytes(model), await File.ReadAllBytesAsync(source, token));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public void PackageCacheDefaultsStayCompatibleAndRejectRelativeOrDisposedOwners()
    {
        var workspace = new EditorProjectWorkspace();
        Assert.Equal(EditorProjectWorkspace.CacheDirectory, workspace.PackageCacheDirectory);
        Assert.Throws<ArgumentException>(() => workspace.PackageCacheDirectory = "relative-cache");
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.PackageCacheDirectory = Path.GetTempPath());
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);

    internal static PmxModel Model() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "recovery", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = [Vertex(Vector3.Zero), Vertex(Vector3.UnitX), Vertex(Vector3.UnitY)], Faces = [new(0, 1, 2)], Textures = [],
        Materials = [new() { Name = "surface", EnglishName = "", Diffuse = Vector4.One, SpecularPower = 1,
            Specular = Vector3.Zero, Ambient = Vector3.Zero, DrawMode = 0, EdgeColor = Vector4.One, EdgeSize = 0,
            TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
            ToonTextureIndex = -1, ToonMode = PmxToonMode.Separate, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, DeformDepth = 0,
            ParentIndex = -1, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate }],
        Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
    };

    private static PmxVertex Vertex(Vector3 position) => new()
    {
        Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
        WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1
    };
}
