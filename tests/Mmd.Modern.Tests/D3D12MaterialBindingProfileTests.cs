using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12MaterialBindingProfileTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "material-profile-tests", Guid.NewGuid().ToString("N"));
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static D3D12MaterialBindingProfileTexture Texture(int ordinal = 2)
        => new(ordinal, 2, 3, SceneTextureKind.Image, new string('b', 64));
    private static D3D12MaterialBindingProfile Definition(params D3D12MaterialBindingProfileRow[] rows)
        => new("custom-source", "Explicit mapping", Fingerprint, rows);
    private static JsonObject Document(D3D12MaterialBindingProfile value)
        => JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(value))!.AsObject();
    private static D3D12MaterialBindingProfile Parse(JsonObject json)
        => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()));

    [Fact]
    public void ExplicitFamiliesAndRolesRoundTripWithoutObjectIdentityOrCombinedSlots()
    {
        var profile = new D3D12MaterialBindingProfile(
            "custom-source",
            "Explicit mapping",
            Fingerprint,
            [
                new(0, D3D12CharacterMaterialRole.Body, pmxMaterialTreatment: new(.95f, .8f)),
                new(1, D3D12CharacterMaterialRole.Eye, eyeGeometricShadowStrength: .12f),
                new(2, D3D12CharacterMaterialRole.Face,
                    pluginBindings: [TestStylePlugins.FaceSdf("genshin", 1, 3, offset: .55f, transition: .001f)],
                    textureGuards: [Texture(1), Texture(3)]),
                new(3, D3D12CharacterMaterialRole.Hair,
                    pluginBindings: [TestStylePlugins.GenshinHair(4, globalSpecularScale: 1.2f, shininess: 18f, multiplier: .2f, intensity: .8f)],
                    textureGuards: [Texture(4)]),
                new(4, D3D12CharacterMaterialRole.Hair,
                    pluginBindings: [TestStylePlugins.StarMcHair("star-rail", 5, 6)],
                    textureGuards: [Texture(5), Texture(6)]),
                new(5, D3D12CharacterMaterialRole.Hair,
                    pluginBindings: [TestStylePlugins.StarMcHair("wuthering-waves", 7, 8, highlightChannel: "Alpha")],
                    textureGuards: [Texture(7), Texture(8)]),
                new(6, D3D12CharacterMaterialRole.Face,
                    pluginBindings: [TestStylePlugins.FaceSdf("endfield", 9, 10)],
                    textureGuards: [Texture(9), Texture(10)])
            ],
            pipelineId: "genshin",
            guessMaterialRoles: false);
        byte[] encoded = D3D12MaterialBindingProfileJson.Encode(profile);
        var restored = D3D12MaterialBindingProfileJson.Decode(encoded, profile.StableId);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(profile.StableId, restored.StableId);
        Assert.Equal(profile.LayoutFingerprint, restored.LayoutFingerprint);
        Assert.Equal("genshin", restored.PipelineId);
        Assert.False(restored.GuessMaterialRoles);
        Assert.Equal(profile.Materials, restored.Materials);
        Assert.Equal(encoded, D3D12MaterialBindingProfileJson.Encode(restored));
        string text = Encoding.UTF8.GetString(encoded);
        Assert.DoesNotContain("CombinedTexture", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ObjectId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixels", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CallerCollectionAndRecordReplacementCannotMutateSnapshot()
    {
        var row = new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            pluginBindings: [TestStylePlugins.GenshinHair(2)], textureGuards: [Texture()]);
        var source = new List<D3D12MaterialBindingProfileRow> { row };
        var profile = new D3D12MaterialBindingProfile("custom-detached", "Detached", Fingerprint, source);
        byte[] before = D3D12MaterialBindingProfileJson.Encode(profile);
        source[0] = row with { LocalMaterialIndex = 99 };
        source.Clear();
        Assert.Equal(0, profile.Materials[0].LocalMaterialIndex);
        Assert.Equal(before, D3D12MaterialBindingProfileJson.Encode(profile));
        Assert.Throws<NotSupportedException>(() => ((IList<D3D12MaterialBindingProfileRow>)profile.Materials).Clear());
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("unknown-row")]
    [InlineData("unknown-binding")]
    [InlineData("unknown-bone")]
    [InlineData("numeric-enum-text")]
    [InlineData("nonfinite-number")]
    [InlineData("numeric-role")]
    [InlineData("wrong-case-role")]
    [InlineData("missing-field")]
    [InlineData("missing-binding-styleId")]
    [InlineData("bad-binding-slot")]
    [InlineData("nonfinite")]
    [InlineData("duplicate-material")]
    public void StrictSchemaRejectsAmbiguousOrUnsupportedMappings(string kind)
    {
        var doc = Document(Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Face,
            pluginBindings: [TestStylePlugins.FaceSdf("star-rail", 2)], textureGuards: [Texture()])));
        var row = doc["materials"]![0]!.AsObject();
        var binding = row["pluginBindings"]![0]!.AsObject();
        switch (kind)
        {
            case "unknown-root": doc["ignored"] = 1; break;
            case "unknown-row": row["ignored"] = true; break;
            case "unknown-binding": binding["ignored"] = 7; break;
            case "unknown-bone": binding["boneBinding"]!.AsObject()["selectedBone"] = 4; break;
            case "numeric-enum-text": row["role"] = "1"; break;
            case "nonfinite-number": row["faceNormalShadeStrength"] = 1e200; break;
            case "numeric-role": row["role"] = 1; break;
            case "wrong-case-role": row["role"] = "face"; break;
            case "missing-field": row.Remove("eyeGeometricShadowStrength"); break;
            case "missing-binding-styleId": binding.Remove("styleId"); break;
            case "bad-binding-slot": binding["textureSlots"]!["primary"] = "2"; break;
            case "nonfinite": row["faceNormalShadeStrength"] = "NaN"; break;
            case "duplicate-material": doc["materials"]!.AsArray().Add(row.DeepClone()); break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(doc));
    }

    [Theory]
    [InlineData("valid-v1")]
    [InlineData("valid-v11")]
    [InlineData("valid-v13")]
    [InlineData("combined-slot")]
    [InlineData("endfield-hair-v3")]
    [InlineData("selected-bone")]
    [InlineData("selected-bone-basis")]
    [InlineData("treatment-plus-dedicated")]
    public void LegacySchemasDecodeV1ThroughV13AndRetainLegacyStrictChecks(string kind)
    {
        static JsonObject TexJson(int ordinal) => new()
        {
            ["sourceSceneTextureOrdinal"] = ordinal,
            ["width"] = 2,
            ["height"] = 3,
            ["kind"] = "Image",
            ["rgbaSha256"] = new string('b', 64)
        };
        static JsonObject LegacyRow(int schema, string role = "Face")
        {
            var row = new JsonObject
            {
                ["localMaterialIndex"] = 0,
                ["role"] = role,
                ["faceNormalShadeStrength"] = 0.35,
                ["faceGeometricShadowStrength"] = 1.0,
                ["eyeGeometricShadowStrength"] = 0.2,
                ["pmxMaterialTreatment"] = null,
                ["face"] = new JsonObject
                {
                    ["pipelineId"] = "star-rail",
                    ["primary"] = TexJson(2),
                    ["secondary"] = null,
                    ["headBasis"] = "FixedWorld",
                    ["headWorldForward"] = new JsonArray(0, 0, 1),
                    ["headWorldRight"] = new JsonArray(1, 0, 0),
                    ["transition"] = 0.01,
                    ["offset"] = 0.5,
                    ["encoding"] = "GrayRed"
                },
                ["genshinHair"] = null,
                ["starMcHair"] = null
            };
            if (schema >= 2) row["endfieldRim"] = null;
            if (schema >= 3) row["genshinHairAtlas"] = null;
            if (schema >= 4) row["endfieldHair"] = null;
            if (schema >= 5) row["endfieldMatcap"] = null;
            if (schema >= 6) row["genshinMetallic"] = null;
            if (schema >= 12) row["starMcBody"] = null;
            return row;
        }
        static JsonObject LegacyDoc(int schema, JsonObject row)
        {
            var doc = new JsonObject
            {
                ["format"] = "mda-d3d12-material-binding-profile",
                ["schemaVersion"] = schema,
                ["stableId"] = "legacy-profile",
                ["name"] = "Legacy",
                ["layoutVersion"] = 1,
                ["layoutFingerprint"] = Fingerprint,
                ["materials"] = new JsonArray(row)
            };
            if (schema >= 13)
                doc["outlineWidths"] = new JsonArray(new JsonObject { ["localMaterialIndex"] = 0, ["scale"] = 0.5 });
            return doc;
        }

        if (kind is "valid-v1" or "valid-v11" or "valid-v13")
        {
            int schema = kind == "valid-v1" ? 1 : kind == "valid-v11" ? 11 : 13;
            var decoded = Parse(LegacyDoc(schema, LegacyRow(schema)));
            Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, decoded.SchemaVersion);
            var decodedRow = Assert.Single(decoded.Materials);
            var face = Assert.Single(decodedRow.PluginBindings);
            Assert.Equal("star-rail", face.StyleId);
            Assert.Equal("face-sdf", face.ModeId);
            Assert.Equal(2, face.TextureSlots!["primary"]);
            Assert.Equal(Texture(2), Assert.Single(decodedRow.TextureGuards));
            if (schema == 13)
                Assert.Equal(0.5f, Assert.Single(decoded.OutlineWidths).Scale);
            else
                Assert.Empty(decoded.OutlineWidths);
            return;
        }

        int badSchema = kind == "endfield-hair-v3" ? 3 : 11;
        var badRow = LegacyRow(badSchema);
        switch (kind)
        {
            case "combined-slot": badRow["face"]!["primary"]!["combinedTextureIndex"] = 7; break;
            case "endfield-hair-v3": badRow["endfieldHair"] = new JsonObject(); break;
            case "selected-bone": badRow["face"]!["selectedBone"] = 4; break;
            case "selected-bone-basis": badRow["face"]!["headBasis"] = "SelectedBone"; break;
            case "treatment-plus-dedicated":
                badRow["pmxMaterialTreatment"] = new JsonObject { ["saturation"] = 1.0, ["transitionWidthScale"] = 1.0 };
                break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(LegacyDoc(badSchema, badRow)));
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-nested")]
    [InlineData("comments")]
    [InlineData("trailing-comma")]
    [InlineData("invalid-utf8")]
    [InlineData("deep")]
    [InlineData("over-budget")]
    public void ParserRejectsMalformedAndUnboundedInput(string kind)
    {
        string json = Encoding.UTF8.GetString(D3D12MaterialBindingProfileJson.Encode(Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Body))));
        byte[] bytes = kind switch
        {
            "duplicate-root" => Encoding.UTF8.GetBytes(json.Insert(json.IndexOf('{') + 1, "\"schemaVersion\":1,")),
            "duplicate-nested" => Encoding.UTF8.GetBytes(json.Replace("\"localMaterialIndex\": 0", "\"localMaterialIndex\": 0,\"localMaterialIndex\":0", StringComparison.Ordinal)),
            "comments" => Encoding.UTF8.GetBytes("/*comment*/" + json),
            "trailing-comma" => Encoding.UTF8.GetBytes(json.Insert(json.LastIndexOf('}'), ",")),
            "invalid-utf8" => [0xff, 0xfe],
            "deep" => Encoding.UTF8.GetBytes(new string('[', 40) + new string(']', 40)),
            _ => new byte[D3D12MaterialBindingProfileJson.MaxProfileBytes + 1],
        };
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(bytes));
    }

    [Fact]
    public void FutureSchemaAndFilenameIdentityAreExplicitFailures()
    {
        var doc = Document(Definition());
        doc["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => Parse(doc));
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(D3D12MaterialBindingProfileJson.Encode(Definition()), "CUSTOM-SOURCE"));
    }

    [Fact]
    public void DomainValidationRejectsConflictingRolesFamiliesAndUnsafeSelectors()
    {
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Body, pmxMaterialTreatment: new(HairHighlightStrength: .2f))));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Face, pmxMaterialTreatment: new())));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(new D3D12MaterialBindingProfileRow(-1, D3D12CharacterMaterialRole.Body)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            textureGuards: [Texture(-1)])));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            textureGuards: [Texture(2), Texture(2) with { Width = 4 }])));
        Assert.Throws<ArgumentException>(() => new D3D12MaterialBindingProfile("../unsafe", "Name", Fingerprint, []));
        Assert.Throws<ArgumentException>(() => new D3D12MaterialBindingProfile("custom-id", "Name", "not-a-hash", []));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            textureGuards: [Texture() with { RgbaSha256 = new string('x', 64) }])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Body, faceNormalShadeStrength: float.NaN)));

        var source = FixtureScene();
        var layout = D3D12MaterialBindingLayout.Capture(source);
        Guid actor = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            new("bad-body", "Body", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Body,
                pluginBindings: [TestStylePlugins.GenshinHair(1)], textureGuards: [layout.CaptureTexture(1)])]),
            source, actor, "genshin", TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            new("bad-genshin-face", "Face", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Face,
                pluginBindings: [TestStylePlugins.FaceSdf("genshin", 1)], textureGuards: [layout.CaptureTexture(1)])]),
            source, actor, "genshin", TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            new("bad-sr-channel", "Hair", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Hair,
                pluginBindings: [TestStylePlugins.StarMcHair("star-rail", 1, 2, highlightChannel: "Alpha")], textureGuards: [layout.CaptureTexture(1), layout.CaptureTexture(2)])]),
            source, actor, "star-rail", TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            new("bad-endfield", "Hair", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Hair,
                pluginBindings: [TestStylePlugins.StarMcHair("endfield", 1, 2)], textureGuards: [layout.CaptureTexture(1), layout.CaptureTexture(2)])]),
            source, actor, "endfield", TestStylePlugins.AllViewCatalog));
    }

    private static OriginalFrameScene FixtureScene()
    {
        SceneVertex[] vertices =
        [
            new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX),
            new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)
        ];
        var textures = Enumerable.Range(0, 4).Select(i => new SceneTexture($"tex-{i}", 1, 1, [255, 255, 255, 255])).ToArray();
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2],
            [new("mat", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], textures);
        return new(new(snapshot, [new(Vector3.One)]));
    }

    [Fact]
    public void MaterialEnumerationStopsOneBeyondBound()
    {
        int yielded = 0;
        IEnumerable<D3D12MaterialBindingProfileRow> Rows()
        {
            while (true) { yielded++; yield return new(yielded, D3D12CharacterMaterialRole.Body); }
        }
        Assert.Throws<ArgumentException>(() => new D3D12MaterialBindingProfile("custom-big", "Big", Fingerprint, Rows()));
        Assert.Equal(D3D12MaterialBindingProfile.MaxMaterials + 1, yielded);
    }

    [Fact]
    public void SaveAsNewCreatesDistinctAtomicFilesAndReloadedSnapshots()
    {
        var source = Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Eye));
        var store = new D3D12MaterialBindingProfileStore(directory);
        Assert.Empty(store.Load().List());
        var first = store.SaveAsNew(source, "Eyes");
        byte[] firstBytes = File.ReadAllBytes(Path.Combine(directory, first.StableId + ".json"));
        var second = store.SaveAsNew(first, "Eyes again");
        Assert.NotEqual(first.StableId, second.StableId);
        Assert.NotEqual(source.StableId, first.StableId);
        Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(directory, first.StableId + ".json")));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        var catalog = new D3D12MaterialBindingProfileStore(directory).Load();
        Assert.Equal(2, catalog.List().Count);
        Assert.Equal(source.Materials, catalog.Find(first.StableId).Profile!.Materials);
        Assert.Equal(D3D12MaterialBindingProfileStatus.Missing, catalog.Find("CUSTOM-ABSENT").Status);
        File.WriteAllText(Path.Combine(directory, first.StableId + ".json"), "{}");
        Assert.True(catalog.Find(first.StableId).IsAvailable);
        Assert.Equal(D3D12MaterialBindingProfileStatus.InvalidData, store.Load().Find(first.StableId).Status);
    }

    [Fact]
    public void LibraryPreservesPerFileFailuresAndFutureSchema()
    {
        Directory.CreateDirectory(directory);
        var doc = Document(Definition());
        doc["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        File.WriteAllText(Path.Combine(directory, "custom-source.json"), doc.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{}");
        File.WriteAllBytes(Path.Combine(directory, "too-large.json"), new byte[D3D12MaterialBindingProfileJson.MaxProfileBytes + 1]);
        var catalog = new D3D12MaterialBindingProfileStore(directory).Load();
        Assert.Equal(D3D12MaterialBindingProfileStatus.IncompatibleSchema, catalog.Find("custom-source").Status);
        Assert.Equal(D3D12MaterialBindingProfileStatus.InvalidData, catalog.Find("broken").Status);
        Assert.Equal(D3D12MaterialBindingProfileStatus.InvalidData, catalog.Find("too-large").Status);
        Assert.Equal(3, catalog.List().Count);
    }

    [Fact]
    public void CapacityStopsSaveAndLoadWithoutPartialLibrary()
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < D3D12MaterialBindingProfileStore.MaxLocalProfiles; i++)
            File.WriteAllText(Path.Combine(directory, $"custom-{i}.json"), "{}");
        var store = new D3D12MaterialBindingProfileStore(directory);
        Assert.Throws<InvalidOperationException>(() => store.SaveAsNew(Definition(), "Over capacity"));
        File.WriteAllText(Path.Combine(directory, "one-extra.json"), "{}");
        Assert.Equal(D3D12MaterialBindingProfileStatus.CapacityExceeded, Assert.Single(store.Load().List()).Status);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void InvalidSaveDoesNotCreateDirectory()
    {
        var store = new D3D12MaterialBindingProfileStore(directory);
        Assert.Throws<ArgumentException>(() => store.SaveAsNew(Definition(), "\n"));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void EncodedByteBudgetIsCheckedBeforeAnySaveSideEffect()
    {
        var many = Definition(Enumerable.Range(0, D3D12MaterialBindingProfile.MaxMaterials)
            .Select(i => new D3D12MaterialBindingProfileRow(i, D3D12CharacterMaterialRole.Body)).ToArray());
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Encode(many));
        Assert.Throws<InvalidDataException>(() => new D3D12MaterialBindingProfileStore(directory).SaveAsNew(many, "Too large"));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void LibraryFilePathProducesExplicitIoFailure()
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(file, "file");
        var store = new D3D12MaterialBindingProfileStore(file);
        Assert.Equal(D3D12MaterialBindingProfileStatus.IoFailure, Assert.Single(store.Load().List()).Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
