using System.Collections;
using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaSaveSnapshotTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pma-snapshot-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmaSaveSnapshotTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task CapturedAuthoredContentAndClockSurviveLiveEditsSeekAndDisposal()
    {
        var project = Fixture(out _, out var id);
        project.Seek(30, Token);
        var before = project.Frame;
        using var snapshot = PmaProjectStore.CaptureSnapshot(project, Options(), Token);
        Assert.Same(before, project.Frame);
        Assert.False(project.CanUndo);
        Assert.False(project.CanRedo);
        Assert.Equal(30, project.CurrentFrame);
        project.Edit(edit =>
        {
            edit.Composition.SetTransform(id, new(50, 0, 0), Quaternion.Identity, 2);
            edit.Settings = edit.Settings with { Width = 3840, Height = 2160 };
            edit.Lighting.Remove(edit.Lighting.Lights[0].Id);
            var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root", Translation = new(99, 0, 0) }] });
            edit.Motions[0] = edit.Motions[0] with { Motion = asset.Motion, Asset = asset };
        }, Token);
        project.Seek(0, Token);
        project.Dispose();
        string path = Package("captured");
        await Task.Run(() => snapshot.Save(path, Token), Token);
        using var loaded = Load(path);
        Assert.Equal(snapshot.ProjectId, loaded.ProjectId);
        Assert.Equal(30, loaded.Session.CurrentFrame);
        Assert.Equal(1280, loaded.Session.Settings.Width);
        Assert.Equal(Vector3.Zero, loaded.Session.Objects[0].Position);
        Assert.Single(loaded.Session.Lighting.Lights);
        Assert.Equal(before.Surface.Snapshot.Vertices, loaded.Session.Frame.Surface.Snapshot.Vertices);
    }

    [Fact]
    public void ReceiverSelectionSurvivesDetachedPmaCaptureReopenAndResave()
    {
        using var project = Fixture(out _, out var objectId);
        var light = project.Lighting.Lights[0] with { Receivers = new(SceneLightReceiverMode.Include, [objectId]) };
        project.Edit(edit => edit.Lighting.Update(light), Token);
        using var captured = PmaProjectStore.CaptureSnapshot(project, Options(), Token);
        project.Edit(edit => edit.Lighting.Update(light with { Receivers = null }), Token);
        captured.Save(Package("receiver-captured"), Token);
        using var loaded = Load(Package("receiver-captured"));
        Assert.Equal(light, Assert.Single(loaded.Session.Lighting.Lights));
        PmaProjectStore.Save(Package("receiver-resaved"), loaded.Session, Options(), Token);
        using var resaved = Load(Package("receiver-resaved"));
        Assert.Equal(light, Assert.Single(resaved.Session.Lighting.Lights));
        Assert.Null(Assert.Single(project.Lighting.Lights).Receivers);
    }

    [Fact]
    public void DetachedPmaCaptureRetainsParentGroupAndLocalTransformAfterLiveParentRemoval()
    {
        using var project = Fixture(out _, out var objectId);
        var group = new SceneLightGroup(Guid.NewGuid(), "跟随灯组", false, 2.5f);
        var light = project.Lighting.Lights[0] with
        {
            GroupId = group.Id, Parent = new(objectId, 0), Position = new(2, -3, 4),
            Rotation = Quaternion.CreateFromYawPitchRoll(.2f, .3f, -.4f), Range = 17,
        };
        project.Edit(edit =>
        {
            edit.Lighting.AddGroup(group);
            edit.Lighting.Update(light);
        }, Token);
        using var captured = PmaProjectStore.CaptureSnapshot(project, Options(), Token);
        project.Edit(edit => edit.Lighting.Update(light with { Parent = null, Position = Vector3.Zero }), Token);
        captured.Save(Package("parent-captured"), Token);
        using var loaded = Load(Package("parent-captured"));
        Assert.Equal(light, Assert.Single(loaded.Session.Lighting.Lights));
        Assert.Equal(group, Assert.Single(loaded.Session.Lighting.Groups));
        Assert.Equal(objectId, Assert.Single(loaded.Session.Objects).Id);
        Assert.Null(Assert.Single(project.Lighting.Lights).Parent);
        PmaProjectStore.Save(Package("parent-resaved"), loaded.Session, Options(), Token);
        using var resaved = Load(Package("parent-resaved"));
        Assert.Equal(light, Assert.Single(resaved.Session.Lighting.Lights));
        Assert.Equal(group, Assert.Single(resaved.Session.Lighting.Groups));
    }

    [Fact]
    public void LoadedPackageResourcesOriginalModelsAndUnknownExtensionsArePinnedUntilLastSnapshot()
    {
        using var project = Fixture(out var original, out var id);
        string initial = Package("initial");
        using var extension = JsonDocument.Parse("{\"version\":17,\"future\":[\"保留\",3]}");
        PmaProjectStore.Save(initial, project, Options() with
        {
            ModelOverrides = new Dictionary<Guid, PmxModel> { [id] = Copy(original, "edited") },
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.future"] = extension.RootElement },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/data.bin"] = new byte[] { 1, 2, 3 } },
        }, Token);
        PmaModelResourceContext? context = null;
        var loaded = PmaProjectStore.Load(initial, Path.Combine(directory, "load-cache"), LoadModel, new NoPhysics(),
            new() { ModelAssetTransform = (resources, asset) => { context = resources; return asset; } }, Token);
        string extracted = loaded.CacheDirectory;
        var first = PmaProjectStore.CaptureSnapshot(loaded.Session, Options(), Token);
        var second = PmaProjectStore.CaptureSnapshot(loaded.Session, Options(), Token);
        Assert.Equal(first.ProjectId, second.ProjectId);
        loaded.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.ReadResource("render-resources/data.bin"));
        Assert.NotNull(context);
        Assert.Throws<ObjectDisposedException>(() => context.ReadResource("render-resources/data.bin"));
        Assert.True(Directory.Exists(extracted));
        File.Delete(project.Objects[0].SourcePath);
        File.Delete(Path.Combine(directory, "texture.png"));
        string recovery = Package("retained");
        first.Save(recovery, Token);
        first.Dispose();
        Assert.True(Directory.Exists(extracted));
        using (var reopened = Load(recovery))
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, reopened.ReadResource("render-resources/data.bin"));
            Assert.True(JsonElement.DeepEquals(extension.RootElement, reopened.OptionalExtensions["org.example.future"]));
            var version = Assert.Single(reopened.Models);
            Assert.Equal(PmxWriter.ToBytes(original), File.ReadAllBytes(Path.Combine(reopened.CacheDirectory, version.OriginalSourcePath)));
            using var input = File.OpenRead(reopened.Session.Objects[0].SourcePath);
            Assert.Equal("edited", PmxReader.Read(input).Comment);
        }
        second.Dispose();
        second.Dispose();
        Assert.False(Directory.Exists(extracted));
    }

    [Fact]
    public void FailedCaptureReleasesItsExtractionLease()
    {
        using var project = Fixture(out _, out _);
        PmaProjectStore.Save(Package("lease-source"), project, Options(), Token);
        var loaded = Load(Package("lease-source"));
        string extracted = loaded.CacheDirectory;
        using var extension = JsonDocument.Parse("{\"value\":3}");
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.CaptureSnapshot(loaded.Session, Options() with
        { OptionalExtensions = new Dictionary<string, JsonElement> { ["invalid"] = extension.RootElement } }, Token));
        loaded.Dispose();
        Assert.False(Directory.Exists(extracted));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "save-cache")));
    }

    [Fact]
    public void SaveOnlyDictionariesJsonAndBytesAreFrozenAtCapture()
    {
        using var project = Fixture(out var original, out var id);
        byte[] data = [4, 5, 6];
        var resources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/data.bin"] = data };
        var models = new Dictionary<Guid, PmxModel> { [id] = Copy(original, "captured override") };
        var document = JsonDocument.Parse("{\"value\":3}");
        var extensions = new Dictionary<string, JsonElement> { ["org.example.frozen"] = document.RootElement };
        using var snapshot = PmaProjectStore.CaptureSnapshot(project, Options() with
        { ModelOverrides = models, OptionalExtensions = extensions, AdditionalResources = resources }, Token);
        data[0] = 99; models.Clear(); resources.Clear(); extensions.Clear(); document.Dispose();
        snapshot.Save(Package("options"), Token);
        using var loaded = Load(Package("options"));
        Assert.Equal(new byte[] { 4, 5, 6 }, loaded.ReadResource("render-resources/data.bin"));
        Assert.Equal(3, loaded.OptionalExtensions["org.example.frozen"].GetProperty("value").GetInt32());
        using var input = File.OpenRead(loaded.Session.Objects[0].SourcePath);
        Assert.Equal("captured override", PmxReader.Read(input).Comment);
        using var next = PmaProjectStore.CaptureSnapshot(project, Options(), Token);
        Assert.Equal(snapshot.ProjectId, next.ProjectId);
        next.Save(Package("ordinary"), Token);
        using var ordinary = Load(Package("ordinary"));
        Assert.Empty(ordinary.ResourcePaths);
        Assert.Empty(ordinary.OptionalExtensions);
    }

    [Fact]
    public void CancelledOrFailedSnapshotSavePreservesLastGoodPackageAndCleansPrivateStage()
    {
        using var project = Fixture(out _, out _);
        string path = Package("safe");
        PmaProjectStore.Save(path, project, Options(), Token);
        byte[] previous = File.ReadAllBytes(path);
        using var snapshot = PmaProjectStore.CaptureSnapshot(project, Options(), Token);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => snapshot.Save(path, cancelled.Token));
        Assert.Equal(previous, File.ReadAllBytes(path));
        File.Delete(Path.Combine(directory, "texture.png"));
        Assert.ThrowsAny<IOException>(() => snapshot.Save(path, Token));
        Assert.Equal(previous, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "save-cache")));
    }

    [Fact]
    public async Task ConcurrentSaveRejectsAndDisposalLetsActiveSaveFinishBeforeReleasingItsLease()
    {
        using var project = Fixture(out _, out var id);
        PmaProjectStore.Save(Package("source"), project, Options(), Token);
        var loaded = Load(Package("source"));
        string extracted = loaded.CacheDirectory;
        // Overrides resolve textures relative to this actor's current source model. The package
        // has rewritten texture.png to a portable alias, so use the actual loaded authored model.
        var model = Assert.Single(loaded.Session.CaptureAuthoredContent().Motions).Model;
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var blocking = new BlockingTextures(model.Textures, entered, resume);
        var snapshot = PmaProjectStore.CaptureSnapshot(loaded.Session, Options() with
        { ModelOverrides = new Dictionary<Guid, PmxModel> { [id] = Copy(model, "blocking", blocking) } }, Token);
        loaded.Dispose();
        var save = Task.Run(() => snapshot.Save(Package("active"), Token), Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20), Token));
            Assert.Throws<InvalidOperationException>(() => snapshot.Save(Package("concurrent"), Token));
            snapshot.Dispose();
            Assert.Throws<ObjectDisposedException>(() => snapshot.Save(Package("disposed"), Token));
            Assert.True(Directory.Exists(extracted));
        }
        finally { resume.Set(); }
        await save;
        Assert.False(Directory.Exists(extracted));
        using var reopened = Load(Package("active"));
        Assert.Equal(snapshot.ProjectId, reopened.ProjectId);
    }

    [Fact]
    public void CaptureRejectsNestedTransactionCancellationAndDisposedOwner()
    {
        var project = Fixture(out _, out _);
        Assert.Throws<InvalidOperationException>(() => project.Edit(_ => PmaProjectStore.CaptureSnapshot(project, Options(), Token), Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PmaProjectStore.CaptureSnapshot(project, Options(), cancelled.Token));
        project.Dispose();
        Assert.Throws<ObjectDisposedException>(() => PmaProjectStore.CaptureSnapshot(project, Options(), Token));
        Assert.False(Directory.Exists(Path.Combine(directory, "save-cache")));
    }

    private PmaSaveOptions Options() => new() { CacheDirectory = Path.Combine(directory, "save-cache") };
    private string Package(string name) => Path.Combine(directory, name + ".pma");
    private PmaLoadedProject Load(string path) => PmaProjectStore.Load(path, Path.Combine(directory, "load-cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
    private SceneProjectSession Fixture(out PmxModel model, out Guid id)
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        model = new()
        {
            Header = template.Header, Name = "actor", EnglishName = "", Comment = "original", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = ["texture.png"], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.Zero, EdgeSize = 1, DrawMode = PmxDrawModeFlags.None,
                TextureIndex = 0, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        File.WriteAllBytes(Path.Combine(directory, "texture.png"), [1, 2, 3, 4]);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", source, ModelRole.Character, Rest(model)); id = actor.Id;
        var lights = new SceneLightingEditor();
        lights.Add(new(Guid.NewGuid(), "light", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One));
        var motion = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitX }] });
        return new(layout, lights, [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics(),
            new() { Width = 1280, Height = 720 }, cancellationToken: Token);
    }
    private static PmxModel Copy(PmxModel m, string comment, IReadOnlyList<string>? textures = null) => new()
    { Header = m.Header, Name = m.Name, EnglishName = m.EnglishName, Comment = comment, EnglishComment = m.EnglishComment,
        Vertices = m.Vertices, Faces = m.Faces, Textures = textures ?? m.Textures, Materials = m.Materials, Bones = m.Bones, Morphs = m.Morphs,
        DisplayFrames = m.DisplayFrames, RigidBodies = m.RigidBodies, Joints = m.Joints, SoftBodies = m.SoftBodies };
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
        new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException("Snapshot capture/save must not create a physics world.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException("Snapshot capture/save must not create a physics world.");
    }
    private sealed class BlockingTextures(IReadOnlyList<string> source, ManualResetEventSlim entered, ManualResetEventSlim resume) : IReadOnlyList<string>
    {
        public int Count => source.Count;
        public string this[int index] => source[index];
        public IEnumerator<string> GetEnumerator()
        { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(30), Token)) throw new TimeoutException("Snapshot save was not released."); return source.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
