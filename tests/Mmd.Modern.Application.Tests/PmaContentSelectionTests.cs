using System.Buffers;
using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaContentSelectionTests : IDisposable
{
    private const string ExcludedExtension = "org.example.selected-out";
    private const string ExcludedPrefix = "render-resources/selected-out/";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pma-content-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmaContentSelectionTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ProjectionIsDetachedAndPreservesOriginalSceneTracksModelsAndCoreSettings()
    {
        var physics = new CountingNoPhysics();
        using var project = Fixture(physics);
        project.Edit(edit => edit.Composition.SetTransform(edit.Composition.Objects[0].Id,
            new(2, 3, 4), Quaternion.Identity, 1.25f), Token);
        Assert.True(project.Undo(Token));
        var frame = project.Frame;
        var actor = Assert.Single(project.Objects);
        var original = File.ReadAllBytes(actor.SourcePath);
        var sceneAsset = project.CaptureAuthoredContent().SceneMotion!;
        var settings = project.Settings;
        var calls = physics.Calls;
        bool undo = project.CanUndo, redo = project.CanRedo;
        using var capture = PmaProjectStore.CaptureSnapshot(project, Options() with { ContentSelection = Selection() }, Token);
        capture.Save(Package("projected"), Token);
        Assert.Same(frame, project.Frame);
        Assert.Equal(30, project.CurrentFrame);
        Assert.Equal(undo, project.CanUndo); Assert.Equal(redo, project.CanRedo);
        Assert.Equal(calls, physics.Calls);
        Assert.Equal(ModelRole.Character, actor.Role);
        Assert.Equal(ModelRole.Environment, project.GetModelPurpose(actor.Id).Role);
        Assert.Single(project.Lighting.Lights);
        Assert.Equal("genshin", project.RenderingStyles.DefaultStyle.StableId);
        Assert.Equal(original, File.ReadAllBytes(actor.SourcePath));
        using var loaded = Load(Package("projected"), new() { ContentSelection = Selection() });
        Assert.Empty(loaded.Session.Lighting.Lights);
        Assert.Equal(ModelRole.Unspecified, Assert.Single(loaded.Session.Objects).Role);
        Assert.Equal(new(ModelRole.Unspecified), loaded.Session.GetModelPurpose(actor.Id));
        Assert.Equal("mmd", loaded.Session.RenderingStyles.DefaultStyle.StableId);
        Assert.Equal(settings, loaded.Session.Settings);
        Assert.Equal(30, loaded.Session.CurrentFrame);
        var restoredScene = loaded.Session.CaptureAuthoredContent().SceneMotion!;
        Assert.Equal(sceneAsset.Sha256, restoredScene.Sha256);
        Assert.Equal(sceneAsset.Motion.CameraKeyframes[0].Interest, restoredScene.Motion.CameraKeyframes[0].Interest);
        Assert.Equal(sceneAsset.Motion.LightKeyframes[0].Color, restoredScene.Motion.LightKeyframes[0].Color);
        Assert.Equal(sceneAsset.Motion.LightKeyframes[0].Direction, restoredScene.Motion.LightKeyframes[0].Direction);
        var version = Assert.Single(loaded.Models);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(loaded.CacheDirectory, version.OriginalSourcePath)));
    }

    [Fact]
    public void RetainedAndSuppliedContentAreFilteredBeforeBudgetingOrReadingAndLeaseSurvivesSourceClose()
    {
        using var project = Fixture(new());
        using var opaque = JsonDocument.Parse("{\"version\":73,\"future\":[1,2,3]}");
        PmaProjectStore.Save(Package("source"), project, Options() with
        {
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>>
            { [ExcludedPrefix + "old.bin"] = new byte[16_384], ["render-resources/unknown/data.bin"] = new byte[] { 4, 5, 6 } },
            OptionalExtensions = new Dictionary<string, JsonElement>
            { [ExcludedExtension] = opaque.RootElement, ["org.example.unknown"] = opaque.RootElement },
        }, Token);
        var loaded = Load(Package("source"));
        string extracted = loaded.CacheDirectory;
        // A missing excluded retained file must never be opened by capture or staging.
        File.Delete(Path.Combine(extracted, ExcludedPrefix.Replace('/', Path.DirectorySeparatorChar), "old.bin"));
        using var unreadable = new UnreadableMemory();
        using var disposedJson = JsonDocument.Parse("{\"mustNotBeCloned\":true}");
        JsonElement excludedJson = disposedJson.RootElement; disposedJson.Dispose();
        using var snapshot = PmaProjectStore.CaptureSnapshot(loaded.Session, Options() with
        {
            ContentSelection = Selection(),
            ValidationOptions = new() { MaxEntryBytes = 8192, MaxExpandedBytes = 32768 },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [ExcludedPrefix + "new.bin"] = unreadable.Memory },
            OptionalExtensions = new Dictionary<string, JsonElement> { [ExcludedExtension] = excludedJson },
        }, Token);
        loaded.Dispose();
        Assert.True(Directory.Exists(extracted));
        snapshot.Save(Package("filtered"), Token);
        using (var reopened = Load(Package("filtered")))
        {
            Assert.Equal(new[] { "render-resources/unknown/data.bin" }, reopened.ResourcePaths);
            Assert.Equal(new byte[] { 4, 5, 6 }, reopened.ReadResource(reopened.ResourcePaths[0]));
            Assert.False(reopened.OptionalExtensions.ContainsKey(ExcludedExtension));
            Assert.True(JsonElement.DeepEquals(opaque.RootElement, reopened.OptionalExtensions["org.example.unknown"]));
            Assert.False(reopened.OptionalExtensions.ContainsKey(SceneRenderingStyles.ExtensionName));
        }
        snapshot.Dispose();
        Assert.False(Directory.Exists(extracted));
    }

    [Theory]
    [InlineData("lights")]
    [InlineData("groups")]
    [InlineData("global-lighting")]
    [InlineData("purposes")]
    [InlineData("styles")]
    [InlineData("extension")]
    [InlineData("resource")]
    public void SelectedOutContentRejectsLoadBeforeHostCallbacksOrPhysics(string content)
    {
        using var project = Fixture(new());
        if (content == "groups") project.Edit(edit =>
        {
            foreach (var light in edit.Lighting.Lights.ToArray()) edit.Lighting.Remove(light.Id);
            edit.Lighting.AddGroup(new(Guid.NewGuid(), "空灯组"));
        }, Token);
        if (content == "global-lighting") project.Edit(edit =>
        {
            foreach (var light in edit.Lighting.Lights.ToArray()) edit.Lighting.Remove(light.Id);
            edit.Lighting.UpdateSettings(new(.5f, 2));
        }, Token);
        var selection = content switch
        {
            "lights" or "groups" or "global-lighting" => new PmaContentSelection(retainNativeLighting: false),
            "purposes" => new PmaContentSelection(retainModelPurposes: false),
            "styles" => new PmaContentSelection(retainRenderingStyles: false),
            "extension" => new PmaContentSelection([ExcludedExtension]),
            _ => new PmaContentSelection(excludedResourcePrefixes: [ExcludedPrefix]),
        };
        using var opaque = JsonDocument.Parse("{\"version\":1}");
        PmaProjectStore.Save(Package(content), project, Options() with
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { [ExcludedExtension] = opaque.RootElement },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [ExcludedPrefix + "data.bin"] = new byte[] { 1 } },
        }, Token);
        var physics = new CountingNoPhysics(); int models = 0, transforms = 0;
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Load(Package(content), Path.Combine(directory, "rejected-cache"),
            (path, token) => { models++; return LoadModel(path, token); }, physics, new()
            { ContentSelection = selection, ModelAssetTransform = (_, asset) => { transforms++; return asset; } }, Token));
        Assert.Equal(0, models); Assert.Equal(0, transforms); Assert.Equal(0, physics.Calls);
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "rejected-cache")));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void TypedSelectionsAreIndependentAndDefaultSaveRetainsExistingBehavior(bool lights, bool purposes, bool styles)
    {
        using var project = Fixture(new());
        var group = new SceneLightGroup(Guid.NewGuid(), "窗口", Intensity: 2);
        project.Edit(edit =>
        {
            edit.Lighting.AddGroup(group);
            edit.Lighting.AssignToGroup(edit.Lighting.Lights[0].Id, group.Id);
            edit.Lighting.UpdateSettings(new(.25f, -3));
        }, Token);
        var selection = new PmaContentSelection(retainNativeLighting: lights, retainModelPurposes: purposes, retainRenderingStyles: styles);
        PmaProjectStore.Save(Package("independent"), project, Options() with { ContentSelection = selection }, Token);
        using var loaded = Load(Package("independent"), new() { ContentSelection = selection });
        Assert.Equal(lights ? 1 : 0, loaded.Session.Lighting.Lights.Count);
        Assert.Equal(lights ? 1 : 0, loaded.Session.Lighting.Groups.Count);
        Assert.Equal(lights ? project.Lighting.Settings : SceneLightingSettings.Default, loaded.Session.Lighting.Settings);
        if (lights)
        {
            Assert.Equal(group, Assert.Single(loaded.Session.Lighting.Groups));
            Assert.Equal(group.Id, Assert.Single(loaded.Session.Lighting.Lights).GroupId);
        }
        // SceneProjectStore persists the effective lighting purpose, including an override of the layout role.
        Assert.Equal(purposes ? project.GetModelPurpose(project.Objects[0].Id).Role : ModelRole.Unspecified, loaded.Session.Objects[0].Role);
        Assert.Equal(purposes ? new(ModelRole.Environment, true) : new ModelPurpose(ModelRole.Unspecified),
            loaded.Session.GetModelPurpose(loaded.Session.Objects[0].Id));
        Assert.Equal(styles ? "genshin" : "mmd", loaded.Session.RenderingStyles.DefaultStyle.StableId);
    }

    [Fact]
    public void PolicyCopiesCollectionsAndUsesExactExtensionNamesAndDirectoryResourcePrefixes()
    {
        var names = new List<string> { ExcludedExtension };
        var prefixes = new List<string> { ExcludedPrefix };
        var selection = new PmaContentSelection(names, prefixes);
        names.Clear(); prefixes.Clear();
        using var project = Fixture(new());
        using var opaque = JsonDocument.Parse("{}");
        PmaProjectStore.Save(Package("canonical"), project, Options() with
        {
            ContentSelection = selection,
            OptionalExtensions = new Dictionary<string, JsonElement>
            { [ExcludedExtension] = opaque.RootElement, [ExcludedExtension + ".future"] = opaque.RootElement },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>>
            { ["render-resources/SELECTED-OUT/data.bin"] = new byte[] { 1 }, ["render-resources/selected-out-other/data.bin"] = new byte[] { 2 } },
        }, Token);
        using var loaded = Load(Package("canonical"));
        Assert.False(loaded.OptionalExtensions.ContainsKey(ExcludedExtension));
        Assert.True(loaded.OptionalExtensions.ContainsKey(ExcludedExtension + ".future"));
        Assert.Equal(new[] { "render-resources/selected-out-other/data.bin" }, loaded.ResourcePaths);
        Assert.Equal(new[] { ExcludedExtension }, selection.ExcludedExtensionNames);
        Assert.Equal(new[] { ExcludedPrefix }, selection.ExcludedResourcePrefixes);
    }

    [Theory]
    [InlineData("render-resources/selected-out")]
    [InlineData("render-resources/../out/")]
    [InlineData("render-resources//out/")]
    [InlineData("render-resources/CON/")]
    [InlineData("render-resources/trailing./")]
    [InlineData("RENDER-RESOURCES/out/")]
    [InlineData("render-resources\\out\\")]
    [InlineData("assets/out/")]
    [InlineData("")]
    public void NoncanonicalResourcePrefixesAreRejected(string prefix)
        => Assert.Throws<InvalidDataException>(() => new PmaContentSelection(excludedResourcePrefixes: [prefix]));

    [Theory]
    [InlineData("invalid")]
    [InlineData("org.example/invalid")]
    [InlineData("")]
    public void NoncanonicalExtensionNamesAreRejected(string name)
        => Assert.Throws<InvalidDataException>(() => new PmaContentSelection([name]));

    [Fact]
    public void BudgetPreflightAppliesTheSameSelectionToRetainedAndSuppliedResources()
    {
        using var project = Fixture(new());
        PmaProjectStore.Save(Package("budget"), project, Options() with
        { AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [ExcludedPrefix + "old.bin"] = new byte[4096] } }, Token);
        using var loaded = Load(Package("budget"));
        PmaProjectStore.ValidateAdditionalResourceBudget(loaded.Session,
            new Dictionary<string, long> { [ExcludedPrefix + "new.bin"] = long.MaxValue, ["render-resources/keep.bin"] = 3 },
            new() { MaxEntryBytes = 3, MaxExpandedBytes = 3, MaxFiles = 1, ContentSelection = Selection() });
    }

    private static PmaContentSelection Selection() => new([ExcludedExtension], [ExcludedPrefix], false, false, false);
    private PmaSaveOptions Options() => new() { CacheDirectory = Path.Combine(directory, "save-cache") };
    private string Package(string name) => Path.Combine(directory, name + ".pma");
    private PmaLoadedProject Load(string path, PmaLoadOptions? options = null)
        => PmaProjectStore.Load(path, Path.Combine(directory, "load-cache"), LoadModel, new CountingNoPhysics(), options, Token);
    private SceneProjectSession Fixture(CountingNoPhysics physics)
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        var model = new PmxModel
        {
            Header = template.Header, Name = "actor", EnglishName = "", Comment = "original material metadata", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = .6f }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = new(.2f, .4f, .6f, 1),
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = new(.1f), EdgeColor = new(0, 0, 0, 1), EdgeSize = .7f,
                DrawMode = PmxDrawModeFlags.None, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "native material retained", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor();
        var actor = composition.Add("actor", source, ModelRole.Character, Rest(model));
        var lighting = new SceneLightingEditor();
        lighting.Add(new(Guid.NewGuid(), "artistic", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One));
        lighting.SetModelRole(actor.Key, ModelRole.Environment, true);
        var motion = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root", Frame = 30 }] });
        var scene = SceneMotionAsset.FromMotion(new()
        {
            CameraKeyframes = [new() { Distance = -30, Interest = new(1, 2, 3), ViewAngle = 30, Perspective = true }],
            LightKeyframes = [new() { Color = new(.2f, .4f, .8f), Direction = new(1, -2, 3) }],
        });
        var project = new SceneProjectSession(composition, lighting,
            [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)], physics,
            new() { Width = 1280, Height = 720, Physics = new() { Backend = ProjectPhysicsBackend.Bepu, GravityY = -123, GroundEnabled = false } },
            sceneMotion: scene, initialFrame: 30, cancellationToken: Token);
        project.SetDefaultRenderingStyle(RenderingStyleId.Genshin, Token);
        return project;
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private sealed class CountingNoPhysics : IProjectPhysicsBackendProvider
    {
        public int Calls { get; private set; }
        public int PoseInitializationSubsteps { get { Calls++; return 0; } }
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        { Calls++; throw new InvalidOperationException("No solver expected."); }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        { Calls++; throw new InvalidOperationException("No solver expected."); }
    }
    private sealed class UnreadableMemory : MemoryManager<byte>
    {
        public override Memory<byte> Memory => CreateMemory(64 * 1024);
        public override Span<byte> GetSpan() => throw new InvalidOperationException("Excluded bytes must not be read.");
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
