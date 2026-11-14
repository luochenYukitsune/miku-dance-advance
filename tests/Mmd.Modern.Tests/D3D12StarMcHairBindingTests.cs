using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12StarMcHairBindingTests
{
    [Fact]
    public void ExplicitTwoMaskDefaultsAppendWithoutChangingExistingMaterialCalls()
    {
        Assert.Equal(0, (int)D3D12HairHighlightChannel.Blue);
        Assert.Equal(1, (int)D3D12HairHighlightChannel.Alpha);
        var binding = new D3D12StarMcHairBinding(0, 1);
        Assert.Equal(D3D12HairHighlightChannel.Blue, binding.HighlightChannel);
        Assert.Equal(0, binding.HighlightCoefficient);
        Assert.Equal(2, binding.HighlightBrightness);
        Assert.Equal(.5f, binding.ShadowCoefficient);
        Assert.Equal(.5f, binding.ShadowSoftness);
        Assert.Equal(.1f, binding.Intensity);
        binding.Validate();
        var oldCaller = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair, .35f, .6f, null, .2f, new(0));
        Assert.Null(oldCaller.StarMcHair);
        foreach (var family in new[] { D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx })
        {
            var map = Map(binding, family);
            map.Validate(Frame(Texture(), Texture()));
            Assert.True(map.HasConsumedStarMcHair);
            Assert.Same(binding, map.EffectiveStarMcHair(0));
            Assert.Equal(1, map.EffectiveNormalShadeStrength(0));
            Assert.Equal(1, map.EffectiveGeometricShadowStrength(0));
        }
    }

    public static IEnumerable<object[]> InvalidScalars()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            yield return [new D3D12StarMcHairBinding(0, 1, HighlightCoefficient: value)];
            yield return [new D3D12StarMcHairBinding(0, 1, HighlightBrightness: value)];
            yield return [new D3D12StarMcHairBinding(0, 1, ShadowCoefficient: value)];
            yield return [new D3D12StarMcHairBinding(0, 1, ShadowSoftness: value)];
            yield return [new D3D12StarMcHairBinding(0, 1, Intensity: value)];
        }
        foreach (float value in new[] { -.01f, 1.01f })
        {
            yield return [new D3D12StarMcHairBinding(0, 1, HighlightCoefficient: value)];
            yield return [new D3D12StarMcHairBinding(0, 1, ShadowCoefficient: value)];
        }
        foreach (float value in new[] { -1.01f, 10.01f })
            yield return [new D3D12StarMcHairBinding(0, 1, HighlightBrightness: value)];
        foreach (float value in new[] { .099f, 1.01f })
            yield return [new D3D12StarMcHairBinding(0, 1, ShadowSoftness: value)];
        foreach (float value in new[] { -.01f, 16.01f })
            yield return [new D3D12StarMcHairBinding(0, 1, Intensity: value)];
    }

    [Theory, MemberData(nameof(InvalidScalars))]
    public void NonFiniteAndOutOfDomainScalarsAreRejected(D3D12StarMcHairBinding binding)
        => Assert.Throws<ArgumentOutOfRangeException>(binding.Validate);

    [Fact]
    public void InclusiveAdapterEndpointsRetainNegativeAndLargeBrightness()
    {
        var minimum = new D3D12StarMcHairBinding(0, 1, HighlightCoefficient: 0, HighlightBrightness: -1,
            ShadowCoefficient: 0, ShadowSoftness: .1f, Intensity: 0);
        var maximum = new D3D12StarMcHairBinding(0, 1, HighlightCoefficient: 1, HighlightBrightness: 10,
            ShadowCoefficient: 1, ShadowSoftness: 1, Intensity: 16);
        minimum.Validate(); maximum.Validate();
        Assert.Equal(-1, minimum.HighlightBrightness);
        Assert.Equal(10, maximum.HighlightBrightness);
    }

    [Theory, InlineData(-1), InlineData(2), InlineData(int.MaxValue)]
    public void BothTextureSlotsMustExistInTheActualComposedTable(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(new(index, 1)).Validate(Frame(Texture(), Texture())));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(new(0, index)).Validate(Frame(Texture(), Texture())));
    }

    [Fact]
    public void HairRoleAndExclusiveFamilyPayloadAreRequired()
    {
        var binding = new D3D12StarMcHairBinding(0, 1);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(role, StarMcHair: binding).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            FaceSdf: new(0, -Vector3.UnitZ, Vector3.UnitX), StarMcHair: binding).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            GenshinNonMetallicSpecular: new(0), StarMcHair: binding).Validate());
        foreach (var family in new[] { D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.EndfieldPmx })
            Assert.Throws<NotSupportedException>(() => Map(binding, family).Validate(Frame(Texture(), Texture())));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(binding with { HighlightBrightness = float.NaN }).Validate(Frame(Texture(), Texture())));
    }

    [Fact]
    public void NumericalAlphaIsExplicitAndOnlySupportedForWuthering()
    {
        var binding = new D3D12StarMcHairBinding(0, 1, D3D12HairHighlightChannel.Alpha);
        var map = Map(binding, D3D12SurfaceStyle.WutheringPmx);
        map.Validate(Frame(Texture(), Texture()));
        Assert.Same(binding, map.EffectiveStarMcHair(0));
        Assert.Throws<NotSupportedException>(() => Map(binding).Validate(Frame(Texture(), Texture())));
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { HighlightChannel = (D3D12HairHighlightChannel)123 }).Validate());
    }

    [Theory]
    [InlineData(D3D12SurfaceStyle.Mmd)]
    [InlineData(D3D12SurfaceStyle.GenshinPmx)]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [InlineData(D3D12SurfaceStyle.EndfieldPmx)]
    public void OrdinaryHairKeepsTheEstablishedBodyValuesAcrossEveryFamily(D3D12SurfaceStyle family)
    {
        var hair = Map(null, family);
        var body = new D3D12SurfaceStyleMap([family]);
        hair.Validate(Frame(Texture()));
        Assert.Null(hair.EffectiveStarMcHair(0));
        Assert.False(hair.HasConsumedStarMcHair);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(body, hair));
        Assert.Same(hair, hair.CaptureFaceTextures(Frame(Texture())));
        Assert.Equal(body.EffectiveNormalShadeStrength(0), hair.EffectiveNormalShadeStrength(0));
        Assert.Equal(body.EffectiveGeometricShadowStrength(0), hair.EffectiveGeometricShadowStrength(0));
    }

    [Fact]
    public void UncapturedKeysIncludeBothSlotsEveryScalarTheChannelAndRemoval()
    {
        var binding = new D3D12StarMcHairBinding(0, 1);
        var first = Map(binding, D3D12SurfaceStyle.WutheringPmx);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(binding with { }, D3D12SurfaceStyle.WutheringPmx)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(null, D3D12SurfaceStyle.WutheringPmx)));
        foreach (var changed in new[] { binding with { HighlightCombinedTextureIndex = 2 }, binding with { ShadowCombinedTextureIndex = 2 },
            binding with { HighlightChannel = D3D12HairHighlightChannel.Alpha }, binding with { HighlightCoefficient = .2f },
            binding with { HighlightBrightness = -1 }, binding with { ShadowCoefficient = .25f },
            binding with { ShadowSoftness = .6f }, binding with { Intensity = .3f } })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(changed, D3D12SurfaceStyle.WutheringPmx)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(binding with { HighlightCoefficient = 0, HighlightBrightness = 0, ShadowCoefficient = 0, Intensity = 0 }),
            Map(binding with { HighlightCoefficient = -0f, HighlightBrightness = -0f, ShadowCoefficient = -0f, Intensity = -0f })));
    }

    [Fact]
    public void DetachedBlueKeysConsumeOnlyHighlightBlueAndIndependentShadowRed()
    {
        var map = Map(new(0, 1));
        var first = map.CaptureFaceTextures(Frame(Texture(), Texture()));
        Assert.NotSame(first, map);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture("new owner"), Texture("new shadow owner")))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(r: 0, g: 200, a: 255), Texture(g: 200, b: 0, a: 255)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(b: 129), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(), Texture(r: 65)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(WideTexture(), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(), WideTexture()))));
        var rebased = Map(new(2, 0)).CaptureFaceTextures(Frame(Texture("shadow rebased"), Texture(b: 0), Texture("highlight rebased")));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        // Later captures of the reusable public map cannot replace the submitted detached data.
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(b: 1), Texture(r: 1)))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(), Texture()))));
    }

    [Fact]
    public void DetachedAlphaKeysConsumeNumericalAlphaWithoutConflatingBlueOrCoverage()
    {
        var map = Map(new(0, 1, D3D12HairHighlightChannel.Alpha), D3D12SurfaceStyle.WutheringPmx);
        var first = map.CaptureFaceTextures(Frame(Texture(), Texture()));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(r: 0, g: 250, b: 0), Texture(g: 0, b: 0, a: 255)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(a: 1), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(), Texture(r: 0)))));
        var blue = Map(new(0, 1), D3D12SurfaceStyle.WutheringPmx).CaptureFaceTextures(Frame(Texture(a: 128), Texture()));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, blue));
    }

    [Theory, InlineData(D3D12HairHighlightChannel.Blue), InlineData(D3D12HairHighlightChannel.Alpha)]
    public void OneImageMayExplicitlySupplyBothMasksWithBothConsumedChannelsRetained(D3D12HairHighlightChannel channel)
    {
        var map = Map(new(0, 0, channel), D3D12SurfaceStyle.WutheringPmx);
        var frame = Frame(Texture()); map.Validate(frame);
        var first = map.CaptureFaceTextures(frame);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(r: 0)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(channel == D3D12HairHighlightChannel.Blue ? Texture(b: 0) : Texture(a: 1)))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(g: 255)))));
    }

    [Fact]
    public void MixedFaceGenshinHairAndBothNewHairFamiliesKeepIndependentDetachedInputs()
    {
        var map = new D3D12SurfaceStyleMap(
            [D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.Mmd],
            [new(D3D12CharacterMaterialRole.Face, FaceSdf: new(0, -Vector3.UnitZ, Vector3.UnitX)),
                new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(1)),
                new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(2, 3)),
                new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(4, 5, D3D12HairHighlightChannel.Alpha)), new()]);
        SceneTexture[] textures = Enumerable.Range(0, 6).Select(i => Texture($"mixed data {i}")).ToArray();
        var frame = MakeFrame(5, textures); map.Validate(frame);
        var first = map.CaptureFaceTextures(frame);
        Assert.True(map.HasConsumedFaceSdf); Assert.True(map.HasConsumedGenshinNonMetallicSpecular); Assert.True(map.HasConsumedStarMcHair);
        SceneTexture[] unconsumed = [Texture(g: 1, b: 1, a: 1), Texture(g: 1, a: 1), Texture(r: 1, g: 1, a: 1),
            Texture(g: 1, b: 1, a: 1), Texture(r: 1, g: 1, b: 1), Texture(g: 1, b: 1, a: 1)];
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(MakeFrame(5, unconsumed))));
        for (int i = 0; i < textures.Length; i++)
        {
            var changed = textures.ToArray();
            changed[i] = i switch { 2 => Texture(b: 0), 4 => Texture(a: 1), _ => Texture(r: 0) };
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(MakeFrame(5, changed))));
        }
    }

    private static D3D12SurfaceStyleMap Map(D3D12StarMcHairBinding? binding, D3D12SurfaceStyle family = D3D12SurfaceStyle.StarRailPmx)
        => new([family], [new(D3D12CharacterMaterialRole.Hair, StarMcHair: binding)]);

    private static SceneTexture Texture(string name = "explicit numerical masks", byte r = 64, byte g = 17, byte b = 128, byte a = 0)
        => new(name, 1, 1, [r, g, b, a]);
    private static SceneTexture WideTexture() => new("same channels larger", 2, 1, [64, 17, 128, 0, 64, 17, 128, 0]);
    private static OriginalFrameScene Frame(params SceneTexture[] textures) => MakeFrame(1, textures);
    private static OriginalFrameScene MakeFrame(int count, params SceneTexture[] textures)
    {
        SceneVertex[] vertices = [new(new(-1, -1, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, 0), -Vector3.UnitZ, Vector2.Zero), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.Zero)];
        var materials = Enumerable.Range(0, count).Select(i => new SceneMaterial($"explicit material {i}", i * 3, 3,
            Vector4.One, Vector3.One, Vector3.Zero, 10)).ToArray();
        var indices = Enumerable.Range(0, count).SelectMany(_ => new uint[] { 0, 2, 1 }).ToArray();
        var snapshot = new SceneSnapshot(vertices, indices, materials, textures);
        return new(new(snapshot, Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true), count).ToArray(),
            new(Vector3.One, Vector3.One, Matrix4x4.Identity)), options: new(OriginalAlphaBlendMode.Normal, true, false));
    }
}
