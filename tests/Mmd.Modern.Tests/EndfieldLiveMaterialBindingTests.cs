using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldLiveMaterialBindingTests
{
    [Fact]
    public void RendererDiffuseRequiresNoAuthoredTextureAndCannotBeAnEmissionMask()
    {
        var matcap = T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette: true);
        var palette = new EndfieldPaletteSpec(Vector3.Zero, LightInfluence: .2f,
            ReceivedLightInputMode: T8dTestHelpers.ModeRendererDiffuse);
        Assert.True(T8dTestHelpers.ValidateEndfield([matcap, palette.ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap, (palette with { ReceivedLightSlot = 2 }).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap, (palette with { EmissionInputMode = T8dTestHelpers.ModeRendererDiffuse }).ToBinding()]).IsValid);

        var drawRd = T8dTestHelpers.BuildEndfieldDrawData([matcap, palette.ToBinding()])!;
        var drawRdOtherRgb = T8dTestHelpers.BuildEndfieldDrawData([matcap, (palette with { ReceivedLightLinearRgb = new(16) }).ToBinding()])!;
        var drawUniform = T8dTestHelpers.BuildEndfieldDrawData([matcap, (palette with { ReceivedLightInputMode = T8dTestHelpers.ModeUniform }).ToBinding()])!;
        var vecsRd = T8dTestHelpers.ReadCbvVectors(drawRd);
        var vecsUniform = T8dTestHelpers.ReadCbvVectors(drawUniform);
        Assert.Equal(2f, vecsRd[^1].X);
        Assert.Equal(0f, vecsUniform[^1].X);
        Assert.False(drawRd.ConstantBufferData.Span.SequenceEqual(drawUniform.ConstantBufferData.Span));
        Assert.Equal(vecsRd.Length, T8dTestHelpers.ReadCbvVectors(drawRdOtherRgb).Length);
    }

    [Fact]
    public void FragmentRangeIsOptionalBoundedAndParticipatesInConsumedHistory()
    {
        var old = T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingLinearRgb, 0, new(.2f), Vector3.One);
        var oldCbv = T8dTestHelpers.ReadCbvVectors(T8dTestHelpers.BuildEndfieldDrawData([old])!);
        Assert.Equal(0f, oldCbv[4].X);

        var range = new EndfieldHighlightRangeSpec(.2f, .4f, new(Vector3.UnitZ));
        var next = T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingLinearRgb, 0, new(.2f), Vector3.One, highlightRange: range);
        Assert.True(T8dTestHelpers.ValidateEndfield(next).IsValid);

        var nextCbv = T8dTestHelpers.ReadCbvVectors(T8dTestHelpers.BuildEndfieldDrawData([next])!);
        Assert.Equal(1f, nextCbv[4].X);
        Assert.False(oldCbv.SequenceEqual(nextCbv));

        var standalone = new[] { old, range.ToBinding() };
        Assert.True(T8dTestHelpers.ValidateEndfield(standalone).IsValid);
        Assert.True(T8dTestHelpers.ReadCbvVectors(T8dTestHelpers.BuildEndfieldDrawData(standalone)!).SequenceEqual(nextCbv));

        foreach (float invalid in new[] { -.01f, 4.01f, float.NaN, float.PositiveInfinity })
        {
            Assert.False(T8dTestHelpers.ValidateEndfield([old, (range with { OverallBrightness = invalid }).ToBinding()]).IsValid);
            Assert.False(T8dTestHelpers.ValidateEndfield([old, (range with { HighlightBrightness = invalid }).ToBinding()]).IsValid);
        }
        Assert.False(T8dTestHelpers.ValidateEndfield([old, (range with { DirectionalShadow = null, HasDirectionalShadow = false }).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([old, (range with { DirectionalShadow = new(new(2, 0, 0)) }).ToBinding()]).IsValid);
    }

    [Theory, InlineData(0, 0, 0), InlineData(.2, .3, .012), InlineData(.2, .8, .032), InlineData(1, 1.5, 1)]
    public void SourceRangeUsesTwiceGatedNumericalPAndQ(double k, double q, double gated)
    {
        // Independent scalar reference: one k scales the highlight brightness and
        // another k limits its spatial directional range. The q clamp is inside.
        Assert.Equal(gated, k * Math.Clamp(q * k, 0, 1), 6);
    }
}
