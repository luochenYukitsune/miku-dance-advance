using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OutlineProfileTests
{
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void OutlineOnlyProfileRoundTripsDetachedWithoutAssigningMaterialRoles()
    {
        var outlines = new List<D3D12MaterialBindingProfileOutline> { new(0, .25f), new(1) };
        var profile = new D3D12MaterialBindingProfile("outline", "Outline", Fingerprint, [], outlineWidths: outlines);
        byte[] encoded = D3D12MaterialBindingProfileJson.Encode(profile);
        outlines.Clear();
        var restored = D3D12MaterialBindingProfileJson.Decode(encoded);
        Assert.Equal(13, restored.SchemaVersion);
        Assert.Empty(restored.Materials);
        Assert.Equal(new D3D12MaterialBindingProfileOutline[] { new(0, .25f), new(1, 1f) }, restored.OutlineWidths);
        Assert.Equal(encoded, D3D12MaterialBindingProfileJson.Encode(restored));
        Assert.Throws<NotSupportedException>(() => ((IList<D3D12MaterialBindingProfileOutline>)restored.OutlineWidths).Clear());
    }

    [Fact]
    public void SavingAsALibraryCopyRetainsIndependentWidths()
    {
        string directory = Path.Combine("G:/MDA-Work/settings/20261113/outline659", "library-" + Guid.NewGuid().ToString("N"));
        var profile = new D3D12MaterialBindingProfile("outline", "Outline", Fingerprint, [], outlineWidths: [new(0, .25f)]);
        var saved = new D3D12MaterialBindingProfileStore(directory).SaveAsNew(profile, "Fine outlines");
        Assert.NotEqual(profile.StableId, saved.StableId);
        Assert.Equal(profile.OutlineWidths, saved.OutlineWidths);
        Assert.Empty(saved.Materials);
        var restored = D3D12MaterialBindingProfileJson.Decode(File.ReadAllBytes(Path.Combine(directory, saved.StableId + ".json")));
        Assert.Equal(saved.OutlineWidths, restored.OutlineWidths);
    }

    [Fact]
    public void LegacySchemasDefaultToNoOverridesAndKeepTheirMinimumSchema()
    {
        var legacy = new D3D12MaterialBindingProfile("outline", "Outline", Fingerprint, []);
        byte[] original = D3D12MaterialBindingProfileJson.Encode(legacy);
        Assert.DoesNotContain("outlineWidths", Encoding.UTF8.GetString(original), StringComparison.Ordinal);
        for (int schema = 1; schema <= 12; schema++)
        {
            var document = JsonNode.Parse(original)!.AsObject();
            document["schemaVersion"] = schema;
            var restored = Decode(document);
            Assert.Empty(restored.OutlineWidths);
            Assert.Equal(1, restored.SchemaVersion);
            Assert.Equal(original, D3D12MaterialBindingProfileJson.Encode(restored));
        }
        var treatment = new D3D12MaterialBindingProfile("tone", "Tone", Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, PmxMaterialTreatment: new(ValueContrast: .1f))]);
        Assert.Equal(11, treatment.SchemaVersion);
        Assert.Equal(11, D3D12MaterialBindingProfileJson.Decode(D3D12MaterialBindingProfileJson.Encode(treatment)).SchemaVersion);
    }

    [Theory]
    [InlineData("old-schema")]
    [InlineData("missing-array")]
    [InlineData("unknown")]
    [InlineData("negative-index")]
    [InlineData("fractional-index")]
    [InlineData("negative-scale")]
    [InlineData("large-scale")]
    [InlineData("nonfinite")]
    [InlineData("duplicate-material")]
    [InlineData("duplicate-field")]
    [InlineData("over-budget")]
    public void CodecRejectsInvalidOutlineOverrides(string kind)
    {
        var profile = new D3D12MaterialBindingProfile("outline", "Outline", Fingerprint, [], outlineWidths: [new(0, .25f)]);
        var document = JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(profile))!.AsObject();
        var row = document["outlineWidths"]![0]!.AsObject();
        switch (kind)
        {
            case "old-schema": document["schemaVersion"] = 12; break;
            case "missing-array": document.Remove("outlineWidths"); break;
            case "unknown": row["role"] = "Hair"; break;
            case "negative-index": row["localMaterialIndex"] = -1; break;
            case "fractional-index": row["localMaterialIndex"] = .5; break;
            case "negative-scale": row["scale"] = -.01; break;
            case "large-scale": row["scale"] = 4.01; break;
            case "nonfinite": row["scale"] = 1e200; break;
            case "duplicate-material": document["outlineWidths"]!.AsArray().Add(row.DeepClone()); break;
            case "duplicate-field":
                string json = document.ToJsonString().Replace("\"scale\":0.25", "\"scale\":0.25,\"scale\":1", StringComparison.Ordinal);
                Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(json)));
                return;
            case "over-budget":
                document["outlineWidths"] = new JsonArray(Enumerable.Range(0, D3D12MaterialBindingProfile.MaxMaterials + 1)
                    .Select(i => (JsonNode?)new JsonObject { ["localMaterialIndex"] = i, ["scale"] = 1 }).ToArray());
                break;
        }
        Assert.Throws<InvalidDataException>(() => Decode(document));
    }

    [Fact]
    public void DomainRejectsInvalidScalesDuplicateSelectorsAndUnboundedEnumeration()
    {
        new D3D12MaterialBindingProfileOutline(0, 0f).Validate();
        new D3D12MaterialBindingProfileOutline(0, 4f).Validate();
        foreach (float scale in new[] { -.01f, 4.01f, float.NaN, float.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12MaterialBindingProfileOutline(0, scale).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12MaterialBindingProfileOutline(-1).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12MaterialBindingProfile("outline", "Outline", Fingerprint, [],
            outlineWidths: [new(0, 0f), new(0, 4f)]));
        int yielded = 0;
        IEnumerable<D3D12MaterialBindingProfileOutline> Unbounded()
        {
            while (true) yield return new(yielded++);
        }
        Assert.Throws<ArgumentException>(() => new D3D12MaterialBindingProfile("outline", "Outline", Fingerprint, [], outlineWidths: Unbounded()));
        Assert.Equal(D3D12MaterialBindingProfile.MaxMaterials + 1, yielded);
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void ResolutionUsesOnlyExplicitObjectAndMaterialWithoutClassifyingOrChangingSource(string pipeline)
    {
        var source = Fixture();
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var profile = new D3D12MaterialBindingProfile("outline", "Outline", layout.Fingerprint, [], outlineWidths: [new(0, .25f)]);
        Guid actor = Guid.NewGuid();
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, actor, pipeline);
        Assert.Empty(resolved.CharacterMaterials);
        Assert.Equal(new D3D12ViewOutlineWidthBinding(actor, 0, .25f), Assert.Single(resolved.OutlineWidths));
        Assert.Equal(layout.Fingerprint, D3D12MaterialBindingLayout.Capture(source).Fingerprint);
        var classified = new D3D12MaterialBindingProfile("outline", "Outline", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: .15f)], outlineWidths: profile.OutlineWidths);
        var combined = D3D12MaterialBindingProfileResolver.Resolve(classified, source, actor, pipeline);
        Assert.Equal(D3D12CharacterMaterialRole.Eye, Assert.Single(combined.CharacterMaterials).Style.Role);
        Assert.Equal(.15f, combined.CharacterMaterials[0].Style.EyeGeometricShadowStrength);
        Assert.Equal(resolved.OutlineWidths, combined.OutlineWidths);
        var outside = new D3D12MaterialBindingProfile("outline", "Outline", layout.Fingerprint, [], outlineWidths: [new(1, .25f)]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(outside, source, actor, pipeline));
    }

    private static D3D12MaterialBindingProfile Decode(JsonObject document)
        => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(document.ToJsonString()));

    private static OriginalFrameScene Fixture()
    {
        var scene = new SceneSnapshot(
            [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero)],
            [0, 1, 2], [new("unclassified", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], []);
        return new(new(scene, [new(Vector3.One)]));
    }
}
