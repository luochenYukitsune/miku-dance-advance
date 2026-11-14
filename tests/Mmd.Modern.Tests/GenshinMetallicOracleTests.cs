using System.Numerics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicOracleTests
{
    [Fact]
    public void FloatThresholdRawGateAndBothMixFactorsStaySeparate()
    {
        Assert.Equal(0, GenshinMetallicOracle.RawGate(MathF.BitDecrement(.9f), 1));
        Assert.Equal(0, GenshinMetallicOracle.RawGate(.9f, 1));
        Assert.Equal(1, GenshinMetallicOracle.RawGate(MathF.BitIncrement(.9f), 1));
        foreach (float enable in new[] { -2f, 0, .25f, 1, 2 })
            Assert.Equal(enable, GenshinMetallicOracle.RawGate(1, enable));
        var nonmetal = new Vector3(.2f, .4f, .6f); var tint = new Vector3(2, 3, 4);
        Assert.Equal(nonmetal, GenshinMetallicOracle.Palette(nonmetal, tint, -2));
        Assert.Equal(nonmetal, GenshinMetallicOracle.Palette(nonmetal, tint, 0));
        Assert.Equal(tint, GenshinMetallicOracle.Palette(nonmetal, tint, 2));
        Near(new(.9f, 1.4f, 2), GenshinMetallicOracle.Palette(nonmetal, tint, .5f));
    }

    [Fact]
    public void AllImageChannelsUseLuminanceAndSourcePaletteUsesPlainGamma()
    {
        foreach (var (image, q) in new[] { (Vector3.UnitX, .2126f), (Vector3.UnitY, .7152f), (Vector3.UnitZ, .0722f) })
            Near(new(q), GenshinMetallicOracle.LocalTint(image, 1, Vector3.Zero, Vector3.One, Vector3.One, .8f));
        Assert.Equal(-2, GenshinMetallicOracle.Gamma(-2));
        Assert.Equal(0, GenshinMetallicOracle.Gamma(0));
        Assert.True(GenshinMetallicOracle.Gamma(2) > 4);
        Assert.NotEqual(GenshinMetallicOracle.Decode(.5f), GenshinMetallicOracle.Gamma(.5f));
        var shadow = new Vector3(.5f);
        var at = GenshinMetallicOracle.LocalTint(Vector3.One, 1, Vector3.One, Vector3.One, shadow, .8f);
        Assert.Equal(Vector3.One, at);
        Assert.Equal(at, GenshinMetallicOracle.LocalTint(Vector3.One, 1, Vector3.One, Vector3.One, shadow, MathF.BitDecrement(.8f)));
        Assert.True(GenshinMetallicOracle.LocalTint(Vector3.One, 1, Vector3.One, Vector3.One, shadow, 1).X < at.X);
        Near(Vector3.One, GenshinMetallicOracle.LocalTint(Vector3.One, -1, Vector3.One, new(8), shadow, 0));
    }

    [Fact]
    public void CameraBasisKeepsCrossLengthsRollPerspectiveAndDegeneracy()
    {
        static Vector2 Uv(Vector3 position, Vector3 eye, Vector3 up, bool ortho = false, float tile = 1)
            => GenshinMetallicOracle.Coordinates(Vector3.UnitX, position, eye, up, Vector3.UnitZ, ortho, tile);
        Assert.Equal(new Vector2(1, .5f), Uv(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY));
        Assert.Equal(new Vector2(.5f), Uv(Vector3.Zero, Vector3.UnitY, Vector3.UnitY));
        Assert.NotEqual(Uv(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY), Uv(Vector3.Zero, new(1, 1, 1), Vector3.UnitY));
        Assert.Equal(Uv(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, true), Uv(new(3), new(8), Vector3.UnitY, true));
        Assert.NotEqual(Uv(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY), Uv(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX));
        var first = Uv(Vector3.Zero, new(1, 1, 1), Vector3.UnitY);
        var tiled = Uv(Vector3.Zero, new(1, 1, 1), Vector3.UnitY, tile: 3);
        Assert.Equal(first.Y, tiled.Y);
        Assert.InRange(Math.Abs((first.X - .5f) * 3 + .5f - tiled.X), 0, .000001f);
        Assert.InRange(first.X, .5f, .999f); // Normalizing either cross would incorrectly change this.
    }

    [Fact]
    public void CubicDecodesBeforeFilteringIgnoresAlphaAndRepeatsAsymmetricRows()
    {
        byte[] pixels = [255, 0, 0, 0, 0, 255, 0, 64, 0, 0, 255, 128, 255, 255, 255, 255];
        var uv = new Vector2(.19f, .81f);
        var original = GenshinMetallicOracle.CubicRepeat(pixels, 2, 2, uv, true);
        var changed = pixels.ToArray(); for (int i = 3; i < changed.Length; i += 4) changed[i] ^= 255;
        Assert.Equal(original, GenshinMetallicOracle.CubicRepeat(changed, 2, 2, uv, true));
        Near(original, GenshinMetallicOracle.CubicRepeat(pixels, 2, 2, uv + new Vector2(2, -3), true));
        Assert.NotEqual(original, GenshinMetallicOracle.CubicRepeat(pixels, 2, 2, new(uv.X, 1 - uv.Y), true));
        byte[] encoded = [0, 0, 0, 0, 255, 255, 255, 255];
        Near(new(.5f), GenshinMetallicOracle.CubicRepeat(encoded, 2, 1, new(.5f), true));
        Assert.True(Math.Abs(GenshinMetallicOracle.Decode(.5f) - .5f) > .2f);
    }

    [Fact]
    public void BindingValidatesEveryConsumedFieldAndIgnoresInactiveUniform()
    {
        var binding = Binding(); binding.Validate();
        (binding with { DarkColor = new(-1, 2, 16), TileScale = -3, Brightness = -1, EnableMetallics = 2 }).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { LightmapCombinedTextureIndex = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { MatcapCombinedTextureIndex = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { MatcapEncoding = (D3D12MatcapColorEncoding)99 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { LitMode = (D3D12GenshinMetallicLitMode)99 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { DarkColor = new(float.NaN) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { LightColor = new(17) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { ShadowMultiplyColor = new(float.PositiveInfinity) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { Brightness = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { TileScale = 33 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { EnableMetallics = float.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { UniformLitFactor = float.NaN }).Validate());
        Assert.True(D3D12GenshinMetallicBinding.ConsumedEquivalent(binding, binding with { UniformLitFactor = 2 }));
        var uniform = binding with { LitMode = D3D12GenshinMetallicLitMode.Uniform };
        Assert.False(D3D12GenshinMetallicBinding.ConsumedEquivalent(uniform, uniform with { UniformLitFactor = 2 }));
        Assert.False(D3D12GenshinMetallicBinding.ConsumedEquivalent(binding, binding with { Brightness = 2 }));
        Assert.False(D3D12GenshinMetallicBinding.ConsumedEquivalent(binding, binding with { MatcapEncoding = D3D12MatcapColorEncoding.SrgbRgb }));
        Assert.False(D3D12GenshinMetallicBinding.ConsumedEquivalent(binding, binding with { LightmapCombinedTextureIndex = 8 }));
        Assert.True(D3D12GenshinMetallicBinding.ConsumedEquivalent(binding, binding with { LightmapCombinedTextureIndex = 8 }, false));
    }

    internal static D3D12GenshinMetallicBinding Binding() => new(0, 1, D3D12MatcapColorEncoding.LinearRgb, Vector3.Zero, Vector3.One, Vector3.One);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, .000002f);
}
