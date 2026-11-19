using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldPaletteOracleTests
{
    [Fact]
    public void PartialEmissionUsesDistinctPreColorNumericalBlueAndUnclampedSignedStrength()
    {
        Vector3 pre = new(.2f, .4f, .8f), mat = new(.03f, .12f, .6f);
        foreach (float strength in new[] { 0, .5f, -2, 4 })
        {
            var result = Run(pre, mat, blue: .7f, emission: strength);
            Near(mat + pre * 1.4f * strength, result.Partial);
            if (strength != 0) Assert.True(Vector3.Distance(result.Partial, mat * (1 + 1.4f * strength)) > .1);
        }
        Assert.True(Run(pre, mat, blue: 2, emission: -3).Partial.X < 0);
        Assert.True(Run(pre, mat, blue: 2, emission: 3).Partial.Z > 1);
    }

    [Theory, InlineData(-2, 0), InlineData(0, 0), InlineData(.5, .5), InlineData(1, 1), InlineData(4, 1)]
    public void ReceivedLightIsAddedAfterPartialAndOnlyItsFactorIsClamped(float influence, float factor)
    {
        Vector3 light = new(-.3f, .7f, 3), mat = new(.4f, .25f, .1f);
        var result = Run(new(.2f), mat, influence: influence, light: light);
        Near(mat + factor * light, result.Lit);
        if (factor > 0)
        {
            Assert.True(Vector3.Distance(result.Lit, Vector3.Lerp(mat, light, factor)) > .1);
            Assert.True(Vector3.Distance(result.Lit, mat * (Vector3.One + factor * light)) > .1);
        }
    }

    [Fact]
    public void ReceivedLightCrossesOverlayThresholdBeforeRimIsApplied()
    {
        var result = Run(new(.2f), new(.4f), rim: .6f, influence: 1, light: new(.3f));
        float expected = (float)EndfieldPaletteOracle.Overlay(.7, .8, .6);
        Near(new(expected), result.Rimmed);
        float wronglyPrecomposed = (float)EndfieldPaletteOracle.Overlay(.4, .8, .6) + .3f;
        Assert.True(Math.Abs(result.Rimmed.X - wronglyPrecomposed) > .02);
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public void CancellationUsesActualOldIndependentContributionsAndReplacesOnlyOwnedPalette(bool hair, bool rim)
    {
        Vector3 pre = new(.2f, .65f, .1f), mat = new(.15f, .55f, .04f), tint = new(.8f, .3f, .7f);
        const float baseline = .7f, r = .3f, p = .4f;
        var result = EndfieldPaletteOracle.Evaluate(pre, mat, tint, baseline, r, p, .7f, .4f,
            .6f, new(.1f, .4f, .3f), .37f, new(-.2f, 1.7f), hair, rim);
        Vector3 oldHair = hair ? pre * p : Vector3.Zero;
        Vector3 oldRim = Vector3.Zero;
        if (rim) for (int c = 0; c < 3; c++)
            oldRim[c] = (float)EndfieldPaletteOracle.Overlay(pre[c], tint[c], r) * (baseline + r) - pre[c] * baseline;
        Vector3 unrelated = new(.11f, .23f, .37f);
        Near(unrelated + result.Emission - pre * baseline, unrelated + oldHair + oldRim + result.AddedDelta);
        if (hair && rim)
        {
            Vector3 wrongOld = Vector3.Zero;
            for (int c = 0; c < 3; c++) wrongOld[c] = (float)EndfieldPaletteOracle.Overlay(pre[c], tint[c], r) * (baseline + r + p) - pre[c] * baseline;
            Assert.True(Vector3.Distance(wrongOld, oldHair + oldRim) > .02);
        }
    }

    [Fact]
    public void SavedWaveRetainsPrecisionOffsetsUAxisAndDensityScaleIncludingNegativeUvs()
    {
        double off = EndfieldPaletteOracle.SavedWave(7, 0);
        Assert.InRange(off, 9e-11, 1.1e-10);
        Assert.Equal(off, EndfieldPaletteOracle.SavedWave(-35, 0));
        foreach ((double u, double density) in new[] { (-.17, .3), (1.27, -.2), (.011, 3.1), (2.43, .7) })
        {
            double expected = .5 + .5 * Math.Sin(2000 * u * density * .999999 + 20 * .000001 * .999999 - Math.PI / 2);
            Assert.InRange(Math.Abs(expected - EndfieldPaletteOracle.SavedWave(u, density)), 0, 2e-12);
        }
        var a = Run(new(.2f), new(.4f), density: .7f, uv: new(.231f, -.9f));
        var b = Run(new(.2f), new(.4f), density: .7f, uv: new(.231f, 5));
        Assert.Equal(a.Waved, b.Waved);
        Assert.NotEqual(a.Waved, Run(new(.2f), new(.4f), density: .7f, uv: new(.192f, -.9f)).Waved);
    }

    [Fact]
    public void SavedHsvRetainsHdrAndExplicitlyClampsSaturationForMixedSignedRgb()
    {
        Vector3 positive = new(.3f, 2, .7f);
        Near(positive * .7999999523162842f, EndfieldPaletteOracle.SavedHsv(positive));
        Near(new(0, .4f, .17142856f), EndfieldPaletteOracle.SavedHsv(new(-.2f, .5f, .1f)));
        Near(new(-.79999995f), EndfieldPaletteOracle.SavedHsv(new(-2, -1, -3)));
        Assert.Equal(Vector3.Zero, EndfieldPaletteOracle.SavedHsv(new(-.2f, 0, -1)));
        Assert.True(Vector3.Distance(new Vector3(-.2f, .5f, .1f) * .8f,
            EndfieldPaletteOracle.SavedHsv(new(-.2f, .5f, .1f))) > .1);
    }

    [Fact]
    public void ValidationRejectsNonfiniteOutOfDomainAndAmbiguousInputOwnership()
    {
        var matcap = T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette: true);
        var valid = new EndfieldPaletteSpec(new(-16, 0, 16), -32, -16, 32, -32);
        Assert.True(T8dTestHelpers.ValidateEndfield([matcap, valid.ToBinding()]).IsValid);
        foreach (var bad in new[]
        {
            valid with { Density = float.NaN },
            valid with { EmissionStrength = 33 },
            valid with { UniformEmissionBlue = -17 },
            valid with { ReceivedLightLinearRgb = new(float.PositiveInfinity) },
            valid with { EmissionSlot = 0 },
            valid with { ReceivedLightSlot = 0 },
            valid with { EmissionInputMode = T8dTestHelpers.ModeTexture },
            valid with { ReceivedLightInputMode = T8dTestHelpers.ModeTexture, ReceivedLightSlot = -1 },
            valid with { ReceivedLightInputMode = "InvalidMode" },
        })
            Assert.False(T8dTestHelpers.ValidateEndfield([matcap, bad.ToBinding()]).IsValid);
    }

    [Fact]
    public void ConsumedEqualityIgnoresInactiveInputsAndUnusedUniformsButTracksEffectiveFactors()
    {
        var matcap = T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette: true);
        var off = new EndfieldPaletteSpec(Vector3.Zero);
        var texture = off with
        {
            EmissionInputMode = T8dTestHelpers.ModeTexture,
            EmissionSlot = 2,
            ReceivedLightInputMode = T8dTestHelpers.ModeTexture,
            ReceivedLightSlot = 3,
            UniformEmissionBlue = 4,
            ReceivedLightLinearRgb = new(4),
            LightInfluence = -3,
        };
        Assert.True(T8dTestHelpers.ValidateEndfield([matcap, texture.ToBinding()]).IsValid);
        var inactiveDraw = T8dTestHelpers.BuildEndfieldDrawData([matcap, texture.ToBinding()])!;
        Assert.DoesNotContain(inactiveDraw.TextureBindings!, static t => t.SlotIndex is 4 or 5);

        var active = texture with { EmissionStrength = .5f, LightInfluence = 2 };
        var activeDraw = T8dTestHelpers.BuildEndfieldDrawData([matcap, active.ToBinding()])!;
        Assert.Contains(activeDraw.TextureBindings!, static t => t.SlotIndex == 4 && t.SceneTextureIndex == 2);
        Assert.Contains(activeDraw.TextureBindings!, static t => t.SlotIndex == 5 && t.SceneTextureIndex == 3);

        var tex = new SceneTexture("tex", 1, 1, [64, 96, 128, 255]);
        SceneVertex[] verts = [new(new(-1,-1,0), Vector3.UnitZ, Vector2.Zero), new(new(1,-1,0), Vector3.UnitZ, Vector2.UnitX), new(new(0,1,0), Vector3.UnitZ, Vector2.UnitY)];
        OriginalFrameScene FrameWith(int count) => new(new(new(verts, [0, 1, 2], [new("m", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], Enumerable.Repeat(tex, count).ToArray()),
            [new(Vector3.Zero)], new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: new(OriginalAlphaBlendMode.Normal, false, false));

        var frame = FrameWith(9);
        var mapInactiveA = T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["endfield"], bindingsByMaterial: [[matcap, texture.ToBinding()]]);
        var mapInactiveB = T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["endfield"], bindingsByMaterial: [[matcap, (texture with { EmissionSlot = 4, ReceivedLightSlot = 8 }).ToBinding()]]);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(mapInactiveA, mapInactiveB));

        var mapActive = T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["endfield"], bindingsByMaterial: [[matcap, active.ToBinding()]]);
        var mapRebased = T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["endfield"], bindingsByMaterial: [[matcap, (active with { EmissionSlot = 4, ReceivedLightSlot = 8 }).ToBinding()]]);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(mapActive, mapRebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(mapActive.CaptureResolvedInputs(frame), mapRebased.CaptureResolvedInputs(frame)));

        var mapOff = T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["endfield"], bindingsByMaterial: [[matcap, off.ToBinding()]]);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(mapOff, T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["endfield"], bindingsByMaterial: [[matcap, (off with { Density = .1f }).ToBinding()]])));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(mapOff, T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["endfield"], bindingsByMaterial: [[matcap]])));
    }

    private static EndfieldPaletteOracle.Result Run(Vector3 pre, Vector3 mat, float blue = 0,
        float emission = 0, float influence = 0, Vector3 light = default, float rim = 0,
        float density = 0, Vector2 uv = default)
        => EndfieldPaletteOracle.Evaluate(pre, mat, new(.8f), 1, rim, 0, blue, emission, influence, light, density, uv);
    private static void Near(Vector3 expected, Vector3 actual)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, 3e-6f);
}
