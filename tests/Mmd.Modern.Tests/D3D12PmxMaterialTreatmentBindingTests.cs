using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12PmxMaterialTreatmentBindingTests
{
    [Fact]
    public void OptionalTreatmentAppendsWithoutChangingExistingRoleOrMaterialDefaults()
    {
        var previousCaller = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face, .35f, .6f, .2f, null, null, null);
        Assert.Null(previousCaller.PmxMaterialTreatment);
        var treatment = new D3D12PmxMaterialTreatment();
        Assert.Equal(1, treatment.Saturation);
        Assert.Equal(1, treatment.TransitionWidthScale);
        treatment.Validate();
        var defaults = T8cTestHelpers.CreateDirectMap([T8cTestHelpers.GenshinStyleId]);
        Assert.Null(defaults.EffectivePmxMaterialTreatment(0));
        Assert.Null(defaults.EffectiveToonResponse(0, null));
    }

    public static IEnumerable<object[]> InvalidScalars()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            yield return [new D3D12PmxMaterialTreatment(Saturation: value)];
            yield return [new D3D12PmxMaterialTreatment(TransitionWidthScale: value)];
        }
        yield return [new D3D12PmxMaterialTreatment(Saturation: -.001f)];
        yield return [new D3D12PmxMaterialTreatment(Saturation: 2.001f)];
        yield return [new D3D12PmxMaterialTreatment(TransitionWidthScale: .499f)];
        yield return [new D3D12PmxMaterialTreatment(TransitionWidthScale: 1.501f)];
    }

    [Theory, MemberData(nameof(InvalidScalars))]
    public void NonFiniteAndOutOfDomainScalarsAreRejected(D3D12PmxMaterialTreatment treatment)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Map(treatment).Validate(Frame()));

    [Fact]
    public void BothInclusiveDomainEndpointsAreAcceptedWithoutClamping()
    {
        foreach (var treatment in new[] { new D3D12PmxMaterialTreatment(0, .5f), new D3D12PmxMaterialTreatment(2, 1.5f) })
        {
            treatment.Validate();
            var map = Map(treatment);
            map.Validate(Frame());
            Assert.Equal(treatment, map.EffectivePmxMaterialTreatment(0));
        }
    }

    [Fact]
    public void FaceEyeRejectEvenAnIdentityTreatmentAndDedicatedPayloadsIgnoreIt()
    {
        foreach (var treatment in new[] { new D3D12PmxMaterialTreatment(), new D3D12PmxMaterialTreatment(1.02f, .9f) })
        {
            foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
                Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(role,
                    PmxMaterialTreatment: treatment).Validate());
            Assert.Throws<ArgumentException>(() => T8cTestHelpers.MaterialWithBinding(
                T8cTestHelpers.GenshinStyleId,
                T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.GenshinStyleId, 0),
                D3D12CharacterMaterialRole.Face,
                pmxMaterialTreatment: treatment).Validate());

            var faceOnBody = T8cTestHelpers.CreateDirectMap(
                [T8cTestHelpers.GenshinStyleId],
                [T8cTestHelpers.MaterialWithBinding(
                    T8cTestHelpers.GenshinStyleId,
                    T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.GenshinStyleId, 0),
                    D3D12CharacterMaterialRole.Body,
                    pmxMaterialTreatment: treatment)]);
            var genshinHair = T8cTestHelpers.CreateDirectMap(
                [T8cTestHelpers.GenshinStyleId],
                [T8cTestHelpers.MaterialWithBinding(
                    T8cTestHelpers.GenshinStyleId,
                    TestStylePlugins.GenshinHair(0),
                    D3D12CharacterMaterialRole.Hair,
                    pmxMaterialTreatment: treatment)]);
            var starMcHair = T8cTestHelpers.CreateDirectMap(
                [T8cTestHelpers.StarRailStyleId],
                [T8cTestHelpers.MaterialWithBinding(
                    T8cTestHelpers.StarRailStyleId,
                    T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 0),
                    D3D12CharacterMaterialRole.Hair,
                    pmxMaterialTreatment: treatment)]);
            foreach (var dedicated in new[] { faceOnBody, genshinHair, starMcHair })
            {
                dedicated.Validate(Frame(textures: [new("t", 1, 1, [0, 0, 0, 0])]));
                Assert.Null(dedicated.EffectivePmxMaterialTreatment(0));
            }
        }
    }

    [Theory]
    [InlineData(T8cTestHelpers.GenshinStyleId)]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    [InlineData(T8cTestHelpers.EndfieldStyleId)]
    public void OnlyExplicitOrdinaryBodyAndHairConsumeNonidentityValues(string family)
    {
        var treatment = new D3D12PmxMaterialTreatment(1.02f, .9f);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Hair })
        {
            var map = Map(treatment, family, role);
            map.Validate(Frame());
            Assert.Equal(treatment, map.EffectivePmxMaterialTreatment(0));
            Assert.NotSame(treatment, map.EffectivePmxMaterialTreatment(0));
            Assert.Equal(1, map.EffectiveNormalShadeStrength(0));
            Assert.Equal(1, map.EffectiveGeometricShadowStrength(0));
        }
        var absent = Map(null, family);
        var identity = Map(new(), family, D3D12CharacterMaterialRole.Hair);
        Assert.Null(absent.EffectivePmxMaterialTreatment(0));
        Assert.Null(identity.EffectivePmxMaterialTreatment(0));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(absent, identity));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(absent, Map(treatment, family)));
    }

    [Fact]
    public void GenericHairBandRequiresAnExplicitHairRoleWhileDormantWidthCanTravel()
    {
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(role,
                PmxMaterialTreatment: new(HairHighlightStrength: .2f)).Validate());
        new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            PmxMaterialTreatment: new(HairHighlightStrength: .2f)).Validate();
        new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Body,
            PmxMaterialTreatment: new(HairHighlightWidth: .3f)).Validate();
    }

    [Fact]
    public void OriginalMmdAcceptsValidOrdinaryDataAndConsumesNoTreatmentOrWidth()
    {
        var treatment = new D3D12PmxMaterialTreatment(2, 1.5f);
        var map = Map(treatment, T8cTestHelpers.MmdStyleId);
        var response = new CharacterToonResponse(.05f, .1f);
        map.Validate(Frame(response: response));
        Assert.Null(map.EffectivePmxMaterialTreatment(0));
        Assert.Same(response, map.EffectiveToonResponse(0, response));
        Assert.Null(map.EffectiveToonResponse(0, null));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, map));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map, Map(null, T8cTestHelpers.MmdStyleId)));
    }

    [Fact]
    public void MaterialOrderAndTreatmentValuesAreDetachedFromCallerArrays()
    {
        var treatment = new D3D12PmxMaterialTreatment(1.02f, .9f);
        string[] families = [T8cTestHelpers.StarRailStyleId, T8cTestHelpers.StarRailStyleId];
        D3D12CharacterMaterialStyle[] rows = [new(PmxMaterialTreatment: treatment), new()];
        var map = T8cTestHelpers.CreateDirectMap(families, rows);
        families[0] = T8cTestHelpers.MmdStyleId;
        rows[0] = new(PmxMaterialTreatment: new(0, 1.5f));
        Assert.Equal(T8cTestHelpers.StarRailStyleId, map.StyleIds[0]);
        Assert.Equal(treatment, map.EffectivePmxMaterialTreatment(0));
        Assert.NotSame(treatment, map.CharacterMaterials[0].PmxMaterialTreatment);
        Assert.Null(map.EffectivePmxMaterialTreatment(1));
        var equal = T8cTestHelpers.CreateDirectMap([T8cTestHelpers.StarRailStyleId, T8cTestHelpers.StarRailStyleId],
            [new(D3D12CharacterMaterialRole.Hair, PmxMaterialTreatment: treatment with { }), new()]);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map, equal));
        var reordered = T8cTestHelpers.CreateDirectMap(equal.StyleIds, [new(), new(PmxMaterialTreatment: treatment)]);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(map, reordered));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map, map.CaptureResolvedInputs(Frame(2))));
    }

    [Fact]
    public void ConsumedKeyDistinguishesBothScalarsAndRemovalButIgnoresUnusedControls()
    {
        var treatment = new D3D12PmxMaterialTreatment(1.02f, .9f);
        var map = Map(treatment);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map, Map(treatment with { })));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(map, Map(treatment with { Saturation = 1.01f })));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(map, Map(treatment with { TransitionWidthScale = 1.1f })));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(map, Map(null)));
        var unused = T8cTestHelpers.CreateDirectMap(map.StyleIds,
            [new(D3D12CharacterMaterialRole.Hair, .99f, .01f, EyeGeometricShadowStrength: .8f,
                PmxMaterialTreatment: treatment)]);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map, unused));
    }

    [Fact]
    public void EffectiveWidthUsesTheSelectedResponseOrTheExistingModernDefault()
    {
        var map = Map(new(1.02f, 1.5f));
        var derived = Assert.IsType<CharacterToonResponse>(map.EffectiveToonResponse(0, null));
        Assert.Equal(.5f, derived.Threshold);
        Assert.Equal(.1f * 1.5f, derived.TransitionWidth);
        var explicitResponse = new CharacterToonResponse(.4f, .2f);
        var modified = Assert.IsType<CharacterToonResponse>(map.EffectiveToonResponse(0, explicitResponse));
        Assert.Equal(.4f, modified.Threshold);
        Assert.Equal(.2f * 1.5f, modified.TransitionWidth);
        Assert.Equal(.2f, explicitResponse.TransitionWidth);
        map.Validate(Frame(response: explicitResponse));
        foreach (var inactive in new[] { Map(null), Map(new()), Map(new(1.02f), T8cTestHelpers.MmdStyleId) })
        {
            Assert.Same(explicitResponse, inactive.EffectiveToonResponse(0, explicitResponse));
            Assert.Null(inactive.EffectiveToonResponse(0, null));
        }
    }

    [Fact]
    public void AValidCustomIntervalCannotBecomeInvalidAfterTheWidthScale()
    {
        var response = new CharacterToonResponse(.05f, .1f);
        var frame = Frame(response: response);
        Map(new(1.02f, 1)).Validate(frame);
        Map(new(1.02f, .5f)).Validate(frame);
        Assert.Throws<ArgumentException>(() => Map(new(1.02f, 1.5f)).Validate(frame));
        Assert.Equal(.1f, response.TransitionWidth);
    }

    [Theory, InlineData(-1), InlineData(1), InlineData(int.MaxValue)]
    public void EffectiveHelpersRejectMaterialIndicesOutsideTheMap(int index)
    {
        var map = Map(new(1.02f));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.EffectivePmxMaterialTreatment(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.EffectiveToonResponse(index, null));
    }

    private static D3D12SurfaceStyleMap Map(D3D12PmxMaterialTreatment? treatment,
        string family = T8cTestHelpers.GenshinStyleId,
        D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body)
        => T8cTestHelpers.CreateDirectMap([family], [new(role, PmxMaterialTreatment: treatment)]);

    private static OriginalFrameScene Frame(int count = 1, CharacterToonResponse? response = null, SceneTexture[]? textures = null)
    {
        SceneVertex[] vertices = [new(new(-1, -1, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, 0), -Vector3.UnitZ, Vector2.Zero), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.Zero)];
        var materials = Enumerable.Range(0, count).Select(i => new SceneMaterial($"explicit ordinary material {i}",
            i * 3, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 10)).ToArray();
        var snapshot = new SceneSnapshot(vertices, Enumerable.Range(0, count).SelectMany(_ => new uint[] { 0, 2, 1 }).ToArray(), materials, textures);
        return new(new(snapshot, Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true), count).ToArray(),
            new(Vector3.One, Vector3.One, Matrix4x4.Identity)), options: new(OriginalAlphaBlendMode.Normal, true, false),
            materials: Enumerable.Repeat(new OriginalFrameMaterialFlags(false, false, false) { CharacterToonResponse = response }, count));
    }
}
