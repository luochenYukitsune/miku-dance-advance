using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldHairPmaSeedProfileTests
{
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, bool rim = false)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        return new("endfield-seed", "Explicit radial hair", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair,
                EndfieldRim: rim ? new(new(.1f), new(.2f)) : null,
                EndfieldHair: new(layout.CaptureTexture(0), new(.3f, .4f, 2), -1.5f))]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewHairRoundTripsInSchemaFourAndCanCoexistWithIndependentRim(bool rim)
    {
        var source = D3D12EndfieldHairBindingTests.Frame(); var profile = Profile(source, rim);
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(4, profile.SchemaVersion);
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
        Assert.Equal(profile.Materials[0].EndfieldHair, restored.Materials[0].EndfieldHair);
        Assert.Equal(profile.Materials[0].EndfieldRim, restored.Materials[0].EndfieldRim);
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(restored, source, Guid.NewGuid(), "endfield");
        Assert.Equal(-1.5f, Assert.Single(resolved.EndfieldHairBindings).Brightness);
        Assert.Equal(profile.Materials[0].EndfieldRim, Assert.Single(resolved.CharacterMaterials).Style.EndfieldRim);
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void DormantSelectionKeepsStrictGreenAndAuthoredSeedGuards(string pipeline)
    {
        var source = D3D12EndfieldHairBindingTests.Frame(); var id = Guid.NewGuid();
        var profile = Profile(source, true);
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline);
        Assert.Empty(resolved.EndfieldHairBindings); Assert.Null(Assert.Single(resolved.CharacterMaterials).Style.EndfieldRim);
        var changedImage = D3D12EndfieldHairBindingTests.Frame(D3D12EndfieldHairBindingTests.Texture(g: 0));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, changedImage, id, pipeline));
        var plain = D3D12EndfieldHairBindingTests.Frame(seeds: false);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(Profile(plain), plain, id, pipeline));
    }

    [Fact]
    public void RoleAndCrossFamilyConflictsRejectButLegacySchemaFieldsStayAbsent()
    {
        var source = D3D12EndfieldHairBindingTests.Frame(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var row = Profile(source).Materials[0];
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => (row with { Role = role }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { GenshinHair = new(layout.CaptureTexture(0)) }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { StarMcHair = new("star-rail", layout.CaptureTexture(0), layout.CaptureTexture(0)) }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { Face = new("star-rail", layout.CaptureTexture(0), null, Vector3.UnitZ, Vector3.UnitX) }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { PmxMaterialTreatment = new() }).Validate());
        for (int schema = 1; schema <= 3; schema++)
        {
            var oldRow = row with { EndfieldHair = null,
                EndfieldRim = schema == 2 ? new(new(.1f), new(.2f)) : null,
                GenshinHairAtlas = schema == 3 ? new(layout.CaptureTexture(0), layout.CaptureTexture(0),
                    D3D12GenshinAtlasColorEncoding.LinearRgb, 1, 0, 1, .5f, 1, 1, 0, 0) : null };
            var old = new D3D12MaterialBindingProfile("legacy", "Legacy", layout.Fingerprint, [oldRow]);
            byte[] bytes = D3D12MaterialBindingProfileJson.Encode(old);
            Assert.Equal(schema, old.SchemaVersion);
            Assert.False(JsonNode.Parse(bytes)!["materials"]![0]!.AsObject().ContainsKey("endfieldHair"));
            Assert.Equal(LegacyBytes(old), bytes);
            Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        }
    }

    // Independent legacy wire shape: schema 1/2/3 have no new field, including null placeholders.
    private static byte[] LegacyBytes(D3D12MaterialBindingProfile profile)
    {
        var row = profile.Materials[0];
        var material = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Hair", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null
        };
        if (profile.SchemaVersion >= 2)
            material["endfieldRim"] = row.EndfieldRim is null ? null : new JsonObject
            {
                ["baseColor"] = new JsonArray(.1f, .1f, .1f), ["rimColor"] = new JsonArray(.2f, .2f, .2f),
                ["edgeSize"] = .2f, ["edgeStrength"] = 0f, ["directionRadians"] = 5.5f,
                ["rangeStrength"] = 1f, ["radialStrength"] = 0f
            };
        if (profile.SchemaVersion == 3)
        {
            var texture = row.GenshinHairAtlas!.Lightmap;
            JsonObject Image() => new()
            {
                ["sourceSceneTextureOrdinal"] = texture.SourceSceneTextureOrdinal, ["width"] = texture.Width,
                ["height"] = texture.Height, ["kind"] = texture.Kind.ToString(), ["rgbaSha256"] = texture.RgbaSha256
            };
            material["genshinHairAtlas"] = new JsonObject
            {
                ["lightmap"] = Image(), ["atlas"] = Image(), ["atlasEncoding"] = "LinearRgb", ["explicitRowId"] = 1f,
                ["dayNight"] = 0f, ["shadowPosition"] = 1f, ["shadowRampWidth"] = .5f,
                ["vertexWidth"] = 1f, ["vertexAo"] = 1f, ["useLightmapAo"] = 0f, ["useVertexColorAo"] = 0f
            };
        }
        var root = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = profile.SchemaVersion,
            ["stableId"] = "legacy", ["name"] = "Legacy", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = profile.LayoutFingerprint, ["materials"] = new JsonArray(material)
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("schema3")]
    [InlineData("palette")]
    [InlineData("brightness")]
    [InlineData("nonfinite")]
    [InlineData("duplicate")]
    public void SchemaFourIsStrict(string damage)
    {
        var doc = JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(Profile(D3D12EndfieldHairBindingTests.Frame())))!;
        var hair = doc["materials"]![0]!["endfieldHair"]!.AsObject();
        switch (damage)
        {
            case "unknown": hair["guess"] = 1; break;
            case "missing": hair.Remove("texture"); break;
            case "schema3": doc["schemaVersion"] = 3; break;
            case "palette": hair["paletteColor"] = new JsonArray(0, -1, 0); break;
            case "brightness": hair["brightness"] = 10.01f; break;
        }
        string json = doc.ToJsonString();
        if (damage == "nonfinite") json = json.Replace("\"brightness\":-1.5", "\"brightness\":1e100", StringComparison.Ordinal);
        if (damage == "duplicate") json = json.Replace("\"brightness\":-1.5", "\"brightness\":-1.5,\"brightness\":0", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(json)));
    }
}
