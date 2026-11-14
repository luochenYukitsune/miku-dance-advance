using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
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
        var profile = Definition(
            new(0, D3D12CharacterMaterialRole.Body, PmxMaterialTreatment: new(.95f, .8f)),
            new(1, D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: .12f),
            new(2, D3D12CharacterMaterialRole.Face, Face: new("genshin", Texture(1), Texture(3), Vector3.UnitZ, Vector3.UnitX, .001f, .55f, D3D12FaceSdfEncoding.GenshinSoftFace)),
            new(3, D3D12CharacterMaterialRole.Hair, GenshinHair: new(Texture(4), 1.2f, 18f, .2f, .8f)),
            new(4, D3D12CharacterMaterialRole.Hair, StarMcHair: new("star-rail", Texture(5), Texture(6))),
            new(5, D3D12CharacterMaterialRole.Hair, StarMcHair: new("wuthering-waves", Texture(7), Texture(8), D3D12HairHighlightChannel.Alpha)),
            new(6, D3D12CharacterMaterialRole.Face, Face: new("endfield", Texture(9), Texture(10), Vector3.UnitZ, Vector3.UnitX, Encoding: D3D12FaceSdfEncoding.EndfieldRgbCmM)));
        byte[] encoded = D3D12MaterialBindingProfileJson.Encode(profile);
        var restored = D3D12MaterialBindingProfileJson.Decode(encoded, profile.StableId);
        Assert.Equal(profile.StableId, restored.StableId);
        Assert.Equal(profile.LayoutFingerprint, restored.LayoutFingerprint);
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
        var row = new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair, GenshinHair: new(Texture()));
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
    [InlineData("combined-slot")]
    [InlineData("endfield-hair")]
    [InlineData("selected-bone")]
    [InlineData("selected-bone-basis")]
    [InlineData("numeric-enum-text")]
    [InlineData("nonfinite-number")]
    [InlineData("numeric-role")]
    [InlineData("wrong-case-role")]
    [InlineData("missing-field")]
    [InlineData("nonfinite")]
    [InlineData("duplicate-material")]
    public void StrictSchemaRejectsAmbiguousOrUnsupportedMappings(string kind)
    {
        var doc = Document(Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Face,
            Face: new("star-rail", Texture(), null, Vector3.UnitZ, Vector3.UnitX))));
        var row = doc["materials"]![0]!.AsObject();
        switch (kind)
        {
            case "unknown-root": doc["ignored"] = 1; break;
            case "unknown-row": row["ignored"] = true; break;
            case "combined-slot": row["face"]!["primary"]!["combinedTextureIndex"] = 7; break;
            case "endfield-hair": row["endfieldHair"] = new JsonObject(); break;
            case "selected-bone": row["face"]!["selectedBone"] = 4; break;
            case "selected-bone-basis": row["face"]!["headBasis"] = "SelectedBone"; break;
            case "numeric-enum-text": row["role"] = "1"; break;
            case "nonfinite-number": row["faceNormalShadeStrength"] = 1e200; break;
            case "numeric-role": row["role"] = 1; break;
            case "wrong-case-role": row["role"] = "face"; break;
            case "missing-field": row.Remove("eyeGeometricShadowStrength"); break;
            case "nonfinite": row["faceNormalShadeStrength"] = "NaN"; break;
            case "duplicate-material": doc["materials"]!.AsArray().Add(row.DeepClone()); break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(doc));
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
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Body, GenshinHair: new(Texture()))));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair, GenshinHair: new(Texture()), PmxMaterialTreatment: new())));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Face,
            Face: new("genshin", Texture(), null, Vector3.UnitZ, Vector3.UnitX))));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            StarMcHair: new("star-rail", Texture(), Texture(3), D3D12HairHighlightChannel.Alpha))));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            StarMcHair: new("endfield", Texture(), Texture(3)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(new D3D12MaterialBindingProfileRow(-1, D3D12CharacterMaterialRole.Body)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            GenshinHair: new(Texture(-1)))));
        Assert.Throws<ArgumentException>(() => new D3D12MaterialBindingProfile("../unsafe", "Name", Fingerprint, []));
        Assert.Throws<ArgumentException>(() => new D3D12MaterialBindingProfile("custom-id", "Name", "not-a-hash", []));
        Assert.Throws<ArgumentException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
            GenshinHair: new(Texture() with { RgbaSha256 = new string('x', 64) }))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Body, FaceNormalShadeStrength: float.NaN)));
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
