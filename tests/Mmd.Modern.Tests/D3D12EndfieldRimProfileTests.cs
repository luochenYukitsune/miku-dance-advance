using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12EndfieldRimProfileTests
{
    private static EndfieldRimSpec Rim => new(new(.2f,.3f,.4f), new(.7f,.8f,.9f),
        EdgeSize: 1.3f, EdgeStrength: .15f, DirectionRadians: 2.4f, RangeStrength: .7f, RadialStrength: -.1f);
    private static OriginalFrameScene Source() => D3D12EndfieldHairBindingTests.Frame(seeds: false);
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, EndfieldRimSpec? rim)
        => new("rim-map", "Rim mapping", D3D12MaterialBindingLayout.Capture(source).Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair, pluginBindings: rim is null ? null : [rim.ToBinding()])]);

    [Fact]
    public void SchemaFourteenRoundTripsRimAndLegacySchemaTwoDecodesToPluginRim()
    {
        var source = Source();
        byte[] withoutRim = D3D12MaterialBindingProfileJson.Encode(Profile(source, null));
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, JsonNode.Parse(withoutRim)!["schemaVersion"]!.GetValue<int>());
        Assert.Empty(JsonNode.Parse(withoutRim)!["materials"]![0]!["pluginBindings"]!.AsArray());
        Assert.Equal(withoutRim, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(withoutRim)));

        byte[] encoded = D3D12MaterialBindingProfileJson.Encode(Profile(source, Rim));
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, JsonNode.Parse(encoded)!["schemaVersion"]!.GetValue<int>());
        var restored = D3D12MaterialBindingProfileJson.Decode(encoded);
        var binding = Assert.Single(restored.Materials[0].PluginBindings);
        Assert.Equal("endfield", binding.StyleId);
        Assert.Equal("rim", binding.ModeId);
        Assert.Equal(encoded, D3D12MaterialBindingProfileJson.Encode(restored));

        // Legacy schema 2 JSON decodes into the exact same plugin binding payload.
        byte[] legacy = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile",
            ["schemaVersion"] = 2,
            ["stableId"] = "rim-map",
            ["name"] = "Rim mapping",
            ["layoutVersion"] = 1,
            ["layoutFingerprint"] = D3D12MaterialBindingLayout.Capture(source).Fingerprint,
            ["materials"] = new JsonArray(new JsonObject
            {
                ["localMaterialIndex"] = 0, ["role"] = "Hair", ["faceNormalShadeStrength"] = .35f,
                ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
                ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
                ["endfieldRim"] = new JsonObject
                {
                    ["baseColor"] = new JsonArray(.2f, .3f, .4f), ["rimColor"] = new JsonArray(.7f, .8f, .9f),
                    ["edgeSize"] = 1.3f, ["edgeStrength"] = .15f, ["directionRadians"] = 2.4f,
                    ["rangeStrength"] = .7f, ["radialStrength"] = -.1f
                }
            })
        }.ToJsonString());
        var legacyDecoded = D3D12MaterialBindingProfileJson.Decode(legacy);
        Assert.Equal(encoded, D3D12MaterialBindingProfileJson.Encode(legacyDecoded));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void RestoredRimNeedsNoTextureOrSeedAndOnlyActivatesExplicitEndfield(string pipeline)
    {
        var source = Source(); var id = Guid.NewGuid();
        var restored = D3D12MaterialBindingProfileResolver.Resolve(Profile(source, Rim), source, id, pipeline, TestStylePlugins.AllViewCatalog);
        var binding = Assert.Single(restored.CharacterMaterials);
        Assert.Equal(id, binding.ObjectId);
        if (pipeline == "endfield")
            Assert.Single(restored.PluginBindings);
        else
            Assert.Empty(restored.PluginBindings);

        var objects = new[] { new SceneRenderingStyleBinding(id, new(pipeline), ModelRole.Character, 0, 1, true) };
        var result = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, objects,
            restored.CharacterMaterials, restored.PluginBindings, styleCatalog: TestStylePlugins.AllViewCatalog);
        var draw = result.SurfaceStyles.CharacterMaterials[0].PluginDraw;
        if (pipeline == "endfield")
        {
            Assert.NotNull(draw);
            Assert.Equal("rim", draw.ShaderModuleId);
            var expectedDraw = T8dTestHelpers.BuildEndfieldDrawData([Rim.ToBinding()], StyleMaterialRole.Hair, source)!;
            Assert.Equal(expectedDraw.ConstantBufferData.ToArray(), draw.ConstantBufferData.ToArray());
        }
        else
        {
            Assert.Null(draw);
            if (pipeline != "mmd")
                Assert.Equal("default", result.SurfaceStyles.ResolveShaderModule(0)?.ModuleId);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("v1")]
    [InlineData("vector")]
    [InlineData("range")]
    public void LegacySchemaTwoAndCurrentSchemaAreStrictAndNeverSilentlyDropRimData(string damage)
    {
        var source = Source();
        var v14Doc = JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(Profile(source, Rim)))!;
        switch (damage)
        {
            case "unknown": v14Doc["materials"]![0]!["pluginBindings"]![0]!["guessedIntensity"] = 1; break;
            case "missing": v14Doc["materials"]![0]!["pluginBindings"]![0]!.AsObject().Remove("modeId"); break;
            case "v1": v14Doc["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1; break;
            case "vector": v14Doc["materials"]![0]!["pluginBindings"]![0]!["vectorValues"]!["baseColor"] = new JsonArray(1, 2); break;
            case "range": v14Doc["materials"]![0]!["pluginBindings"]![0]!["numberValues"]!["edgeStrength"] = 100000; break;
        }
        if (damage == "v1")
            Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(v14Doc.ToJsonString())));
        else if (damage is "unknown" or "missing")
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(v14Doc.ToJsonString())));
        else
        {
            var decoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(v14Doc.ToJsonString()));
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                decoded, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
        }

        // Legacy schema 2 structural checks remain strict too.
        var legacyDoc = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 2,
            ["stableId"] = "rim-map", ["name"] = "Rim", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = D3D12MaterialBindingLayout.Capture(source).Fingerprint,
            ["materials"] = new JsonArray(new JsonObject
            {
                ["localMaterialIndex"] = 0, ["role"] = "Hair", ["faceNormalShadeStrength"] = .35f,
                ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
                ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
                ["endfieldRim"] = new JsonObject
                {
                    ["baseColor"] = new JsonArray(.2f, .3f, .4f), ["rimColor"] = new JsonArray(.7f, .8f, .9f),
                    ["edgeSize"] = 1.3f, ["edgeStrength"] = .15f, ["directionRadians"] = 2.4f,
                    ["rangeStrength"] = .7f, ["radialStrength"] = -.1f
                }
            })
        };
        var legacyRim = legacyDoc["materials"]![0]!["endfieldRim"]!.AsObject();
        switch (damage)
        {
            case "unknown": legacyRim["guessedIntensity"] = 1; break;
            case "missing": legacyRim.Remove("rangeStrength"); break;
            case "v1": legacyDoc["schemaVersion"] = 1; break;
            case "vector": legacyRim["baseColor"] = new JsonArray(1, 2); break;
            case "range": legacyRim["edgeStrength"] = 100000; break;
        }
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyDoc.ToJsonString())));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    public void ActiveComposedPayloadCannotBypassSelectedPipelinePreflight(string pipeline)
    {
        var source = Source(); var id = Guid.NewGuid();
        var rimDraw = D3D12StylePluginAdapter.ToD3D12DrawData(
            T8dTestHelpers.BuildEndfieldDrawData([Rim.ToBinding()], StyleMaterialRole.Hair, source)!);
        var invalidMap = new D3D12SurfaceStyleMap(
            [pipeline == "mmd" ? D3D12SurfaceStyleMap.DefaultStyleId : pipeline],
            [new D3D12CharacterMaterialStyle(Role: D3D12CharacterMaterialRole.Hair, PluginDraw: rimDraw)],
            TestStylePlugins.AllPresetCatalog);
        Assert.ThrowsAny<Exception>(() => invalidMap.Validate(source));

        if (pipeline != "mmd")
        {
            var objects = new[] { new SceneRenderingStyleBinding(id, new(pipeline), ModelRole.Character, 0, 1, true) };
            D3D12ViewPluginMaterialBinding[] bogus = [TestStylePlugins.Bind(id, 0, Rim.ToBinding() with { StyleId = pipeline })];
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(
                source, TestStylePlugins.AllPresetCatalog, objects, [new(id, 0, D3D12CharacterMaterialRole.Hair)], bogus, styleCatalog: TestStylePlugins.AllViewCatalog));
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(
                source, TestStylePlugins.AllPresetCatalog, objects, [new(id, 0, D3D12CharacterMaterialRole.Hair)], bogus, styleCatalog: TestStylePlugins.AllViewCatalog));
        }
    }
}
