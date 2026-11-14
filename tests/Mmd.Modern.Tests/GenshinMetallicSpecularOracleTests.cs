using System.Numerics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicSpecularOracleTests
{
    [Fact]
    public void GateAndCompositionUseStrictRedAndSingleFractionalReplacement()
    {
        Assert.Equal(0, GenshinMetallicSpecularOracle.Gate(.9f, 1));
        Assert.Equal(0, GenshinMetallicSpecularOracle.Gate(MathF.BitDecrement(.9f), 1));
        Assert.Equal(.25f, GenshinMetallicSpecularOracle.Gate(MathF.BitIncrement(.9f), .25f));
        Assert.Equal(0, GenshinMetallicSpecularOracle.Gate(1, 0));
        Vector3 original = new(.21f, .42f, .63f), metal = new(2, 3, 4);
        Assert.Equal(original, GenshinMetallicSpecularOracle.Compose(original, metal, 0));
        Assert.Equal(original, GenshinMetallicSpecularOracle.Compose(original, metal, -2));
        Assert.Equal(metal, GenshinMetallicSpecularOracle.Compose(original, metal, 2));
        Near(original * .75f + metal * .25f, GenshinMetallicSpecularOracle.Compose(original, metal, .25f));
    }

    [Fact]
    public void PowerFloorAndScaleClampPrecedeRampAndStrictSharpSelection()
    {
        Assert.Equal(.001f, GenshinMetallicSpecularOracle.P(-.6f, 1, 1));
        Assert.Equal(.001f, GenshinMetallicSpecularOracle.P(0, 1, 1));
        Assert.Equal(.375f, GenshinMetallicSpecularOracle.P(.75f, 1, 1, .625f));
        float nh = .000001f, global = 1.1f;
        float sourceOrdered = nh + (global - 1), reassociated = (nh + global) - 1;
        Assert.NotEqual(sourceOrdered, reassociated);
        Assert.Equal(sourceOrdered, GenshinMetallicSpecularOracle.P(nh, 1, 1, global));
        Assert.Equal(1, GenshinMetallicSpecularOracle.P(.5f, 1, 32));
        Assert.Equal(1, GenshinMetallicSpecularOracle.P(-1, 0, 1));
        var color = Vector3.One; var sharp = new Vector3(2);
        Near(new(.125f), GenshinMetallicSpecularOracle.Local(.5f, .25f, color, sharp, shininess: 1, sharpOffset: .5f));
        Near(GenshinMetallicSpecularOracle.Gamma(sharp), GenshinMetallicSpecularOracle.Local(.5f, .25f,
            color, sharp, shininess: 1, sharpOffset: MathF.BitDecrement(.5f)));
        Near(new(.125f), GenshinMetallicSpecularOracle.Local(.5f, .25f, color, sharp,
            shininess: 1, sharpOffset: MathF.BitIncrement(.5f)));
        float sampled = -1;
        Near(new(.25f, .5f, .75f), GenshinMetallicSpecularOracle.Local(.5f, 1, color, sharp,
            shininess: 1, scale: 32, rampMix: 1, ramp: p => { sampled = p; return new(.25f, .5f, .75f); }));
        Assert.Equal(1, sampled);
    }

    [Fact]
    public void SharpBypassesBlueIntensityAndRampWhileKeepingGammaAndAttenuation()
    {
        Vector3 sharp = new(-2, 2, .5f);
        var expected = GenshinMetallicSpecularOracle.Gamma(sharp) * .25f;
        Near(expected, GenshinMetallicSpecularOracle.Local(.5f, 0, Vector3.Zero, sharp, shininess: 1,
            attenuation: .25f, sharpOffset: 0, lit: 0, rampMix: 1, ramp: _ => new(9), intensity: 0));
        Near(expected, GenshinMetallicSpecularOracle.Local(.5f, 0, Vector3.Zero, sharp, shininess: 1,
            scale: 0, attenuation: .25f, sharpOffset: -.1f, lit: 0, intensity: 0));
        Assert.Equal(-2, GenshinMetallicSpecularOracle.Gamma(-2));
        Assert.True(GenshinMetallicSpecularOracle.Gamma(2) > 4);
        Assert.InRange(GenshinMetallicSpecularOracle.Gamma(.5f), .21763f, .21764f);
        // No pre-composition clipping: signed channels and HDR survive the ordinary
        // endpoint as well as sharp replacement. UNorm capture alone cannot prove it.
        var signedHdr = GenshinMetallicSpecularOracle.Local(1, 1, sharp, Vector3.Zero,
            shininess: 1, attenuation: 2, lit: 0);
        Near(GenshinMetallicSpecularOracle.Gamma(sharp) * 2, signedHdr);
        Assert.True(signedHdr.X < 0 && signedHdr.Y > 1);
    }

    [Fact]
    public void BlueAmplitudeRgbRampMixAndLitAttenuationRemainIndependent()
    {
        Func<float, Vector3> ramp = _ => new(.25f, .5f, .75f);
        Vector3 Local(float blue = 1, float mix = 0, float lit = 1, float attenuation = .25f)
            => GenshinMetallicSpecularOracle.Local(.5f, blue, Vector3.One, Vector3.One,
                shininess: 1, attenuation: attenuation, lit: lit, rampMix: mix, ramp: ramp);
        Near(new(.5f), Local());
        Near(new(.125f), Local(.25f));
        Near(new(.25f, .5f, .75f), Local(mix: 1));
        Near(new(.375f, .5f, .625f), Local(mix: .5f));
        Near(Local(mix: 1), Local(mix: 8));
        Near(Local(), Local(mix: -8));
        Near(new(.125f), Local(lit: -3));
        Near(new(.5f), Local(lit: 3));
        Near(new(.3125f), Local(lit: .5f));
        Near(Vector3.Zero, Local(lit: 0, attenuation: 0));
        Near(new(.5f), Local(lit: 1, attenuation: 0));
        Near(new(.5f), Local(lit: 0, attenuation: 1));
        // Unconsumed ramp requires neither image nor invented neutral pixels.
        Near(new(.5f), GenshinMetallicSpecularOracle.Local(.5f, 1, Vector3.One, Vector3.One, shininess: 1));
    }

    [Fact]
    public void LinearNumericRampUsesMiddleRowsRgbAndWrappedSeamAndIgnoresAlpha()
    {
        byte[] rgba = [255, 0, 0, 4, 0, 255, 0, 18, 0, 0, 255, 64, 255, 255, 255, 255];
        Near(new(.5f), GenshinMetallicSpecularOracle.LinearRepeat(rgba, 2, 2, 0));
        Near(new(.5f), GenshinMetallicSpecularOracle.LinearRepeat(rgba, 2, 2, 1));
        Near(new(.5f, 0, .5f), GenshinMetallicSpecularOracle.LinearRepeat(rgba, 2, 2, .25f));
        Near(new(.5f, 1, .5f), GenshinMetallicSpecularOracle.LinearRepeat(rgba, 2, 2, .75f));
        Near(new(.5f, .25f, .5f), GenshinMetallicSpecularOracle.LinearRepeat(rgba, 2, 2, .375f));
        byte[] alpha = rgba.ToArray(); for (int i = 3; i < alpha.Length; i += 4) alpha[i] ^= 255;
        Assert.Equal(GenshinMetallicSpecularOracle.LinearRepeat(rgba, 2, 2, .375f),
            GenshinMetallicSpecularOracle.LinearRepeat(alpha, 2, 2, .375f));
        byte[] numeric = [128, 64, 32, 0];
        Near(new(128 / 255f, 64 / 255f, 32 / 255f), GenshinMetallicSpecularOracle.LinearRepeat(numeric, 1, 1, .66f));
    }

    [Fact]
    public void GeometryAdapterSafelyHandlesZeroVectorsAndInheritedOrthographicDirection()
    {
        float Nh(Vector3 position, Vector3 eye, Vector3? light = null,
            Vector3? normal = null) => GenshinMetallicSpecularOracle.NdotH(normal ?? Vector3.UnitZ,
                position, eye, light ?? Vector3.UnitZ);
        Assert.Equal(1, Nh(Vector3.Zero, Vector3.UnitZ));
        Assert.Equal(0, Nh(Vector3.Zero, Vector3.UnitZ, light: -Vector3.UnitZ));
        Assert.Equal(0, Nh(Vector3.Zero, Vector3.Zero, light: Vector3.Zero));
        Assert.Equal(0, Nh(Vector3.Zero, Vector3.UnitZ, normal: Vector3.Zero));
        // Orthographic inherits this same eye-position adapter; translating the
        // fragment changes nh rather than selecting the parent's MatCap basis.
        Assert.NotEqual(Nh(Vector3.Zero, Vector3.UnitZ), Nh(new(5), new(-9)));
    }

    [Fact]
    public void BindingBoundsAndConsumedRampEquivalenceAreExplicit()
    {
        var binding = new D3D12GenshinMetallicSpecularBinding(Vector3.One, Vector3.One);
        binding.Validate();
        (binding with { Color = new(-16, 0, 16), Shininess = 256, Scale = 32, Attenuation = 32,
            SharpOffset = -32, RampMix = -32, RampCombinedTextureIndex = 5 }).Validate();
        foreach (var invalid in new[] { binding with { Color = new(float.NaN) }, binding with { SharpColor = new(17) },
            binding with { Shininess = -1 }, binding with { Shininess = 257 }, binding with { Scale = 33 },
            binding with { Scale = -1 }, binding with { Attenuation = -1 }, binding with { Attenuation = 33 },
            binding with { SharpOffset = float.PositiveInfinity }, binding with { SharpOffset = 33 },
            binding with { RampMix = float.NaN }, binding with { RampMix = 33 }, binding with { RampCombinedTextureIndex = -1 } })
            Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate());
        Assert.Throws<ArgumentException>(() => (binding with { RampMix = .5f }).Validate());
        Assert.False(binding.ConsumesRamp);
        Assert.True((binding with { RampMix = .5f, RampCombinedTextureIndex = 1 }).ConsumesRamp);
        Assert.True(D3D12GenshinMetallicSpecularBinding.ConsumedEquivalent(binding, binding with { RampCombinedTextureIndex = 3 }));
        Assert.False(D3D12GenshinMetallicSpecularBinding.ConsumedEquivalent(null, binding));
        Assert.False(D3D12GenshinMetallicSpecularBinding.ConsumedEquivalent(binding, binding with { Color = Vector3.Zero }));
        Assert.False(D3D12GenshinMetallicSpecularBinding.ConsumedEquivalent(binding, binding with { RampMix = -1 }));
        var active = binding with { RampMix = .5f, RampCombinedTextureIndex = 1 };
        Assert.False(D3D12GenshinMetallicSpecularBinding.ConsumedEquivalent(active, active with { RampCombinedTextureIndex = 2 }));
        Assert.True(D3D12GenshinMetallicSpecularBinding.ConsumedEquivalent(active, active with { RampCombinedTextureIndex = 2 }, false));
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, .000002f);
}
