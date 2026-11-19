using System.Numerics;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
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
        var binding = Binding();
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body, null, binding).IsValid);
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body, null,
            MakeBinding(darkColor: new(-1, 2, 16), tileScale: -3, brightness: -1, enableMetallics: 2)).IsValid);
        foreach (var bad in new[] {
            MakeBinding(lightmapSlot: -1),
            MakeBinding(matcapSlot: -1),
            MakeBinding(matcapEncoding: "Unknown"),
            MakeBinding(litMode: "Unknown"),
            MakeBinding(darkColor: new(float.NaN)),
            MakeBinding(lightColor: new(17)),
            MakeBinding(shadowMultiplyColor: new(float.PositiveInfinity)),
            MakeBinding(brightness: float.NaN),
            MakeBinding(tileScale: 33),
            MakeBinding(enableMetallics: float.PositiveInfinity),
            MakeBinding(uniformLitFactor: float.NaN),
        })
        {
            Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body, null, bad).IsValid);
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([],
                [TestStylePlugins.Bind(Guid.NewGuid(), 0, bad)], TestStylePlugins.AllViewCatalog));
        }

        static D3D12SurfaceStyleMap Map(StyleMaterialBindingData b)
            => T8bTestHelpers.CreateGenshinMap(D3D12CharacterMaterialRole.Body, b);

        var uniform = MakeBinding(litMode: "Uniform");
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(uniform), Map(MakeBinding(litMode: "Uniform", uniformLitFactor: 2))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(MakeBinding(brightness: 2))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(MakeBinding(matcapEncoding: "SrgbRgb"))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(MakeBinding(lightmapSlot: 8))));

        var frame = T8bTestHelpers.CreateMetallicFrame(Enumerable.Repeat(new Mmd.Modern.Core.SceneTexture("t", 1, 1, [240, 70, 40, 230]), 9).ToArray());
        var capA = Map(binding).CaptureResolvedInputs(frame);
        var capB = Map(MakeBinding(lightmapSlot: 8)).CaptureResolvedInputs(frame);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(capA, capB));
    }

    internal static StyleMaterialBindingData MakeBinding(
        int lightmapSlot = 0,
        int matcapSlot = 1,
        string matcapEncoding = "LinearRgb",
        Vector3? darkColor = null,
        Vector3? lightColor = null,
        Vector3? shadowMultiplyColor = null,
        float enableMetallics = 1f,
        float tileScale = 1f,
        float brightness = 1f,
        string litMode = "PrimaryResponse",
        float uniformLitFactor = 1f)
        => TestStylePlugins.GenshinMetallic(
            lightmapSlot, matcapSlot, matcapEncoding,
            darkColor ?? Vector3.Zero, lightColor ?? Vector3.One, shadowMultiplyColor ?? Vector3.One,
            enableMetallics, tileScale, brightness, litMode, uniformLitFactor);

    internal static StyleMaterialBindingData Binding() => MakeBinding();
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, .000002f);
}
