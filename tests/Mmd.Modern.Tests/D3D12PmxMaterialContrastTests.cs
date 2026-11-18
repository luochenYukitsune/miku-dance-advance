using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12PmxMaterialContrastTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector3 Toon = new(.2f, .3f, .4f);
    private static readonly Vector3 DarkNormal = new(-.6f, 0, -.8f);

    [Fact]
    public void ExistingPositionalCallersKeepIdentityContrastAndShadeGamma()
    {
        var treatment = new D3D12PmxMaterialTreatment(.9f, 1.1f);
        Assert.Equal(0, treatment.ValueContrast);
        Assert.Equal(1, treatment.ShadeGamma);
        treatment.Validate();
    }

    public static IEnumerable<object[]> InvalidScalars()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            yield return [new D3D12PmxMaterialTreatment(ValueContrast: value), "ValueContrast"];
            yield return [new D3D12PmxMaterialTreatment(ShadeGamma: value), "ShadeGamma"];
        }
        yield return [new D3D12PmxMaterialTreatment(ValueContrast: -.5001f), "ValueContrast"];
        yield return [new D3D12PmxMaterialTreatment(ValueContrast: .5001f), "ValueContrast"];
        yield return [new D3D12PmxMaterialTreatment(ShadeGamma: .4999f), "ShadeGamma"];
        yield return [new D3D12PmxMaterialTreatment(ShadeGamma: 2.0001f), "ShadeGamma"];
    }

    [Theory, MemberData(nameof(InvalidScalars))]
    public void NonFiniteAndOutOfDomainValuesFailBeforeConsumption(D3D12PmxMaterialTreatment treatment, string parameter)
        => Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(treatment.Validate).ParamName);

    [Theory, InlineData(-.5f, .5f), InlineData(.5f, 2f)]
    public void InclusiveEndpointsAreAcceptedWithoutClamping(float contrast, float gamma)
    {
        var treatment = new D3D12PmxMaterialTreatment(ValueContrast: contrast, ShadeGamma: gamma);
        treatment.Validate();
        Assert.Equal(contrast, treatment.ValueContrast);
        Assert.Equal(gamma, treatment.ShadeGamma);
    }

    [Fact]
    public void ConsumedKeyIncludesContrastGammaAndExistingScalars()
    {
        var treatment = new D3D12PmxMaterialTreatment(.9f, 1.1f, .15f, .8f);
        Assert.True(D3D12PmxMaterialTreatment.ConsumedEquivalent(treatment, treatment with { }));
        Assert.True(D3D12PmxMaterialTreatment.ConsumedEquivalent(null, null));
        foreach (var changed in new[] { treatment with { Saturation = 1 }, treatment with { TransitionWidthScale = 1 },
            treatment with { ValueContrast = 0 }, treatment with { ShadeGamma = 1 } })
            Assert.False(D3D12PmxMaterialTreatment.ConsumedEquivalent(treatment, changed));
        Assert.False(D3D12PmxMaterialTreatment.ConsumedEquivalent(treatment, null));
        Assert.False(D3D12PmxMaterialTreatment.ConsumedEquivalent(null, treatment));
    }

    [Fact]
    public void IdentityShadePreservesEveryComponentBitIncludingSignedZero()
    {
        var shade = new Vector4(-0f, -.25f, 1.5f, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)));
        var actual = new D3D12PmxMaterialTreatment().TransformShade(shade);
        for (int i = 0; i < 4; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(shade[i]), BitConverter.SingleToInt32Bits(actual[i]));
    }

    [Theory, InlineData(.5f), InlineData(2f)]
    public void ShadeGammaTransformsRgbIndependentlyAndPreservesAlphaBits(float gamma)
    {
        var shade = new Vector4(.0625f, .25f, 1.5f, -0f);
        var actual = new D3D12PmxMaterialTreatment(ShadeGamma: gamma).TransformShade(shade);
        Assert.Equal(MathF.Pow(shade.X, gamma), actual.X);
        Assert.Equal(MathF.Pow(shade.Y, gamma), actual.Y);
        Assert.Equal(shade.Z, actual.Z);
        Assert.Equal(BitConverter.SingleToInt32Bits(shade.W), BitConverter.SingleToInt32Bits(actual.W));
    }

    [Theory, InlineData(.5f), InlineData(2f)]
    public void SignedAndHdrShadeChannelsPassThroughIndependentlyWithoutClamping(float gamma)
    {
        var shade = new Vector4(-.25f, .25f, float.MaxValue, .37f);
        var actual = new D3D12PmxMaterialTreatment(ShadeGamma: gamma).TransformShade(shade);
        Assert.Equal(shade.X, actual.X);
        Assert.Equal(MathF.Pow(shade.Y, gamma), actual.Y);
        Assert.Equal(shade.Z, actual.Z);
        Assert.Equal(shade.W, actual.W);
    }

    [Theory]
    [InlineData(D3D12SurfaceStyle.GenshinPmx)]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [InlineData(D3D12SurfaceStyle.EndfieldPmx)]
    public void EveryFamilyAppliesBoundedValueContrastAfterSaturationAndBeforeAmbientAllocation(D3D12SurfaceStyle family)
    {
        string source = family switch
        {
            D3D12SurfaceStyle.GenshinPmx => OriginalFrameShaders.GenshinSurfaceSource(false, true, false, false),
            D3D12SurfaceStyle.StarRailPmx => OriginalFrameShaders.StarRailSurfaceSource(false, true, false, false),
            D3D12SurfaceStyle.WutheringPmx => OriginalFrameShaders.WutheringSurfaceSource(false, true, false, false),
            _ => OriginalFrameShaders.EndfieldSurfaceSource(false, true, false, false),
        };
        Assert.Contains("if(saturation==1 && contrast==0) return rgb;", source);
        Assert.Contains("if(any(rgb<0)) return rgb;", source);
        Assert.Contains("if(saturation!=1 && chroma!=0)", source);
        Assert.Contains("rgb=value+(rgb-value)*factor;", source);
        Assert.Contains("if(contrast!=0 && all(rgb>=0) && value>0 && value<=1)", source);
        Assert.Contains("rgb*=((1-contrast)+contrast*value);", source);
        Assert.True(source.IndexOf("rgb=value+(rgb-value)*factor;", StringComparison.Ordinal)
            < source.IndexOf("rgb*=((1-contrast)+contrast*value);", StringComparison.Ordinal));
        Assert.Contains("if(PmxMaterialTreatmentParams.x!=1 || PmxMaterialTreatmentParams.y!=0) fill=PmxMaterialSaturation(EgColor.rgb*texel)*GiAmbientShare.rgb;", source);
    }

    public static IEnumerable<object[]> Families()
    {
        foreach (var family in new[] { D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx,
            D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx }) yield return [family];
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void ActualRgbContrastUsesTheIndependentValueCurveAndLeavesSignedHdrAndCoverageExact(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        foreach (var rgb in new[] { new Vector3(.7f, .3f, .1f), new Vector3(.37f), Vector3.Zero,
            new Vector3(-.2f, .6f, .3f), new Vector3(1.4f, .7f, .2f) })
        {
            var frame = Surface(rgb);
            var baseline = session.Capture(frame, Width, Height, surfaceStyles: Map(family));
            foreach (float contrast in new[] { -.5f, .5f })
            {
                var actual = session.Capture(frame, Width, Height,
                    surfaceStyles: Map(family, new(ValueContrast: contrast)));
                AssertPixel(actual, ContrastOracle(rgb, contrast));
                AssertCoverage(baseline, actual);
                if (rgb == Vector3.Zero || rgb.X < 0 || rgb.X > 1)
                    Assert.Equal(baseline.Rgba, actual.Rgba);
                else if (rgb == new Vector3(.7f, .3f, .1f))
                    Assert.True(contrast < 0 ? Pixel(actual)[0] > Pixel(baseline)[0] : Pixel(actual)[0] < Pixel(baseline)[0]);
            }
            Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height,
                surfaceStyles: Map(family, new())).Rgba);
        }
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void ContrastAndShadeGammaCombineAtTheDarkEndpointWithoutChangingStraightAlpha(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var rgb = new Vector3(.7f, .3f, .1f);
        var frame = Surface(rgb, dark: true, alpha: .6f, straight: true);
        var baseline = session.Capture(frame, Width, Height, surfaceStyles: Map(family));
        var actual = session.Capture(frame, Width, Height,
            surfaceStyles: Map(family, new(ValueContrast: .4f, ShadeGamma: 2)));
        // The authored .2/.3/.4 shade has an independently specified square.
        AssertPixel(actual, ContrastOracle(rgb, .4f) * new Vector3(.04f, .09f, .16f), .6f);
        AssertCoverage(baseline, actual);
        Assert.NotEqual(Pixel(baseline), Pixel(actual));
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height,
            surfaceStyles: Map(family, new())).Rgba);
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void ActualNativeAndGiIncrementsShareTheContrastAndGammaEndpoints(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var frame = Bounce();
        var map = new D3D12SurfaceStyleMap([family, D3D12SurfaceStyle.Mmd],
            [new(PmxMaterialTreatment: new(ValueContrast: .4f, ShadeGamma: 2)), new()]);
        var lit = ContrastOracle(new Vector3(.4f, .24f, .16f) * .5f, .4f);
        var dark = lit * new Vector3(.04f, .09f, .16f);
        var budget = lit - dark;
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 20, SamplesPerPixel: 32, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectionalLightDirection: Vector3.UnitZ,
            DirectionalIrradiance: Vector3.Zero, DirectScale: 0, OcclusionStrength: 0, Composition: RayGiComposition.ToonPalette);
        var plain = session.Capture(frame, Width, Height, surfaceStyles: map);
        var bounce = session.Capture(frame, Width, Height, gi, surfaceStyles: map);
        AssertPixel(plain, dark);
        AssertPixel(bounce, dark + budget * .25f);
        const float nativeIntensity = .2f;
        // NativeDirect treats the unlit material diffuse, while the visible/GI
        // endpoint treats EgColor (half of it here). The native light is fully lit
        // and stays below the shared highlight shoulder, so it adds exactly.
        var directIncrement = ContrastOracle(new(.4f, .24f, .16f), .4f) * nativeIntensity;
        var giIncrement = budget * .25f;
        var native = new OriginalFrameScene(frame.Surface, options: frame.Options, materials: frame.Materials,
            lighting: new([new NativeSceneLight(Guid.NewGuid(), "contrast endpoint fill", SceneLightKind.Directional,
                Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Vector3.One, nativeIntensity, 30, CastShadows: false)]));
        var direct = session.Capture(native, Width, Height, surfaceStyles: map);
        var combined = session.Capture(native, Width, Height, gi, surfaceStyles: map);
        AssertPixel(direct, dark + directIncrement);
        AssertPixel(combined, dark + directIncrement + giIncrement);
        Assert.True(Pixel(combined)[0] > Pixel(direct)[0], "Actual GI must still add its treated increment.");
        AssertCoverage(plain, bounce);
        AssertCoverage(direct, combined);
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void ContrastAndGammaEditsResetConsumedGiHistoryOnceAndNeutralRestorationIsExact(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var frame = Surface(new(.7f, .3f, .1f), dark: true);
        var baseline = session.Capture(frame, Width, Height, surfaceStyles: Map(family));
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: .5f, SamplesPerPixel: 4, Seed: 31,
            MaxRayDistance: 30, RayEpsilon: .001f, DirectScale: 0, OcclusionStrength: 0,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
        Render(null, 1, "FirstFrame");
        Render(new(), 2, "None");
        Render(new(ValueContrast: .4f), 1, "ExplicitReset");
        Render(new(ValueContrast: .4f), 2, "None");
        Render(new(ValueContrast: .4f, ShadeGamma: 2), 1, "ExplicitReset");
        Render(new(ValueContrast: .4f, ShadeGamma: 2), 2, "None");
        Render(new(), 1, "ExplicitReset");
        Render(null, 2, "None");
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height,
            surfaceStyles: Map(family, new())).Rgba);

        void Render(D3D12PmxMaterialTreatment? treatment, ulong frames, string reason)
        {
            using var output = session.Render(frame, Width, Height, gi, surfaceStyles: Map(family, treatment));
            Assert.Equal(0, output.Statistics.ReadbackCount);
            var statistics = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
            Assert.Equal(LightingMode.SoftwareRayTracing, statistics.Effective);
            Assert.Equal(frames, statistics.TemporalHistoryFrames);
            Assert.Equal(reason, statistics.TemporalResetReason);
        }
    }

    private static Vector3 ContrastOracle(Vector3 rgb, float contrast)
    {
        float value = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        if (MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z)) < 0 || value <= 0 || value > 1) return rgb;
        // Interpolate V between the identity and square curves, then retain RGB/V.
        float curvedValue = value + contrast * (value * value - value);
        return rgb / value * curvedValue;
    }

    private static OriginalFrameScene Surface(Vector3 rgb, bool dark = false, float alpha = 1, bool straight = false)
    {
        var normal = dark ? DarkNormal : -Vector3.UnitZ;
        // Original-style host EgColor caps positive input at one. Produce HDR at
        // the actual post-morph endpoint, where contrast is defined to pass it.
        bool hdr = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)) > 1;
        SceneVertex[] vertices = [new(new(-.8f, -.6f, 0), normal, new(.25f)), new(new(.8f, -.6f, 0), normal, new(.25f)),
            new(new(.8f, .6f, 0), normal, new(.25f)), new(new(-.8f, .6f, 0), normal, new(.25f))];
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("explicit contrast witness", 0, 6, new(hdr ? rgb * .5f : rgb, alpha), Vector3.Zero, Vector3.Zero, 1, hdr ? 0 : -1)],
            hdr ? [new SceneTexture("white post-morph HDR witness", 1, 1, [255, 255, 255, 255])] : [],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30),
            lightDirection: dark ? Vector3.UnitX : -Vector3.UnitZ);
        var binding = new OriginalStyleMaterialBinding(Toon, DoubleSided: true)
        {
            TextureFactors = OriginalMaterialTextureFactors.Identity with
            {
                Diffuse = hdr ? new(new(2, 2, 2, 1), Vector4.Zero) : OriginalMaterialTextureFactors.Identity.Diffuse,
            },
        };
        return new(new(scene, [binding],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: Options() with { AlphaOutput = straight ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax },
            materials: [new(false, false, false)]);
    }

    private static OriginalFrameScene Bounce()
    {
        var source = D3D12RayGiTests.Bounce(); var mesh = source.Surface.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for (int i = 0; i < 4; i++) vertices[i] = vertices[i] with { Normal = DarkNormal };
        var materials = mesh.Materials.ToArray();
        materials[0] = materials[0] with { Diffuse = new(.4f, .24f, .16f, 1), Ambient = Vector3.Zero, Specular = Vector3.Zero };
        materials[1] = materials[1] with { Emission = new(10) };
        var scene = new SceneSnapshot(vertices, mesh.Indices, materials, mesh.Textures, mesh.World, mesh.Camera, Vector3.UnitX);
        return new(new(scene, [new(Toon), source.Surface.Materials[1]],
            new(new(.5f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: Options(),
            materials: [new(false, false, false) { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands }, new(false, false, false)]);
    }

    private static OriginalFrameOptions Options() => new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero);
    private static D3D12SurfaceStyleMap Map(D3D12SurfaceStyle family, D3D12PmxMaterialTreatment? treatment = null)
        => new([family], [new(PmxMaterialTreatment: treatment)]);
    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan((Height / 2 * Width + Width / 2) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame frame, Vector3 rgb, float alpha = 1)
    {
        var pixel = Pixel(frame); var expected = Vector4.Clamp(new(rgb, alpha), Vector4.Zero, Vector4.One);
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(pixel[channel] - expected[channel] * 255) <= 1.1f,
                $"channel {channel}: actual=({string.Join(',', pixel)}), expected={expected} * 255");
    }
    private static void AssertCoverage(RenderedFrame a, RenderedFrame b)
    { for (int i = 3; i < a.Rgba.Length; i += 4) Assert.Equal(a.Rgba[i], b.Rgba[i]); }
}
