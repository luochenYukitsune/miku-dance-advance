using System.Collections;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaAdditionalResourcesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pma-resources-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmaAdditionalResourcesTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ResourcesMoveWithPackageUseExistingBlobDedupAndReturnDetachedCopies()
    {
        using var project = Fixture();
        string path = Package("original");
        byte[] bytes = [13, 27, 91, 4];
        PmaProjectStore.Save(path, project, Options(("render-resources/actor/map.json", bytes), ("render-resources/actor/copy.bin", bytes)), Token);
        using (var archive = ZipFile.OpenRead(path))
        using (var input = archive.GetEntry("manifest.json")!.Open())
        using (var manifest = JsonDocument.Parse(input))
        {
            var files = manifest.RootElement.GetProperty("files").EnumerateArray()
                .Where(f => f.GetProperty("path").GetString()!.StartsWith("render-resources/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, files.Length);
            string hash = files[0].GetProperty("sha256").GetString()!;
            Assert.Equal(hash, files[1].GetProperty("sha256").GetString());
            Assert.Single(archive.Entries, e => e.FullName == "blobs/" + hash);
        }
        string moved = Package("moved"); File.Move(path, moved);
        File.Delete(project.Objects[0].SourcePath);
        using var loaded = Load(moved);
        Assert.Equal(new[] { "render-resources/actor/copy.bin", "render-resources/actor/map.json" }, loaded.ResourcePaths);
        byte[] copy = loaded.ReadResource("render-resources/actor/map.json"); copy[0] = 255;
        Assert.Equal(bytes, loaded.ReadResource("render-resources/actor/map.json"));
        Assert.Throws<FileNotFoundException>(() => loaded.ReadResource("render-resources/missing.png"));
        Assert.Throws<InvalidDataException>(() => loaded.ReadResource("project.mmdscene"));
    }

    [Fact]
    public void DefaultResavePreservesLoadedResourcesAndSaveOnlyOverrideDoesNotChangeLiveState()
    {
        using var project = Fixture();
        string original = Package("initial"), changed = Package("changed"), restored = Package("restored");
        PmaProjectStore.Save(original, project, Options(("render-resources/a.bin", [1, 2]), ("render-resources/b.bin", [3])), Token);
        using var loaded = Load(original);
        PmaProjectStore.Save(changed, loaded.Session, Options(("render-resources/a.bin", [9])), Token);
        Assert.Equal(new byte[] { 1, 2 }, loaded.ReadResource("render-resources/a.bin"));
        PmaProjectStore.Save(restored, loaded.Session, cancellationToken: Token);
        using var changedLoad = Load(changed); using var restoredLoad = Load(restored);
        Assert.Equal(new byte[] { 9 }, changedLoad.ReadResource("render-resources/a.bin"));
        Assert.Equal(new byte[] { 3 }, changedLoad.ReadResource("render-resources/b.bin"));
        Assert.Equal(new byte[] { 1, 2 }, restoredLoad.ReadResource("render-resources/a.bin"));
        Assert.Equal(new byte[] { 3 }, restoredLoad.ReadResource("render-resources/b.bin"));
    }

    [Fact]
    public void RejectedDisposalDuringEditPreservesLoadedResourcesAndOwnerLifetime()
    {
        using var project = Fixture();
        string path = Package("dispose-transaction");
        PmaProjectStore.Save(path, project, Options(("render-resources/data.bin", [4, 5, 6])), Token);
        using var loaded = Load(path);
        var frame = loaded.Session.Frame;
        Assert.Throws<InvalidOperationException>(() => loaded.Session.Edit(_ => loaded.Dispose(), Token));
        Assert.Same(frame, loaded.Session.Frame);
        Assert.False(loaded.Session.CanUndo);
        Assert.True(Directory.Exists(loaded.CacheDirectory));
        Assert.Equal(new byte[] { 4, 5, 6 }, loaded.ReadResource("render-resources/data.bin"));
        PmaProjectStore.Save(Package("after-rejected-disposal"), loaded.Session, cancellationToken: Token);
        loaded.Dispose();
        Assert.False(Directory.Exists(loaded.CacheDirectory));
        Assert.Throws<ObjectDisposedException>(() => loaded.ReadResource("render-resources/data.bin"));
    }

    [Fact]
    public void PackageDisposalStillCleansCacheAfterSessionWasDisposedSeparately()
    {
        using var project = Fixture(); string path = Package("separate-session-dispose");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        using var loaded = Load(path);
        loaded.Session.Dispose(); loaded.Dispose(); loaded.Dispose();
        Assert.False(Directory.Exists(loaded.CacheDirectory));
    }

    [Fact]
    public void SuppliedResourcesAreSnapshottedBeforeOtherSaveInputsAreEnumerated()
    {
        using var project = Fixture(); byte[] supplied = [4, 5, 6];
        var options = Options(("render-resources/data.bin", supplied)) with
        { OptionalExtensions = new MutatingExtensions(() => supplied[0] = 99) };
        string path = Package("snapshot"); PmaProjectStore.Save(path, project, options, Token);
        Assert.Equal(99, supplied[0]);
        using var loaded = Load(path);
        Assert.Equal(new byte[] { 4, 5, 6 }, loaded.ReadResource("render-resources/data.bin"));
        string plain = Package("not-sticky"); PmaProjectStore.Save(plain, project, cancellationToken: Token);
        using var plainLoad = Load(plain); Assert.Empty(plainLoad.ResourcePaths);
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("project.mmdscene")]
    [InlineData("sources/actor.pmx")]
    [InlineData("render-resources/")]
    [InlineData("render-resources/../escape.bin")]
    [InlineData("render-resources/dir//data.bin")]
    [InlineData("render-resources/dir\\data.bin")]
    [InlineData("render-resources/CON.png")]
    [InlineData("render-resources/dir/AuX.json")]
    [InlineData("render-resources/COM1/data.bin")]
    [InlineData("render-resources/name.")]
    [InlineData("render-resources/x:y")]
    [InlineData("RENDER-RESOURCES/data.bin")]
    public void UnsafeOrForeignPathsRejectBeforeCreatingStagingDirectory(string logical)
    {
        using var project = Fixture(); string path = Package("safe");
        PmaProjectStore.Save(path, project, cancellationToken: Token); byte[] before = File.ReadAllBytes(path);
        string cache = Path.Combine(directory, "never-created");
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(path, project,
            Options((logical, [7])) with { CacheDirectory = cache }, Token));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void CaseAliasPathsRejectAndCannotOverwriteEachOther()
    {
        using var project = Fixture();
        var options = Options(("render-resources/A.bin", [1]), ("render-resources/a.bin", [2]));
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(Package("alias"), project, options, Token));
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("expanded")]
    [InlineData("count")]
    public void AdditionalResourcesAreBudgetedBeforeStagingAndPreserveDestination(string kind)
    {
        using var project = Fixture(); string path = Package("budget");
        PmaProjectStore.Save(path, project, cancellationToken: Token); byte[] before = File.ReadAllBytes(path);
        var limits = kind switch
        {
            "entry" => new PmaLoadOptions { MaxEntryBytes = 2 },
            "expanded" => new PmaLoadOptions { MaxExpandedBytes = 5 },
            _ => new PmaLoadOptions { MaxFiles = 1 },
        };
        string cache = Path.Combine(directory, "budget-cache");
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(path, project,
            Options(("render-resources/a.bin", [1, 2, 3]), ("render-resources/b.bin", [4, 5, 6]))
                with { CacheDirectory = cache, ValidationOptions = limits }, Token));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void RetainedResourcesAlsoParticipateInEarlyBudgetChecks()
    {
        using var project = Fixture(); string path = Package("retained-budget");
        PmaProjectStore.Save(path, project, Options(("render-resources/a.bin", [1, 2, 3])), Token);
        using var loaded = Load(path); string cache = Path.Combine(directory, "retained-budget-cache");
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(Package("too-small"), loaded.Session,
            new() { CacheDirectory = cache, ValidationOptions = new() { MaxEntryBytes = 2 } }, Token));
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void TamperedRetainedResourcesRejectReadsAndDefaultSaveAtomically()
    {
        using var project = Fixture(); string path = Package("tamper");
        PmaProjectStore.Save(path, project, Options(("render-resources/data.bin", [1, 2, 3])), Token);
        byte[] before = File.ReadAllBytes(path);
        using var loaded = Load(path);
        File.WriteAllBytes(Path.Combine(loaded.CacheDirectory, "render-resources", "data.bin"), [3, 2, 1]);
        Assert.Throws<InvalidDataException>(() => loaded.ReadResource("render-resources/data.bin"));
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void DisposedOwnerRejectsReadsAndRemovesItsExtractedResources()
    {
        using var project = Fixture(); string path = Package("lifetime");
        PmaProjectStore.Save(path, project, Options(("render-resources/data.bin", [])), Token);
        var loaded = Load(path); string cache = loaded.CacheDirectory;
        Assert.Empty(loaded.ReadResource("render-resources/data.bin")); loaded.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.ReadResource("render-resources/data.bin"));
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void ResourceReadBudgetRejectsBeforeReturningBytesAndExactLimitWorks()
    {
        using var project = Fixture(); string path = Package("read-budget");
        PmaProjectStore.Save(path, project, Options(("render-resources/data.bin", [1, 2, 3])), Token);
        PmaModelResourceContext? context = null;
        using var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "read-budget-cache"), LoadAsset, new NoPhysics(), new()
        { ModelAssetTransform = (current, asset) => { context = current; return asset; } }, Token);
        Assert.Throws<InvalidDataException>(() => loaded.ReadResource("render-resources/data.bin", 2));
        Assert.NotNull(context);
        Assert.Throws<InvalidDataException>(() => context.ReadResource("render-resources/data.bin", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => loaded.ReadResource("render-resources/data.bin", -1));
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.ReadResource("render-resources/data.bin", 3));
        Assert.Equal(new byte[] { 1, 2, 3 }, context.ReadResource("render-resources/data.bin", 3));
    }

    [Fact]
    public void CancelledResourceSaveLeavesExistingPackageUnchanged()
    {
        using var project = Fixture(); string path = Package("cancel");
        PmaProjectStore.Save(path, project, cancellationToken: Token); byte[] before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PmaProjectStore.Save(path, project,
            Options(("render-resources/data.bin", [1, 2, 3])), cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void LegacyPackageWithNoResourcesRemainsResourceFreeOnResave()
    {
        using var project = Fixture(); string path = Package("legacy");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        using (var loaded = Load(path))
        { Assert.Empty(loaded.ResourcePaths); PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token); }
        using var reopened = Load(path); Assert.Empty(reopened.ResourcePaths);
    }

    [Fact]
    public void ResourceDecoratorGetsExplicitInstanceIdsForTwoActorsSharingOnePackagedPmx()
    {
        using var project = Fixture(secondInstance: true); string path = Package("two-instances");
        using var extension = JsonDocument.Parse("{\"version\":1}");
        PmaProjectStore.Save(path, project, Options(("render-resources/data.bin", [7, 8])) with
        { OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.render"] = extension.RootElement } }, Token);
        var visited = new List<Guid>(); var contexts = new List<PmaModelResourceContext>(); int hostCalls = 0;
        var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "decorator-cache"), (p, t) =>
        { hostCalls++; return LoadAsset(p, t); }, new NoPhysics(), new()
        {
            ModelAssetTransform = (context, asset) =>
            {
                Assert.Equal(hostCalls, visited.Count + 1);
                visited.Add(context.ObjectId); contexts.Add(context);
                Assert.Equal(new byte[] { 7, 8 }, context.ReadResource("render-resources/data.bin"));
                Assert.Equal(1, context.OptionalExtensions["org.example.render"].GetProperty("version").GetInt32());
                return new(asset.Model, asset.Source);
            },
        }, Token);
        Assert.Equal(project.Objects.Select(o => o.Id), visited);
        Assert.Equal(2, visited.Distinct().Count()); Assert.Equal(2, hostCalls);
        Assert.Equal(loaded.Models[0].EditedModelPath, loaded.Models[1].EditedModelPath);
        loaded.Dispose();
        Assert.All(contexts, context => Assert.Throws<ObjectDisposedException>(() => context.ReadResource("render-resources/data.bin")));
    }

    [Fact]
    public void ResourceDecoratorWorksForOldPackagesWithoutAdditionalResources()
    {
        using var project = Fixture(); string path = Package("old-decorated");
        PmaProjectStore.Save(path, project, cancellationToken: Token); int calls = 0;
        using var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "old-decorator-cache"), LoadAsset, new NoPhysics(), new()
        {
            ModelAssetTransform = (context, asset) =>
            { calls++; Assert.Equal(project.Objects[0].Id, context.ObjectId); Assert.Empty(context.ResourcePaths); return asset; },
        }, Token);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FailedDecoratorCleansResourcesAndInvalidatesCapturedContext()
    {
        using var project = Fixture(); string path = Package("decorator-failure");
        PmaProjectStore.Save(path, project, Options(("render-resources/data.bin", [2])), Token);
        PmaModelResourceContext? captured = null; string cache = Path.Combine(directory, "failure-cache");
        Assert.Throws<SceneProjectAssetException>(() => PmaProjectStore.Load(path, cache, LoadAsset, new NoPhysics(), new()
        {
            ModelAssetTransform = (context, _) =>
            { captured = context; Assert.Equal(new byte[] { 2 }, context.ReadResource("render-resources/data.bin")); throw new InvalidDataException("Invalid renderer mapping."); },
        }, Token));
        Assert.NotNull(captured); Assert.Throws<ObjectDisposedException>(() => captured.ReadResource("render-resources/data.bin"));
        Assert.Empty(Directory.GetDirectories(cache));
    }

    [Fact]
    public void ResourceDecoratorCannotReplaceHostPmxModel()
    {
        using var project = Fixture(); string path = Package("different-model");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        Assert.Throws<SceneProjectAssetException>(() => PmaProjectStore.Load(path, Path.Combine(directory, "different-cache"), LoadAsset, new NoPhysics(), new()
        { ModelAssetTransform = (_, asset) => new(PmmMotionAdapterTests.Pmx(["other"]), asset.Source) }, Token));
    }

    [Fact]
    public void CorruptAdditionalBlobRejectsBeforeModelLoaderOrDecorator()
    {
        using var project = Fixture(); string path = Package("corrupt-blob");
        PmaProjectStore.Save(path, project, Options(("render-resources/data.bin", [7, 8, 9])), Token);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            string hash;
            using (var input = archive.GetEntry("manifest.json")!.Open())
            using (var manifest = JsonDocument.Parse(input))
                hash = manifest.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "render-resources/data.bin")
                    .GetProperty("sha256").GetString()!;
            archive.GetEntry("blobs/" + hash)!.Delete();
            using var output = archive.CreateEntry("blobs/" + hash).Open(); output.Write(new byte[] { 9, 8, 7 });
        }
        int hostCalls = 0, decoratorCalls = 0;
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Load(path, Path.Combine(directory, "invalid-cache"), (p, t) =>
        { hostCalls++; return LoadAsset(p, t); }, new NoPhysics(), new() { ModelAssetTransform = (_, asset) => { decoratorCalls++; return asset; } }, Token));
        Assert.Equal(0, hostCalls); Assert.Equal(0, decoratorCalls);
        Assert.False(Directory.Exists(Path.Combine(directory, "invalid-cache")));
    }

    private string Package(string name) => Path.Combine(directory, name + ".pma");
    private PmaLoadedProject Load(string path) => PmaProjectStore.Load(path, Path.Combine(directory, "load-cache"),
        LoadAsset,
        new NoPhysics(), cancellationToken: Token);
    private static SceneAnimationModelAsset LoadAsset(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private PmaSaveOptions Options(params (string Path, byte[] Bytes)[] entries) => new()
    {
        CacheDirectory = Path.Combine(directory, "save-cache"),
        AdditionalResources = entries.ToDictionary(p => p.Path, p => new ReadOnlyMemory<byte>(p.Bytes), StringComparer.Ordinal),
    };
    private SceneProjectSession Fixture(bool secondInstance = false)
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        var model = new PmxModel
        {
            Header = template.Header, Name = "actor", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.Zero, EdgeSize = 1, DrawMode = PmxDrawModeFlags.None,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor(); composition.Add("actor", source, ModelRole.Character, Rest(model));
        if (secondInstance) composition.Add("second", source, ModelRole.Character, Rest(model));
        return new(composition, new(), [], new NoPhysics(), cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
    private sealed class MutatingExtensions(Action onEnumeration) : IReadOnlyDictionary<string, JsonElement>
    {
        public int Count => 0;
        public IEnumerable<string> Keys => [];
        public IEnumerable<JsonElement> Values => [];
        public JsonElement this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out JsonElement value) { value = default; return false; }
        public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator()
        { onEnumeration(); return Enumerable.Empty<KeyValuePair<string, JsonElement>>().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
