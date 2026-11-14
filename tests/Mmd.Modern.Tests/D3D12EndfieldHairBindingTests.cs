using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12EndfieldHairBindingTests
{
    [Fact]
    public void ExplicitHairBindingRequiresItsFamilyImageAndAuthoredSeedStream()
    {
        var binding = new D3D12EndfieldHairBinding(0, new(.3f, .2f, .1f));
        binding.Validate();
        Assert.Equal(0, binding.Brightness);
        var map = Map(binding); map.Validate(Frame());
        Assert.True(map.HasConsumedEndfieldHair);
        Assert.Same(binding, map.EffectiveEndfieldHair(0));
        Assert.Equal(1, map.EffectiveNormalShadeStrength(0));
        Assert.Equal(1, map.EffectiveGeometricShadowStrength(0));
        Assert.Throws<ArgumentException>(() => map.Validate(Frame(seeds: false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(binding with { CombinedTextureIndex = 1 }).Validate(Frame()));
        foreach (var family in new[] { D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx,
            D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx })
            Assert.Throws<NotSupportedException>(() => Map(binding, family).Validate(Frame()));
        // An explicit zero stream is authored data, not an absent stream.
        map.Validate(Frame(seed: Vector3.Zero));
    }

    [Fact]
    public void ConsumedWorldDirectionOverflowFailsButTranslationAndInactiveStreamsAreIgnored()
    {
        var binding = new D3D12EndfieldHairBinding(0, Vector3.One);
        var large = new Vector3(float.MaxValue, 1, 0);
        Assert.Throws<ArgumentException>(() => Map(binding).Validate(Frame(seed: large, world: Matrix4x4.CreateScale(2))));
        Map(binding).Validate(Frame(seed: large, world: Matrix4x4.CreateTranslation(new Vector3(float.MaxValue))));
        Map(null).Validate(Frame(seed: large, world: Matrix4x4.CreateScale(2)));
        Map(binding).Validate(Frame(seed: Vector3.Zero, world: Matrix4x4.CreateScale(-2, 3, 1)));
    }

    [Fact]
    public void RolesAndDedicatedPayloadsAreExclusiveIncludingIdentityOrdinaryTreatment()
    {
        var binding = new D3D12EndfieldHairBinding(0, Vector3.One);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(role, EndfieldHair: binding).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            EndfieldHair: binding, FaceSdf: new(0, Vector3.UnitZ, Vector3.UnitX)).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            EndfieldHair: binding, GenshinNonMetallicSpecular: new(0)).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            EndfieldHair: binding, StarMcHair: new(0, 0)).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            EndfieldHair: binding, PmxMaterialTreatment: new()).Validate());
        Assert.Null(new D3D12CharacterMaterialStyle().EndfieldHair);
    }

    public static IEnumerable<object[]> InvalidValues()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -10.01f, 10.01f })
            yield return [new D3D12EndfieldHairBinding(0, Vector3.One, value)];
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.001f, 16.001f })
            for (int i = 0; i < 3; i++)
                yield return [new D3D12EndfieldHairBinding(0, i == 0 ? new(value, 1, 1) : i == 1 ? new(1, value, 1) : new(1, 1, value))];
        yield return [new D3D12EndfieldHairBinding(-1, Vector3.One)];
    }

    [Theory, MemberData(nameof(InvalidValues))]
    public void NonFiniteAndOutOfDomainValuesFailBeforeRecording(D3D12EndfieldHairBinding binding)
        => Assert.Throws<ArgumentOutOfRangeException>(binding.Validate);

    [Fact]
    public void InclusiveSignedBrightnessAndHdrPaletteRemainUnclamped()
    {
        foreach (float value in new[] { -10f, -1.5001001358032227f, 0f, 10f })
        {
            var binding = new D3D12EndfieldHairBinding(0, new(0, 1, 16), value);
            binding.Validate(); Assert.Equal(value, binding.Brightness); Assert.Equal(16, binding.PaletteColor.Z);
        }
    }

    [Fact]
    public void PublicKeysConsumeSlotsPaletteBrightnessAndRemoval()
    {
        var binding = new D3D12EndfieldHairBinding(0, Vector3.One);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(binding with { })));
        foreach (var changed in new[] { binding with { CombinedTextureIndex = 1 }, binding with { PaletteColor = new(.5f) },
            binding with { Brightness = -3f } })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(changed)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(null)));
    }

    [Fact]
    public void DetachedKeysConsumeOnlyNumericalGreenAndImmutableRawSeedValues()
    {
        var binding = new D3D12EndfieldHairBinding(0, Vector3.One);
        var map = Map(binding);
        var first = map.CaptureFaceTextures(Frame());
        Assert.NotSame(first, map);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture("other owner")))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(r: 0, b: 0, a: 255)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(g: 128)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(seed: Vector3.UnitY))));
        // Detached snapshots of a reusable public map retain the previous successful inputs.
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame())));
        var rebased = Map(binding with { CombinedTextureIndex = 1 }).CaptureFaceTextures(Frame(textures: [Texture(g: 0), Texture()]));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        var inactive = Map(null);
        Assert.Same(inactive, inactive.CaptureFaceTextures(Frame(seeds: false)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(inactive, Map(null).CaptureFaceTextures(Frame(seed: Vector3.UnitY))));
    }

    internal static D3D12SurfaceStyleMap Map(D3D12EndfieldHairBinding? binding, D3D12SurfaceStyle family = D3D12SurfaceStyle.EndfieldPmx)
        => new([family], [new(D3D12CharacterMaterialRole.Hair, EndfieldHair: binding)]);
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
