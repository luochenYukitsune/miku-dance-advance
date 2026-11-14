using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class ClassicPackagePreflightTests : IDisposable
{
    private readonly string directory = Path.Combine("G:/MDA-Work/compat/20261105/preflight436", "tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string LoadCache => Path.Combine(directory, "load-cache");
    public ClassicPackagePreflightTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false, "emm-schema")]
    [InlineData(true, "emm-schema")]
    [InlineData(false, "emm-sha")]
    [InlineData(true, "emm-sha")]
    [InlineData(false, "emm-match")]
    [InlineData(true, "emm-match")]
    [InlineData(false, "emm-missing")]
    [InlineData(true, "emm-missing")]
    [InlineData(false, "tree-schema")]
    [InlineData(true, "tree-schema")]
    [InlineData(false, "tree-traversal")]
    [InlineData(true, "tree-traversal")]
    [InlineData(false, "tree-sha")]
    [InlineData(true, "tree-sha")]
    [InlineData(false, "tree-missing")]
    [InlineData(true, "tree-missing")]
    public void InvalidTraditionalPayloadRejectsBeforeCallerHookEveryHostCallbackAndPhysics(bool prepared, string damage)
    {
        var fixture = SaveFixture(damage: damage);
        // A structurally valid neutral PMC intentionally carries uninterpreted traditional bytes.
        using (var neutral = PmcProjectStore.Load(fixture.Path, Path.Combine(directory, "neutral-cache"), LoadModel, new CountingPhysics(), cancellationToken: Token))
            Assert.Equal(PmcProjectStore.Format, neutral.Format);
        int hooks = 0, models = 0, transforms = 0; var physics = new CountingPhysics();
        var options = new PmaLoadOptions
        {
            PackagePreflight = (_, _) => hooks++,
            ModelAssetTransform = (_, asset) => { transforms++; return asset; }
        };
        Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = Load(prepared, fixture.Path, options, physics,
                (path, token) => { models++; return LoadModel(path, token); });
        });
        Assert.Equal(0, hooks); Assert.Equal(0, models); Assert.Equal(0, transforms); Assert.Equal(0, physics.Calls);
        Assert.Empty(Directory.GetDirectories(LoadCache));
    }

    [Theory]
    [InlineData(932)]
    [InlineData(936)]
    public void PreparedLoadPreservesFullOpaquePlanTreeAndUnknownDataBeforeHostConstruction(int codePage)
    {
        var fixture = SaveFixture(codePage); int hooks = 0, models = 0, transforms = 0;
        var physics = new CountingPhysics(); PmaPackageReadContext? captured = null;
        var options = new PmaLoadOptions
        {
            PackagePreflight = (context, token) =>
            {
                hooks++; captured = context;
                Assert.Equal(Token, token); Assert.Equal(PmcProjectStore.Format, context.Format);
                Assert.Equal(0, models); Assert.Equal(0, transforms); Assert.Equal(0, physics.Calls);
                Assert.Equal(17, context.OptionalExtensions["org.example.future"].GetProperty("value").GetInt32());
            },
            ModelAssetTransform = (_, asset) => { Assert.Equal(1, hooks); transforms++; return asset; }
        };
        var prepared = LoadPrepared(fixture.Path, options, physics, (path, token) =>
        {
            models++; Assert.Equal(1, hooks); AssertInvalid(captured!); return LoadModel(path, token);
        });
        using var package = Property<PmaLoadedProject>(prepared, "Package");
        var emm = Property<D3D9EmmPmaState>(prepared, "Emm");
        var tree = Property<D3D9EffectResourceTree>(prepared, "Tree");
        Assert.Equal(1, hooks); Assert.Equal(1, models); Assert.Equal(1, transforms); Assert.True(physics.Calls > 0);
        Assert.Equal(fixture.Emm.Document.CopySourceBytes(), emm.Document.CopySourceBytes());
        Assert.Equal(fixture.Emm.ImportPlan.LiveObjects.Select(value => (value.ObjectId, value.IsModel, value.SourcePath, value.RenderOrder)),
            emm.ImportPlan.LiveObjects.Select(value => (value.ObjectId, value.IsModel, value.SourcePath, value.RenderOrder)));
        Assert.Equal(fixture.Emm.ImportPlan.Matches, emm.ImportPlan.Matches);
        Assert.Equal(fixture.Emm.ImportPlan.PathContext.OriginalEmmPath, emm.ImportPlan.PathContext.OriginalEmmPath);
        Assert.Equal(fixture.Emm.ImportPlan.PathContext.OriginalWorkingDirectory, emm.ImportPlan.PathContext.OriginalWorkingDirectory);
        Assert.Equal(3, emm.ImportPlan.LiveObjects.Count); // actor + .x carrier + unknown object identity
        Assert.Equal(2, emm.Document.EffectSections.Count); Assert.Equal("Acs0", emm.Document.EffectSections[1].Owner!.ObjectKey);
        Assert.Equal(fixture.Tree.Files, tree.Files); Assert.Equal(fixture.Tree.Directories, tree.Directories);
        Assert.Equal(fixture.Tree.RequiredRelativeFiles, tree.RequiredRelativeFiles);
        Assert.Equal(fixture.Tree.CopyFileBytes("effect/main.fx"), tree.CopyFileBytes("effect/main.fx"));
        Assert.Equal(new byte[] { 8, 9 }, package.ReadResource("render-resources/future.bin"));
        AssertInvalid(captured!);
        package.Dispose(); Assert.Empty(Directory.GetDirectories(LoadCache));
        // Parsed state owns detached bytes and remains useful after the package owner is closed.
        Assert.Equal(fixture.Emm.Document.CopySourceBytes(), emm.Document.CopySourceBytes());
        Assert.Equal(fixture.Tree.CopyFileBytes("effect/main.fx"), tree.CopyFileBytes("effect/main.fx"));
    }

    [Fact]
    public void PreparedLoadFreezesTraditionalBytesOnceBeforeCallerHookRatherThanRereadingAfterHostLoad()
    {
        var fixture = SaveFixture(); string[] ownedResources = fixture.Options.AdditionalResources.Keys
            .Where(path => path.StartsWith("render-resources/traditional/", StringComparison.Ordinal)).ToArray();
        var prepared = LoadPrepared(fixture.Path, new()
        {
            PackagePreflight = (context, _) =>
            {
                string stage = Assert.Single(Directory.GetDirectories(LoadCache));
                foreach (string path in ownedResources)
                {
                    byte[] bytes = context.ReadResource(path); Assert.NotEmpty(bytes); bytes[0] ^= 255;
                    File.WriteAllBytes(Path.Combine(stage, path.Replace('/', Path.DirectorySeparatorChar)), bytes);
                }
            }
        }, new());
        using var package = Property<PmaLoadedProject>(prepared, "Package");
        Assert.Equal(fixture.Emm.Document.CopySourceBytes(), Property<D3D9EmmPmaState>(prepared, "Emm").Document.CopySourceBytes());
        Assert.Equal(fixture.Tree.CopyFileBytes("effect/main.fx"), Property<D3D9EffectResourceTree>(prepared, "Tree").CopyFileBytes("effect/main.fx"));
        Assert.Throws<InvalidDataException>(() => package.ReadResource(ownedResources[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerRejectionOrCancellationPreservesPrimaryFailureAndCleansBeforeHostCallbacks(bool cancel)
    {
        var fixture = SaveFixture(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int hooks = 0, models = 0; var physics = new CountingPhysics(); PmaPackageReadContext? captured = null;
        var primary = new InvalidDataException("caller rejected validated classic metadata");
        var options = new PmaLoadOptions
        {
            PackagePreflight = (context, _) =>
            { hooks++; captured = context; if (cancel) cancellation.Cancel(); else throw primary; }
        };
        var error = Record.Exception(() => LoadPrepared(fixture.Path, options, physics,
            (path, token) => { models++; return LoadModel(path, token); }, cancellation.Token));
        if (cancel) Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
        else Assert.Same(primary, error);
        Assert.Equal(1, hooks); Assert.Equal(0, models); Assert.Equal(0, physics.Calls);
        AssertInvalid(captured!); Assert.Empty(Directory.GetDirectories(LoadCache));
    }

    [Fact]
    public void PreCancelledPreparedLoadDoesNotInvokeCallerHookOrCreateStage()
    {
        var fixture = SaveFixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        int hooks = 0; var physics = new CountingPhysics();
        Assert.Throws<OperationCanceledException>(() => LoadPrepared(fixture.Path,
            new() { PackagePreflight = (_, _) => hooks++ }, physics, token: cancellation.Token));
        Assert.Equal(0, hooks); Assert.Equal(0, physics.Calls); Assert.False(Directory.Exists(LoadCache));
    }

    [Fact]
    public void HostFailureAfterSuccessfulTraditionalPreflightClosesContextAndCleansStage()
    {
        var fixture = SaveFixture(); PmaPackageReadContext? captured = null;
        var primary = new InvalidDataException("host model rejection"); var physics = new CountingPhysics();
        var error = Record.Exception(() => LoadPrepared(fixture.Path,
            new() { PackagePreflight = (context, _) => captured = context }, physics,
            (_, _) => { AssertInvalid(captured!); throw primary; }));
        Assert.Same(primary, Assert.IsType<SceneProjectAssetException>(error).InnerException); Assert.Equal(0, physics.Calls);
        AssertInvalid(captured!); Assert.Empty(Directory.GetDirectories(LoadCache));
    }

    [Fact]
    public void PreparedLoadWithoutTraditionalSchemasReturnsNullMetadataAndKeepsUnknownOpaqueData()
    {
        var fixture = SaveFixture(plain: true); var prepared = LoadPrepared(fixture.Path, new(), new());
        using var package = Property<PmaLoadedProject>(prepared, "Package");
        Assert.Null(Property<object?>(prepared, "Emm")); Assert.Null(Property<object?>(prepared, "Tree"));
        Assert.Equal(17, package.OptionalExtensions["org.example.future"].GetProperty("value").GetInt32());
        Assert.Equal(new byte[] { 8, 9 }, package.ReadResource("render-resources/future.bin"));
    }

    [Fact]
    public void PackageCodecEntrypointsShareValidationAndPreserveUniqueLegacyReadSignatures()
    {
        var fixture = SaveFixture(); using var package = PmcProjectStore.Load(fixture.Path,
            Path.Combine(directory, "codec-cache"), LoadModel, new CountingPhysics(), cancellationToken: Token);
        Assert.Equal(typeof(PmaLoadedProject), typeof(D3D9EmmPma).GetMethod("Read")!.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(PmaLoadedProject), typeof(D3D9EffectResourceTree).GetMethod("Read")!.GetParameters()[0].ParameterType);
        var counted = new CountedResources(package);
        var emm = (D3D9EmmPmaState)ReadPackage(typeof(D3D9EmmPma), counted)!;
        var tree = (D3D9EffectResourceTree)ReadPackage(typeof(D3D9EffectResourceTree), counted)!;
        Assert.Equal(D3D9EmmPma.Read(package)!.Document.CopySourceBytes(), emm.Document.CopySourceBytes());
        Assert.Equal(D3D9EffectResourceTree.Read(package, cancellationToken: Token)!.Files, tree.Files);
        Assert.All(counted.Reads.Values, count => Assert.Equal(1, count));
        Assert.Equal(fixture.Options.AdditionalResources.Keys.Count(path => path.StartsWith("render-resources/traditional/", StringComparison.Ordinal)), counted.Reads.Count);
    }

    private PmaLoadedProject Load(bool prepared, string path, PmaLoadOptions options, CountingPhysics physics,
        Func<string, CancellationToken, SceneAnimationModelAsset>? loader = null)
        => prepared ? Property<PmaLoadedProject>(LoadPrepared(path, options, physics, loader), "Package")
            : D3D9ClassicProjectStore.Load(path, LoadCache, loader ?? LoadModel, physics, options, Token);
    private object LoadPrepared(string path, PmaLoadOptions options, CountingPhysics physics,
        Func<string, CancellationToken, SceneAnimationModelAsset>? loader = null, CancellationToken? token = null)
    {
        var method = typeof(D3D9ClassicProjectStore).GetMethod("LoadPrepared"); Assert.NotNull(method);
        return Unwrap(() => method.Invoke(null, [path, LoadCache, loader ?? LoadModel, physics, options, token ?? Token]))!;
    }
    private static object? ReadPackage(Type type, IPmaPackageResources package)
    {
        var method = type.GetMethod("ReadPackage"); Assert.NotNull(method);
        Assert.Equal(typeof(IPmaPackageResources), method.GetParameters()[0].ParameterType);
        return Unwrap(() => method.Invoke(null, type == typeof(D3D9EmmPma) ? [package] : [package, null, Token]));
    }
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static object? Unwrap(Func<object?> callback)
    {
        try { return callback(); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void AssertInvalid(PmaPackageReadContext context)
        => Assert.Throws<ObjectDisposedException>(() => context.ReadResource("render-resources/future.bin"));

    private sealed record Fixture(string Path, D3D9EmmPmaState Emm, D3D9EffectResourceTree Tree, PmaSaveOptions Options);
    private Fixture SaveFixture(int codePage = 936, string? damage = null, bool plain = false)
    {
        using var project = Project(); Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text = "[Info]\r\nVersion=3\r\n[Object]\r\nPmd0=models\\actor.pmx\r\nAcs0=effects\\carrier.x\r\n[Effect]\r\nDefault=main_default\r\nPmd0=effects\\main.fx\r\nUnrecognized=保持\r\n[Effect@Layer]\r\nOwner=Acs0\r\nPmd0[0]=effects\\sub.fx\r\n";
        var document = EmmReader.Read(Encoding.GetEncoding(codePage).GetBytes(text), new() { AnsiCodePage = codePage });
        var plan = EmmImportPlan.Create(document, @"C:\original\project.emm",
            [new(project.Objects[0].Id, true, @"models\actor.pmx", 0), new(Guid.NewGuid(), false, @"effects\carrier.x", 1),
                new(Guid.NewGuid(), true, @"unknown\retained.pmx", 2)], @"C:\original");
        var emm = D3D9EmmPma.Capture(plan, [new(@"Z:\not-present\dependency.fx", new byte[] { 1, 3, 5 })]);
        string source = Path.Combine(directory, "tree"); Directory.CreateDirectory(Path.Combine(source, "effect"));
        Directory.CreateDirectory(Path.Combine(source, "shared")); Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "effect", "main.fx"), "#include \"../shared/common.fx\"\r\n");
        File.WriteAllBytes(Path.Combine(source, "shared", "common.fx"), [2, 4, 6]);
        var tree = D3D9EffectResourceTree.Capture(source, ["effect/main.fx"], cancellationToken: Token);
        var options = new PmaSaveOptions
        {
            CacheDirectory = Path.Combine(directory, "save-cache"),
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.future"] = JsonSerializer.SerializeToElement(new { value = 17 }) },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/future.bin"] = new byte[] { 8, 9 } }
        };
        options = D3D9ClassicProjectStore.CreateSaveOptions(options);
        if (!plain)
        {
            options = D3D9EmmPma.CreateSaveOptions(project, emm, options);
            options = D3D9EffectResourceTree.CreateSaveOptions(project, tree, options);
        }
        if (damage is not null)
        {
            string extension = damage.StartsWith("emm-", StringComparison.Ordinal) ? D3D9EmmPma.ExtensionName : D3D9EffectResourceTree.ExtensionName;
            var node = JsonNode.Parse(options.OptionalExtensions[extension].GetRawText())!.AsObject();
            var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value);
            switch (damage)
            {
                case "emm-schema": case "tree-schema": node["schemaVersion"] = 99; break;
                case "emm-sha": node["source"]!["sha256"] = new string('0', 64); break;
                case "emm-match": node["matches"]![0]!["objectId"] = Guid.NewGuid().ToString(); break;
                case "emm-missing": resources.Remove(node["assets"]![0]!["resource"]!["path"]!.GetValue<string>()); break;
                case "tree-traversal": node["files"]![0]!["relativePath"] = "../outside.fx"; break;
                case "tree-sha": node["files"]![0]!["sha256"] = new string('0', 64); break;
                case "tree-missing": resources.Remove(node["files"]![0]!["resourcePath"]!.GetValue<string>()); break;
                default: throw new ArgumentException("Unknown test damage.", nameof(damage));
            }
            var extensions = options.OptionalExtensions.ToDictionary(pair => pair.Key, pair => pair.Value);
            extensions[extension] = JsonSerializer.SerializeToElement(node);
            options = options with { OptionalExtensions = extensions, AdditionalResources = resources };
        }
        string path = Path.Combine(directory, "fixture.pmc"); PmcProjectStore.Save(path, project, options, Token);
        return new(path, emm, tree, options);
    }

    private SceneProjectSession Project()
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "generic fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor(); composition.Add("actor", source, ModelRole.Environment, Rest(model));
        return new(composition, new(), [], new CountingPhysics(), cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class CountingPhysics : IProjectPhysicsBackendProvider
    {
        public int Calls { get; private set; }
        public int PoseInitializationSubsteps { get { Calls++; return 0; } }
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor)
        { Calls++; throw new InvalidOperationException("Fixture should not construct a solver."); }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        { Calls++; throw new InvalidOperationException("Fixture should not construct a solver."); }
    }
    private sealed class CountedResources(IPmaPackageResources package) : IPmaPackageResources
    {
        public Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);
        public string Format => package.Format;
        public Guid ProjectId => package.ProjectId;
        public IReadOnlyDictionary<string, JsonElement> OptionalExtensions => package.OptionalExtensions;
        public IReadOnlyList<string> ResourcePaths => package.ResourcePaths;
        public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
        { Reads[path] = Reads.GetValueOrDefault(path) + 1; return package.ReadResource(path, maxBytes); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
