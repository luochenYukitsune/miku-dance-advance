using System.Numerics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12PmxMaterialTreatmentCatalogTests
{
    public static IEnumerable<object[]> Profiles()
    {
        yield return [T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(1.04f, 1.1f, -.25f, .6f)];
        yield return [T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(1.08f, .9f, -.2f, .65f, .26f, .22f)];
        yield return [T8cTestHelpers.StarRailStyleId, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(1f, .55f, .3f, 1.7f)];
        yield return [T8cTestHelpers.StarRailStyleId, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(1.03f, .55f, .12f, 1.5f, .38f, .05f)];
        yield return [T8cTestHelpers.WutheringStyleId, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(.75f, 1.2f, .32f, 1.35f)];
        yield return [T8cTestHelpers.WutheringStyleId, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(.82f, 1.15f, .24f, 1.25f, .12f, .3f)];
        yield return [T8cTestHelpers.EndfieldStyleId, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(.92f, .6f, -.2f, 2f)];
        yield return [T8cTestHelpers.EndfieldStyleId, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(.94f, .7f, -.14f, 1.8f, .3f, .12f)];
    }

    [Theory, MemberData(nameof(Profiles))]
    public void ProfilesAreBoundedImmutableValuesForExplicitBodyOrHair(string family,
        D3D12CharacterMaterialRole role, D3D12PmxMaterialTreatment expected)
    {
        var resolved = Assert.IsType<D3D12PmxMaterialTreatment>(T8cTestHelpers.GetDefaultTreatment(family, role));
        Assert.Equal(expected, resolved);
        resolved.Validate();
        var edited = resolved with { Saturation = 0 };
        Assert.NotEqual(edited, T8cTestHelpers.GetDefaultTreatment(family, role));
        Assert.Equal(expected, T8cTestHelpers.GetDefaultTreatment(family, role));
    }

    [Fact]
    public void HairBandDefaultsAreExclusiveToOrdinaryModernHair()
    {
        foreach (var family in T8cTestHelpers.ModernStyleIds)
        {
            Assert.Equal(0, T8cTestHelpers.GetDefaultTreatment(family, D3D12CharacterMaterialRole.Body)!.HairHighlightStrength);
            Assert.Null(T8cTestHelpers.GetDefaultTreatment(family, D3D12CharacterMaterialRole.Face));
            Assert.Null(T8cTestHelpers.GetDefaultTreatment(family, D3D12CharacterMaterialRole.Eye));
            var hair = T8cTestHelpers.GetDefaultTreatment(family, D3D12CharacterMaterialRole.Hair)!;
            Assert.InRange(hair.HairHighlightStrength, .01f, .4f);
            Assert.InRange(hair.HairHighlightWidth, .02f, .4f);
        }
        Assert.Null(T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.MmdStyleId, D3D12CharacterMaterialRole.Hair));
    }

    [Fact]
    public void FamiliesHaveDifferentBodyAndHairProcessing()
    {
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Hair })
            Assert.Equal(4, T8cTestHelpers.ModernStyleIds.Select(family => T8cTestHelpers.GetDefaultTreatment(family, role)).Distinct().Count());
        foreach (var family in T8cTestHelpers.ModernStyleIds)
            Assert.NotEqual(T8cTestHelpers.GetDefaultTreatment(family, D3D12CharacterMaterialRole.Body),
                T8cTestHelpers.GetDefaultTreatment(family, D3D12CharacterMaterialRole.Hair));
    }

    [Fact]
    public void MmdFaceEyeAndUnknownSelectionsNeverResolve()
    {
        foreach (var family in T8cTestHelpers.ModernStyleIds.Append(T8cTestHelpers.MmdStyleId))
        {
            foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye, (D3D12CharacterMaterialRole)(-1), (D3D12CharacterMaterialRole)999 })
                Assert.Null(T8cTestHelpers.GetDefaultTreatment(family, role));
        }
        foreach (var role in Enum.GetValues<D3D12CharacterMaterialRole>())
            Assert.Null(T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.MmdStyleId, role));
        var unknownStyle = Assert.Throws<KeyNotFoundException>(() => TestStylePlugins.GetStyle("unknown-style"));
        Assert.Contains("unknown-style", unknownStyle.Message);
    }

    [Fact]
    public void SingleRowDefaultPreservesProtectedInstancesAndOnlyAllocatesForEligibleRows()
    {
        var genshinBody = T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Body);
        var genshinHair = T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Hair);
        Assert.Throws<ArgumentNullException>(() => D3D12PmxMaterialTreatmentCatalog.ApplyDefault(null!, genshinBody, genshinHair));
        var starMcHair = T8cTestHelpers.MaterialWithBinding(
            T8cTestHelpers.StarRailStyleId,
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 0, intensity: 0, highlightCoefficient: 0),
            D3D12CharacterMaterialRole.Hair);
        D3D12CharacterMaterialStyle[] protectedRows = [new(D3D12CharacterMaterialRole.Face), new(D3D12CharacterMaterialRole.Eye),
            new(PmxMaterialTreatment: new()), starMcHair];
        foreach (var row in protectedRows)
            Assert.Same(row, D3D12PmxMaterialTreatmentCatalog.ApplyDefault(row, genshinBody, genshinHair));
        var body = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Body);
        Assert.Same(body, D3D12PmxMaterialTreatmentCatalog.ApplyDefault(body, null, null));
        var treated = D3D12PmxMaterialTreatmentCatalog.ApplyDefault(body, genshinBody, genshinHair);
        Assert.NotSame(body, treated);
        Assert.Null(body.PmxMaterialTreatment);
        Assert.Equal(genshinBody, treated.PmxMaterialTreatment);
        Assert.Same(treated, D3D12PmxMaterialTreatmentCatalog.ApplyDefault(treated, genshinBody, genshinHair));
    }

    [Fact]
    public void ApplyDefaultsIsExplicitPureAndIdempotentAcrossMixedMaterials()
    {
        var genshinBody = T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Body);
        var genshinHair = T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Hair);
        string[] families = [T8cTestHelpers.GenshinStyleId, T8cTestHelpers.StarRailStyleId,
            T8cTestHelpers.WutheringStyleId, T8cTestHelpers.EndfieldStyleId, T8cTestHelpers.MmdStyleId];
        D3D12CharacterMaterialStyle[] rows = [new(D3D12CharacterMaterialRole.Body), new(D3D12CharacterMaterialRole.Hair),
            new(D3D12CharacterMaterialRole.Face, .27f, .6f), new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: .17f), new(D3D12CharacterMaterialRole.Body)];
        var source = T8cTestHelpers.CreateDirectMap(families, rows);
        var applied = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(source, genshinBody, genshinHair);
        Assert.NotSame(source, applied);
        Assert.Equal(source.StyleIds, applied.StyleIds);
        Assert.All(source.CharacterMaterials, row => Assert.Null(row.PmxMaterialTreatment));
        Assert.Equal(genshinBody, applied.CharacterMaterials[0].PmxMaterialTreatment);
        Assert.Equal(genshinHair, applied.CharacterMaterials[1].PmxMaterialTreatment);
        for (int i = 2; i < rows.Length; i++) Assert.Equal(rows[i], applied.CharacterMaterials[i]);
        var repeated = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(applied, genshinBody, genshinHair);
        Assert.Equal(applied.StyleIds, repeated.StyleIds);
        Assert.Equal(applied.CharacterMaterials, repeated.CharacterMaterials);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(applied, repeated));
    }

    [Fact]
    public void ApplyDefaultsPreservesCallerTreatmentIncludingIdentityAndMmd()
    {
        foreach (var family in T8cTestHelpers.ModernStyleIds.Append(T8cTestHelpers.MmdStyleId))
        foreach (var treatment in new[] { new D3D12PmxMaterialTreatment(), new D3D12PmxMaterialTreatment(.93f, 1.15f, .1f, .9f) })
        {
            var row = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair, PmxMaterialTreatment: treatment);
            var source = T8cTestHelpers.CreateDirectMap([family], [row]);
            var applied = T8cTestHelpers.ApplyPluginDefaults(source, family);
            Assert.Equal(row, applied.CharacterMaterials[0]);
        }
        var defaultMmd = T8cTestHelpers.CreateDirectMap([T8cTestHelpers.MmdStyleId, T8cTestHelpers.MmdStyleId]);
        var mmd = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(defaultMmd, null, null);
        Assert.True(mmd.IsDefault);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, mmd));
        Assert.All(mmd.CharacterMaterials, row => Assert.Null(row.PmxMaterialTreatment));
    }

    [Fact]
    public void ApplyDefaultsDoesNotMixWithAnyDedicatedBindingEvenWhenDisabledOrOtherFamily()
    {
        D3D12CharacterMaterialStyle[] dedicated = [
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.GenshinStyleId, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.GenshinStyleId, 0), D3D12CharacterMaterialRole.Body),
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.GenshinStyleId, TestStylePlugins.GenshinHair(0), D3D12CharacterMaterialRole.Hair, validateWithPlugin: true),
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.StarRailStyleId, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 0, intensity: 0, highlightCoefficient: 0), D3D12CharacterMaterialRole.Hair, validateWithPlugin: true),
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.EndfieldStyleId, T8cTestHelpers.EndfieldHairBinding(0, Vector3.One, brightness: 0), D3D12CharacterMaterialRole.Hair, validateWithPlugin: true),
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.EndfieldStyleId, T8cTestHelpers.EndfieldRimBinding(Vector3.One, Vector3.One, edgeStrength: 0), D3D12CharacterMaterialRole.Body, validateWithPlugin: true),
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.GenshinStyleId, TestStylePlugins.GenshinHairAtlas(0, 1, "LinearRgb", 1, 0, 0, .1f, 1, 1, 0, 0), D3D12CharacterMaterialRole.Hair, validateWithPlugin: true),
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.EndfieldStyleId, T8cTestHelpers.EndfieldMatcapBinding(0, 1, 2, "LinearRgb", 0, Vector3.One, Vector3.One), D3D12CharacterMaterialRole.Body, validateWithPlugin: true),
            T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.GenshinStyleId, TestStylePlugins.GenshinMetallic(0, 1, "LinearRgb", Vector3.One, Vector3.One, Vector3.One, enableMetallics: 0), D3D12CharacterMaterialRole.Body, validateWithPlugin: true)
        ];
        foreach (var family in T8cTestHelpers.ModernStyleIds)
        {
            var source = T8cTestHelpers.CreateDirectMap(Enumerable.Repeat(family, dedicated.Length), dedicated);
            var applied = T8cTestHelpers.ApplyPluginDefaults(source, family);
            Assert.Equal(source.CharacterMaterials, applied.CharacterMaterials);
            Assert.All(applied.CharacterMaterials, row => Assert.Null(row.PmxMaterialTreatment));
        }
    }

    [Fact]
    public void ApplyDefaultsRejectsIncompleteInputsWithoutGuessing()
    {
        var genshinBody = T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Body);
        var genshinHair = T8cTestHelpers.GetDefaultTreatment(T8cTestHelpers.GenshinStyleId, D3D12CharacterMaterialRole.Hair);
        Assert.Throws<ArgumentNullException>(() => D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(null!, genshinBody, genshinHair));
        var missing = T8cTestHelpers.CreateDirectMap([T8cTestHelpers.GenshinStyleId], []);
        Assert.Throws<ArgumentException>(() => T8cTestHelpers.ApplyPluginDefaults(missing, T8cTestHelpers.GenshinStyleId));
        var nullRow = T8cTestHelpers.CreateDirectMap([T8cTestHelpers.GenshinStyleId], [null!]);
        Assert.Throws<ArgumentException>(() => T8cTestHelpers.ApplyPluginDefaults(nullRow, T8cTestHelpers.GenshinStyleId));
        var empty = T8cTestHelpers.ApplyPluginDefaults(T8cTestHelpers.CreateDirectMap([]), T8cTestHelpers.GenshinStyleId);
        Assert.Empty(empty.StyleIds);
        Assert.Empty(empty.CharacterMaterials);
    }
}
