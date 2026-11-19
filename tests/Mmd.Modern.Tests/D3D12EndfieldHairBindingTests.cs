using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12EndfieldHairBindingTests
{
    [Fact]
    public void ExplicitHairBindingRequiresItsFamilyImageAndFallsBackWhenSeedsOmitted()
    {
        var binding = T8dTestHelpers.EndfieldHair(0, new(.3f, .2f, .1f));
        Assert.True(T8dTestHelpers.ValidateEndfield(binding, StyleMaterialRole.Hair, 1).IsValid);
        Assert.Equal(0, T8dTestHelpers.ReadCbvVectors(T8dTestHelpers.BuildEndfieldDrawData([binding], StyleMaterialRole.Hair)!)[0].W);
        var map = Map(binding); map.Validate(Frame());
        Assert.Equal("hair", map.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.Equal(1, map.EffectiveNormalShadeStrength(0));
        Assert.Equal(1, map.EffectiveGeometricShadowStrength(0));
        // Absent raw radial seeds fall back to deterministic geometric radial seeds.
        map.Validate(Frame(seeds: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(T8dTestHelpers.EndfieldHair(1, new(.3f, .2f, .1f))).Validate(Frame()));
        Assert.False(T8dTestHelpers.ValidateEndfield(T8dTestHelpers.EndfieldHair(1, new(.3f, .2f, .1f)), StyleMaterialRole.Hair, 1).IsValid);
        Assert.Throws<NotSupportedException>(() => new D3D12SurfaceStyleMap(["mmd"], [map.CharacterMaterials[0]], TestStylePlugins.AllPresetCatalog).Validate(Frame()));
        foreach (var family in new[] { "genshin", "star-rail", "wuthering-waves" })
            Assert.NotEqual("hair", TestStylePlugins.GetStyle(family).BuildMaterialDrawData(
                new(Guid.Empty, 0, null, StyleMaterialRole.Hair, [binding], Vector4.One, Vector3.Zero, 1, Vector3.Zero, -1, -1, 0, -1, -1, false, -Vector3.UnitZ, Vector3.Zero, new(0, 0, -5), null, null, new()))?.ShaderModuleId);
        // An explicit zero stream is authored data, not an absent stream.
        map.Validate(Frame(seed: Vector3.Zero));
    }

    [Fact]
    public void ConsumedWorldDirectionOverflowFailsButTranslationAndInactiveStreamsAreIgnored()
    {
        var binding = T8dTestHelpers.EndfieldHair(0, Vector3.One);
        var large = new Vector3(float.MaxValue, 1, 0);
        Assert.Throws<ArgumentException>(() => Map(binding).Validate(Frame(seed: large, world: Matrix4x4.CreateScale(2))));
        Map(binding).Validate(Frame(seed: large, world: Matrix4x4.CreateTranslation(new Vector3(float.MaxValue))));
        Map(null).Validate(Frame(seed: large, world: Matrix4x4.CreateScale(2)));
        Map(binding).Validate(Frame(seed: Vector3.Zero, world: Matrix4x4.CreateScale(-2, 3, 1)));
    }

    [Fact]
    public void RolesAndDedicatedPayloadsAreExclusiveIncludingIdentityOrdinaryTreatment()
    {
        var binding = T8dTestHelpers.EndfieldHair(0, Vector3.One);
        foreach (var role in new[] { StyleMaterialRole.Body, StyleMaterialRole.Face, StyleMaterialRole.Eye })
            Assert.False(T8dTestHelpers.ValidateEndfield(binding, role).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([binding, T8dTestHelpers.EndfieldFaceSdf()], StyleMaterialRole.Hair).IsValid);
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            PmxMaterialTreatment: new(HairHighlightStrength: 0.2f),
            PluginDraw: D3D12StylePluginAdapter.ToD3D12DrawData(T8dTestHelpers.BuildEndfieldDrawData([binding], StyleMaterialRole.Hair)! with { TreatmentOverride = new(HairHighlightStrength: 0.2f) })).Validate());
        var hairMap = Map(binding);
        Assert.Equal("hair", hairMap.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.Null(new D3D12CharacterMaterialStyle().PluginDraw);
    }

    public static IEnumerable<object[]> InvalidValues()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -10.01f, 10.01f })
            yield return [T8dTestHelpers.EndfieldHair(0, Vector3.One, value)];
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.001f, 16.001f })
            for (int i = 0; i < 3; i++)
                yield return [T8dTestHelpers.EndfieldHair(0, i == 0 ? new(value, 1, 1) : i == 1 ? new(1, value, 1) : new(1, 1, value))];
        yield return [T8dTestHelpers.EndfieldHair(-1, Vector3.One)];
    }

    [Theory, MemberData(nameof(InvalidValues))]
    public void NonFiniteAndOutOfDomainValuesFailBeforeRecording(StyleMaterialBindingData binding)
        => Assert.False(T8dTestHelpers.ValidateEndfield(binding, StyleMaterialRole.Hair, 1).IsValid);

    [Fact]
    public void InclusiveSignedBrightnessAndHdrPaletteRemainUnclamped()
    {
        foreach (float value in new[] { -10f, -1.5001001358032227f, 0f, 10f })
        {
            var binding = T8dTestHelpers.EndfieldHair(0, new(0, 1, 16), value);
            Assert.True(T8dTestHelpers.ValidateEndfield(binding, StyleMaterialRole.Hair, 1).IsValid);
            var vec = T8dTestHelpers.ReadCbvVectors(T8dTestHelpers.BuildEndfieldDrawData([binding], StyleMaterialRole.Hair)!)[0];
            Assert.Equal(value, vec.W);
            Assert.Equal(16, vec.Z);
        }
    }

    [Fact]
    public void PublicKeysConsumeSlotsPaletteBrightnessAndRemoval()
    {
        var binding = T8dTestHelpers.EndfieldHair(0, Vector3.One);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(binding with { })));
        foreach (var changed in new[] { T8dTestHelpers.EndfieldHair(1, Vector3.One), T8dTestHelpers.EndfieldHair(0, new(.5f)),
            T8dTestHelpers.EndfieldHair(0, Vector3.One, -3f) })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(changed)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(null)));
    }

    [Fact]
    public void DetachedKeysConsumeTextureBytesAndImmutableRawSeedValues()
    {
        var binding = T8dTestHelpers.EndfieldHair(0, Vector3.One);
        var map = Map(binding);
        var first = map.CaptureResolvedInputs(Frame());
        Assert.NotSame(first, map);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture("other owner")))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(g: 128)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(seed: Vector3.UnitY))));
        // Detached snapshots of a reusable public map retain the previous successful inputs.
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame())));
        var rebased = Map(T8dTestHelpers.EndfieldHair(1, Vector3.One)).CaptureResolvedInputs(Frame(textures: [Texture(g: 0), Texture()]));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        var inactive = Map(null);
        Assert.Same(inactive, inactive.CaptureResolvedInputs(Frame(seeds: false)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(inactive, Map(null).CaptureResolvedInputs(Frame(seed: Vector3.UnitY))));
    }

    internal static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding, string family = "endfield", OriginalFrameScene? frame = null)
        => T8dTestHelpers.CreateDirectSurfaceStyleMap(frame ?? Frame(), [family],
            [new(D3D12CharacterMaterialRole.Hair)], [binding is null ? null : [binding]]);
    internal static SceneTexture Texture(string name = "explicit green mask", byte r = 90, byte g = 255, byte b = 12, byte a = 0)
        => new(name, 1, 1, [r, g, b, a]);
    internal static OriginalFrameScene Frame(SceneTexture? texture = null, bool seeds = true, Vector3? seed = null, SceneTexture[]? textures = null, Matrix4x4? world = null)
    {
        SceneVertex[] vertices = [new(new(-1.5f, -1.1f, 0), -Vector3.UnitZ, new(0, 1)),
            new(new(1.5f, -1.1f, 0), -Vector3.UnitZ, new(1, 1)),
            new(new(1.5f, 1.1f, 0), -Vector3.UnitZ, new(1, 0)),
            new(new(-1.5f, 1.1f, 0), -Vector3.UnitZ, new(0, 0))];
        var snapshot = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("explicit radial receiver", 0, 6, new(.25f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 10)],
            textures ?? [texture ?? Texture()], world: world, camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 30),
            lightDirection: -Vector3.UnitZ,
            rawRadialSeeds: seeds ? new SceneRawRadialSeeds(Enumerable.Repeat(seed ?? Vector3.UnitX, 4).ToArray()) : null);
        return new(new(snapshot, [new(new(.22f), DoubleSided: true)],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)), materials: [new(false, false, false)]);
    }
}
