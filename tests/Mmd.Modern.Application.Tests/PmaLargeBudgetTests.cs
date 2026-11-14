using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaLargeBudgetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pma-large-budget-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmaLargeBudgetTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void PortableSpoolHonorsCallerDirectoryAndRemovesOnlyOwnedFiles()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(new PmaSaveOptions().CacheDirectory),
            StringComparison.OrdinalIgnoreCase);
        string unrelated = Path.Combine(directory, "keep.bin"); File.WriteAllBytes(unrelated, [19]);
        var captureType = typeof(PmaProjectStore).Assembly.GetType("Mmd.Modern.Application.PortableModelAssets+Capture", true)!;
        string spool;
        using (var capture = (IDisposable)Activator.CreateInstance(captureType, directory)!)
        {
            spool = (string)captureType.GetMethod("ModelTemporary", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture, null)!;
            Assert.StartsWith(directory + Path.DirectorySeparatorChar, spool, StringComparison.OrdinalIgnoreCase);
            File.WriteAllBytes(spool, [7]); Assert.True(File.Exists(spool));
        }
        Assert.False(File.Exists(spool));
        Assert.Equal(new byte[] { 19 }, File.ReadAllBytes(unrelated));
    }

    [Fact]
    public void DefaultsAndExplicitBudgetsUseLongResourceLengths()
    {
        var defaults = new PmaLoadOptions();
        Assert.Equal(50L << 30, defaults.MaxPackageBytes);
        Assert.Equal(50L << 30, defaults.MaxExpandedBytes);
        Assert.Equal(10L << 30, defaults.MaxEntryBytes);
        using var project = Fixture();
        var lengths = new Dictionary<string, long> { ["render-resources/large.bin"] = 10L << 30 };
        PmaProjectStore.ValidateAdditionalResourceBudget(project, lengths);
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.ValidateAdditionalResourceBudget(project,
            lengths, defaults with { MaxEntryBytes = (10L << 30) - 1 }));
        lengths["render-resources/large.bin"] = 11L << 30;
        PmaProjectStore.ValidateAdditionalResourceBudget(project, lengths, defaults with { MaxEntryBytes = 12L << 30 });
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.ValidateAdditionalResourceBudget(project,
            lengths, defaults with { MaxEntryBytes = 12L << 30, MaxExpandedBytes = 10L << 30 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPortableBudgetRejectsBeforePublishingAndCanBeRaised(bool expanded)
    {
        using var project = Fixture();
        string path = Path.Combine(directory, "limited.pma"), cache = Path.Combine(directory, "stage");
        var options = new PmaSaveOptions { CacheDirectory = cache,
            ValidationOptions = expanded ? new() { MaxExpandedBytes = 1 } : new() { MaxEntryBytes = 1 } };
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(path, project, options, Token));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetDirectories(cache));
        PmaProjectStore.Save(path, project, options with { ValidationOptions = new() { MaxEntryBytes = 1024 * 1024 } }, Token);
        using var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "load"), LoadModel, new NoPhysics(),
            new() { MaxEntryBytes = 1024 * 1024 }, Token);
        Assert.Single(loaded.Session.Objects);
    }

    [Fact]
    public void ByteArrayReaderRejectsVirtualResourceAboveArrayMaximumBeforeOpeningFile()
    {
        var assembly = typeof(PmaProjectStore).Assembly;
        var resourceType = assembly.GetType("Mmd.Modern.Application.PmaAdditionalResource", throwOnError: true)!;
        var readerType = assembly.GetType("Mmd.Modern.Application.PmaResourceReader", throwOnError: true)!;
        var resource = Activator.CreateInstance(resourceType, "render-resources/large.bin", (long)Array.MaxLength + 1, new string('0', 64));
        var resources = Array.CreateInstance(resourceType, 1); resources.SetValue(resource, 0);
        using var reader = (IDisposable)Activator.CreateInstance(readerType, directory, resources)!;
        var exception = Assert.Throws<TargetInvocationException>(() => readerType.GetMethod("ReadResource")!
            .Invoke(reader, ["render-resources/large.bin", long.MaxValue]));
        Assert.Contains("in-memory", Assert.IsType<InvalidDataException>(exception.InnerException).Message);
    }

    [Fact]
    public void PmaStreamsTextureAboveLegacyFileAndBundleLimitsThroughMoveAndResave()
    {
        using var project = Fixture();
        const long length = 512L * 1024 * 1024 + 1;
        string texture = Path.Combine(directory, "texture.png");
        using (var stream = new FileStream(texture, FileMode.Open, FileAccess.Write))
        { stream.SetLength(length); stream.Position = length - 1; stream.WriteByte(71); }
        string package = Path.Combine(directory, "large.pma"), moved = Path.Combine(directory, "moved.pma");
        // Ordinary independent project saves retain their previous conservative limits.
        Assert.Throws<InvalidDataException>(() => SceneProjectStore.Save(Path.Combine(directory, "ordinary.mmdscene"),
            Composition(project.Objects[0].SourcePath), new(), new(), Token));
        PmaProjectStore.Save(package, project, new() { CacheDirectory = Path.Combine(directory, "stage") }, Token);
        // A second logical name shares the large blob without allocating another byte array.
        // Its default resave must use the retained-resource streaming path as well.
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!; JsonNode manifest;
            using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!;
            var file = manifest["files"]!.AsArray().First(f => f!["length"]!.GetValue<long>() == length)!.DeepClone();
            file["path"] = "render-resources/large.bin"; manifest["files"]!.AsArray().Add(file);
            entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(manifest.ToJsonString());
        }
        File.Move(package, moved); File.Delete(texture); File.Delete(project.Objects[0].SourcePath);
        using var loaded = PmaProjectStore.Load(moved, Path.Combine(directory, "load"), LoadModel, new NoPhysics(), cancellationToken: Token);
        AssertTexture(loaded);
        PmaProjectStore.Save(package, loaded.Session, new() { CacheDirectory = Path.Combine(directory, "restage") }, Token);
        using var reopened = PmaProjectStore.Load(package, Path.Combine(directory, "reload"), LoadModel, new NoPhysics(), cancellationToken: Token);
        AssertTexture(reopened);
        void AssertTexture(PmaLoadedProject owner)
        {
            Assert.Contains("render-resources/large.bin", owner.ResourcePaths);
            string modelPath = owner.Session.Objects[0].SourcePath;
            var model = PmxReader.Read(modelPath);
            string path = Path.Combine(Path.GetDirectoryName(modelPath)!, Assert.Single(model.Textures));
            Assert.Equal(length, new FileInfo(path).Length);
            using var input = File.OpenRead(path); input.Position = length - 1; Assert.Equal(71, input.ReadByte());
        }
    }

    private SceneProjectSession Fixture()
    {
        var m = PmmMotionAdapterTests.Pmx(["root"]);
        var model = new PmxModel { Header = m.Header, Name = m.Name, EnglishName = m.EnglishName, Comment = m.Comment,
            EnglishComment = m.EnglishComment,
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = ["texture.png"], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.Zero, EdgeSize = 1,
                DrawMode = PmxDrawModeFlags.None, TextureIndex = 0, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = m.Bones, Morphs = m.Morphs, DisplayFrames = m.DisplayFrames,
            RigidBodies = m.RigidBodies, Joints = m.Joints, SoftBodies = m.SoftBodies };
        string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        File.WriteAllBytes(Path.Combine(directory, "texture.png"), [1, 2, 3]);
        return new(Composition(path), new(), [], new NoPhysics(), cancellationToken: Token);
    }
    private static SceneCompositionEditor Composition(string path)
    { var result = new SceneCompositionEditor(); result.Add("actor", path, ModelRole.Character, Rest(PmxReader.Read(path))); return result; }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
