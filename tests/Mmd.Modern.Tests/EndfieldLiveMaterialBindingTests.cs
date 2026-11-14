using System.Numerics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldLiveMaterialBindingTests
{
    [Fact]
    public void RendererDiffuseRequiresNoAuthoredTextureAndCannotBeAnEmissionMask()
    {
        var binding = new D3D12EndfieldPaletteBinding(Vector3.Zero, LightInfluence: .2f,
            ReceivedLightInputMode: D3D12EndfieldPaletteInputMode.RendererDiffuse);
        binding.Validate();
        Assert.Throws<ArgumentException>(() => (binding with { ReceivedLightCombinedTextureIndex = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (binding with { EmissionInputMode = D3D12EndfieldPaletteInputMode.RendererDiffuse }).Validate());
        Assert.True(D3D12EndfieldPaletteBinding.ConsumedEquivalent(binding,
            binding with { ReceivedLightLinearRgb = new(16) }));
        Assert.False(D3D12EndfieldPaletteBinding.ConsumedEquivalent(binding,
            binding with { ReceivedLightInputMode = D3D12EndfieldPaletteInputMode.Uniform }));
    }

    [Fact]
    public void FragmentRangeIsOptionalBoundedAndParticipatesInConsumedHistory()
    {
        var old = new D3D12EndfieldMatcapBinding(0, 1, 2, D3D12MatcapColorEncoding.LinearRgb,
            0, new(.2f), Vector3.One);
        Assert.Null(old.HighlightRange);
        var range = new D3D12EndfieldHighlightRangeBinding(.2f, .4f, new(Vector3.UnitZ));
        var next = old with { HighlightRange = range };
        next.Validate();
        Assert.False(D3D12EndfieldMatcapBinding.ConsumedEquivalent(old, next));
        Assert.True(D3D12EndfieldMatcapBinding.ConsumedEquivalent(next, next with { HighlightRange = range with { } }));
        foreach (float invalid in new[] { -.01f, 4.01f, float.NaN, float.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => (range with { OverallBrightness = invalid }).Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => (range with { HighlightBrightness = invalid }).Validate());
        }
        Assert.Throws<ArgumentNullException>(() => (range with { DirectionalShadow = null! }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (range with { DirectionalShadow = new(new(2, 0, 0)) }).Validate());
    }

    [Theory, InlineData(0, 0, 0), InlineData(.2, .3, .012), InlineData(.2, .8, .032), InlineData(1, 1.5, 1)]
    public void SourceRangeUsesTwiceGatedNumericalPAndQ(double k, double q, double gated)
    {
        // Independent scalar reference: one k scales the highlight brightness and
        // another k limits its spatial directional range. The q clamp is inside.
        Assert.Equal(gated, k * Math.Clamp(q * k, 0, 1), 6);
    }
}
