using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Synthetic, explicitly authored downstream inputs. This does not claim Blender
// ShaderToRGB, missing upstream colored ramps or full HA/HB material parity.
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class EndfieldPaletteGpuTests
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

    [Fact, Trait("Category", "Gpu")]
    public void LitOverlayThresholdAndRimRadialCrossTermsReplaceActualOldOwnershipOnce()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame();
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var old = session.Capture(frame, W, H, surfaceStyles: Map(null, true, true));
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(Palette, true, true));
        Vector3 cmat = MatColor(frame.Surface.Snapshot, Matcap);
        Vector3 lit = cmat + Cpre * (2 * Palette.UniformEmissionBlue * Palette.EmissionStrength) + Palette.ReceivedLightLinearRgb;
        Assert.True(cmat.X < .5f && lit.X > .5f, "The overlay must switch branches after partial emission and received light.");
        Assert.True(Vector3.Distance(Cpre, cmat) > .1f, "Emission must use Cpre, not Cmat.");
        foreach (int y in new[] { 40, 48, 56 }) foreach (int x in new[] { 40, 48, 56 })
        {
            var expected = Delta(frame.Surface.Snapshot, Matcap, Palette, x, y, true, true);
            AssertPixel(plain, actual, x, y, expected);
            var wrongPrelit = Overlay(cmat, Rim.RimColor, Rim.EdgeStrength)
                + Cpre * (2 * Palette.UniformEmissionBlue * Palette.EmissionStrength) + Palette.ReceivedLightLinearRgb;
            Vector3 wrong = Wave(wrongPrelit, UvAt(frame.Surface.Snapshot, Matcap.UvChannel, x, y), Palette.Density)
                * (Matcap.BaselineStrength + Rim.EdgeStrength + Radial()) - Cpre * Matcap.BaselineStrength;
            Assert.True(Vector3.Distance(expected, wrong) * 255 > 3, "Fixture must reject overlay before emission/light.");
        }
        AssertCoverage(old, actual);
        Assert.NotEqual(old.Rgba, actual.Rgba);
        Assert.Equal(old.Rgba, session.Capture(frame, W, H, surfaceStyles: Map(null, true, true)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void NonuniformBlueAndReceivedRgbUseSelectedCurrentUv1WithDynamicGeometry()
    {
        using var session = new D3D12FrameSession();
        var binding = Matcap with { UvChannel = 1 };
        var palette = Palette with { EmissionInputMode = T8dTestHelpers.ModeTexture,
            EmissionSlot = 4, ReceivedLightInputMode = T8dTestHelpers.ModeTexture,
            ReceivedLightSlot = 5, EmissionStrength = .7f, LightInfluence = .45f, Density = .021f };
        byte[]? previous = null;
        foreach (float shift in new[] { 0f, .29f })
        {
            // This is the captured evaluated geometry/UV boundary; no PMX morph evaluator parity is claimed.
            var source = Frame(uvShift: shift);
            var snapshot = source.Surface.Snapshot;
            snapshot = snapshot.WithDeformedVertices(snapshot.Vertices.ToArray().Select(v => v with
                { Position = v.Position + new Vector3(shift * .2f, 0, 0), Uv = v.Uv + new Vector2(.19f, -.11f) }).ToArray());
            var frame = Replace(source, snapshot);
            Assert.True(snapshot.HasDynamicVertices);
            var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(palette, matcap: binding, frame: frame));
            foreach (int x in new[] { 36, 48, 60 })
                AssertPixel(plain, actual, x, 44, Delta(snapshot, binding, palette, x, 44));
            AssertCoverage(plain, actual);
            var wrong = Delta(snapshot, binding with { UvChannel = 0 }, palette, 48, 44);
            Assert.True(Vector3.Distance(Delta(snapshot, binding, palette, 48, 44), wrong) * 255 > 3,
                "Selected additional UV must visibly differ from morphed UV0.");
            if (previous is not null) Assert.NotEqual(previous, Pixel(actual, 48, 44));
            previous = Pixel(actual, 48, 44);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void SameImageHasIndependentSrgbMatcapAndNumericalBlueReceivedRolesAndIgnoresInputAlpha()
    {
        using var session = new D3D12FrameSession();
        var source = Textures();
        var binding = Matcap with { MatcapEncoding = T8dTestHelpers.EncodingSrgbRgb };
        var palette = Palette with { EmissionInputMode = T8dTestHelpers.ModeTexture,
            EmissionSlot = 2, ReceivedLightInputMode = T8dTestHelpers.ModeTexture,
            ReceivedLightSlot = 2, EmissionStrength = .6f, LightInfluence = .35f };
        var frame = Frame(textures: source);
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(palette, matcap: binding, frame: frame));
        AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, binding, palette, 48, 48));
        // Change only the alpha of the separate palette inputs: the MatCap image keeps its own alpha.
        var separate = palette with { EmissionSlot = 4, ReceivedLightSlot = 5 };
        var rgba = source[2].Rgba.ToArray(); rgba[3] = 0;
        var zeroAlpha = Frame(textures: [source[0], source[1], source[2], source[3],
            new("numeric same RGB alpha0", 1, 1, rgba), new("received same RGB alpha0", 1, 1, rgba)]);
        rgba[3] = 255;
        var fullAlpha = Frame(textures: [source[0], source[1], source[2], source[3],
            new("numeric same RGB alpha1", 1, 1, rgba), new("received same RGB alpha1", 1, 1, rgba)]);
        Assert.Equal(actual.Rgba, session.Capture(zeroAlpha, W, H, surfaceStyles: Map(separate, matcap: binding, frame: zeroAlpha)).Rgba);
        Assert.Equal(actual.Rgba, session.Capture(fullAlpha, W, H, surfaceStyles: Map(separate, matcap: binding, frame: fullAlpha)).Rgba);
        var incorrectSrgb = palette with { EmissionInputMode = T8dTestHelpers.ModeUniform,
            EmissionSlot = null, UniformEmissionBlue = MatcapOracle.Srgb(70f / 255),
            ReceivedLightInputMode = T8dTestHelpers.ModeUniform, ReceivedLightSlot = null,
            ReceivedLightLinearRgb = new(MatcapOracle.Srgb(40f / 255), MatcapOracle.Srgb(180f / 255), MatcapOracle.Srgb(70f / 255)) };
        Assert.True(Vector3.Distance(Delta(frame.Surface.Snapshot, binding, palette, 48, 48),
            Delta(frame.Surface.Snapshot, binding, incorrectSrgb, 48, 48)) * 255 > 4,
            "The same source image must retain numerical RGB for both new inputs.");
    }

    [Fact, Trait("Category", "Gpu")]
    public void SignedHdrLightAndBlenderHsvSaturationRemainUnclampedUntilFinalTarget()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(constantUv: MathF.PI / 20);
        var palette = Palette with { EmissionStrength = -2, UniformEmissionBlue = .5f,
            ReceivedLightLinearRgb = new(.15f, .6f, .3f), Density = .01f };
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(palette));
        AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, Matcap, palette, 48, 48));
        Vector3 lit = MatColor(frame.Surface.Snapshot, Matcap) - Cpre * 2 + palette.ReceivedLightLinearRgb;
        Assert.True(lit.X < 0 && lit.Y > 0);
        Vector3 wrong = lit * .7999999523162842f * Matcap.BaselineStrength - Cpre * Matcap.BaselineStrength;
        Assert.True(Vector3.Distance(Delta(frame.Surface.Snapshot, Matcap, palette, 48, 48), wrong) * 255 > 8,
            "Mixed-sign HSV clamps saturation and cannot be replaced by RGB*.8.");
        foreach (float influence in new[] { -4f, 4f })
        {
            var edited = palette with { LightInfluence = influence, ReceivedLightLinearRgb = new(-.1f, 1.2f, .2f) };
            var image = session.Capture(frame, W, H, surfaceStyles: Map(edited));
            AssertPixel(plain, image, 48, 48, Delta(frame.Surface.Snapshot, Matcap, edited, 48, 48));
            Assert.Equal(image.Rgba, session.Capture(frame, W, H,
                surfaceStyles: Map(edited with { LightInfluence = Math.Clamp(influence, 0, 1) })).Rgba);
        }
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void MainSurfacePaletteDeltaLeavesNativeGiPmxSpecularAndCoverageIntact(bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(native: native, alpha: .61f);
        var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4,
            Seed: 13, Composition: RayGiComposition.ToonPalette) : null;
        var plain = session.Capture(frame, W, H, options, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, options, surfaceStyles: Map(Palette));
        AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, Matcap, Palette, 48, 48), 2.1);
        AssertCoverage(plain, actual);
        Assert.Equal(plain.Rgba, session.Capture(frame, W, H, options, surfaceStyles: Plain()).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void MixedMmdAndEndfieldDrawUseLocalCornersAfterNonzeroMaterialStartIndex()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(split: true);
        D3D12SurfaceStyleMap Styles(bool enabled) => T8dTestHelpers.CreateDirectSurfaceStyleMap(frame,
            ["mmd", "endfield"], [new(), new()],
            [null, enabled ? [(Matcap with { Palette = Palette }).ToBinding()] : null]);
        var plain = session.Capture(frame, W, H, surfaceStyles: Styles(false));
        var actual = session.Capture(frame, W, H, surfaceStyles: Styles(true));
        Assert.Equal(6, frame.Surface.Snapshot.Materials[1].StartIndex);
        foreach (int dy in new[] { -4, 4 }) foreach (int dx in new[] { -4, 4 })
        {
            var first = Project(frame.Surface.Snapshot, new(-.45f, 0, 0));
            var second = Project(frame.Surface.Snapshot, new(.45f, 0, 0));
            AssertPixel(plain, actual, first.X + dx, first.Y + dy, Vector3.Zero, 0);
            AssertPixel(plain, actual, second.X + dx, second.Y + dy,
                Delta(frame.Surface.Snapshot, Matcap, Palette, second.X + dx, second.Y + dy, material: 1));
        }
        AssertCoverage(plain, actual);
    }

    [Fact, Trait("Category", "Gpu")]
    public void StraightSsaaSmaaAndFinalDiffusionPreserveHighResolutionAlphaAndExactOff()
    {
        using var session = new D3D12FrameSession();
        var source = Frame(alpha: .43f);
        var frame = new OriginalFrameScene(source.Surface, options: source.Options! with
            { SpatialSamples = 2, Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.High });
        var plain = session.Capture(frame, W, H, surfaceStyles: Plain());
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(Palette, true, true));
        AssertCoverage(plain, actual);
        AssertPixel(plain, actual, 48, 48, Delta(frame.Surface.Snapshot, Matcap, Palette, 48, 48, true, true), 2.1);
        var original = session.Capture(frame, 1440, 900, surfaceStyles: Map(Palette, true, true));
        var filtered = session.Capture(frame, 1440, 900, surfaceStyles: Map(Palette, true, true), diffusion: new(true, .7f, .002f));
        AssertCoverage(original, filtered);
        int changed = 0;
        for (int at = 0; at < filtered.Rgba.Length; at += 4)
        {
            if (filtered.Rgba[at + 3] == 0) Assert.Equal(new byte[] { 0, 0, 0 }, filtered.Rgba.AsSpan(at, 3).ToArray());
            else if (!original.Rgba.AsSpan(at, 3).SequenceEqual(filtered.Rgba.AsSpan(at, 3))) changed++;
        }
        Assert.True(changed > 100);
        Assert.Equal(original.Rgba, session.Capture(frame, 1440, 900, surfaceStyles: Map(Palette, true, true),
            diffusion: D3D12DiffusionOptions.Off).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void PaletteEditsResetBothHistoriesOnlyAfterSubmitAndWarmRenderHasNoReadbackOrCacheGrowth()
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
        var original = Map(Palette, true, true);
        var edited = Map(Palette with { EmissionStrength = .3f, ReceivedLightLinearRgb = new(.02f, .18f, .04f), Density = .035f }, true, true);
        long reads = session.Runtime.Readbacks;
        Render(original, 1, "FirstFrame", "FirstFrame");
        Render(Map(Palette with { }, true, true), 2, "None", "None");
        var warm = session.Statistics!;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, 96, 72, gi,
            (_, _) => throw new OperationCanceledException("Abort unsubmitted downstream palette edit."),
            temporalUpscaling: temporal, surfaceStyles: edited));
        Assert.False(session.Runtime.IsRecording);
        Render(original, 3, "None", "AbortedFrame");
        Render(edited, 1, "ExplicitReset", "ExplicitRequest");
        Render(edited, 2, "None", "None");
        Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
        Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);
        Assert.InRange(session.Statistics.CachedResources, 0, warm.CachedResources + 1);
        Render(Map(null, true, true), 1, "ExplicitReset", "ExplicitRequest");
        Assert.Equal(reads, session.Runtime.Readbacks);

        void Render(D3D12SurfaceStyleMap styles, ulong count, string giReason, string aaReason)
        {
            using var image = session.Render(frame, 96, 72, gi, temporalUpscaling: temporal, surfaceStyles: styles);
            Assert.Equal(count, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(giReason, session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(aaReason, image.Statistics.TemporalUpscaling!.ResetReason);
        }
    }

    private static D3D12SurfaceStyleMap Plain() => new(["endfield"], registeredStyles: TestStylePlugins.AllPresetCatalog.Plugins.Values);
    private static D3D12SurfaceStyleMap Map(EndfieldPaletteSpec? palette, bool rim = false, bool hair = false,
        EndfieldMatcapSpec? matcap = null, OriginalFrameScene? frame = null)
    {
        var list = new List<StyleMaterialBindingData>(3);
        if (hair) list.Add(Hair.ToBinding());
        if (rim) list.Add(Rim.ToBinding());
        list.Add(((matcap ?? Matcap) with { Palette = palette }).ToBinding());
        return T8dTestHelpers.CreateDirectSurfaceStyleMap(frame ?? Frame(), ["endfield"],
            [new(hair ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body)],
            [list]);
    }

    private static SceneTexture[] Textures() => [new("packed RG", 1, 1, [179, 153, 0, 0]),
        new("P gate", 1, 1, [40, 0, 0, 24]), new("nonwhite MatCap", 1, 1, [40, 180, 70, 180]),
        new("radial Green", 1, 1, [0, 64, 0, 0]),
        new("numerical Blue", 2, 2, [255, 0, 15, 0, 0, 255, 230, 255, 15, 64, 150, 63, 200, 16, 65, 192]),
        new("explicit linear received RGB", 2, 2, [10, 80, 15, 255, 150, 5, 35, 0, 40, 100, 5, 32, 15, 20, 130, 224])];

    private static OriginalFrameScene Frame(SceneTexture[]? textures = null, float uvShift = 0,
        float? constantUv = null, bool native = false, float alpha = 1, bool split = false)
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>(); var materials = new List<SceneMaterial>();
        foreach (float center in split ? new[] { -.45f, .45f } : new[] { 0f })
        {
            float half = split ? .34f : .8f; uint start = (uint)vertices.Count;
            foreach (var p in new[] { new Vector2(-half, -half), new(half, -half), new(half, half), new(-half, half) })
                vertices.Add(new(new(center + p.X, p.Y, 0), Vector3.UnitZ,
                    constantUv is { } u ? new(u, .5f) : new((p.X / half + 1) * .5f, (p.Y / half + 1) * .5f)));
            int first = indices.Count;
            indices.AddRange([start, start + 2, start + 1, start, start + 3, start + 2]);
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
    private static Vector3 MatColor(SceneSnapshot scene, EndfieldMatcapSpec binding)
    {
        var color = scene.Textures[binding.MatcapSlot];
        var image = MatcapOracle.BilinearRepeat(color.Rgba, color.Width, color.Height, new(.5f),
            binding.MatcapEncoding == T8dTestHelpers.EncodingSrgbRgb);
        // All MatCap color fixtures are constant; the source gate saturates to1 for the fixed N/P/coefficient.
        return MatcapOracle.MaterialColor(binding.EffectiveBaseColor, binding.EffectiveHighlightColor, image, 1);
    }

    // Independent source composition, grounded against a genuinely plain Endfield GPU
    // frame G. Its target is G+Enew-Cpre*B, not old coupled output plus an idealized delta.
    private static Vector3 Delta(SceneSnapshot scene, EndfieldMatcapSpec binding,
        EndfieldPaletteSpec palette, int x, int y, bool rim = false, bool hair = false, int material = 0)
    {
        Vector2 uv = UvAt(scene, binding.UvChannel, x, y, material);
        float blue = palette.UniformEmissionBlue;
        if (palette.EmissionInputMode == T8dTestHelpers.ModeTexture)
        {
            var image = scene.Textures[palette.EmissionSlot!.Value];
            blue = MatcapOracle.BilinearRepeat(image.Rgba, image.Width, image.Height, uv, false).Z;
        }
        Vector3 received = palette.ReceivedLightLinearRgb;
        if (palette.ReceivedLightInputMode == T8dTestHelpers.ModeTexture)
        {
            var image = scene.Textures[palette.ReceivedLightSlot!.Value];
            Vector4 sampled = MatcapOracle.BilinearRepeat(image.Rgba, image.Width, image.Height, uv, false);
            received = new(sampled.X, sampled.Y, sampled.Z);
        }
        Vector3 lit = MatColor(scene, binding) + binding.EffectiveBaseColor * (2 * blue * palette.EmissionStrength)
            + Math.Clamp(palette.LightInfluence, 0, 1) * received;
        float r = rim ? Rim.EdgeStrength : 0, p = hair ? Radial() : 0;
        if (rim) lit = Overlay(lit, Rim.RimColor, r);
        return Wave(lit, uv, palette.Density) * (binding.BaselineStrength + r + p) - binding.EffectiveBaseColor * binding.BaselineStrength;
    }

    private static Vector3 Overlay(Vector3 color, Vector3 tint, float factor)
        => MatcapOracle.Overlay(color, tint, factor);

    private static Vector3 Wave(Vector3 rgb, Vector2 uv, float density)
    {
        double coordinate = ((double)uv.X * (100 * density) + .000001) * .999999;
        float fac = (float)(.5 + .5 * Math.Sin(20 * coordinate - Math.PI / 2));
        float max = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z)), min = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
        float chroma = max - min, saturation = max == 0 ? 0 : chroma / max, hue = 0;
        if (saturation != 0)
        {
            hue = rgb.X == max ? (rgb.Y - rgb.Z) / chroma : rgb.Y == max ? 2 + (rgb.Z - rgb.X) / chroma : 4 + (rgb.X - rgb.Y) / chroma;
            hue /= 6; if (hue < 0) hue += 1;
        }
        saturation = Math.Clamp(saturation, 0, 1);
        float value = max * .7999999523162842f;
        float wrapped = hue - MathF.Floor(hue), sector = wrapped * 6, fraction = sector - MathF.Floor(sector);
        float low = value * (1 - saturation), falling = value * (1 - saturation * fraction), rising = value * (1 - saturation * (1 - fraction));
        Vector3 hsv = ((int)sector % 6) switch { 0 => new(value, rising, low), 1 => new(falling, value, low),
            2 => new(low, value, rising), 3 => new(low, falling, value), 4 => new(rising, low, value), _ => new(value, low, falling) };
        return Vector3.Lerp(rgb, hsv, Math.Clamp(fac, 0, 1));
    }

    private static Vector2 UvAt(SceneSnapshot scene, int channel, int x, int y, int material = 0)
    {
        Matrix4x4.Invert(scene.Camera.ViewProjection(W, H), out var inverse);
        var point = Vector4.Transform(new Vector4(2 * (x + .5f) / W - 1, 1 - 2 * (y + .5f) / H, 0, 1), inverse);
        Vector2 local = new(point.X / point.W, point.Y / point.W);
        int start = scene.Materials[material].StartIndex;
        int ia = (int)scene.Indices[start], ib = (int)scene.Indices[start + 1];
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
