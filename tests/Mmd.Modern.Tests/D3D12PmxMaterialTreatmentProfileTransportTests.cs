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
    public void NondefaultToneParametersSelectSchema11AndRoundTrip(float contrast, float gamma)
    {
        var profile = Profile(1, new(.94f, .8f, contrast, gamma));
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var document = JsonNode.Parse(bytes)!;
        var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
        Assert.Equal(11, profile.SchemaVersion);
        Assert.Equal(11, document["schemaVersion"]!.GetValue<int>());
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
        var profile = Profile(1, new(HairHighlightStrength: strength, HairHighlightWidth: width));
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var document = JsonNode.Parse(bytes)!;
        var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
        Assert.Equal(11, profile.SchemaVersion);
        Assert.Equal(11, document["schemaVersion"]!.GetValue<int>());
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
        var implicitDefaults = Profile(schema, new(.94f, .8f));
        var explicitDefaults = Profile(schema, new(.94f, .8f, 0f, 1f, 0f, .12f));
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(implicitDefaults);
        Assert.Equal(schema, implicitDefaults.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(explicitDefaults));
        string json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("valueContrast", json);
        Assert.DoesNotContain("shadeGamma", json);
        Assert.DoesNotContain("hairHighlightStrength", json);
        Assert.DoesNotContain("hairHighlightWidth", json);
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(0f, restored.Materials[0].PmxMaterialTreatment!.ValueContrast);
        Assert.Equal(1f, restored.Materials[0].PmxMaterialTreatment!.ShadeGamma);
        Assert.Equal(0f, restored.Materials[0].PmxMaterialTreatment!.HairHighlightStrength);
        Assert.Equal(.12f, restored.Materials[0].PmxMaterialTreatment!.HairHighlightWidth);
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
    public void LegacySchemasRejectEveryNewFieldEvenAtItsDefault(int schema)
    {
        foreach (string field in new[] { "valueContrast", "shadeGamma", "hairHighlightStrength", "hairHighlightWidth" })
        {
            var document = Document(Profile(schema, new()));
            document["materials"]![0]!["pmxMaterialTreatment"]![field] = field switch
            { "shadeGamma" => 1f, "hairHighlightWidth" => .12f, _ => 0f };
            Assert.Throws<InvalidDataException>(() => Parse(document));
        }
    }

    [Fact]
    public void Schema11AllowsIndependentlyAbsentDefaultsAndRejectsMissingLegacyFields()
    {
        var document = Document(Profile(1, new(ValueContrast: .1f, ShadeGamma: .8f)));
        var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
        treatment.Remove("valueContrast");
        Assert.Equal(new D3D12PmxMaterialTreatment(ShadeGamma: .8f), Parse(document).Materials[0].PmxMaterialTreatment);
        treatment.Remove("shadeGamma");
        Assert.Equal(new D3D12PmxMaterialTreatment(), Parse(document).Materials[0].PmxMaterialTreatment);
        treatment.Remove("saturation");
        Assert.Throws<InvalidDataException>(() => Parse(document));
    }

    [Fact]
    public void Schema11HairFieldsAreIndependentlyOptionalAndCombineWithToneFields()
    {
        var document = Document(Profile(1, new(ValueContrast: .1f, ShadeGamma: .8f,
            HairHighlightStrength: .2f, HairHighlightWidth: .25f)));
        var treatment = document["materials"]![0]!["pmxMaterialTreatment"]!.AsObject();
        treatment.Remove("hairHighlightStrength");
        Assert.Equal(new D3D12PmxMaterialTreatment(ValueContrast: .1f, ShadeGamma: .8f,
            HairHighlightWidth: .25f), Parse(document).Materials[0].PmxMaterialTreatment);
        treatment.Remove("hairHighlightWidth");
        Assert.Equal(new D3D12PmxMaterialTreatment(ValueContrast: .1f, ShadeGamma: .8f),
            Parse(document).Materials[0].PmxMaterialTreatment);
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
    public void Schema11RejectsInvalidOrUnknownToneFields(string field, string value)
    {
        var document = Document(Profile(1, new(ValueContrast: .1f)));
        document["materials"]![0]!["pmxMaterialTreatment"]![field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => Parse(document));
    }

    private static JsonObject Document(D3D12MaterialBindingProfile profile)
        => JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(profile))!.AsObject();

    private static D3D12MaterialBindingProfile Parse(JsonObject document)
        => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(document.ToJsonString()));

    private static D3D12MaterialBindingProfile Profile(int schema, D3D12PmxMaterialTreatment treatment)
    {
        var rows = new List<D3D12MaterialBindingProfileRow>
        { new(0, treatment.HairHighlightStrength > 0f ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body,
            PmxMaterialTreatment: treatment) };
        var texture = new D3D12MaterialBindingProfileTexture(0, 1, 1, SceneTextureKind.Image, new string('b', 64));
        if (schema == 2)
            rows.Add(new(1, D3D12CharacterMaterialRole.Body, EndfieldRim: new(Vector3.One, Vector3.One)));
        if (schema == 3)
            rows.Add(new(1, D3D12CharacterMaterialRole.Hair, GenshinHairAtlas: new(texture, texture,
                D3D12GenshinAtlasColorEncoding.LinearRgb, 1, 0, .5f, .1f, 1, 1, 0, 0)));
        if (schema == 4)
            rows.Add(new(1, D3D12CharacterMaterialRole.Hair, EndfieldHair: new(texture, Vector3.One)));
        if (schema is 5 or 8 or 9 or 10)
        {
            var basis = schema < 9 ? null : new D3D12MaterialBindingProfileEndfieldBaseImage(Vector3.One,
                Vector3.One, Vector3.One, Vector3.One, FacingBase: schema < 10 ? null : new());
            var palette = schema < 8 ? null : new D3D12MaterialBindingProfileEndfieldPalette(Vector3.One, BaseImage: basis);
            rows.Add(new(1, D3D12CharacterMaterialRole.Body, EndfieldMatcap: new(texture, texture, texture,
                D3D12MatcapColorEncoding.LinearRgb, 0, Vector3.One, Vector3.One,
                CoupleEndfieldPalette: palette is not null, Palette: palette)));
        }
        if (schema is 6 or 7)
            rows.Add(new(1, D3D12CharacterMaterialRole.Body, GenshinMetallic: new(texture, texture,
                D3D12MatcapColorEncoding.LinearRgb, Vector3.One, Vector3.One, Vector3.One,
                Specular: schema < 7 ? null : new(Vector3.One, Vector3.One))));
        return new("custom-tone", "Tone mapping", new string('a', 64), rows);
    }
}
