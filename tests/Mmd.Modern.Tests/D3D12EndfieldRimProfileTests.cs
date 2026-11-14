using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12EndfieldRimProfileTests
{
    private static D3D12EndfieldRimBinding Rim => new(new(.2f,.3f,.4f), new(.7f,.8f,.9f),
        EdgeSize: 1.3f, EdgeStrength: .15f, DirectionRadians: 2.4f, RangeStrength: .7f, RadialStrength: -.1f);
    private static OriginalFrameScene Source() => D3D12EndfieldHairBindingTests.Frame(seeds: false);
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, D3D12EndfieldRimBinding? rim)
        => new("rim-map", "Rim mapping", D3D12MaterialBindingLayout.Capture(source).Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair, EndfieldRim: rim)]);

    [Fact]
    public void VersionOnePayloadIsUnchangedAndNewRimRoundTripsInVersionTwo()
    {
        var source = Source();
        byte[] old = D3D12MaterialBindingProfileJson.Encode(Profile(source, null));
        Assert.Equal(1, JsonNode.Parse(old)!["schemaVersion"]!.GetValue<int>());
        Assert.False(JsonNode.Parse(old)!["materials"]![0]!.AsObject().ContainsKey("endfieldRim"));
        Assert.Equal(old, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(old)));
        byte[] encoded = D3D12MaterialBindingProfileJson.Encode(Profile(source, Rim));
        Assert.Equal(2, JsonNode.Parse(encoded)!["schemaVersion"]!.GetValue<int>());
        var restored = D3D12MaterialBindingProfileJson.Decode(encoded);
        Assert.Equal(Rim, restored.Materials[0].EndfieldRim);
        Assert.Equal(encoded, D3D12MaterialBindingProfileJson.Encode(restored));
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
        var restored = D3D12MaterialBindingProfileResolver.Resolve(Profile(source, Rim), source, id, pipeline);
        var binding = Assert.Single(restored.CharacterMaterials);
        Assert.Equal(id, binding.ObjectId);
        Assert.Equal(pipeline == "endfield" ? Rim : null, binding.Style.EndfieldRim);
        var objects = new[] { new SceneRenderingStyleBinding(id, new(pipeline), ModelRole.Character, 0, 1, true) };
        var result = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects, restored.CharacterMaterials);
        Assert.Equal(pipeline == "endfield" ? Rim : null, result.SurfaceStyles.CharacterMaterials[0].EndfieldRim);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("v1")]
    [InlineData("vector")]
    [InlineData("range")]
    public void VersionTwoIsStrictAndNeverSilentlyDropsRimData(string damage)
    {
        var doc = JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(Profile(Source(), Rim)))!;
        var row = doc["materials"]![0]!.AsObject(); var rim = row["endfieldRim"]!.AsObject();
        switch (damage)
        {
            case "unknown": rim["guessedIntensity"] = 1; break;
            case "missing": rim.Remove("rangeStrength"); break;
            case "v1": doc["schemaVersion"] = 1; break;
            case "vector": rim["baseColor"] = new JsonArray(1, 2); break;
            case "range": rim["edgeStrength"] = 100000; break;
        }
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(System.Text.Encoding.UTF8.GetBytes(doc.ToJsonString())));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    public void ActiveComposedPayloadCannotBypassSelectedPipelinePreflight(string pipeline)
    {
        var source = Source(); var id = Guid.NewGuid();
        var objects = new[] { new SceneRenderingStyleBinding(id, new(pipeline), ModelRole.Character, 0, 1, true) };
        D3D12ViewCharacterMaterialBinding[] active = [new(id, 0, new(D3D12CharacterMaterialRole.Hair, EndfieldRim: Rim))];
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(source, D3D12StylePresetCatalog.BuiltIn, objects, active));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects, active));
    }
}
