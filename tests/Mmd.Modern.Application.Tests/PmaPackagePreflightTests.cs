using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaPackagePreflightTests : IDisposable
{
    private const string Resource = "render-resources/unknown/opaque.bin";
    private const string Extension = "org.example.unknown";
    private readonly string directory = Path.Combine("G:/MDA-Work/compat/20261105/preflight435", "tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmaPackagePreflightTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreflightReadsSameOpaqueBytesAndIdentityOnceBeforeEveryHostAndPhysicsCallback(bool classic)
    {
        string package = SaveFixture(classic);
        var physics = new CountingPhysics();
        int callbacks = 0, models = 0, transforms = 0;
        object? captured = null;
        Guid projectId = default;
        var options = WithPreflight((context, token) =>
        {
            captured = context; callbacks++;
            Assert.Equal(Token, token);
            Assert.Equal(0, models); Assert.Equal(0, transforms); Assert.Equal(0, physics.Calls);
            Assert.Equal(Format(classic), Property<string>(context, "Format"));
            projectId = Property<Guid>(context, "ProjectId"); Assert.NotEqual(Guid.Empty, projectId);
            Assert.Equal(new[] { Resource }, Property<IReadOnlyList<string>>(context, "ResourcePaths"));
            var extensions = Property<IReadOnlyDictionary<string, JsonElement>>(context, "OptionalExtensions");
            Assert.Equal(91, extensions[Extension].GetProperty("future").GetInt32());
            byte[] bytes = Read(context, Resource); Assert.Equal(new byte[] { 4, 5, 6 }, bytes);
            bytes[0] = 99;
            Assert.Equal(new byte[] { 4, 5, 6 }, Read(context, Resource, 3));
        }) with { ModelAssetTransform = (_, asset) => { transforms++; return asset; } };

        using var loaded = Load(classic, package, options, physics, (path, token) =>
        {
            models++;
            Assert.Equal(1, callbacks);
            AssertInvalid(captured!);
            return LoadModel(path, token);
        });

        Assert.Equal(1, callbacks); Assert.Equal(1, models); Assert.Equal(1, transforms);
        Assert.True(physics.Calls > 0);
        Assert.Equal(projectId, loaded.ProjectId); Assert.Equal(Format(classic), loaded.Format);
        Assert.Equal(new byte[] { 4, 5, 6 }, loaded.ReadResource(Resource));
        AssertInvalid(captured!);
        var contract = typeof(PmaLoadedProject).Assembly.GetType("Mmd.Modern.Application.IPmaPackageResources");
        Assert.NotNull(contract); Assert.True(contract.IsInterface); Assert.True(contract.IsPublic);
        Assert.True(contract.IsInstanceOfType(loaded)); Assert.True(contract.IsInstanceOfType(captured));
        Assert.Equal(new[] { "Format", "OptionalExtensions", "ProjectId", "ResourcePaths" },
            contract.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextHasOnlyReadOnlyDetachedMetadataAndNoSessionOrStagingPath(bool classic)
    {
        string package = SaveFixture(classic);
        IReadOnlyDictionary<string, JsonElement>? savedExtensions = null;
        IReadOnlyList<string>? savedPaths = null;
        var options = WithPreflight((context, _) =>
        {
            Type type = context.GetType(); Assert.True(type.IsPublic); Assert.True(type.IsSealed);
            Assert.Empty(type.GetConstructors()); Assert.False(context is IDisposable);
            Assert.Null(type.GetProperty("Session")); Assert.Null(type.GetProperty("CacheDirectory"));
            Assert.Null(type.GetProperty("Directory")); Assert.Null(type.GetProperty("Models"));
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
            savedExtensions = Property<IReadOnlyDictionary<string, JsonElement>>(context, "OptionalExtensions");
            savedPaths = Property<IReadOnlyList<string>>(context, "ResourcePaths");
            var dictionary = Assert.IsAssignableFrom<IDictionary<string, JsonElement>>(savedExtensions);
            Assert.True(dictionary.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => dictionary.Remove(Extension));
            var paths = Assert.IsAssignableFrom<IList<string>>(savedPaths); Assert.True(paths.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => paths[0] = "render-resources/replaced.bin");
        });
        using var loaded = Load(classic, package, options, new());
        Assert.NotNull(savedExtensions); Assert.NotNull(savedPaths);
        Assert.Equal(91, savedExtensions[Extension].GetProperty("future").GetInt32());
        Assert.Equal(Resource, Assert.Single(savedPaths));
        Assert.False(ReferenceEquals(savedExtensions, loaded.OptionalExtensions));
        Assert.True(JsonElement.DeepEquals(savedExtensions[Extension], loaded.OptionalExtensions[Extension]));
        Assert.Equal(new byte[] { 4, 5, 6 }, loaded.ReadResource(Resource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreflightResourceReadsEnforceBudgetAndCanonicalNames(bool classic)
    {
        string package = SaveFixture(classic);
        using var loaded = Load(classic, package, WithPreflight((context, _) =>
        {
            Assert.Throws<InvalidDataException>(() => Read(context, Resource, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => Read(context, Resource, -1));
            Assert.Throws<FileNotFoundException>(() => Read(context, "render-resources/missing.bin"));
            Assert.Throws<InvalidDataException>(() => Read(context, "project.mmdscene"));
            Assert.Throws<InvalidDataException>(() => Read(context, "render-resources/../escape.bin"));
            Assert.Equal(new byte[] { 4, 5, 6 }, Read(context, Resource, 3));
        }), new());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EveryPreflightReadRevalidatesExtractedLengthAndSha(bool classic, bool changeLength)
    {
        string package = SaveFixture(classic);
        int models = 0; var physics = new CountingPhysics();
        var options = WithPreflight((context, _) =>
        {
            Assert.Equal(new byte[] { 4, 5, 6 }, Read(context, Resource));
            string stage = Assert.Single(Directory.GetDirectories(LoadCache));
            File.WriteAllBytes(Path.Combine(stage, Resource.Replace('/', Path.DirectorySeparatorChar)),
                changeLength ? new byte[] { 4, 5 } : new byte[] { 6, 5, 4 });
            Read(context, Resource);
        });
        Assert.Throws<InvalidDataException>(() => Load(classic, package, options, physics,
            (path, token) => { models++; return LoadModel(path, token); }));
        Assert.Equal(0, models); Assert.Equal(0, physics.Calls);
        Assert.Empty(Directory.GetDirectories(LoadCache));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RejectedOrCancelledPreflightInvalidatesContextAndCleansBeforeAnyModelOrPhysics(bool classic, bool cancel)
    {
        string package = SaveFixture(classic);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int models = 0, callbacks = 0; var physics = new CountingPhysics(); object? captured = null;
        var primary = new InvalidDataException("consumer rejected opaque metadata");
        var options = WithPreflight((context, _) =>
        {
            callbacks++; captured = context;
            if (cancel) cancellation.Cancel(); else throw primary;
        });
        var error = Record.Exception(() => Load(classic, package, options, physics,
            (path, token) => { models++; return LoadModel(path, token); }, cancellation.Token));
        if (cancel) Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
        else Assert.Same(primary, error);
        Assert.Equal(1, callbacks); Assert.Equal(0, models); Assert.Equal(0, physics.Calls);
        AssertInvalid(captured!); Assert.Empty(Directory.GetDirectories(LoadCache));
        // A rejected context never owns the reader of a later successful load.
        using var reopened = Load(classic, package, WithPreflight((context, _) => Read(context, Resource)), new());
        Assert.Equal(new byte[] { 4, 5, 6 }, reopened.ReadResource(Resource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyCancelledLoadDoesNotInvokePreflightOrCreateItsStage(bool classic)
    {
        string package = SaveFixture(classic); int callbacks = 0;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var options = WithPreflight((_, _) => callbacks++);
        Assert.Throws<OperationCanceledException>(() => Load(classic, package, options, new(), token: cancellation.Token));
        Assert.Equal(0, callbacks); Assert.False(Directory.Exists(LoadCache));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PrimaryPreflightOrHostFailureSurvivesASeparateStageCleanupFailure(bool classic, bool hostFailure)
    {
        string package = SaveFixture(classic); object? captured = null; int models = 0;
        var primary = new InvalidDataException("first load failure"); var physics = new CountingPhysics();
        FileStream? fileLock = null;
        void Fail()
        {
            string stage = Assert.Single(Directory.GetDirectories(LoadCache));
            fileLock = new(Path.Combine(stage, Resource.Replace('/', Path.DirectorySeparatorChar)),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            throw primary;
        }
        var options = WithPreflight((context, _) => { captured = context; if (!hostFailure) Fail(); });
        try
        {
            var error = Record.Exception(() => Load(classic, package, options, physics,
                (_, _) => { models++; AssertInvalid(captured!); Fail(); throw new InvalidOperationException(); }));
            // Model preparation adds the established object/path diagnostic wrapper;
            // preflight failures propagate directly. Cleanup must preserve either primary.
            if (hostFailure) Assert.Same(primary, Assert.IsType<SceneProjectAssetException>(error).InnerException);
            else Assert.Same(primary, error);
            Assert.Contains(error!.Data.Values.Cast<object>(), value => value is IOException);
            Assert.Equal(hostFailure ? 1 : 0, models); Assert.Equal(0, physics.Calls);
            AssertInvalid(captured!); Assert.Single(Directory.GetDirectories(LoadCache));
        }
        finally { fileLock?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceAndContentValidationRejectBeforePreflight(bool classic)
    {
        string package = SaveFixture(classic); int callbacks = 0;
        var options = WithPreflight((_, _) => callbacks++) with
        { ContentSelection = new PmaContentSelection([Extension]) };
        Assert.Throws<InvalidDataException>(() => Load(classic, package, options, new()));
        Assert.Equal(0, callbacks); Assert.Empty(Directory.GetDirectories(LoadCache));
    }

    private static PmaLoadOptions WithPreflight(Action<object, CancellationToken> callback)
    {
        var property = typeof(PmaLoadOptions).GetProperty("PackagePreflight", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        var signature = property.PropertyType.GenericTypeArguments;
        Assert.Equal(typeof(Action<,>), property.PropertyType.GetGenericTypeDefinition());
        Assert.Equal("Mmd.Modern.Application.PmaPackageReadContext", signature[0].FullName);
        Assert.Equal(typeof(CancellationToken), signature[1]);
        var context = Expression.Parameter(signature[0], "context");
        var token = Expression.Parameter(typeof(CancellationToken), "token");
        var action = Expression.Lambda(property.PropertyType,
            Expression.Invoke(Expression.Constant(callback), Expression.Convert(context, typeof(object)), token), context, token).Compile();
        var options = new PmaLoadOptions(); property.SetValue(options, action); return options;
    }
    private static T Property<T>(object context, string name)
        => (T)Unwrap(() => context.GetType().GetProperty(name)!.GetValue(context))!;
    private static byte[] Read(object context, string path, long maxBytes = long.MaxValue)
        => (byte[])Unwrap(() => context.GetType().GetMethod("ReadResource")!.Invoke(context, [path, maxBytes]))!;
    private static object? Unwrap(Func<object?> action)
    {
        try { return action(); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void AssertInvalid(object context)
    {
        Assert.Throws<ObjectDisposedException>(() => Read(context, Resource));
        Assert.Throws<ObjectDisposedException>(() => Property<string>(context, "Format"));
        Assert.Throws<ObjectDisposedException>(() => Property<Guid>(context, "ProjectId"));
        Assert.Throws<ObjectDisposedException>(() => Property<IReadOnlyList<string>>(context, "ResourcePaths"));
        Assert.Throws<ObjectDisposedException>(() => Property<IReadOnlyDictionary<string, JsonElement>>(context, "OptionalExtensions"));
    }
    private string LoadCache => Path.Combine(directory, "load-cache");
    private string SaveFixture(bool classic)
    {
        using var project = Fixture();
        string package = Path.Combine(directory, "source" + (classic ? ".pmc" : ".pma"));
        using var opaque = JsonDocument.Parse("{\"future\":91,\"opaque\":[1,{\"keep\":true}]}");
        var options = new PmaSaveOptions
        {
            CacheDirectory = Path.Combine(directory, "save-cache"),
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [Resource] = new byte[] { 4, 5, 6 } },
            OptionalExtensions = new Dictionary<string, JsonElement> { [Extension] = opaque.RootElement },
        };
        if (classic) PmcProjectStore.Save(package, project, options, Token); else PmaProjectStore.Save(package, project, options, Token);
        return package;
    }
    private PmaLoadedProject Load(bool classic, string path, PmaLoadOptions options, CountingPhysics physics,
        Func<string, CancellationToken, SceneAnimationModelAsset>? loader = null, CancellationToken? token = null)
        => classic ? PmcProjectStore.Load(path, LoadCache, loader ?? LoadModel, physics, options, token ?? Token)
            : PmaProjectStore.Load(path, LoadCache, loader ?? LoadModel, physics, options, token ?? Token);
    private static string Format(bool classic) => classic ? PmcProjectStore.Format : PmaProjectStore.Format;
    private SceneProjectSession Fixture()
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        var model = new PmxModel
        {
            Header = template.Header, Name = "actor", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.Zero, EdgeSize = 1,
                DrawMode = PmxDrawModeFlags.None, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor(); var actor = composition.Add("actor", source, ModelRole.Character, Rest(model));
        var motion = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }] });
        return new(composition, new(), [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)],
            new CountingPhysics(), cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private sealed class CountingPhysics : IProjectPhysicsBackendProvider
    {
        public int Calls { get; private set; }
        public int PoseInitializationSubsteps { get { Calls++; return 0; } }
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor)
        { Calls++; throw new InvalidOperationException("Fixture should not construct a solver."); }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        { Calls++; throw new InvalidOperationException("Fixture should not construct a solver."); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
