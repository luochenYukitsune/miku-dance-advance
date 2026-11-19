using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldHairPmaSeedProfileTests
{
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, bool rim = false)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var hair = T8dTestHelpers.EndfieldHair(0, new(.3f, .4f, 2), -1.5f);
        StyleMaterialBindingData[] bindings = rim
            ? [T8dTestHelpers.EndfieldRim(new(.1f), new(.2f)), hair]
            : [hair];
        return new("endfield-seed", "Explicit radial hair", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair, pluginBindings: bindings, textureGuards: [layout.CaptureTexture(0)])]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewHairRoundTripsInSchemaFourteenAndCanCoexistWithIndependentRim(bool rim)
    {
        var source = D3D12EndfieldHairBindingTests.Frame(); var profile = Profile(source, rim);
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
        Assert.Equal(profile.Materials[0], restored.Materials[0]);
        Assert.Equal(profile.Materials[0].TextureGuards, restored.Materials[0].TextureGuards);
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(restored, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog);
        var hairBinding = Assert.Single(resolved.PluginBindings, b => b.Binding.ModeId == "hair");
        Assert.Equal(-1.5, hairBinding.Binding.NumberValues!["brightness"]);
        if (rim)
            Assert.Single(resolved.PluginBindings, b => b.Binding.ModeId == "rim");
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void DormantSelectionKeepsStrictGreenAndLayoutSeedGuards(string pipeline)
    {
        var source = D3D12EndfieldHairBindingTests.Frame(); var id = Guid.NewGuid();
        var profile = Profile(source, true);
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog);
        Assert.Empty(resolved.PluginBindings);
        var changedImage = D3D12EndfieldHairBindingTests.Frame(D3D12EndfieldHairBindingTests.Texture(g: 0));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, changedImage, id, pipeline, TestStylePlugins.AllViewCatalog));
        var plain = D3D12EndfieldHairBindingTests.Frame(seeds: false);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, plain, id, pipeline, TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void RoleAndCrossModeConflictsRejectAndLegacySchemaOneToFourDecodeCleanly()
    {
        var source = D3D12EndfieldHairBindingTests.Frame(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var profile = Profile(source);
        var hair = Assert.Single(profile.Materials[0].PluginBindings);
        foreach (var contractRole in new[] { StyleMaterialRole.Body, StyleMaterialRole.Face, StyleMaterialRole.Eye })
            Assert.False(T8dTestHelpers.ValidateEndfield(hair, contractRole).IsValid);
        foreach (var d3dRole in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
        {
            var badProfile = new D3D12MaterialBindingProfile("bad-role", "Bad role", layout.Fingerprint,
                [profile.Materials[0] with { Role = d3dRole }]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                badProfile, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
        }

        Assert.False(T8dTestHelpers.ValidateEndfield([hair, T8dTestHelpers.EndfieldFaceSdf()], StyleMaterialRole.Hair).IsValid);

        var texture = layout.CaptureTexture(0);
        for (int schema = 1; schema <= 4; schema++)
        {
            byte[] legacy = LegacyBytes(layout.Fingerprint, schema, texture);
            var decoded = D3D12MaterialBindingProfileJson.Decode(legacy);
            if (schema == 1)
                Assert.Empty(decoded.Materials[0].PluginBindings);
            else if (schema == 2)
                Assert.Equal("rim", Assert.Single(decoded.Materials[0].PluginBindings).ModeId);
            else if (schema == 3)
                Assert.Equal("hair-atlas", Assert.Single(decoded.Materials[0].PluginBindings).ModeId);
            else
            {
                var h = Assert.Single(decoded.Materials[0].PluginBindings);
                Assert.Equal("endfield", h.StyleId);
                Assert.Equal("hair", h.ModeId);
                Assert.Equal(-1.5, h.NumberValues!["brightness"]);
            }
            byte[] reencoded = D3D12MaterialBindingProfileJson.Encode(decoded);
            Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, JsonNode.Parse(reencoded)!["schemaVersion"]!.GetValue<int>());
            Assert.Equal(reencoded, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(reencoded)));
        }
    }

    private static byte[] LegacyBytes(string fingerprint, int schema, D3D12MaterialBindingProfileTexture texture)
    {
        var material = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Hair", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null
        };
        JsonObject Image() => new()
        {
            ["sourceSceneTextureOrdinal"] = texture.SourceSceneTextureOrdinal, ["width"] = texture.Width,
            ["height"] = texture.Height, ["kind"] = texture.Kind.ToString(), ["rgbaSha256"] = texture.RgbaSha256
        };
        if (schema >= 2)
            material["endfieldRim"] = schema == 2 ? new JsonObject
            {
                ["baseColor"] = new JsonArray(.1f, .1f, .1f), ["rimColor"] = new JsonArray(.2f, .2f, .2f),
                ["edgeSize"] = .2f, ["edgeStrength"] = 0f, ["directionRadians"] = 5.5f,
                ["rangeStrength"] = 1f, ["radialStrength"] = 0f
            } : null;
        if (schema >= 3)
            material["genshinHairAtlas"] = schema == 3 ? new JsonObject
            {
                ["lightmap"] = Image(), ["atlas"] = Image(), ["atlasEncoding"] = "LinearRgb", ["explicitRowId"] = 1f,
                ["dayNight"] = 0f, ["shadowPosition"] = 1f, ["shadowRampWidth"] = .5f,
                ["vertexWidth"] = 1f, ["vertexAo"] = 1f, ["useLightmapAo"] = 0f, ["useVertexColorAo"] = 0f
            } : null;
        if (schema == 4)
            material["endfieldHair"] = new JsonObject
            {
                ["texture"] = Image(),
                ["paletteColor"] = new JsonArray(.3f, .4f, 2f),
                ["brightness"] = -1.5f
            };
        var root = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = schema,
            ["stableId"] = "legacy", ["name"] = "Legacy", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = fingerprint, ["materials"] = new JsonArray(material)
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
    public void SchemaFourteenAndLegacySchemaFourAreStrict(string damage)
    {
        var source = D3D12EndfieldHairBindingTests.Frame();
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var doc = JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(Profile(source)))!;
        var hair = doc["materials"]![0]!["pluginBindings"]![0]!.AsObject();
        switch (damage)
        {
            case "unknown": hair["guess"] = 1; break;
            case "missing": hair["textureSlots"]!.AsObject().Remove("texture"); break;
            case "schema3": doc["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1; break;
            case "palette": hair["vectorValues"]!["paletteColor"] = new JsonArray(0, -1, 0); break;
            case "brightness": hair["numberValues"]!["brightness"] = 10.01f; break;
        }
        string json = doc.ToJsonString();
        if (damage == "nonfinite") json = json.Replace("\"brightness\":-1.5", "\"brightness\":1e100", StringComparison.Ordinal);
        if (damage == "duplicate") json = json.Replace("\"brightness\":-1.5", "\"brightness\":-1.5,\"brightness\":0", StringComparison.Ordinal);
        if (damage is "unknown" or "nonfinite" or "duplicate")
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(json)));
        else if (damage == "schema3")
            Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(json)));
        else
        {
            var decoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(json));
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                decoded, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
        }

        // Legacy schema 4 checks remain strict too.
        var legacyNode = JsonNode.Parse(LegacyBytes(layout.Fingerprint, 4, layout.CaptureTexture(0)))!;
        var legacyHair = legacyNode["materials"]![0]!["endfieldHair"]!.AsObject();
        switch (damage)
        {
            case "unknown": legacyHair["guess"] = 1; break;
            case "missing": legacyHair.Remove("texture"); break;
            case "schema3": legacyNode["schemaVersion"] = 3; break;
            case "palette": legacyHair["paletteColor"] = new JsonArray(0, -1, 0); break;
            case "brightness": legacyHair["brightness"] = 10.01f; break;
        }
        string legacyJson = legacyNode.ToJsonString();
        if (damage == "nonfinite") legacyJson = legacyJson.Replace("\"brightness\":-1.5", "\"brightness\":1e100", StringComparison.Ordinal);
        if (damage == "duplicate") legacyJson = legacyJson.Replace("\"brightness\":-1.5", "\"brightness\":-1.5,\"brightness\":0", StringComparison.Ordinal);
        if (damage == "brightness")
        {
            var decodedLegacy = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyJson));
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                decodedLegacy, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
        }
        else
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyJson)));
    }
}
