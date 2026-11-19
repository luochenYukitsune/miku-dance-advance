using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12GeneratedMaterialRestoreTests
{
    [Fact]
    public void RestoredResultSupportsRepeatedImmutableEditingWithOriginalOptionsAndEditFlags()
    {
        var original = Frame();
        var prepared = Prepare(original);
        var first = prepared.WithEditedMaps(0, normal: Edited(prepared, 1));
        byte[] savedManifest = first.ManifestBytes;
        var reopened = D3D12GeneratedMaterialRestore.Restore(first.SourceRest,
            D3D12MaterialBindingProfileJson.Decode(D3D12MaterialBindingProfileJson.Encode(first.Profile)), savedManifest);
        Assert.Equal(savedManifest, reopened.ManifestBytes);
        var edited = reopened.WithEditedMaps(0, p: Edited(reopened, 2));
        using var before = JsonDocument.Parse(savedManifest);
        using var after = JsonDocument.Parse(edited.ManifestBytes);
        var oldEntry = before.RootElement.GetProperty("materials")[0];
        var newEntry = after.RootElement.GetProperty("materials")[0];
        Assert.Equal(oldEntry.GetProperty("options").GetRawText(), newEntry.GetProperty("options").GetRawText());
        Assert.Equal(oldEntry.GetProperty("sourceRgbaSha256").GetString(), newEntry.GetProperty("sourceRgbaSha256").GetString());
        Assert.True(newEntry.GetProperty("outputs")[0].GetProperty("userEdited").GetBoolean());
        Assert.True(newEntry.GetProperty("outputs")[1].GetProperty("userEdited").GetBoolean());
        Assert.False(newEntry.GetProperty("outputs")[2].GetProperty("userEdited").GetBoolean());
        Assert.Equal(savedManifest, first.ManifestBytes);
        int pSlot = reopened.Profile.Materials[0].PluginBindings[0].TextureSlots!["p"];
        Assert.NotEqual(
            reopened.Profile.Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == pSlot).RgbaSha256,
            edited.Profile.Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == pSlot).RgbaSha256);
        Assert.Equal(new byte[] { 80, 120, 160, 255 }, original.Surface.Snapshot.Textures[0].Rgba.ToArray());
        Assert.Single(original.Surface.Snapshot.Textures);
        var restoredAgain = D3D12GeneratedMaterialRestore.Restore(edited.SourceRest, edited.Profile, edited.ManifestBytes);
        Assert.Equal(edited.ManifestBytes, restoredAgain.ManifestBytes);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("id")]
    [InlineData("sourceHash")]
    [InlineData("sourceOrdinal")]
    [InlineData("outputHash")]
    [InlineData("outputOrdinal")]
    [InlineData("dimensions")]
    [InlineData("revision")]
    [InlineData("options")]
    [InlineData("duplicateMaterial")]
    [InlineData("duplicateOutput")]
    [InlineData("unknown")]
    [InlineData("flags")]
    public void RejectsInconsistentOrUnsupportedProvenance(string corruption)
    {
        var prepared = Prepare(Frame());
        var root = JsonNode.Parse(prepared.ManifestBytes)!;
        var material = root["materials"]![0]!;
        var output = material["outputs"]![0]!;
        switch (corruption)
        {
            case "schema": root["schemaVersion"] = 3; break;
            case "id": root["profileStableId"] = "another-profile"; break;
            case "sourceHash": material["sourceRgbaSha256"] = new string('0', 64); break;
            case "sourceOrdinal": material["sourceSceneTextureOrdinal"] = 1; break;
            case "outputHash": output["rgbaSha256"] = new string('0', 64); break;
            case "outputOrdinal": output["sourceSceneTextureOrdinal"] = 0; break;
            case "dimensions": output["width"] = 17; break;
            case "revision": material["generatorRevision"] = "future"; break;
            case "options": material["options"]!["mapSize"] = 15; break;
            case "duplicateMaterial": root["materials"]!.AsArray().Add(material.DeepClone()); break;
            case "duplicateOutput": material["outputs"]![1] = output.DeepClone(); break;
            case "unknown": output["unexpected"] = true; break;
            case "flags": material["outputsUserEdited"] = true; break;
        }
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedMaterialRestore.Restore(
            prepared.SourceRest, prepared.Profile, Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void RejectsDuplicatePropertiesMissingFieldsOversizedPayloadAndChangedSourcePixels()
    {
        var prepared = Prepare(Frame());
        string manifest = Encoding.UTF8.GetString(prepared.ManifestBytes);
        Assert.Throws<InvalidDataException>(() => Restore(manifest.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 2, \"schemaVersion\": 2")));
        Assert.Throws<InvalidDataException>(() => Restore(manifest.Replace("\"mapSize\": 16,", "")));
        Assert.Throws<InvalidDataException>(() => Restore(manifest.Replace("\"kind\": \"Hair\"", "\"kind\": \"Hair\", \"kind\": \"Hair\"")));
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedMaterialRestore.Restore(
            prepared.SourceRest, prepared.Profile, new byte[D3D12GeneratedMaterialRestore.MaxManifestBytes + 1]));
        var source = prepared.SourceRest.Surface.Snapshot;
        var changed = new SceneSnapshot(source.Vertices, source.Indices, source.Materials,
            [new SceneTexture("changed", 1, 1, [81, 120, 160, 255]), .. source.Textures.Skip(1)]);
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedMaterialRestore.Restore(
            new(new(changed, prepared.SourceRest.Surface.Materials)), prepared.Profile, prepared.ManifestBytes));
        void Restore(string text) => D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest, prepared.Profile, Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void RejectsUnrelatedProfilesAndOriginalTextureSlotsDisguisedAsGeneratedOutputs()
    {
        var prepared = Prepare(Frame());
        var row = prepared.Profile.Materials[0];
        var matcap = row.PluginBindings[0];
        var sameSlot = new Dictionary<string, int>(matcap.TextureSlots!) { ["p"] = matcap.TextureSlots!["normal"] };
        var changed = new D3D12MaterialBindingProfile(prepared.Profile.StableId, prepared.Profile.Name,
            prepared.Profile.LayoutFingerprint, [row with { PluginBindings = [matcap with { TextureSlots = sameSlot }] }]);
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest, changed, prepared.ManifestBytes));
        var originalSlot = D3D12MaterialBindingLayout.Capture(prepared.SourceRest).CaptureTexture(0);
        int generatedNormal = matcap.TextureSlots["normal"];
        var origSlots = new Dictionary<string, int>(matcap.TextureSlots) { ["normal"] = 0 };
        var origGuards = row.TextureGuards.Where(g => g.SourceSceneTextureOrdinal != generatedNormal).Append(originalSlot).ToArray();
        var originalProfile = new D3D12MaterialBindingProfile(prepared.Profile.StableId, prepared.Profile.Name,
            prepared.Profile.LayoutFingerprint, [row with { PluginBindings = [matcap with { TextureSlots = origSlots }], TextureGuards = origGuards }]);
        var json = JsonNode.Parse(prepared.ManifestBytes)!;
        var output = json["materials"]![0]!["outputs"]![0]!;
        output["sourceSceneTextureOrdinal"] = 0; output["width"] = 1; output["height"] = 1;
        output["rgbaSha256"] = originalSlot.RgbaSha256;
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest,
            originalProfile, Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void MovedPmaCanReopenEditAndSaveGeneratedMapsAgainWithoutOriginalFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "generated-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
            var model = new PmxModel
            {
                Header = template.Header, Name = "generated restore", EnglishName = "", Comment = "", EnglishComment = "",
                Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
                {
                    Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero,
                    AdditionalUvs = new Vector4[template.Header.AdditionalUvCount], WeightType = PmxVertexWeightType.Bdef1,
                    BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1
                }).ToArray(),
                Faces = [new(0, 1, 2)], Textures = [], Materials = [new()
                {
                    Name = "explicit body", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero,
                    SpecularPower = 1, Ambient = new(.2f), EdgeColor = Vector4.Zero, EdgeSize = 0,
                    DrawMode = PmxDrawModeFlags.BothFaces, TextureIndex = -1, SphereTextureIndex = -1,
                    SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common, ToonTextureIndex = 0,
                    Memo = "", FaceVertexCount = 3
                }], Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
            };
            string sourcePath = Path.Combine(directory, "original.pmx");
            File.WriteAllBytes(sourcePath, PmxWriter.ToBytes(model));
            var source = Rest(model);
            var prepared = Prepare(source);
            var composition = new SceneCompositionEditor();
            Guid id = composition.Add("generated fixture", sourcePath, ModelRole.Character, source).Id;
            using var project = new SceneProjectSession(composition, new(), [], new NoPhysics());
            string first = Path.Combine(directory, "first.pma"), moved = Path.Combine(directory, "moved.pma");
            PmaProjectStore.Save(first, project, D3D12GeneratedMaterialPma.CreateSaveOptions(project, [new(id, prepared)]), TestContext.Current.CancellationToken);
            File.Move(first, moved); File.Delete(sourcePath);
            string final = Path.Combine(directory, "edited.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load,
                new NoPhysics(), out var profiles, cancellationToken: TestContext.Current.CancellationToken))
            {
                var obj = Assert.Single(loaded.Session.Objects);
                var restored = D3D12GeneratedMaterialRestore.Restore(obj.Source, profiles[id],
                    loaded.ReadResource(D3D12GeneratedMaterialPma.ManifestPath(id)));
                int pSlot = profiles[id].Materials[0].PluginBindings[0].TextureSlots!["p"];
                var edited = restored.WithEditedMaps(0, p: Edited(restored, pSlot));
                // Replace this owned resource while keeping all other PMA resources and mapping state.
                PmaProjectStore.Save(final, loaded.Session,
                    D3D12GeneratedMaterialPma.CreateSaveOptions(loaded.Session, [new(id, edited)]), TestContext.Current.CancellationToken);
                prepared = edited;
            }
            using var reopened = D3D12MaterialBindingPma.Load(final, Path.Combine(directory, "cache2"), Load,
                new NoPhysics(), out var again, cancellationToken: TestContext.Current.CancellationToken);
            var result = D3D12GeneratedMaterialRestore.Restore(Assert.Single(reopened.Session.Objects).Source,
                again[id], reopened.ReadResource(D3D12GeneratedMaterialPma.ManifestPath(id)));
            Assert.Equal(prepared.ManifestBytes, result.ManifestBytes);
            int savedPSlot = prepared.Profile.Materials[0].PluginBindings[0].TextureSlots!["p"];
            Assert.Equal(
                prepared.Profile.Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == savedPSlot),
                again[id].Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == savedPSlot));
            Assert.False(File.Exists(sourcePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static D3D12GeneratedEndfieldMaterialPreparationResult Prepare(OriginalFrameScene source)
        => D3D12GeneratedEndfieldMaterialPreparation.Prepare(source,
            [new(0, new(D3D12GeneratedPmxSurfaceKind.Hair, 16, .4f, .5f, .2f))], "restore-fixture", "Restore fixture");

    [Fact]
    public void OriginalDimensionsAreGuardedAndOldManifestCanMigrate()
    {
        var original = Frame();
        byte[] pixels = [80,120,160,255, 60,100,140,255];
        OriginalFrameScene Reshape(OriginalFrameScene frame, int width, int height)
        {
            var s = frame.Surface.Snapshot;
            return new(new(new SceneSnapshot(s.Vertices, s.Indices, s.Materials,
                [new SceneTexture("source", width, height, pixels), ..s.Textures.Skip(1)]), frame.Surface.Materials));
        }
        var prepared = Prepare(Reshape(original,2,1));
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedMaterialRestore.Restore(
            Reshape(prepared.SourceRest,1,2),prepared.Profile,prepared.ManifestBytes));
        var legacy=JsonNode.Parse(prepared.ManifestBytes)!;
        legacy["schemaVersion"]=1;
        legacy["materials"]![0]!.AsObject().Remove("sourceWidth");
        legacy["materials"]![0]!.AsObject().Remove("sourceHeight");
        var migrated=D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest,prepared.Profile,
            Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        using var updated=JsonDocument.Parse(migrated.ManifestBytes);
        Assert.Equal(2,updated.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2,updated.RootElement.GetProperty("materials")[0].GetProperty("sourceWidth").GetInt32());
        var missing=JsonNode.Parse(prepared.ManifestBytes)!;
        missing["materials"]![0]!.AsObject().Remove("sourceWidth");
        Assert.Throws<InvalidDataException>(()=>D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest,
            prepared.Profile,Encoding.UTF8.GetBytes(missing.ToJsonString())));
    }

    [Fact]
    public void EditingReopenedMapsRefreshesNestedPaletteGuards()
    {
        var prepared = Prepare(Frame()); var row = prepared.Profile.Materials[0];
        var maps = row.PluginBindings[0];
        int pSlot = maps.TextureSlots!["p"];
        var coupled = WithPaletteEmission(maps, pSlot);
        var profile = new D3D12MaterialBindingProfile(prepared.Profile.StableId, prepared.Profile.Name,
            prepared.Profile.LayoutFingerprint, [row with { PluginBindings = [coupled] }]);
        var restored = D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
        var edited = restored.WithEditedMaps(0, p: Edited(restored, pSlot));
        var expectedGuard = D3D12MaterialBindingLayout.Capture(edited.SourceRest).CaptureTexture(pSlot);
        Assert.Equal(expectedGuard, edited.Profile.Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == pSlot));
        Assert.Equal(pSlot, edited.Profile.Materials[0].PluginBindings[0].TextureSlots!["paletteEmission"]);
        var again = D3D12GeneratedMaterialRestore.Restore(edited.SourceRest, edited.Profile, edited.ManifestBytes);
        Assert.Equal(edited.ManifestBytes, again.ManifestBytes);
    }

    [Fact]
    public void RestoreRejectsChangedOriginalImageSelectedOnlyByNestedPalette()
    {
        var frame = Frame(); var scene = frame.Surface.Snapshot;
        var prepared = Prepare(new(new(new SceneSnapshot(scene.Vertices, scene.Indices, scene.Materials,
            [..scene.Textures, new SceneTexture("artist palette", 1, 1, [20, 40, 60, 255])]), frame.Surface.Materials)));
        var row = prepared.Profile.Materials[0];
        var maps = row.PluginBindings[0];
        var paletteImage = D3D12MaterialBindingLayout.Capture(prepared.SourceRest).CaptureTexture(1);
        var profile = new D3D12MaterialBindingProfile(prepared.Profile.StableId, prepared.Profile.Name,
            prepared.Profile.LayoutFingerprint, [row with
            {
                PluginBindings = [WithPaletteEmission(maps, 1)],
                TextureGuards = [..row.TextureGuards, paletteImage]
            }]);
        _ = D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
        var source = prepared.SourceRest.Surface.Snapshot;
        var changed = new OriginalFrameScene(new(new SceneSnapshot(source.Vertices, source.Indices, source.Materials,
            source.Textures.Select((texture, index) => index == 1
                ? new SceneTexture("changed artist palette", 1, 1, [21, 40, 60, 255]) : texture)),
            prepared.SourceRest.Surface.Materials));
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedMaterialRestore.Restore(changed, profile, prepared.ManifestBytes));
    }

    private static Mmd.Modern.Plugins.Contracts.StyleMaterialBindingData WithPaletteEmission(
        Mmd.Modern.Plugins.Contracts.StyleMaterialBindingData maps, int emissionSlot)
    {
        var slots = new Dictionary<string, int>(maps.TextureSlots!, StringComparer.Ordinal) { ["paletteEmission"] = emissionSlot };
        var bools = new Dictionary<string, bool>(maps.BoolValues!, StringComparer.Ordinal)
        {
            ["coupleEndfieldPalette"] = true,
            ["hasPalette"] = true,
        };
        var numbers = new Dictionary<string, double>(maps.NumberValues!, StringComparer.Ordinal)
        {
            ["paletteEmissionStrength"] = 0.0,
            ["paletteUniformEmissionBlue"] = 0.0,
            ["paletteLightInfluence"] = 0.0,
            ["paletteDensity"] = 0.0,
        };
        var vectors = new Dictionary<string, float[]>(maps.VectorValues!, StringComparer.Ordinal)
        {
            ["paletteReceivedLightLinearRgb"] = [0f, 0f, 0f],
        };
        var strings = new Dictionary<string, string>(maps.StringValues!, StringComparer.Ordinal)
        {
            ["paletteEmissionInputMode"] = "Texture",
            ["paletteReceivedLightInputMode"] = "Uniform",
        };
        return maps with
        {
            TextureSlots = slots,
            BoolValues = bools,
            NumberValues = numbers,
            VectorValues = vectors,
            StringValues = strings,
        };
    }

    private static SceneTexture Edited(D3D12GeneratedEndfieldMaterialPreparationResult prepared, int ordinal)
    {
        var texture = prepared.SourceRest.Surface.Snapshot.Textures[ordinal];
        byte[] pixels = texture.Rgba.ToArray(); pixels[0] ^= 127;
        return new("edited generated map", texture.Width, texture.Height, pixels);
    }

    private static OriginalFrameScene Frame()
    {
        SceneVertex[] vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)];
        return new(new(new SceneSnapshot(vertices, [0, 1, 2],
            [new("explicit", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1, 0)],
            [new SceneTexture("original", 1, 1, [80, 120, 160, 255])]), [new(Vector3.One)]));
    }

    private static OriginalFrameScene Rest(PmxModel model)
    {
        var scene = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        return new(scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, Vector3.Zero, 30, 1),
            materials: PmxOriginalFrameAdapter.FromMaterials(model));
    }

    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
}
