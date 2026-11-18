using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneProjectSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "unified-project-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public SceneProjectSessionTests() => Directory.CreateDirectory(directory);
    private (SceneCompositionEditor Layout, SceneLightingEditor Lights, SceneActorMotion Motion, PmxModel Model) Fixture()
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        var model = new PmxModel
        {
            Header = template.Header, Name = "actor", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.DrawEdge,
                EdgeColor = new(0, 0, 0, 1), EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", path, ModelRole.Character, rest);
        var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" },
            new() { BoneName = "root", Frame = 30, Translation = new(3, 0, 0) }] });
        return (layout, new(), new(actor.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset), model);
    }
    [Fact]
    public void LayoutLightsAndMotionCommitTogetherUndoRedoAndSaveReopen()
    {
        var f = Fixture(); var provider = new NoPhysics();
        using var project = new SceneProjectSession(f.Layout, f.Lights, [f.Motion], provider);
        project.Seek(30, Token); Guid id = f.Motion.ObjectId;
        project.Edit(edit =>
        {
            edit.Composition.SetTransform(id, new(4, 0, 0), Quaternion.Identity, 1);
            edit.Lighting.Add(new(Guid.NewGuid(), "light", SceneLightKind.Point, new(0, 4, 0), Quaternion.Identity, Vector3.One));
            edit.Settings = edit.Settings with { Width = 2560, Height = 1440 };
        }, Token);
        Assert.Equal(new Vector3(7, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Single(project.Lighting.Lights); Assert.Equal(2560, project.Settings.Width);
        Assert.True(project.Undo(Token)); Assert.Equal(new Vector3(3, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Empty(project.Lighting.Lights); Assert.True(project.Redo(Token));
        string path = Path.Combine(directory, "project.mmdscene"); project.Save(path, cancellationToken: Token);
        using var loaded = SceneProjectSession.Load(path, (_, _) => new(f.Model, f.Layout.Objects[0].Source), provider, Token);
        Assert.Equal(project.Objects.Select(o => (o.Id, o.Name, o.Position, o.Rotation, o.Scale)),
            loaded.Objects.Select(o => (o.Id, o.Name, o.Position, o.Rotation, o.Scale)));
        Assert.Equal(project.Settings, loaded.Settings);
        Assert.Equal(project.Frame.Surface.Snapshot.Vertices, loaded.Frame.Surface.Snapshot.Vertices);
        Assert.Single(loaded.Lighting.Lights); Assert.Equal(Vector3.Zero, f.Layout.Objects[0].Position);
    }
    [Fact]
    public void FailureCancellationAndEscapedEditReferencesCannotAlterPublishedState()
    {
        var f = Fixture(); using var project = new SceneProjectSession(f.Layout, f.Lights, [f.Motion], new NoPhysics());
        var before = project.Frame;
        Assert.Throws<InvalidOperationException>(() => project.Edit(edit => { edit.Composition.Remove(f.Motion.ObjectId); }, Token));
        Assert.Same(before, project.Frame); Assert.False(project.CanUndo);
        Assert.Throws<ArgumentException>(() => project.Edit(edit => edit.Motions.Add(f.Motion), Token));
        Assert.Same(before, project.Frame);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => project.Edit(_ => throw new Exception("must not execute"), cancelled.Token));
        SceneProjectEdit? escaped = null;
        project.Edit(edit => { escaped = edit; edit.Composition.SetTransform(f.Motion.ObjectId, Vector3.UnitX, Quaternion.Identity, 1); }, Token);
        escaped!.Composition.SetTransform(f.Motion.ObjectId, new(99, 0, 0), Quaternion.Identity, 1);
        escaped.Motions.Clear();
        Assert.Equal(Vector3.UnitX, project.Objects[0].Position); Assert.Single(project.Actors);
        project.Seek(30, Token); Assert.Equal(new Vector3(4, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
    }
    [Fact]
    public void UndoKeepsCurrentClockAndNewEditInvalidatesRedo()
    {
        var f = Fixture(); using var project = new SceneProjectSession(f.Layout, f.Lights, [f.Motion], new NoPhysics());
        project.Edit(e => e.Composition.SetTransform(f.Motion.ObjectId, Vector3.UnitX, Quaternion.Identity, 1), Token);
        project.Seek(30, Token); Assert.True(project.Undo(Token)); Assert.Equal(30, project.CurrentFrame);
        Assert.Equal(new Vector3(3, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
        project.Edit(e => e.Composition.SetTransform(f.Motion.ObjectId, new(2, 0, 0), Quaternion.Identity, 1), Token);
        Assert.False(project.CanRedo); Assert.False(project.Redo(Token));
    }
    [Fact]
    public void NestedEditsAndDisposedOwnerRejectWithoutPublishing()
    {
        var f = Fixture(); var project = new SceneProjectSession(f.Layout, f.Lights, [f.Motion], new NoPhysics());
        var frame = project.Frame;
        Assert.Throws<InvalidOperationException>(() => project.Edit(_ => project.Edit(_ => { }, Token), Token));
        Assert.Same(frame, project.Frame); project.Dispose(); project.Dispose();
        Assert.Throws<ObjectDisposedException>(() => project.Seek(0, Token));
        Assert.Throws<ObjectDisposedException>(() => project.Edit(_ => { }, Token));
    }
    [Fact]
    public void DeferredVertexSkinningPreservesExportSessionAndAvoidsCpuMaterialization()
    {
        var f = Fixture();
        using var project = new SceneProjectSession(f.Layout, f.Lights, [f.Motion], new NoPhysics());
        long matBefore = SceneDeformation.CpuMaterializations;
        project.DeferVertexSkinningToRenderer = true;
        project.Seek(30, Token);
        Assert.True(project.Frame.Surface.Snapshot.HasDeferredDeformation);
        Assert.True(project.DeferVertexSkinningToRenderer);
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);

        var previewReq = project.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        using var cloned = previewReq.CreateSession(new NoPhysics(), Token);
        Assert.True(cloned.DeferVertexSkinningToRenderer);
        cloned.Seek(30, Token);
        Assert.True(cloned.Frame.Surface.Snapshot.HasDeferredDeformation);
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException("No physical actor.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException("No shared world requested.");
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
