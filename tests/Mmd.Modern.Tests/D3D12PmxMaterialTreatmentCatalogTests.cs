using System.Numerics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12PmxMaterialTreatmentCatalogTests
{
    public static IEnumerable<object[]> Profiles()
    {
        yield return [D3D12SurfaceStyle.GenshinPmx, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(1.04f, 1.1f, -.25f, .6f)];
        yield return [D3D12SurfaceStyle.GenshinPmx, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(1.08f, .9f, -.2f, .65f, .26f, .22f)];
        yield return [D3D12SurfaceStyle.StarRailPmx, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(1f, .55f, .3f, 1.7f)];
        yield return [D3D12SurfaceStyle.StarRailPmx, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(1.03f, .55f, .12f, 1.5f, .38f, .05f)];
        yield return [D3D12SurfaceStyle.WutheringPmx, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(.75f, 1.2f, .32f, 1.35f)];
        yield return [D3D12SurfaceStyle.WutheringPmx, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(.82f, 1.15f, .24f, 1.25f, .12f, .3f)];
        yield return [D3D12SurfaceStyle.EndfieldPmx, D3D12CharacterMaterialRole.Body, new D3D12PmxMaterialTreatment(.92f, .6f, -.2f, 2f)];
        yield return [D3D12SurfaceStyle.EndfieldPmx, D3D12CharacterMaterialRole.Hair, new D3D12PmxMaterialTreatment(.94f, .7f, -.14f, 1.8f, .3f, .12f)];
    }

    [Theory, MemberData(nameof(Profiles))]
    public void ProfilesAreBoundedImmutableValuesForExplicitBodyOrHair(D3D12SurfaceStyle family,
        D3D12CharacterMaterialRole role, D3D12PmxMaterialTreatment expected)
    {
        var resolved = Assert.IsType<D3D12PmxMaterialTreatment>(D3D12PmxMaterialTreatmentCatalog.Resolve(family, role));
        Assert.Equal(expected, resolved);
        resolved.Validate();
        var edited = resolved with { Saturation = 0 };
        Assert.NotEqual(edited, D3D12PmxMaterialTreatmentCatalog.Resolve(family, role));
        Assert.Equal(expected, D3D12PmxMaterialTreatmentCatalog.Resolve(family, role));
    }

    [Fact]
    public void HairBandDefaultsAreExclusiveToOrdinaryModernHair()
    {
        foreach (var family in ModernFamilies)
        {
            Assert.Equal(0, D3D12PmxMaterialTreatmentCatalog.Resolve(family, D3D12CharacterMaterialRole.Body)!.HairHighlightStrength);
            Assert.Null(D3D12PmxMaterialTreatmentCatalog.Resolve(family, D3D12CharacterMaterialRole.Face));
            Assert.Null(D3D12PmxMaterialTreatmentCatalog.Resolve(family, D3D12CharacterMaterialRole.Eye));
            var hair = D3D12PmxMaterialTreatmentCatalog.Resolve(family, D3D12CharacterMaterialRole.Hair)!;
            Assert.InRange(hair.HairHighlightStrength, .01f, .4f);
            Assert.InRange(hair.HairHighlightWidth, .02f, .4f);
        }
        Assert.Null(D3D12PmxMaterialTreatmentCatalog.Resolve(D3D12SurfaceStyle.Mmd, D3D12CharacterMaterialRole.Hair));
    }

    [Fact]
    public void FamiliesHaveDifferentBodyAndHairProcessing()
    {
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Hair })
            Assert.Equal(4, ModernFamilies.Select(family => D3D12PmxMaterialTreatmentCatalog.Resolve(family, role)).Distinct().Count());
        foreach (var family in ModernFamilies)
            Assert.NotEqual(D3D12PmxMaterialTreatmentCatalog.Resolve(family, D3D12CharacterMaterialRole.Body),
                D3D12PmxMaterialTreatmentCatalog.Resolve(family, D3D12CharacterMaterialRole.Hair));
    }

    [Fact]
    public void MmdFaceEyeAndUnknownSelectionsNeverResolve()
    {
        foreach (var family in ModernFamilies.Append(D3D12SurfaceStyle.Mmd).Append((D3D12SurfaceStyle)999))
        {
            foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye, (D3D12CharacterMaterialRole)(-1), (D3D12CharacterMaterialRole)999 })
                Assert.Null(D3D12PmxMaterialTreatmentCatalog.Resolve(family, role));
        }
        foreach (var role in Enum.GetValues<D3D12CharacterMaterialRole>())
        {
            Assert.Null(D3D12PmxMaterialTreatmentCatalog.Resolve(D3D12SurfaceStyle.Mmd, role));
            Assert.Null(D3D12PmxMaterialTreatmentCatalog.Resolve((D3D12SurfaceStyle)999, role));
        }
    }

    [Fact]
    public void SingleRowDefaultPreservesProtectedInstancesAndOnlyAllocatesForEligibleRows()
    {
        Assert.Throws<ArgumentNullException>(() => D3D12PmxMaterialTreatmentCatalog.ApplyDefault(D3D12SurfaceStyle.GenshinPmx, null!));
        D3D12CharacterMaterialStyle[] protectedRows = [new(D3D12CharacterMaterialRole.Face), new(D3D12CharacterMaterialRole.Eye),
            new(PmxMaterialTreatment: new()), new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 0))];
        foreach (var row in protectedRows)
            Assert.Same(row, D3D12PmxMaterialTreatmentCatalog.ApplyDefault(D3D12SurfaceStyle.GenshinPmx, row));
        var body = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Body);
        Assert.Same(body, D3D12PmxMaterialTreatmentCatalog.ApplyDefault(D3D12SurfaceStyle.Mmd, body));
        Assert.Same(body, D3D12PmxMaterialTreatmentCatalog.ApplyDefault((D3D12SurfaceStyle)999, body));
        var treated = D3D12PmxMaterialTreatmentCatalog.ApplyDefault(D3D12SurfaceStyle.GenshinPmx, body);
        Assert.NotSame(body, treated);
        Assert.Null(body.PmxMaterialTreatment);
        Assert.Equal(D3D12PmxMaterialTreatmentCatalog.Resolve(D3D12SurfaceStyle.GenshinPmx, body.Role), treated.PmxMaterialTreatment);
        Assert.Same(treated, D3D12PmxMaterialTreatmentCatalog.ApplyDefault(D3D12SurfaceStyle.GenshinPmx, treated));
    }

    [Fact]
    public void ApplyDefaultsIsExplicitPureAndIdempotentAcrossMixedMaterials()
    {
        D3D12SurfaceStyle[] families = [D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx,
            D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx, D3D12SurfaceStyle.Mmd];
        D3D12CharacterMaterialStyle[] rows = [new(D3D12CharacterMaterialRole.Body), new(D3D12CharacterMaterialRole.Hair),
            new(D3D12CharacterMaterialRole.Face, .27f, .6f), new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: .17f), new(D3D12CharacterMaterialRole.Body)];
        var source = new D3D12SurfaceStyleMap(families, rows);
        var applied = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(source);
        Assert.NotSame(source, applied);
        Assert.Equal(source.Materials, applied.Materials);
        Assert.All(source.CharacterMaterials, row => Assert.Null(row.PmxMaterialTreatment));
        Assert.Equal(D3D12PmxMaterialTreatmentCatalog.Resolve(families[0], rows[0].Role), applied.CharacterMaterials[0].PmxMaterialTreatment);
        Assert.Equal(D3D12PmxMaterialTreatmentCatalog.Resolve(families[1], rows[1].Role), applied.CharacterMaterials[1].PmxMaterialTreatment);
        for (int i = 2; i < rows.Length; i++) Assert.Equal(rows[i], applied.CharacterMaterials[i]);
        var repeated = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(applied);
        Assert.Equal(applied.Materials, repeated.Materials);
        Assert.Equal(applied.CharacterMaterials, repeated.CharacterMaterials);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(applied, repeated));
    }

    [Fact]
    public void ApplyDefaultsPreservesCallerTreatmentIncludingIdentityAndMmd()
    {
        foreach (var family in ModernFamilies.Append(D3D12SurfaceStyle.Mmd))
        foreach (var treatment in new[] { new D3D12PmxMaterialTreatment(), new D3D12PmxMaterialTreatment(.93f, 1.15f, .1f, .9f) })
        {
            var row = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair, PmxMaterialTreatment: treatment);
            var source = new D3D12SurfaceStyleMap([family], [row]);
            var applied = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(source);
            Assert.Equal(row, applied.CharacterMaterials[0]);
        }
        var defaultMmd = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.Mmd]);
        var mmd = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(defaultMmd);
        Assert.True(mmd.IsDefault);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, mmd));
        Assert.All(mmd.CharacterMaterials, row => Assert.Null(row.PmxMaterialTreatment));
    }

    [Fact]
    public void ApplyDefaultsDoesNotMixWithAnyDedicatedBindingEvenWhenDisabledOrOtherFamily()
    {
        D3D12CharacterMaterialStyle[] dedicated = [
            new(D3D12CharacterMaterialRole.Body, FaceSdf: new(0, -Vector3.UnitZ, Vector3.UnitX)),
            new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(0)),
            new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 0)),
            new(D3D12CharacterMaterialRole.Hair, EndfieldHair: new(0, Vector3.One, Brightness: 0)),
            new(D3D12CharacterMaterialRole.Body, EndfieldRim: new(Vector3.One, Vector3.One, EdgeStrength: 0)),
            new(D3D12CharacterMaterialRole.Hair, GenshinHairShadowAtlas: new(0, 1, D3D12GenshinAtlasColorEncoding.LinearRgb, 1, 0, 0, .1f, 1, 1, 0, 0)),
            new(D3D12CharacterMaterialRole.Body, EndfieldMatcap: new(0, 1, 2, D3D12MatcapColorEncoding.LinearRgb, 0, Vector3.One, Vector3.One)),
            new(D3D12CharacterMaterialRole.Body, GenshinMetallic: new(0, 1, D3D12MatcapColorEncoding.LinearRgb, Vector3.One, Vector3.One, Vector3.One, EnableMetallics: 0))
        ];
        foreach (var family in ModernFamilies)
        {
            var source = new D3D12SurfaceStyleMap(Enumerable.Repeat(family, dedicated.Length), dedicated);
            var applied = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(source);
            Assert.Equal(source.CharacterMaterials, applied.CharacterMaterials);
            Assert.All(applied.CharacterMaterials, row => Assert.Null(row.PmxMaterialTreatment));
        }
    }

    [Fact]
    public void ApplyDefaultsRejectsIncompleteInputsWithoutGuessing()
    {
        Assert.Throws<ArgumentNullException>(() => D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(null!));
        var missing = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx], []);
        Assert.Throws<ArgumentException>(() => D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(missing));
        var nullRow = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx], [null!]);
        Assert.Throws<ArgumentException>(() => D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(nullRow));
        var empty = D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(new D3D12SurfaceStyleMap([]));
        Assert.Empty(empty.Materials);
        Assert.Empty(empty.CharacterMaterials);
    }

    private static readonly D3D12SurfaceStyle[] ModernFamilies = [D3D12SurfaceStyle.GenshinPmx,
        D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx];
}
