using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class ClassicProjectStoreTests : IDisposable
{
    private const string Styles = "org.mmd-modern.rendering-styles";
    private const string Materials = "org.mda.model-material-bindings";
    private const string Unknown = "org.example.future";
    private static readonly string[] ModernResources =
    [
        "render-resources/material-bindings/map.bin",
        "render-resources/generated-materials/material.bin",
        "render-resources/generated-styles/style.bin"
    ];
    private readonly string directory = Path.Combine(Path.GetTempPath(), "classic-profile-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ClassicProjectStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ModernSourceProjectsToClassicWithoutChangingAuthoredStateOrClock()
    {
        using var project = Project(modern: true);
        var actor = Assert.Single(project.Objects);
        var sourceBytes = File.ReadAllBytes(actor.SourcePath);
        var emm = EmmState(project);
        var options = D3D9EmmPma.CreateSaveOptions(project, emm, Options() with
        {
            OptionalExtensions = new Dictionary<string, JsonElement>
            {
                [Materials] = JsonSerializer.SerializeToElement(new { opaqueModernMaterial = true }),
                [Unknown] = JsonSerializer.SerializeToElement(new { version = 29, future = "保留", data = new[] { 2, 7 } })
            },
            AdditionalResources = ModernResources.ToDictionary(path => path, _ => (ReadOnlyMemory<byte>)new byte[] { 4, 8, 15 })
                .Append(new("render-resources/unknown/data.bin", new byte[] { 16, 23, 42 }))
                .Append(new("render-resources/generated-styles-other/data.bin", new byte[] { 9 }))
                .ToDictionary(pair => pair.Key, pair => pair.Value)
        });
        string modern = Path.Combine(directory, "source.pma"), classic = Package("projected");
        PmaProjectStore.Save(modern, project, options, Token);
        using var loadedModern = PmaProjectStore.Load(modern, Cache("modern"), LoadModel, new NoPhysics(), cancellationToken: Token);
        var live = loadedModern.Session;
        var before = live.CaptureAuthoredContent();
        double sourceFrame = live.CurrentFrame;
        var settings = live.Settings;
        var objects = live.Objects.ToArray();
        var frame = live.Frame;
        bool undo = live.CanUndo, redo = live.CanRedo;
        var render = live.CaptureRenderSnapshot();
        using var snapshot = Capture(live, Options());
        snapshot.Save(classic, Token);

        Assert.Same(frame, live.Frame);
        Assert.Equal(sourceFrame, live.CurrentFrame);
        Assert.Equal(undo, live.CanUndo); Assert.Equal(redo, live.CanRedo);
        Assert.Single(live.Lighting.Lights);
        Assert.Equal(ModelRole.Environment, live.GetModelPurpose(actor.Id).Role);
        Assert.True(live.GetModelPurpose(actor.Id).EnvironmentIndoor);
        Assert.Equal("custom-modern", live.RenderingStyles.DefaultStyle.StableId);
        Assert.Equal(sourceBytes, File.ReadAllBytes(actor.SourcePath));
        Assert.Contains(Materials, loadedModern.OptionalExtensions.Keys);
        Assert.All(ModernResources, path => Assert.Contains(path, loadedModern.ResourcePaths));

        using var loaded = Load(classic);
        Assert.Equal("mmd-classic-pmc", loaded.Format);
        AssertClassic(loaded);
        Assert.Equal(sourceFrame, loaded.Session.CurrentFrame);
        Assert.Equal(settings, loaded.Session.Settings);
        Assert.Equal(objects.Select(item => (item.Id, item.Name, item.Position, item.Rotation, item.Scale)),
            loaded.Session.Objects.Select(item => (item.Id, item.Name, item.Position, item.Rotation, item.Scale)));
        Assert.Equal(before.SceneMotion!.Sha256, loaded.Session.CaptureAuthoredContent().SceneMotion!.Sha256);
        Assert.Equal(before.SceneMotion.Length, loaded.Session.CaptureAuthoredContent().SceneMotion!.Length);
        var restoredRender = loaded.Session.CaptureRenderSnapshot();
        Assert.Equal(render.Camera, restoredRender.Camera);
        Assert.Equal(render.Light, restoredRender.Light);
        Assert.Equal(render.SelfShadow, restoredRender.SelfShadow);
        Assert.Equal(new PmmLightPose(new(.25f, .5f, .75f), new(1, -2, 3)), restoredRender.Light);
        var model = PmxReader.Read(loaded.Session.Objects[0].SourcePath);
        Assert.Equal("original PMX material and metadata", model.Comment);
        Assert.Equal(new Vector4(.2f, .3f, .4f, .85f), model.Materials[0].Diffuse);
        Assert.Equal(new Vector4(.5f, .6f, .7f, .8f), model.Materials[0].EdgeColor);
        Assert.Equal(.625f, model.Materials[0].EdgeSize);
        Assert.Equal("original material memo", model.Materials[0].Memo);
        Assert.True(JsonElement.DeepEquals(loadedModern.OptionalExtensions[Unknown], loaded.OptionalExtensions[Unknown]));
        Assert.Equal(new byte[] { 16, 23, 42 }, loaded.ReadResource("render-resources/unknown/data.bin"));
        Assert.Equal(new byte[] { 9 }, loaded.ReadResource("render-resources/generated-styles-other/data.bin"));
        var restoredEmm = D3D9EmmPma.Read(loaded);
        Assert.NotNull(restoredEmm);
        Assert.Equal(emm.Document.CopySourceBytes(), restoredEmm.Document.CopySourceBytes());
        Assert.Equal(emm.ImportPlan.Matches, restoredEmm.ImportPlan.Matches);
        Assert.Equal(new byte[] { 0, 255, 13, 10, 128 }, Assert.Single(restoredEmm.Assets).CopySourceBytes());
    }

    [Fact]
    public void SaveOnlyModernOverridesAreRemovedAndUnknownOptionsRemainDetached()
    {
        using var project = Project(modern: true);
        var extensions = new Dictionary<string, JsonElement>
        {
            [Styles] = JsonSerializer.SerializeToElement(new { version = 77, future = true }),
            [Materials] = JsonSerializer.SerializeToElement(new { material = "modern" }),
            [Unknown] = JsonSerializer.SerializeToElement(new { value = 11 })
        };
        byte[] bytes = [1, 3, 5];
        var resources = ModernResources.ToDictionary(path => path, _ => (ReadOnlyMemory<byte>)new byte[] { 2 });
        resources.Add("render-resources/unknown/snapshot.bin", bytes);
        using var snapshot = Capture(project, Options() with { OptionalExtensions = extensions, AdditionalResources = resources });
        extensions.Clear(); resources.Clear(); bytes[0] = 99;
        snapshot.Save(Package("save-options"), Token);
        using var loaded = Load(Package("save-options"));
        AssertClassic(loaded);
        Assert.Equal(11, loaded.OptionalExtensions[Unknown].GetProperty("value").GetInt32());
        Assert.Equal(new byte[] { 1, 3, 5 }, loaded.ReadResource("render-resources/unknown/snapshot.bin"));
        Assert.Single(project.Lighting.Lights);
        Assert.Equal("custom-modern", project.RenderingStyles.DefaultStyle.StableId);
    }

    [Fact]
    public void DetachedClassicSnapshotKeepsRetainedResourcesAfterSourceAndLoadedOwnerAreGone()
    {
        using var project = Project(modern: true);
        string initial = Path.Combine(directory, "retained-source.pma"), result = Package("detached");
        PmaProjectStore.Save(initial, project, Options() with
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { [Materials] = JsonSerializer.SerializeToElement(new { modern = true }) },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [ModernResources[0]] = new byte[] { 9 }, ["render-resources/unknown/retained.bin"] = new byte[] { 6, 2, 6 }
            }
        }, Token);
        var loaded = PmaProjectStore.Load(initial, Cache("source"), LoadModel, new NoPhysics(), cancellationToken: Token);
        string extracted = loaded.CacheDirectory;
        using var snapshot = Capture(loaded.Session, Options());
        Guid identity = loaded.ProjectId;
        loaded.Dispose();
        Assert.True(Directory.Exists(extracted));
        File.Delete(project.Objects[0].SourcePath);
        snapshot.Save(result, Token);
        using (var restored = Load(result))
        {
            AssertClassic(restored);
            Assert.Equal(identity, restored.ProjectId);
            Assert.Equal(new byte[] { 6, 2, 6 }, restored.ReadResource("render-resources/unknown/retained.bin"));
            Save(Package("resaved"), restored.Session, Options());
        }
        using (var resaved = Load(Package("resaved"))) AssertClassic(resaved);
        snapshot.Dispose();
        Assert.False(Directory.Exists(extracted));
    }

    [Theory]
    [InlineData("native-light")]
    [InlineData("model-purpose")]
    [InlineData("rendering-style")]
    [InlineData("material-extension")]
    [InlineData("material-resource")]
    [InlineData("generated-material-resource")]
    [InlineData("generated-style-resource")]
    [InlineData("resource-case")]
    public void ForgedClassicProfileRejectsBeforeModelDecorationAndPhysics(string kind)
    {
        using var project = Project(modern: false);
        PmaSaveOptions options = Options();
        if (kind == "native-light") project.Edit(edit => edit.Lighting.Add(ModernLight()), Token);
        if (kind == "model-purpose") project.Edit(edit => edit.Lighting.SetModelRole(project.Objects[0].Key, ModelRole.Environment, true), Token);
        if (kind == "rendering-style") project.SetDefaultRenderingStyle(new("custom-modern"), Token);
        if (kind == "material-extension") options = options with
        { OptionalExtensions = new Dictionary<string, JsonElement> { [Materials] = JsonSerializer.SerializeToElement(new { opaque = true }) } };
        string? resource = kind switch
        {
            "material-resource" => ModernResources[0], "generated-material-resource" => ModernResources[1],
            "generated-style-resource" => ModernResources[2], "resource-case" => "render-resources/GENERATED-STYLES/case.bin", _ => null
        };
        if (resource is not null) options = options with
        { AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [resource] = new byte[] { 1 } } };
        string path = Package("forged-" + kind);
        PmcProjectStore.Save(path, project, options, Token);
        var physics = new NoPhysics(); int loaders = 0, decorators = 0;
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Load(path, (p, t) => { loaders++; return LoadModel(p, t); }, physics,
            new() { ModelAssetTransform = (_, asset) => { decorators++; return asset; } }));
        Assert.Equal(0, loaders); Assert.Equal(0, decorators); Assert.Equal(0, physics.Calls);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.Exists(Cache("classic")) ? Directory.GetDirectories(Cache("classic")) : []);
    }

    [Fact]
    public void ClassicFacadeKeepsWrongMagicAndExtensionRejectionAtomic()
    {
        using var project = Project(modern: false);
        string modern = Path.Combine(directory, "ordinary.pma"), renamed = Package("renamed");
        PmaProjectStore.Save(modern, project, Options(), Token); File.Copy(modern, renamed);
        byte[] bytes = File.ReadAllBytes(renamed);
        var physics = new NoPhysics(); int loaders = 0;
        Assert.Throws<NotSupportedException>(() => Load(renamed, (p, t) => { loaders++; return LoadModel(p, t); }, physics));
        Assert.Equal(0, loaders); Assert.Equal(0, physics.Calls);
        Assert.Equal(bytes, File.ReadAllBytes(renamed));
        byte[] previous = [8, 5, 3]; File.WriteAllBytes(modern, previous);
        Assert.Throws<ArgumentException>(() => Save(modern, project, Options()));
        Assert.Equal(previous, File.ReadAllBytes(modern));
        using var snapshot = Capture(project, Options());
        Assert.Throws<ArgumentException>(() => snapshot.Save(modern, Token));
        Assert.Equal(previous, File.ReadAllBytes(modern));
    }

    [Fact]
    public void ExistingLoadDecoratorBudgetsAndSaveCancellationArePreserved()
    {
        using var project = Project(modern: false);
        string path = Package("caller-options"); Save(path, project, Options());
        int decorated = 0;
        using (var loaded = Load(path, options: new() { ModelAssetTransform = (_, asset) => { decorated++; return asset; } }))
            Assert.Equal(1, decorated);
        var physics = new NoPhysics(); int loaders = 0;
        Assert.Throws<InvalidDataException>(() => Load(path, (p, t) => { loaders++; return LoadModel(p, t); }, physics,
            new() { MaxPackageBytes = 1 }));
        Assert.Equal(0, loaders); Assert.Equal(0, physics.Calls);
        byte[] previous = File.ReadAllBytes(path);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Save(path, project, Options(), cancelled.Token));
        Assert.Equal(previous, File.ReadAllBytes(path));
    }

    [Fact]
    public void SaveOptionsBuilderCombinesExclusionsBeforeCodecBudgetsAndPreservesCallerOptions()
    {
        using var project = Project(modern: true);
        Func<PmaModelResourceContext, SceneAnimationModelAsset, SceneAnimationModelAsset> decorate = (_, asset) => asset;
        var saveSelection = new PmaContentSelection(["org.example.save-only"], ["render-resources/save-only/"]);
        var validationSelection = new PmaContentSelection(["org.example.validation-only"], ["render-resources/validation-only/"]);
        var original = Options() with
        {
            ContentSelection = saveSelection,
            OptionalExtensions = new Dictionary<string, JsonElement> { [Unknown] = JsonSerializer.SerializeToElement(new { keep = true }) },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/unknown/data.bin"] = new byte[] { 1 } },
            ValidationOptions = new()
            {
                ContentSelection = validationSelection, ModelAssetTransform = decorate, MaxPackageBytes = 100_000_000,
                MaxExpandedBytes = 200_000_000, MaxEntryBytes = 1_000_000, MaxFiles = 77, MaxManifestBytes = 900_000
            }
        };
        var prepared = Method("CreateSaveOptions").CreateDelegate<Func<PmaSaveOptions?, PmaSaveOptions>>()(original);
        Assert.NotSame(original, prepared);
        Assert.Same(original.ModelOverrides, prepared.ModelOverrides);
        Assert.Same(original.OptionalExtensions, prepared.OptionalExtensions);
        Assert.Same(original.AdditionalResources, prepared.AdditionalResources);
        Assert.Equal(original.CacheDirectory, prepared.CacheDirectory);
        Assert.Same(decorate, prepared.ValidationOptions.ModelAssetTransform);
        Assert.Equal(original.ValidationOptions with { ContentSelection = prepared.ContentSelection }, prepared.ValidationOptions);
        var policy = Assert.IsType<PmaContentSelection>(prepared.ContentSelection);
        Assert.Same(policy, prepared.ValidationOptions.ContentSelection);
        Assert.False(policy.RetainNativeLighting); Assert.False(policy.RetainModelPurposes); Assert.False(policy.RetainRenderingStyles);
        Assert.Equal(new[] { Styles, Materials, "org.example.save-only", "org.example.validation-only" }.Order(), policy.ExcludedExtensionNames.Order());
        Assert.Equal(new[] { "render-resources/material-bindings/", "render-resources/generated-materials/", "render-resources/generated-styles/",
            "render-resources/save-only/", "render-resources/validation-only/" }.Order(), policy.ExcludedResourcePrefixes.Order());
        Assert.Same(saveSelection, original.ContentSelection);
        Assert.Same(validationSelection, original.ValidationOptions.ContentSelection);
        Assert.True(saveSelection.RetainNativeLighting); Assert.True(validationSelection.RetainRenderingStyles);
        // Renderer codecs invoke this helper before allocating byte copies. Removed namespaces must not consume that budget.
        PmaProjectStore.ValidateAdditionalResourceBudget(project, new Dictionary<string, long>
        {
            [ModernResources[0]] = 100, ["render-resources/save-only/data.bin"] = 100,
            ["render-resources/validation-only/data.bin"] = 100, ["render-resources/unknown/data.bin"] = 1
        }, prepared.ValidationOptions with { MaxEntryBytes = 2, MaxExpandedBytes = 2, MaxFiles = 1 });
    }

    private static void AssertClassic(PmaLoadedProject loaded)
    {
        Assert.Empty(loaded.Session.Lighting.Lights);
        Assert.All(loaded.Session.Objects, actor =>
        {
            Assert.Equal(ModelRole.Unspecified, actor.Role);
            Assert.Equal(new ModelPurpose(ModelRole.Unspecified), loaded.Session.GetModelPurpose(actor.Id));
        });
        Assert.Equal("mmd", loaded.Session.RenderingStyles.DefaultStyle.StableId);
        Assert.Empty(loaded.Session.RenderingStyles.ModelOverrides);
        Assert.DoesNotContain(Styles, loaded.OptionalExtensions.Keys);
        Assert.DoesNotContain(Materials, loaded.OptionalExtensions.Keys);
        Assert.DoesNotContain(loaded.ResourcePaths, path => ModernResources.Any(modern =>
            path.StartsWith(modern[..(modern.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase)));
    }

    // Reflection lets the initial RED execute before the new engine-owned facade exists.
    private static Type Facade()
    {
        var type = typeof(D3D9SurfaceObjectBinding).Assembly.GetType("Mmd.Modern.ViewOutput.D3D9.Compatibility.D3D9ClassicProjectStore");
        Assert.NotNull(type); return type;
    }
    private static MethodInfo Method(string name) => Assert.Single(Facade().GetMethods(BindingFlags.Public | BindingFlags.Static), method => method.Name == name);
    private static PmaSaveSnapshot Capture(SceneProjectSession project, PmaSaveOptions? options)
        => Method("CaptureSnapshot").CreateDelegate<Func<SceneProjectSession, PmaSaveOptions?, CancellationToken, PmaSaveSnapshot>>()(project, options, Token);
    private static void Save(string path, SceneProjectSession project, PmaSaveOptions? options, CancellationToken? token = null)
        => Method("Save").CreateDelegate<Action<string, SceneProjectSession, PmaSaveOptions?, CancellationToken>>()(path, project, options, token ?? Token);
    private PmaLoadedProject Load(string path, Func<string, CancellationToken, SceneAnimationModelAsset>? loader = null,
        IProjectPhysicsBackendProvider? physics = null, PmaLoadOptions? options = null)
        => Method("Load").CreateDelegate<Func<string, string, Func<string, CancellationToken, SceneAnimationModelAsset>, IProjectPhysicsBackendProvider,
            PmaLoadOptions?, CancellationToken, PmaLoadedProject>>()(path, Cache("classic"), loader ?? LoadModel, physics ?? new NoPhysics(), options, Token);
    private PmaSaveOptions Options() => new() { CacheDirectory = Cache("save") };
    private string Cache(string name) => Path.Combine(directory, name + "-cache");
    private string Package(string name) => Path.Combine(directory, name + ".pmc");

    private SceneProjectSession Project(bool modern)
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "classic fixture", EnglishName = "", Comment = "original PMX material and metadata", EnglishComment = "",
            Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "material", EnglishName = "", Diffuse = new(.2f, .3f, .4f, .85f), Specular = new(.1f, .2f, .3f),
                SpecularPower = 7, Ambient = new(.05f, .1f, .15f), DrawMode = PmxDrawModeFlags.None, EdgeColor = new(.5f, .6f, .7f, .8f),
                EdgeSize = .625f, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "original material memo", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor();
        var actor = composition.Add("classic actor", source, modern ? ModelRole.Character : ModelRole.Unspecified, Rest(model));
        composition.SetTransform(actor.Id, new(1, 2, 3), Quaternion.Identity, 1.25f);
        var lights = new SceneLightingEditor();
        if (modern) { lights.Add(ModernLight()); lights.SetModelRole(actor.Key, ModelRole.Environment, true); }
        var motion = SceneMotionAsset.FromMotion(new()
        {
            CameraKeyframes = [new() { Frame = 0, Distance = -24, Interest = new(1, 2, 3), Rotation = new(.1f, .2f, .3f), ViewAngle = 42 }],
            LightKeyframes = [new() { Frame = 0, Color = new(.25f, .5f, .75f), Direction = new(1, -2, 3) }],
            SelfShadowKeyframes = [new() { Frame = 0, Mode = 1, Distance = .001f }]
        });
        var project = new SceneProjectSession(composition, lights, [], new NoPhysics(), new()
        {
            Width = 1280, Height = 720, FpsNumerator = 60,
            Physics = new() { Backend = ProjectPhysicsBackend.Bepu, GravityX = 1, GravityY = -42, GravityZ = 3, GroundEnabled = false }
        }, sceneMotion: motion, initialFrame: 7.5, cancellationToken: Token);
        if (modern) project.SetDefaultRenderingStyle(new("custom-modern"), Token);
        return project;
    }
    private static NativeSceneLight ModernLight() => new(Guid.NewGuid(), "modern point", SceneLightKind.Point,
        new(4, 5, 6), Quaternion.Identity, new(.9f, .8f, .7f));
    private static D3D9EmmPmaState EmmState(SceneProjectSession project)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("[Info]\r\nVersion=3\r\n[Object]\r\nPmd0=models\\actor.pmx\r\n[Effect]\r\nDefault=main_default\r\nPmd0=effects\\main.fx\r\n");
        var document = EmmReader.Read(bytes, new() { AnsiCodePage = 936 });
        var plan = EmmImportPlan.Create(document, @"C:\original\project.emm",
            [new(project.Objects[0].Id, true, @"models\actor.pmx", 0)], @"C:\original");
        return D3D9EmmPma.Capture(plan, [new(@"C:\original\effects\main.fx", new byte[] { 0, 255, 13, 10, 128 })]);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public int Calls { get; private set; }
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj)
        { Calls++; throw new InvalidOperationException("This model has no physics bodies."); }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        { Calls++; throw new InvalidOperationException("This model has no physics bodies."); }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup left its temporary directory.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
