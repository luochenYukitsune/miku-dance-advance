using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed partial class EditorClassicToonPackageTests
{
    [Fact]
    public void UsedOriginalImagesFreezeBeforeHostLoadAndSurviveMovedOriginalsAndDetachedResave()
    {
        string directory = Path.Combine(MdaWorkRoot.Path("compat/20261110/toon-package598"), "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = Model();
            string sourcePath = Path.Combine(directory, "original.pmx");
            File.WriteAllBytes(sourcePath, PmxWriter.ToBytes(model));
            File.WriteAllBytes(Path.Combine(directory, "authored.bmp"), Bitmap());
            var composition = new SceneCompositionEditor();
            var asset = Load(sourcePath, Token);
            byte[] encoded = Bitmap();
            Guid id = Guid.NewGuid();
            var seed = EditorClassicToonPackage.ReadPackage(new Resources(id, encoded), Token)!;
            asset = seed.Apply(id, asset, sourcePath, Token);
            id = composition.Add("actor", sourcePath, ModelRole.Character, asset.Source).Id;
            seed = EditorClassicToonPackage.ReadPackage(new Resources(id, encoded), Token)!;
            var motion = SceneMotionAsset.FromMotion(new VmdMotion { ModelName = asset.Model.Name }, "motion.vmd");
            using var project = new SceneProjectSession(composition, new(),
                [new(id, asset.Model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics());
            var toons = EditorClassicToonPackage.Capture(project, null, seed, Token);
            string first = Path.Combine(directory, "first.pmc"), second = Path.Combine(directory, "second.pmc");
            PmcProjectStore.Save(first, project, toons.CreateSaveOptions(project, new() { CacheDirectory = Path.Combine(directory, "save-cache") }, Token), Token);
            File.Delete(sourcePath); File.Delete(Path.Combine(directory, "authored.bmp"));
            EditorClassicToonPackage? frozen = null; int hostCalls = 0; string? currentPath = null;
            using var loaded = PmcProjectStore.Load(first, Path.Combine(directory, "cache"), (path, token) =>
            {
                Assert.NotNull(frozen); hostCalls++; currentPath = path; return Load(path, token);
            }, new NoPhysics(), new()
            {
                PackagePreflight = (context, token) => frozen = EditorClassicToonPackage.ReadPackage(context, token),
                ModelAssetTransform = (context, value) => frozen!.Apply(context.ObjectId, value,
                    currentPath!, Token)
            }, Token);
            Assert.Equal(1, hostCalls); frozen!.Validate(loaded.Session);
            Assert.Equal(new byte[] { 17, 34, 51, 255 }, loaded.Session.Objects[0].Source.Surface.Snapshot.Textures[
                loaded.Session.Objects[0].Source.Surface.Snapshot.Materials[0].ToonTexture].Rgba.ToArray());
            Assert.All(loaded.Session.Objects[0].Source.Surface.Snapshot.Materials, material =>
                Assert.Equal(new byte[] { 17, 34, 51, 255 }, loaded.Session.Objects[0].Source.Surface.Snapshot.Textures[material.ToonTexture].Rgba.ToArray()));
            using var snapshot = PmcProjectStore.CaptureSnapshot(loaded.Session,
                frozen.CreateSaveOptions(loaded.Session, new() { CacheDirectory = Path.Combine(directory, "save-cache") }, Token), Token);
            loaded.Dispose(); snapshot.Save(second, Token);
            using var reopened = PmcProjectStore.Load(second, Path.Combine(directory, "cache2"),
                (path, token) => { currentPath = path; return Load(path, token); }, new NoPhysics(), new()
            {
                PackagePreflight = (context, token) =>
                {
                    frozen = EditorClassicToonPackage.ReadPackage(context, token); Assert.NotNull(frozen);
                    Assert.Equal(encoded, context.ReadResource(Assert.Single(context.ResourcePaths)));
                },
                ModelAssetTransform = (context, value) => frozen!.Apply(context.ObjectId, value,
                    currentPath!, Token)
            }, Token);
            frozen!.Validate(reopened.Session);
            var broken = new Resources(id, encoded, schema: 99);
            Assert.Throws<InvalidDataException>(() => EditorClassicToonPackage.ReadPackage(broken, Token));
            Assert.Throws<InvalidDataException>(() => EditorClassicToonPackage.ReadPackage(new Resources(id, [1, 2, 3]), Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        return EditorProjectWorkspace.CreateModelAsset(PmxReader.Read(stream), path, token);
    }
    internal static byte[] Bitmap()
    {
        byte[] bytes = new byte[58];
        using var stream = new MemoryStream(bytes, writable: true); using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(58); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(1); writer.Write(1); writer.Write((short)1); writer.Write((short)24);
        writer.Write(0); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(new byte[] { 51, 34, 17, 0 }); return bytes;
    }
    internal static PmxModel Model(IReadOnlyList<PmxMaterial>? materials = null)
    {
        var template = EditorRecoveryPmaIntegrationTests.Model();
        return new()
        {
            Header = template.Header, Name = "portable toons", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = template.Vertices, Faces = [new(0, 1, 2), new(0, 1, 2), new(0, 1, 2)], Textures = ["authored.bmp"],
            Materials = materials ?? [Material(PmxToonMode.Separate, -1), Material(PmxToonMode.Common, 4), Material(PmxToonMode.Separate, 0)],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }
    private static PmxMaterial Material(PmxToonMode mode, int index) => new()
    {
        Name = "toon material", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero,
        SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = 0, EdgeColor = Vector4.One, EdgeSize = 0,
        TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = mode,
        ToonTextureIndex = index, Memo = "", FaceVertexCount = 3
    };
    private sealed class Resources : IPmaPackageResources
    {
        private readonly byte[] bytes;
        public Resources(Guid id, byte[] bytes, int schema = 1, bool extraCommon = false)
        {
            this.bytes = bytes;
            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            string path = EditorClassicToonPackage.ResourcePrefix + sha.ToLowerInvariant() + ".bin";
            ResourcePaths = [path];
            OptionalExtensions = new Dictionary<string, JsonElement>
            {
                [EditorClassicToonPackage.ExtensionName] = JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = schema,
                    models = new[] { new { objectId = id, materials = new[]
                    { new { materialIndex = 0, slot = 0, imageSha256 = sha }, new { materialIndex = 1, slot = 5, imageSha256 = sha } }
                    .Concat(extraCommon ? [new { materialIndex = 2, slot = 5, imageSha256 = sha }] : []).ToArray() } },
                    images = new[] { new { sha256 = sha, authoredToken = "toon00.bmp", resourcePath = path } }
                })
            };
        }
        public string Format => PmcProjectStore.Format;
        public Guid ProjectId => Guid.Empty;
        public IReadOnlyDictionary<string, JsonElement> OptionalExtensions { get; }
        public IReadOnlyList<string> ResourcePaths { get; }
        public byte[] ReadResource(string path, long maxBytes = long.MaxValue) => bytes.ToArray();
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
}
