using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12PmxMaterialTreatmentProfileTransportTests
{
    [Theory]
    [InlineData(-.5f, 1f)]
    [InlineData(.5f, 1f)]
    [InlineData(0f, .5f)]
    [InlineData(0f, 2f)]
    [InlineData(.12f, .85f)]
    public void NondefaultToneParametersRoundTripOnCurrentSchema14(float contrast, float gamma)
    {
        var profile = Profile(new(.94f, .8f, contrast, gamma));
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var document = JsonNode.Parse(bytes)!;
        var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, document["schemaVersion"]!.GetValue<int>());
        Assert.Equal(contrast != 0f, treatment.ContainsKey("valueContrast"));
        Assert.Equal(gamma != 1f, treatment.ContainsKey("shadeGamma"));
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(profile.Materials, restored.Materials);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
    }

    [Theory]
    [InlineData(.4f, .12f)]
    [InlineData(.15f, .02f)]
    [InlineData(.15f, .4f)]
    [InlineData(0f, .02f)]
    [InlineData(0f, .4f)]
    public void HairHighlightParametersRoundTripIncludingDormantWidth(float strength, float width)
    {
        var profile = Profile(new(HairHighlightStrength: strength, HairHighlightWidth: width));
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var document = JsonNode.Parse(bytes)!;
        var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, document["schemaVersion"]!.GetValue<int>());
        Assert.Equal(strength != 0f, treatment.ContainsKey("hairHighlightStrength"));
        Assert.Equal(width != .12f, treatment.ContainsKey("hairHighlightWidth"));
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(profile.Materials, restored.Materials);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void LegacySchemasKeepWireAndIdentityToneDefaults(int schema)
    {
        byte[] legacyBytes = Encoding.UTF8.GetBytes(LegacyDocument(schema, saturation: .94f, transitionWidthScale: .8f).ToJsonString());
        var restored = D3D12MaterialBindingProfileJson.Decode(legacyBytes);
        Assert.Equal(.94f, restored.Materials[0].PmxMaterialTreatment!.Saturation);
        Assert.Equal(.8f, restored.Materials[0].PmxMaterialTreatment!.TransitionWidthScale);
        Assert.Equal(0f, restored.Materials[0].PmxMaterialTreatment!.ValueContrast);
        Assert.Equal(1f, restored.Materials[0].PmxMaterialTreatment!.ShadeGamma);
        Assert.Equal(0f, restored.Materials[0].PmxMaterialTreatment!.HairHighlightStrength);
        Assert.Equal(.12f, restored.Materials[0].PmxMaterialTreatment!.HairHighlightWidth);
        byte[] upgradedBytes = D3D12MaterialBindingProfileJson.Encode(restored);
        var roundtripped = D3D12MaterialBindingProfileJson.Decode(upgradedBytes);
        Assert.Equal(restored.Materials, roundtripped.Materials);
        Assert.Equal(upgradedBytes, D3D12MaterialBindingProfileJson.Encode(roundtripped));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void LegacySchemasRejectEveryNewFieldEvenAtItsDefault(int schema)
    {
        foreach (string field in new[] { "valueContrast", "shadeGamma", "hairHighlightStrength", "hairHighlightWidth" })
        {
            var document = LegacyDocument(schema);
            document["materials"]![0]!["pmxMaterialTreatment"]![field] = field switch
            { "shadeGamma" => 1f, "hairHighlightWidth" => .12f, _ => 0f };
            Assert.Throws<InvalidDataException>(() => Parse(document));
        }
    }

    [Fact]
    public void Schema11AndCurrentSchemaAllowIndependentlyAbsentDefaultsAndRejectMissingLegacyFields()
    {
        foreach (var document in new[]
        {
            LegacyDocument(11, valueContrast: .1f, shadeGamma: .8f),
            Document(Profile(new(ValueContrast: .1f, ShadeGamma: .8f)))
        })
        {
            var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
            treatment.Remove("valueContrast");
            Assert.Equal(new D3D12PmxMaterialTreatment(ShadeGamma: .8f), Parse(document).Materials[0].PmxMaterialTreatment);
            treatment.Remove("shadeGamma");
            Assert.Equal(new D3D12PmxMaterialTreatment(), Parse(document).Materials[0].PmxMaterialTreatment);
            treatment.Remove("saturation");
            Assert.Throws<InvalidDataException>(() => Parse(document));
        }
    }

    [Fact]
    public void Schema11AndCurrentSchemaHairFieldsAreIndependentlyOptionalAndCombineWithToneFields()
    {
        foreach (var document in new[]
        {
            LegacyDocument(11, role: D3D12CharacterMaterialRole.Hair, valueContrast: .1f, shadeGamma: .8f,
                hairHighlightStrength: .2f, hairHighlightWidth: .25f),
            Document(Profile(new(ValueContrast: .1f, ShadeGamma: .8f,
                HairHighlightStrength: .2f, HairHighlightWidth: .25f)))
        })
        {
            var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
            treatment.Remove("hairHighlightStrength");
            Assert.Equal(new D3D12PmxMaterialTreatment(ValueContrast: .1f, ShadeGamma: .8f,
                HairHighlightWidth: .25f), Parse(document).Materials[0].PmxMaterialTreatment);
            treatment.Remove("hairHighlightWidth");
            Assert.Equal(new D3D12PmxMaterialTreatment(ValueContrast: .1f, ShadeGamma: .8f),
                Parse(document).Materials[0].PmxMaterialTreatment);
        }
    }

    [Theory]
    [InlineData("valueContrast", "-0.5001")]
    [InlineData("valueContrast", "0.5001")]
    [InlineData("shadeGamma", "0.4999")]
    [InlineData("shadeGamma", "2.0001")]
    [InlineData("valueContrast", "null")]
    [InlineData("shadeGamma", "\"1\"")]
    [InlineData("shadeGamma", "1e200")]
    [InlineData("contrast", "0.1")]
    [InlineData("hairHighlightStrength", "-0.0001")]
    [InlineData("hairHighlightStrength", "0.4001")]
    [InlineData("hairHighlightWidth", "0.0199")]
    [InlineData("hairHighlightWidth", "0.4001")]
    [InlineData("hairHighlightStrength", "null")]
    [InlineData("hairHighlightWidth", "\"0.12\"")]
    [InlineData("hairHighlightWidth", "1e200")]
    public void Schema11AndCurrentSchemaRejectInvalidOrUnknownToneFields(string field, string value)
    {
        foreach (var document in new[]
        {
            LegacyDocument(11, valueContrast: .1f),
            Document(Profile(new(ValueContrast: .1f)))
        })
        {
            document["materials"]![0]!["pmxMaterialTreatment"]![field] = JsonNode.Parse(value);
            Assert.Throws<InvalidDataException>(() => Parse(document));
        }
    }

    private static JsonObject Document(D3D12MaterialBindingProfile profile)
        => JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(profile))!.AsObject();

    private static D3D12MaterialBindingProfile Parse(JsonObject document)
        => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(document.ToJsonString()));

    private static D3D12MaterialBindingProfile Profile(D3D12PmxMaterialTreatment treatment)
    {
        D3D12MaterialBindingProfileRow row = new(
            0,
            treatment.HairHighlightStrength > 0f ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body,
            pmxMaterialTreatment: treatment);
        return new("custom-tone", "Tone mapping", new string('a', 64), [row]);
    }

    private static JsonObject LegacyDocument(
        int schema,
        D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body,
        float saturation = 1f,
        float transitionWidthScale = 1f,
        float? valueContrast = null,
        float? shadeGamma = null,
        float? hairHighlightStrength = null,
        float? hairHighlightWidth = null)
    {
        var treatment = new JsonObject
        {
            ["saturation"] = saturation,
            ["transitionWidthScale"] = transitionWidthScale,
        };
        if (valueContrast.HasValue) treatment["valueContrast"] = valueContrast.Value;
        if (shadeGamma.HasValue) treatment["shadeGamma"] = shadeGamma.Value;
        if (hairHighlightStrength.HasValue) treatment["hairHighlightStrength"] = hairHighlightStrength.Value;
        if (hairHighlightWidth.HasValue) treatment["hairHighlightWidth"] = hairHighlightWidth.Value;

        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0,
            ["role"] = role.ToString(),
            ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f,
            ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = treatment,
            ["face"] = null,
            ["genshinHair"] = null,
            ["starMcHair"] = null,
        };
        if (schema >= 2) row["endfieldRim"] = null;
        if (schema >= 3) row["genshinHairAtlas"] = null;
        if (schema >= 4) row["endfieldHair"] = null;
        if (schema >= 5) row["endfieldMatcap"] = null;
        if (schema >= 6) row["genshinMetallic"] = null;

        return new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile",
            ["schemaVersion"] = schema,
            ["stableId"] = "custom-tone",
            ["name"] = "Tone mapping",
            ["layoutVersion"] = 1,
            ["layoutFingerprint"] = new string('a', 64),
            ["materials"] = new JsonArray(row),
        };
    }
}
