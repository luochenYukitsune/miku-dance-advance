using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PortableSceneProjectTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SceneProjectSaveOptions Portable = new() { CopyModelAssets = true };
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "portable-project-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(directory, "original");
    private string Project => Path.Combine(directory, "project", "scene.mmdscene");
    private static OriginalFrameScene Frame => EngineShowcaseScene.Create().Frame;
    public PortableSceneProjectTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Path.GetDirectoryName(Project)!); }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DefaultPmxSaveMovesWithoutSourceAndPreservesTextureSphereToonBytes(bool commonToon, bool explicitDefaults)
    {
        string path = WritePmx(commonToon); var before = HashTree(Source);
        var composition = Composition(path); var lighting = new SceneLightingEditor();
        if (explicitDefaults) SceneProjectStore.Save(Project, composition, lighting, new SceneProjectSaveOptions(), Token);
        else SceneProjectStore.Save(Project, composition, lighting);
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Project))!["version"]!.GetValue<int>());
        string moved = MoveAndHideSource();
        var reopened = SceneProjectStore.Load(moved, (asset, _) =>
        {
            Assert.StartsWith(Path.GetDirectoryName(moved)!, asset, StringComparison.OrdinalIgnoreCase);
            var model = PmxReader.Read(asset);
            Assert.Equal(commonToon ? PmxToonMode.Common : PmxToonMode.Separate, model.Materials[0].ToonMode);
            foreach (string texture in model.Textures)
            {
                Assert.DoesNotContain("..", texture); Assert.DoesNotContain('\\', texture);
                Assert.Equal(ExpectedTexture(Path.GetExtension(texture)), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(asset)!, texture)));
            }
            return Frame;
        }, Token);
        Assert.Single(reopened.Composition.Objects);
        Assert.Equal(before, HashTree(Path.Combine(directory, "hidden-original")));
    }

    [Fact]
    public void PortableParentedLayoutRetainsObjectIdentityLocalPoseAndGroupsAfterSourceMoves()
    {
        string path = WritePmx(); var composition = Composition(path);
        var group = new SceneLightGroup(Guid.NewGuid(), "跟随柔光", Intensity: 2);
        var light = new NativeSceneLight(Guid.NewGuid(), "骨骼补光", SceneLightKind.Spot, new(2, -3, 4),
            Quaternion.CreateFromYawPitchRoll(.2f, .3f, -.4f), Vector3.One, Range: 17,
            GroupId: group.Id, Parent: new(composition.Objects[0].Id, 0));
        var lighting = new SceneLightingEditor(); lighting.ReplaceSnapshot(new([light], [group]), light.Id);
        SceneProjectStore.Save(Project, composition, lighting, Portable, Token);
        var json = JsonNode.Parse(File.ReadAllText(Project))!;
        Assert.Equal(SceneProjectStore.PortableLightingParentsVersion, json["version"]!.GetValue<int>());
        string moved = MoveAndHideSource();
        var reopened = SceneProjectStore.Load(moved, (_, _) => Frame, Token);
        Assert.Equal(light, Assert.Single(reopened.Lighting.Lights));
        Assert.Equal(group, Assert.Single(reopened.Lighting.Groups));
        Assert.Equal(light.Parent!.ObjectId, Assert.Single(reopened.Composition.Objects).Id);
        Assert.Equal(light.Id, reopened.Lighting.SelectedId);
    }

    [Fact]
    public void PortableAreaLayoutUsesVersionTenAndRetainsDimensionsAfterSourceMoves()
    {
        string path = WritePmx(); var composition = Composition(path);
        var light = new NativeSceneLight(Guid.NewGuid(), "窗光", SceneLightKind.Area, new(2, -3, 4),
            Quaternion.Identity, Vector3.One, AreaSize: new(9.123456f, 13.654321f));
        var lighting = new SceneLightingEditor(); lighting.Add(light);
        SceneProjectStore.Save(Project, composition, lighting, Portable, Token);
        Assert.Equal(10, JsonNode.Parse(File.ReadAllText(Project))!["version"]!.GetValue<int>());
        string moved = MoveAndHideSource();
        var reopened = SceneProjectStore.Load(moved, (_, _) => Frame, Token);
        Assert.Equal(light, Assert.Single(reopened.Lighting.Lights));
    }

    [Fact]
    public void PortableGlobalLightingUsesVersionTwelveAndSurvivesSourceMoves()
    {
        string path = WritePmx(); var composition = Composition(path); var lighting = new SceneLightingEditor();
        lighting.UpdateSettings(new(1.234567f, -3.25f));
        SceneProjectStore.Save(Project, composition, lighting, Portable, Token);
        Assert.Equal(12, JsonNode.Parse(File.ReadAllText(Project))!["version"]!.GetValue<int>());
        string moved = MoveAndHideSource(); var reopened = SceneProjectStore.Load(moved, (_, _) => Frame, Token);
        Assert.Equal(lighting.Settings, reopened.Lighting.Settings); Assert.Empty(reopened.Lighting.Lights);
        reopened.Lighting.UpdateSettings(SceneLightingSettings.Default);
        Assert.Equal(1, JsonNode.Parse(SceneProjectStore.Serialize(moved, reopened.Composition, reopened.Lighting))!["version"]!.GetValue<int>());
    }

    [Fact]
    public void PortablePmdKeepsTwoTextureFieldWithinTwentyBytesAndCustomToon()
    {
        var model = Pmd() with { HasAnimationTail = true, HasToonExtension = true,
            BoneDisplayFrames = [new("センター", [])],
            Materials = [Pmd().Materials[0] with { Texture = "d.png*s.sph", ToonIndex = 0 }],
            ToonTextures = ["custom.bmp", .. Enumerable.Range(2, 9).Select(i => $"toon{i:00}.bmp")] };
        WriteTexture("d.png"); WriteTexture("s.sph"); WriteTexture("custom.bmp");
        string path = Path.Combine(Source, "model.pmd"); File.WriteAllBytes(path, PmdWriter.ToBytes(model));
        var before = HashTree(Source);
        SceneProjectStore.Save(Project, Composition(path), new());
        string moved = MoveAndHideSource();
        SceneProjectStore.Load(moved, (asset, _) =>
        {
            using var stream = File.OpenRead(asset); var read = PmdReader.Read(stream);
            Assert.InRange(read.Materials[0].Texture.Length, 1, 20);
            Assert.Equal(2, read.Materials[0].Texture.Split('*').Length);
            Assert.EndsWith(".sph", read.Materials[0].Texture.Split('*')[1]);
            var converted = PmdPmxAdapter.Convert(read);
            Assert.Equal(PmxSphereMode.Multiply, converted.Materials[0].SphereMode);
            Assert.Equal(PmxToonMode.Separate, converted.Materials[0].ToonMode);
            foreach (string texture in converted.Textures)
                Assert.Equal(ExpectedTexture(Path.GetExtension(texture)), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(asset)!, texture)));
            return Frame;
        }, Token);
        Assert.Equal(before, HashTree(Path.Combine(directory, "hidden-original")));
    }

    [Fact]
    public void ModelLocalCanonicalPmdToonOverridesBuiltinEvenWithoutOriginalToonExtension()
    {
        var model = Pmd() with { Materials = [Pmd().Materials[0] with { ToonIndex = 0 }] };
        string path = Path.Combine(Source, "model.pmd"); File.WriteAllBytes(path, PmdWriter.ToBytes(model)); WriteTexture("toon01.bmp");
        var original = HashTree(Source); SceneProjectStore.Save(Project, Composition(path), new(), Portable, Token);
        string moved = MoveAndHideSource();
        SceneProjectStore.Load(moved, (asset, _) =>
        {
            using var stream = File.OpenRead(asset); var read = PmdReader.Read(stream); var converted = PmdPmxAdapter.Convert(read);
            Assert.True(read.HasToonExtension); Assert.Equal(PmxToonMode.Separate, converted.Materials[0].ToonMode);
            string texture = converted.Textures[converted.Materials[0].ToonTextureIndex];
            Assert.Equal(ExpectedTexture(".bmp"), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(asset)!, texture)));
            return Frame;
        }, Token);
        Assert.Equal(original, HashTree(Path.Combine(directory, "hidden-original")));
    }

    [Fact]
    public void MissingCanonicalPmdToonUsesItsNamedBuiltinRatherThanItsTableSlot()
    {
        var model = Pmd() with { HasAnimationTail = true, HasToonExtension = true, BoneDisplayFrames = [new("センター", [])],
            Materials = [Pmd().Materials[0] with { ToonIndex = 0 }],
            ToonTextures = ["toon09.bmp", .. Enumerable.Range(2, 9).Select(i => $"toon{i:00}.bmp")] };
        string path = Path.Combine(Source, "model.pmd"); File.WriteAllBytes(path, PmdWriter.ToBytes(model));
        SceneProjectStore.Save(Project, Composition(path), new(), Portable, Token); string moved = MoveAndHideSource();
        SceneProjectStore.Load(moved, (asset, _) =>
        {
            using var stream = File.OpenRead(asset); var read = PmdReader.Read(stream); var converted = PmdPmxAdapter.Convert(read);
            Assert.Equal(PmxToonMode.Common, converted.Materials[0].ToonMode); Assert.Equal(8, converted.Materials[0].ToonTextureIndex);
            Assert.Empty(converted.Textures); Assert.Equal("toon09.bmp", read.ToonTextures[0]); return Frame;
        }, Token);
    }

    [Fact]
    public void DuplicateModelsAndTextureContentUseOneImmutableBundle()
    {
        string path = WritePmx(); var composition = Composition(path); composition.Add("second", path, ModelRole.Prop, Frame);
        SceneProjectStore.Save(Project, composition, new(), Portable, Token);
        var json = JsonNode.Parse(File.ReadAllText(Project))!;
        Assert.Single(json["portableModels"]!.AsArray());
        Assert.Equal(json["objects"]![0]!["sourcePath"]!.GetValue<string>(), json["objects"]![1]!["sourcePath"]!.GetValue<string>());
        var first = HashTree(Path.GetDirectoryName(Project)!);
        SceneProjectStore.Save(Project, composition, new(), Portable, Token);
        Assert.Equal(first, HashTree(Path.GetDirectoryName(Project)!));
    }

    [Fact]
    public void ChangedTextureCreatesNewBundleWithoutChangingOldReferencedFiles()
    {
        string path = WritePmx(); var composition = Composition(path);
        SceneProjectStore.Save(Project, composition, new(), Portable, Token);
        string oldHash = JsonNode.Parse(File.ReadAllText(Project))!["portableModels"]![0]!.GetValue<string>();
        string oldDirectory = Path.Combine(Path.GetDirectoryName(Project)!, "assets", "models", oldHash);
        var oldFiles = HashTree(oldDirectory); File.WriteAllBytes(Path.Combine(Source, "paint.png"), [22, 33, 44]);
        SceneProjectStore.Save(Project, composition, new(), Portable, Token);
        string newHash = JsonNode.Parse(File.ReadAllText(Project))!["portableModels"]![0]!.GetValue<string>();
        Assert.NotEqual(oldHash, newHash); Assert.Equal(oldFiles, HashTree(oldDirectory));
    }

    [Theory]
    [InlineData("paint.png")]
    [InlineData("shade.bmp")]
    public void MissingDependencyPreservesPreviousDocumentAndOriginalModel(string dependency)
    {
        string path = WritePmx(); var composition = Composition(path); SceneProjectStore.Save(Project, composition, new(), ExternalAssets, Token);
        byte[] old = File.ReadAllBytes(Project), model = File.ReadAllBytes(path); File.Delete(Path.Combine(Source, dependency));
        var error = Assert.Throws<IOException>(() => SceneProjectStore.Save(Project, composition, new()));
        Assert.Contains(dependency, error.Message); Assert.Equal(old, File.ReadAllBytes(Project)); Assert.Equal(model, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Project)!, "assets", "models")));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("model.pmx")]
    [InlineData("0.png")]
    public void CorruptBundleRejectsBeforeHostLoaderAndCannotOverwriteOldAsset(string name)
    {
        string path = WritePmx(); var composition = Composition(path); SceneProjectStore.Save(Project, composition, new(), Portable, Token);
        string hash = JsonNode.Parse(File.ReadAllText(Project))!["portableModels"]![0]!.GetValue<string>();
        string corrupt = Path.Combine(Path.GetDirectoryName(Project)!, "assets", "models", hash, name);
        File.WriteAllBytes(corrupt, [7, 7, 7]); byte[] old = File.ReadAllBytes(Project); int loads = 0;
        Assert.Throws<InvalidDataException>(() => SceneProjectStore.Load(Project, (_, _) => { loads++; return Frame; }, Token));
        Assert.Equal(0, loads);
        Assert.Throws<InvalidDataException>(() => SceneProjectStore.Save(Project, composition, new(), Portable, Token));
        Assert.Equal(old, File.ReadAllBytes(Project)); Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(corrupt));
    }

    [Fact]
    public void ExplicitExternalSaveAndPortableCancellationPreserveExistingContracts()
    {
        string path = WritePmx(); var composition = Composition(path); SceneProjectStore.Save(Project, composition, new(), ExternalAssets, Token);
        byte[] old = File.ReadAllBytes(Project); var json = JsonNode.Parse(old)!;
        Assert.Equal(1, json["version"]!.GetValue<int>()); Assert.Null(json["portableModels"]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SceneProjectStore.Save(Project, composition, new(), new SceneProjectSaveOptions(), cancellation.Token));
        Assert.Equal(old, File.ReadAllBytes(Project));
    }

    [Fact]
    public void DefaultSaveRejectsMissingModelButExplicitExternalAndSerializeRetainReferenceOnlyContract()
    {
        string missing = Path.Combine(Source, "missing.pmx"); var composition = Composition(missing);
        string json = SceneProjectStore.Serialize(Project, composition, new());
        Assert.Equal(1, JsonNode.Parse(json)!["version"]!.GetValue<int>());
        Assert.False(File.Exists(Project)); Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Project)!, "assets")));
        Assert.Throws<FileNotFoundException>(() => SceneProjectStore.Save(Project, composition, new()));
        Assert.False(File.Exists(Project));
        SceneProjectStore.Save(Project, composition, new(), ExternalAssets, Token);
        Assert.Equal(json, File.ReadAllText(Project));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Project)!, "assets")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultAnimationSaveReopensAfterMoveWithCopiedVmdAndCurrentFrame(bool explicitDefaults)
    {
        string path = WritePmx(); var model = PmxReader.Read(path); var rest = Rest(model);
        var composition = new SceneCompositionEditor(); var actor = composition.Add("actor", path, ModelRole.Character, rest); var lighting = new SceneLightingEditor();
        var motion = SceneMotionAsset.FromMotion(new() { ModelName = "model", BoneKeyframes =
            [new() { BoneName = "root", Frame = 0 }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitX }] });
        using var session = new SceneAnimationSession(composition.Objects,
            [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)], NoPhysics, lighting.Snapshot, Token);
        session.Seek(12, Token); var before = session.Frame.Surface.Snapshot.Vertices.ToArray();
        if (explicitDefaults) SceneAnimationProjectStore.Save(Project, composition, lighting, session, new SceneProjectSaveOptions(), Token);
        else SceneAnimationProjectStore.Save(Project, composition, lighting, session, Token);
        var json = JsonNode.Parse(File.ReadAllText(Project))!; Assert.Equal(2, json["layout"]!["version"]!.GetValue<int>());
        string moved = MoveAndHideSource();
        using var reopened = SceneAnimationProjectStore.Load(moved, (asset, _) => { var m = PmxReader.Read(asset); return new(m, Rest(m)); }, NoPhysics, Token);
        Assert.Equal(12, reopened.Animation.CurrentFrame); Assert.Equal(before, reopened.Animation.Frame.Surface.Snapshot.Vertices);
        Assert.True(reopened.Animation.Actors[0].HasMotion);
        Assert.All(reopened.Composition.Objects, item => Assert.StartsWith(Path.GetDirectoryName(moved)!, item.SourcePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BundleMetadataCannotRedirectToUnlistedOrWrongModel()
    {
        string path = WritePmx(); SceneProjectStore.Save(Project, Composition(path), new(), Portable, Token);
        var json = JsonNode.Parse(File.ReadAllText(Project))!; string old = json["objects"]![0]!["sourcePath"]!.GetValue<string>();
        json["objects"]![0]!["sourcePath"] = old.Replace("model.pmx", "model.pmd"); int loads = 0;
        Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(json.ToJsonString(), Project, (_, _) => { loads++; return Frame; }, Token));
        Assert.Equal(0, loads);
    }

    [Fact]
    public void UnreferencedTextureTableEntriesAreNotCopiedIntoThePackage()
    {
        // A crafted model can list extra texture-table entries that no material binds. They are
        // never loaded, so saving must not pull those files into a shareable package.
        string secret = Path.Combine(directory, "secret.txt"); File.WriteAllText(secret, "private");
        WriteTexture("unused.png");
        string path = WritePmx(extraTextures: ["unused.png", "../secret.txt", secret]);
        SceneProjectStore.Save(Project, Composition(path), new(), Portable, Token);
        string hash = JsonNode.Parse(File.ReadAllText(Project))!["portableModels"]![0]!.GetValue<string>();
        string bundle = Path.Combine(Path.GetDirectoryName(Project)!, "assets", "models", hash);
        var saved = PmxReader.Read(Path.Combine(bundle, "model.pmx"));
        Assert.Equal(6, saved.Textures.Count);
        Assert.All(saved.Textures.Skip(3), name => Assert.Equal("", name));
        Assert.Equal(2, saved.Materials[0].ToonTextureIndex);
        foreach (string file in Directory.EnumerateFiles(bundle))
            Assert.NotEqual("private", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("sub/../../secret.png")]
    [InlineData("C:/secret.png")]
    public void ReferencedTextureOutsideTheModelDirectoryIsRejectedBeforeAnyWrite(string reference)
    {
        File.WriteAllBytes(Path.Combine(directory, "secret.png"), [1, 2, 3]);
        string path = WritePmx(diffuse: reference);
        var error = Assert.Throws<InvalidDataException>(() => SceneProjectStore.Save(Project, Composition(path), new(), Portable, Token));
        Assert.Contains(reference, error.Message);
        Assert.False(File.Exists(Project));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Project)!, "assets", "models")));
    }

    [Fact]
    public void AbsoluteReferencedTextureIsRejected()
    {
        string secret = Path.Combine(directory, "secret.png"); File.WriteAllBytes(secret, [1, 2, 3]);
        string path = WritePmx(diffuse: secret);
        Assert.Throws<InvalidDataException>(() => SceneProjectStore.Save(Project, Composition(path), new(), Portable, Token));
        Assert.False(File.Exists(Project));
    }

    private string WritePmx(bool commonToon = false, string[]? extraTextures = null, string diffuse = "paint.png")
    {
        WriteTexture("paint.png"); WriteTexture("shine.spa"); if (!commonToon) WriteTexture("shade.bmp");
        var source = PmdPmxAdapter.Convert(Pmd()); var first = source.Materials[0];
        var model = new PmxModel
        {
            Header = source.Header, Name = source.Name, EnglishName = source.EnglishName, Comment = source.Comment, EnglishComment = source.EnglishComment,
            Vertices = source.Vertices, Faces = source.Faces,
            Textures = [.. (commonToon ? new[] { diffuse, "shine.spa" } : new[] { diffuse, "shine.spa", "shade.bmp" }), .. extraTextures ?? []],
            Materials = [new() { Name = "material", EnglishName = "", Diffuse = first.Diffuse, Specular = first.Specular,
                SpecularPower = first.SpecularPower, Ambient = first.Ambient, DrawMode = first.DrawMode, EdgeColor = first.EdgeColor,
                EdgeSize = first.EdgeSize, TextureIndex = 0, SphereTextureIndex = 1, SphereMode = PmxSphereMode.Add,
                ToonMode = commonToon ? PmxToonMode.Common : PmxToonMode.Separate, ToonTextureIndex = commonToon ? 0 : 2, Memo = "", FaceVertexCount = 3 }],
            Bones = source.Bones, Morphs = source.Morphs, DisplayFrames = source.DisplayFrames, RigidBodies = source.RigidBodies, Joints = source.Joints, SoftBodies = source.SoftBodies,
        };
        string path = Path.Combine(Source, "model.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model)); return path;
    }
    private void WriteTexture(string name) => File.WriteAllBytes(Path.Combine(Source, name), ExpectedTexture(Path.GetExtension(name)));
    private static byte[] ExpectedTexture(string extension) => System.Text.Encoding.ASCII.GetBytes("original-" + extension);
    private static SceneCompositionEditor Composition(string path) { var scene = new SceneCompositionEditor(); scene.Add("model", path, ModelRole.Character, Frame); return scene; }
    private static PmdModel Pmd() => new("model", [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, 0, 0, 1),
        new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero, 0, 0, 1)],
        [0, 1, 2], [new(Vector4.One, Vector3.Zero, new(.3f), 4, "", 0, 3)], [new("root", -1, Vector3.Zero)], 0);
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("Unexpected physics world.");
    private string MoveAndHideSource()
    {
        string moved = Path.Combine(directory, "moved"); Directory.Move(Path.GetDirectoryName(Project)!, moved);
        Directory.Move(Source, Path.Combine(directory, "hidden-original")); return Path.Combine(moved, "scene.mmdscene");
    }
    private static string[] HashTree(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
    public void Dispose()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())); string full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("portable-project-", StringComparison.Ordinal))
            throw new InvalidOperationException("Cleanup outside dedicated temporary fixture.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
