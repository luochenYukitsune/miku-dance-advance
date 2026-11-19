using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Synthetic upstream Cpre inputs and independent numeric reference. Explicit-q
// adapter only: no claim of original dynamic lighting, assets or complete HA/HB.
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class EndfieldBaseImageGpuTests
{
    private const int W = 97, H = 97;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);
    private static readonly Vector3 Cpre = new(.22f, .16f, .10f);
    private static readonly EndfieldMatcapSpec Matcap = new(0, 1, 2,
        T8dTestHelpers.EncodingLinearRgb, 0, Cpre, new(.2f, .45f, .12f),
        NormalStrength: 1.7f, HighlightCoefficient: 1, BaselineStrength: .38f, CoupleEndfieldPalette: true);
    // Planar normal incidence and EdgeSize=4 saturate the rim mask to EdgeStrength.
    private static readonly EndfieldRimSpec Rim = new(Cpre, new(.8f, .7f, .6f),
        EdgeSize: 4, EdgeStrength: .16f, RangeStrength: .38f);
    // N=+Z, incoming=-Z, raw seed=+Y => q=1 and clamped radial q=.75.
    private static readonly EndfieldHairSpec Hair = new(3, Cpre, -.15f);
    private static readonly EndfieldPaletteSpec Palette = new(new(.09f, .07f, .04f),
        EmissionStrength: 1.5f, UniformEmissionBlue: .65f, LightInfluence: 1, Density: .013f);

    private static readonly EndfieldBaseImageSpec Base = new(
        new(.48f, .39f, .28f), new(.35f, .5f, .7f), new(.52f, .63f, .8f),
        new(.85f, .95f, .9f), ShadowFactor: .27f, WarmTint: .7f, Contrast: .15f,
        Saturation: 1.1f, DetailBlue: .72f, ShadowStrength: .66f);

    [Fact, Trait("Category", "Gpu")]
    public void FixedHalfLutsUseNormalizedTexelCoordinatesAndClampExplicitShadowFactor()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame();
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        Assert.Equal(.7998046875f, EndfieldBaseImageOracle.SampleLut(0, .5f).X);
        foreach (float q in new[] { -2f, 0f, .25f, .499f, .5f, .751f, 1f, 2f })
        {
            var baseImg = Base with { ShadowFactor = q, WarmTint = 0 };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(Palette, baseImg));
            AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, Matcap, Palette, baseImg, 48, 48));
            AssertCoverage(plain, actual);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void HalfLutCoordinatesAreVisibleAndRejectDirectStopInterpolation()
    {
        using var session = new D3D12FrameSession();
        float sampled = EndfieldBaseImageOracle.SampleLut(0, .25f).X;
        Assert.Equal(.6492919921875f, sampled);
        float tint = 1 - 1 / (2 * (1 - sampled));
        var input = Base with { BaseImageLinearRgb = Vector3.One, ColorMultiplier = Vector3.One,
            DetailBlue = 1, Saturation = 1, Contrast = 0, ShadowStrength = 1,
            ShadowFactor = .25f, WarmTint = 0, ShadowColor = new(tint) };
        var palette = Palette with { EmissionStrength = 0, LightInfluence = 0, Density = 0 };
        var binding = Matcap with { HighlightColor = Vector3.Zero, BaselineStrength = 16 };
        var textures = Textures();
        textures[2] = new("black MatCap RGB with opaque sample", 1, 1, [0, 0, 0, 255]);
        var frame = Frame(textures: textures);
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(palette, input, matcap: binding, frame: frame));
        Vector3 correct = Delta(frame.Surface.Snapshot, binding, palette, input, 48, 48);
        AssertPixel(plain, actual, 48, 48, correct);
        // Direct stops give .65 instead of the sampled half/texel-coordinate result.
        // A signed overlay input near zero and B=16 magnify this into an observable
        // eight-bit result, rather than tolerating the wrong representation.
        float wrongPre = 1 - 2 * (1 - tint) * (1 - .65f);
        Assert.True(Math.Abs(correct.X + wrongPre * binding.BaselineStrength) * 255 > 6);
        int at = (48 * W + 48) * 4;
        float wrongPixel = Math.Clamp(plain.Rgba[at] - wrongPre * binding.BaselineStrength * 255, 0, 255);
        Assert.True(Math.Abs(actual.Rgba[at] - wrongPixel) > 3,
            "Actual GPU image must reject direct ColorBand stop interpolation.");
    }

    [Fact, Trait("Category", "Gpu")]
    public void WarmShadowStrictLumaAndSignedHdrHsvAreEvaluatedBeforeDownstreamEmission()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame();
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var inputs = new[] {
            Base with { WarmTint = 1, ShadowColor = new(.5f, .6f, .7f) },
            Base with { WarmTint = 1, ShadowColor = new(1 - 1f / 1048576) },
            Base with { WarmTint = 1, ShadowColor = Vector3.One },
            Base with { WarmTint = 1, ShadowColor = new(1 + 1f / 1048576) },
            Base with { BaseImageLinearRgb = new(-.16f, .6f, 1.25f), Contrast = -.3f,
                Saturation = 2, ColorMultiplier = new(.7f, .8f, .3f), ShadowStrength = 0 }
        };
        byte[]? below = null, at = null;
        for (int i = 0; i < inputs.Length; i++)
        {
            var palette = Palette with { EmissionStrength = .3f };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(palette, inputs[i], true, true));
            AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, Matcap, palette, inputs[i], 48, 48, true, true));
            if (i == 1) below = Pixel(actual, 48, 48);
            if (i == 2) at = Pixel(actual, 48, 48);
        }
        Assert.NotEqual(below, at); // strict luma boundary changes the selected source branch.
    }

    [Fact, Trait("Category", "Gpu")]
    public void RasterBackfacingSquaresBaseBeforeUnclampedContrast()
    {
        using var session = new D3D12FrameSession();
        byte[]? frontPixel = null;
        foreach (bool front in new[] { true, false })
        {
            var frame = Frame(front: front);
            var baseImg = Base with { Contrast = -.7f, ShadowStrength = 0, Saturation = .8f };
            // White highlight makes Cmat=Cpre regardless of the front-dependent normal gate;
            // this isolates actual SV_IsFrontFace base squaring from mapped-normal lighting.
            var binding = Matcap with { HighlightColor = Vector3.One };
            var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(Palette, baseImg, matcap: binding, frame: frame));
            AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, binding, Palette, baseImg, 48, 48, front: front));
            if (front) frontPixel = Pixel(actual, 48, 48);
            else Assert.NotEqual(frontPixel, Pixel(actual, 48, 48));
            AssertCoverage(plain, actual);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void NonuniformBaseDetailBlueAndShadowRedShareSelectedCurrentAdditionalUv()
    {
        using var session = new D3D12FrameSession();
        var binding = Matcap with { UvChannel = 1 };
        var palette = Palette with { EmissionStrength = .5f, Density = .023f };
        var baseImg = TexturedBase();
        byte[]? previous = null;
        foreach (float shift in new[] { 0f, .29f })
        {
            var source = Frame(uvShift: shift);
            var snapshot = source.Surface.Snapshot;
            snapshot = snapshot.WithDeformedVertices(snapshot.Vertices.ToArray().Select(v => v with
                { Position = v.Position + new Vector3(shift * .2f, 0, 0), Uv = v.Uv + new Vector2(.19f, -.11f) }).ToArray());
            var frame = Replace(source, snapshot);
            Assert.True(snapshot.HasDynamicVertices);
            var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(palette, baseImg, matcap: binding, frame: frame));
            foreach (int x in new[] { 36, 48, 60 })
                AssertPixel(plain, actual, x, 44, Delta(snapshot, binding, palette, baseImg, x, 44));
            AssertCoverage(plain, actual);
            Assert.True(Vector3.Distance(Delta(snapshot, binding, palette, baseImg, 48, 44),
                Delta(snapshot, binding with { UvChannel = 0 }, palette, baseImg, 48, 44)) * 255 > 2);
            if (previous is not null) Assert.NotEqual(previous, Pixel(actual, 48, 44));
            previous = Pixel(actual, 48, 44);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void SameImageColorSrgbAndNumericBlueRedHaveIndependentViewsAndIgnoreAlpha()
    {
        using var session = new D3D12FrameSession();
        var input = TexturedBase() with { DetailSlot = 6, ShadowSlot = 6 };
        var textures = Textures();
        var frame = Frame(textures: textures);
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(Palette, input, frame: frame));
        foreach (int x in new[] { 36, 48, 60 })
            AssertPixel(plain, actual, x, 48, Delta(frame.Surface.Snapshot, Matcap, Palette, input, x, 48));
        byte[] changed = textures[6].Rgba.ToArray();
        for (int i = 3; i < changed.Length; i += 4) changed[i] = (byte)(255 - changed[i]);
        textures[6] = new("same RGB different ignored alpha", 2, 2, changed);
        Assert.Equal(actual.Rgba, session.Capture(Frame(textures: textures), W, H, surfaceStyles: Map(Palette, input)).Rgba);
        var incorrect = input with { BaseEncoding = T8dTestHelpers.EncodingLinearRgb };
        Assert.True(Vector3.Distance(Delta(frame.Surface.Snapshot, Matcap, Palette, input, 48, 48),
            Delta(frame.Surface.Snapshot, Matcap, Palette, incorrect, 48, 48)) * 255 > 3);
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void PerPixelPreFeedsMatcapEmissionAndActualRimRadialOnceBesideNativeGi(bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(native: native, alpha: .61f);
        var palette = Palette with { EmissionStrength = .6f };
        var baseImg = TexturedBase();
        var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4,
            Seed: 13, Composition: RayGiComposition.ToonPalette) : null;
        var plain = session.Capture(frame, W, H, options, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, options, surfaceStyles: Map(palette, baseImg, true, true, frame: frame));
        foreach (int x in new[] { 40, 48, 56 })
            AssertPixel(plain, actual, x, 48, Delta(frame.Surface.Snapshot, Matcap, palette, baseImg, x, 48, true, true), 2.1);
        AssertCoverage(plain, actual);
        Assert.Equal(plain.Rgba, session.Capture(frame, W, H, options, surfaceStyles: Plain()).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void NonzeroMaterialOffsetLeavesDefaultMmdUntouchedAndRemovingChildRestoresOldPalette()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(split: true);
        var baseImg = TexturedBase();
        D3D12SurfaceStyleMap Styles(EndfieldPaletteSpec? pal, EndfieldBaseImageSpec? b = null) =>
            T8dTestHelpers.CreateDirectSurfaceStyleMap(frame, ["mmd", "endfield"], [new(), new()],
                [null, pal is null ? null : [(Matcap with { Palette = pal, BaseImage = b }).ToBinding()]]);
        var plain = session.Capture(frame, W, H, surfaceStyles: Styles(null));
        var old = session.Capture(frame, W, H, surfaceStyles: Styles(Palette));
        var actual = session.Capture(frame, W, H, surfaceStyles: Styles(Palette, baseImg));
        Assert.Equal(6, frame.Surface.Snapshot.Materials[1].StartIndex);
        foreach (int dy in new[] { -4, 4 }) foreach (int dx in new[] { -4, 4 })
        {
            var first = Project(frame.Surface.Snapshot, new(-.45f, 0, 0));
            var second = Project(frame.Surface.Snapshot, new(.45f, 0, 0));
            AssertPixel(plain, actual, first.X + dx, first.Y + dy, Vector3.Zero, 0);
            AssertPixel(plain, actual, second.X + dx, second.Y + dy,
                Delta(frame.Surface.Snapshot, Matcap, Palette, baseImg, second.X + dx, second.Y + dy, material: 1));
        }
        AssertCoverage(plain, actual);
        Assert.Equal(old.Rgba, session.Capture(frame, W, H, surfaceStyles: Styles(Palette)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void MixedMetallicAndBaseTypedColorsKeepIndependentDescriptorSegmentsBesideNativeGi()
    {
        using var session = new D3D12FrameSession();
        var textures = Textures().Concat(new SceneTexture[] {
            new("metal numerical lightmap", 1, 1, [255, 128, 96, 0]),
            new("metal encoded color", 2, 2, [35, 180, 70, 0, 210, 65, 130, 255,
                95, 140, 220, 40, 160, 45, 85, 190]) }).ToArray();
        var frame = Frame(textures: textures, split: true, native: true);
        var metal = TestStylePlugins.GenshinMetallic(9, 10, T8dTestHelpers.EncodingSrgbRgb,
            new(.25f, .45f, .35f), new(.85f, .7f, .95f), new(.7f, .5f, .8f),
            litMode: "Uniform", uniformLitFactor: 1.15f);
        var matcap = Matcap with { MatcapEncoding = T8dTestHelpers.EncodingSrgbRgb };
        var palette = Palette with { EmissionStrength = .45f };
        var baseImg = TexturedBase();
        D3D12SurfaceStyleMap Styles(bool hasMetal, bool hasBase) => T8dTestHelpers.CreateDirectSurfaceStyleMap(
            frame, ["genshin", "endfield"], [new(), new()],
            [hasMetal ? [metal] : null, [(matcap with { Palette = palette, BaseImage = hasBase ? baseImg : null }).ToBinding()]]);
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4,
            Seed: 39, Composition: RayGiComposition.ToonPalette);
        var baseline = session.Capture(frame, W, H, gi, surfaceStyles: Styles(false, false));
        var metalOnly = session.Capture(frame, W, H, gi, surfaceStyles: Styles(true, false));
        var baseOnly = session.Capture(frame, W, H, gi, surfaceStyles: Styles(false, true));
        var both = session.Capture(frame, W, H, gi, surfaceStyles: Styles(true, true));
        Assert.Equal(6, frame.Surface.Snapshot.Materials[1].StartIndex);
        var first = Project(frame.Surface.Snapshot, new(-.45f, 0, 0));
        var second = Project(frame.Surface.Snapshot, new(.45f, 0, 0));
        int metalChanges = 0, baseChanges = 0;
        foreach (int dy in new[] { -5, 0, 5 }) foreach (int dx in new[] { -5, 0, 5 })
        {
            int x0 = first.X + dx, y0 = first.Y + dy, x1 = second.X + dx, y1 = second.Y + dy;
            Assert.Equal(Pixel(metalOnly, x0, y0), Pixel(both, x0, y0));
            Assert.Equal(Pixel(baseOnly, x1, y1), Pixel(both, x1, y1));
            if (!Pixel(baseline, x0, y0).SequenceEqual(Pixel(both, x0, y0))) metalChanges++;
            if (!Pixel(baseline, x1, y1).SequenceEqual(Pixel(both, x1, y1))) baseChanges++;
        }
        Assert.True(metalChanges > 0 && baseChanges > 0, "Both family-specific typed color inputs must be observable.");
        AssertCoverage(baseline, both);
        Assert.Equal(baseline.Rgba, session.Capture(frame, W, H, gi, surfaceStyles: Styles(false, false)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void StraightSsaaSmaaFinalDiffusionKeepsHighResolutionCoverageAndOffIdentity()
    {
        using var session = new D3D12FrameSession();
        var source = Frame(alpha: .43f);
        var frame = new OriginalFrameScene(source.Surface, options: source.Options! with
            { SpatialSamples = 2, Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.High });
        var baseImg = TexturedBase();
        var styles = Map(Palette, baseImg, true, true, frame: frame);
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, surfaceStyles: styles);
        AssertCoverage(plain, actual);
        AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, Matcap, Palette, baseImg, 48, 48, true, true), 2.1);
        var original = session.Capture(frame, 1440, 900, surfaceStyles: styles);
        var filtered = session.Capture(frame, 1440, 900, surfaceStyles: styles, diffusion: new(true, .7f, .002f));
        AssertCoverage(original, filtered);
        int changed = 0;
        for (int i = 0; i < filtered.Rgba.Length; i += 4)
        {
            if (filtered.Rgba[i + 3] == 0) Assert.Equal(new byte[] { 0, 0, 0 }, filtered.Rgba.AsSpan(i, 3).ToArray());
            else if (!original.Rgba.AsSpan(i, 3).SequenceEqual(filtered.Rgba.AsSpan(i, 3))) changed++;
        }
        Assert.True(changed > 100);
        Assert.Equal(original.Rgba, session.Capture(frame, 1440, 900, surfaceStyles: styles,
            diffusion: D3D12DiffusionOptions.Off).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void BaseImageEditsResetBothHistoriesOnSubmitAndAbortCannotPoisonWarmCache()
    {
        using var session = new D3D12FrameSession();
        var source = Frame();
        var frame = Replace(source, source.Surface.Snapshot.WithCamera(Camera with { VerticalFovDegrees = 30 }));
        frame = new(frame.Surface, options: frame.Options! with { BackgroundColor = Vector4.One, AlphaOutput = FrameAlphaOutput.LegacyMax });
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Real FSR loader required for NativeAA history acceptance.");
        var temporal = new D3D12TemporalUpscalingOptions { Mode = D3D12TemporalUpscalingMode.Fsr,
            QualityPreset = D3D12FsrQualityPreset.NativeAa, LoaderPath = loader };
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 31,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
        var original = Map(Palette, Base, true, true, frame: frame);
        var edited = Map(Palette, Base with { Contrast = -.3f, WarmTint = 0, ShadowColor = new(.2f, .3f, .4f) }, true, true, frame: frame);
        long reads = session.Runtime.Readbacks;
        Render(original, 1, "FirstFrame", "FirstFrame");
        Render(Map(Palette, Base with { }, true, true, frame: frame), 2, "None", "None");
        var warm = session.Statistics!;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, 96, 72, gi,
            (_, _) => throw new OperationCanceledException("Abort unsubmitted upstream Cpre edit."),
            temporalUpscaling: temporal, surfaceStyles: edited));
        Assert.False(session.Runtime.IsRecording);
        Render(original, 3, "None", "AbortedFrame");
        Render(edited, 1, "ExplicitReset", "ExplicitRequest");
        Render(edited, 2, "None", "None");
        Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
        Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);
        Assert.InRange(session.Statistics.CachedResources, 0, warm.CachedResources + 1);
        Assert.Equal(reads, session.Runtime.Readbacks);

        void Render(D3D12SurfaceStyleMap styles, ulong count, string giReason, string aaReason)
        {
            using var image = session.Render(frame, 96, 72, gi, temporalUpscaling: temporal, surfaceStyles: styles);
            Assert.Equal(count, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(giReason, session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(aaReason, image.Statistics.TemporalUpscaling!.ResetReason);
        }
    }

    private static EndfieldBaseImageSpec TexturedBase() => Base with {
        BaseInputMode = T8dTestHelpers.ModeTexture, BaseSlot = 6,
        BaseEncoding = T8dTestHelpers.EncodingSrgbRgb,
        DetailInputMode = T8dTestHelpers.ModeTexture, DetailSlot = 7,
        ShadowInputMode = T8dTestHelpers.ModeTexture, ShadowSlot = 8 };

    private static D3D12SurfaceStyleMap Plain() => new(["endfield"], registeredStyles: TestStylePlugins.AllPresetCatalog.Plugins.Values);
    private static D3D12SurfaceStyleMap Map(EndfieldPaletteSpec? palette, EndfieldBaseImageSpec? baseImg, bool rim = false, bool hair = false,
        EndfieldMatcapSpec? matcap = null, OriginalFrameScene? frame = null)
    {
        var list = new List<StyleMaterialBindingData>(3);
        if (hair) list.Add(Hair.ToBinding());
        if (rim) list.Add(Rim.ToBinding());
        list.Add(((matcap ?? Matcap) with { Palette = palette, BaseImage = baseImg }).ToBinding());
        return T8dTestHelpers.CreateDirectSurfaceStyleMap(frame ?? Frame(), ["endfield"],
            [new(hair ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body)],
            [list]);
    }

    private static SceneTexture[] Textures() => [new("packed RG", 1, 1, [179, 153, 0, 0]),
        new("P gate", 1, 1, [40, 0, 0, 24]), new("nonwhite MatCap", 1, 1, [40, 180, 70, 180]),
        new("radial Green", 1, 1, [0, 64, 0, 0]),
        new("numerical Blue", 2, 2, [255, 0, 15, 0, 0, 255, 230, 255, 15, 64, 150, 63, 200, 16, 65, 192]),
        new("explicit linear received RGB", 2, 2, [10, 80, 15, 255, 150, 5, 35, 0, 40, 100, 5, 32, 15, 20, 130, 224]),
        new("authored base RGB", 2, 2, [45, 100, 210, 0, 210, 160, 80, 255, 90, 180, 70, 120, 170, 65, 130, 24]),
        new("numerical detail Blue", 2, 2, [255, 0, 30, 0, 0, 255, 210, 255, 255, 128, 145, 99, 44, 90, 85, 22]),
        new("explicit shadow R", 2, 2, [10, 255, 0, 0, 230, 0, 255, 255, 64, 44, 22, 33, 185, 255, 16, 80])];

    private static OriginalFrameScene Frame(SceneTexture[]? textures = null, float uvShift = 0,
        float? constantUv = null, bool native = false, float alpha = 1, bool split = false, bool front = true)
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>(); var materials = new List<SceneMaterial>();
        foreach (float center in split ? new[] { -.45f, .45f } : new[] { 0f })
        {
            float half = split ? .34f : .8f; uint start = (uint)vertices.Count;
            foreach (var p in new[] { new Vector2(-half, -half), new(half, -half), new(half, half), new(-half, half) })
                vertices.Add(new(new(center + p.X, p.Y, 0), Vector3.UnitZ,
                    constantUv is { } u ? new(u, .5f) : new((p.X / half + 1) * .5f, (p.Y / half + 1) * .5f)));
            int first = indices.Count;
            indices.AddRange(front ? [start, start + 2, start + 1, start, start + 3, start + 2] : [start, start + 1, start + 2, start, start + 2, start + 3]);
            materials.Add(new("explicit receiver", first, 6, new(.24f, .2f, .16f, alpha), new(.035f, .025f, .015f), new(.02f), 4));
        }
        var additional = new SceneAdditionalUvs(1, vertices.Select(v => new SceneAdditionalUvVertex(
            new(v.Uv.X * .8f - .17f + uvShift, 1 - v.Uv.Y * .65f + uvShift, 0, 0), default, default, default)).ToArray());
        var scene = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials, textures ?? Textures(),
            camera: Camera, lightDirection: -Vector3.UnitZ, additionalUvs: additional,
            rawRadialSeeds: new(Enumerable.Repeat(Vector3.UnitY, vertices.Count).ToArray()));
        return new(new(scene, materials.Select(_ => new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true)).ToArray(),
                new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero, AlphaOutput: FrameAlphaOutput.Straight),
            lighting: native ? new([new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Directional, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Vector3.One, 1.2f, 30, CastShadows: false)]) : null);
    }

    private static OriginalFrameScene Replace(OriginalFrameScene frame, SceneSnapshot snapshot)
        => new(new(snapshot, frame.Surface.Materials, frame.Surface.Profile), options: frame.Options, lighting: frame.Lighting);

    private static float Radial() => .75f * (64f / 255) * (Hair.Brightness + 1.5001001358032227f);
    private static Vector3 MatColor(SceneSnapshot scene, EndfieldMatcapSpec binding, Vector3 pre)
    {
        var color = scene.Textures[binding.MatcapSlot];
        var image = MatcapOracle.BilinearRepeat(color.Rgba, color.Width, color.Height, new(.5f),
            binding.MatcapEncoding == T8dTestHelpers.EncodingSrgbRgb);
        // All MatCap color fixtures are constant; the source gate saturates to1 for the fixed N/P/coefficient.
        return MatcapOracle.MaterialColor(pre, binding.EffectiveHighlightColor, image, 1);
    }

    // Expected total relative to genuinely plain G; old uniform hair/rim wrappers
    // must cancel before the new per-pixel Cpre baseline is subtracted exactly once.
    private static Vector3 Delta(SceneSnapshot scene, EndfieldMatcapSpec binding,
        EndfieldPaletteSpec palette, EndfieldBaseImageSpec input, int x, int y, bool rim = false, bool hair = false,
        int material = 0, bool front = true)
    {
        Vector2 uv = UvAt(scene, binding.UvChannel, x, y, material);
        Vector3? baseRgb = null; float? detail = null, q = null;
        Vector4 Sample(int index, bool srgb = false)
        {
            var image = scene.Textures[index];
            return MatcapOracle.BilinearRepeat(image.Rgba, image.Width, image.Height, uv, srgb);
        }
        if (input.BaseInputMode == T8dTestHelpers.ModeTexture)
        {
            Vector4 sampled = Sample(input.BaseSlot!.Value, input.BaseEncoding == T8dTestHelpers.EncodingSrgbRgb);
            baseRgb = new(sampled.X, sampled.Y, sampled.Z);
        }
        if (input.DetailInputMode == T8dTestHelpers.ModeTexture) detail = Sample(input.DetailSlot!.Value).Z;
        if (input.ShadowInputMode == T8dTestHelpers.ModeTexture) q = Sample(input.ShadowSlot!.Value).X;
        Vector3 pre = EndfieldBaseImageOracle.Evaluate(input, front, baseRgb, detail, q);
        var result = EndfieldPaletteOracle.Evaluate(pre, MatColor(scene, binding, pre), Rim.RimColor,
            binding.BaselineStrength, rim ? Rim.EdgeStrength : 0, hair ? Radial() : 0,
            palette.UniformEmissionBlue, palette.EmissionStrength, palette.LightInfluence,
            palette.ReceivedLightLinearRgb, palette.Density, uv, hasHair: hair, hasRim: rim);
        return result.Emission - pre * binding.BaselineStrength;
    }

    private static Vector2 UvAt(SceneSnapshot scene, int channel, int x, int y, int material = 0)
    {
        Matrix4x4.Invert(scene.Camera.ViewProjection(W, H), out var inverse);
        var point = Vector4.Transform(new Vector4(2 * (x + .5f) / W - 1, 1 - 2 * (y + .5f) / H, 0, 1), inverse);
        Vector2 local = new(point.X / point.W, point.Y / point.W);
        int start = scene.Materials[material].StartIndex;
        int ia = (int)scene.Indices.Slice(start, 6).ToArray().Min(), ib = ia + 2;
        Vector3 a = scene.Vertices[ia].Position, b = scene.Vertices[ib].Position;
        Vector2 f = (local - new Vector2(a.X, a.Y)) / new Vector2(b.X - a.X, b.Y - a.Y);
        Vector2 At(int index)
        {
            if (channel == 0) return scene.Vertices[index].Uv;
            var uv = scene.AdditionalUvs!.Vertices[index].Uv1; return new(uv.X, uv.Y);
        }
        Vector2 ua = At(ia), ub = At(ib);
        return ua + f * (ub - ua);
    }
    private static (int X, int Y) Project(SceneSnapshot scene, Vector3 local)
    {
        var p = Vector4.Transform(new Vector4(local, 1), scene.Camera.ViewProjection(W, H));
        return ((int)((p.X / p.W + 1) * W / 2), (int)((1 - p.Y / p.W) * H / 2));
    }
    private static byte[] Pixel(RenderedFrame frame, int x, int y) => frame.Rgba.AsSpan((y * W + x) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame plain, RenderedFrame actual, int x, int y, Vector3 delta, double tolerance = 1.6)
    {
        int at = (y * W + x) * 4;
        Assert.True(plain.Rgba[at + 3] > 0, "Independent oracle sample must be inside the receiver.");
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(actual.Rgba[at + channel] - Math.Clamp(plain.Rgba[at + channel] + 255 * delta[channel], 0, 255)), 0, tolerance);
    }
    private static void AssertCoverage(RenderedFrame before, RenderedFrame after)
    { for (int at = 3; at < before.Rgba.Length; at += 4) Assert.Equal(before.Rgba[at], after.Rgba[at]); }
}
