using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Independent numerical fixtures for the explicitly selected Star A / MC
/// light-response scalar. PMX specular is the MDA palette adapter, not source emission.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12StarMcHairSurfaceTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector3 Specular = new(.8f, .4f, .2f);

    public static IEnumerable<object[]> Variants()
    {
        foreach (var family in Families())
        foreach (var variant in D3D12GenshinSurfaceTests.SurfaceVariants())
            yield return [family, .. variant];
    }

    [Theory, MemberData(nameof(Variants))]
    public void AllTwentyFourVariantsCompileWithSeparateResourcesAndOneLightDrivenHighlight(
        D3D12SurfaceStyle family, string entry, bool native, bool gi, bool morph, bool sub)
    {
        var shader = OriginalFrameShaders.CompileStarMcHairSurface(family, entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, shader[..4]);
        string source = OriginalFrameShaders.StarMcHairSurfaceSource(family, native, gi, morph, sub);
        Assert.Contains("StarMcHairConstants:register(b6)", source);
        Assert.Contains("StarMcHighlightMap:register(t10)", source);
        Assert.Contains("StarMcShadowMap:register(t11)", source);
        Assert.Contains("StarMcHairSampler:register(s4)", source);
        Assert.Contains("StarMcHairHighlight(v,n,l)", source);
        Assert.Contains("SampleLevel(StarMcHairSampler,v.Uv,0)", source);
        Assert.Contains("gate*(k+StarMcHairParameters.y)", source);
        Assert.DoesNotContain("float authoredLobe=", source);
        Assert.DoesNotContain("pow(max(0,dot(n,h)),SpecularPower.w)", source);
        Assert.Equal(1, source.Split("float3 highlight=", StringSplitOptions.None).Length - 1);
        Assert.Contains("+highlight*originalC;", source);
        Assert.DoesNotContain("FaceSdfConstants:register(b4)", source);
        Assert.DoesNotContain("GenshinHairConstants:register(b5)", source);
        if (family == D3D12SurfaceStyle.StarRailPmx)
            Assert.Contains("q*3.333333969116211-2.000000476837158", source);
        else Assert.Contains("float response=q;", source);
        if (sub) Assert.Contains("originalC<1) alpha*=originalC+ToonShadowColor.w*(1-originalC)", source);
    }

    [Theory, InlineData(D3D12SurfaceStyle.StarRailPmx), InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [Trait("Category", "Gpu")]
    public void FixedViewLightSweepMatchesMapRangeAndDistinguishesStarRampFromMcAndHalfVector(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var binding = new D3D12StarMcHairBinding(0, 1, HighlightCoefficient: .15f,
            HighlightBrightness: 2, ShadowCoefficient: .25f, ShadowSoftness: .7f);
        var increments = new List<float>();
        foreach (float d in new[] { .2f, .4f, .6f, .85f, 1f })
        {
            var light = new Vector3(MathF.Sqrt(1 - d * d), 0, -d);
            var actual = session.Capture(Frame([0, 0, 128, 9], [166, 18, 37, 81], light: light), Width, Height, surfaceStyles: Map(family, binding));
            var baseline = session.Capture(Frame([0, 0, 128, 9], [166, 18, 37, 81], light: light, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(family, binding));
            var expected = Oracle(family, binding, 128 / 255d, 166 / 255d, d) * Math.Clamp(3 * d, 0, 1) * 255;
            AssertIncrement(baseline, actual, expected);
            increments.Add(Pixel(actual)[0] - Pixel(baseline)[0]);
        }
        Assert.True(increments.Max() - increments.Min() > 8, "The fixture must exercise the light-response boundary.");
        // Fixed light and flat UV data keep the selected response independent of
        // the viewing half-vector. Use each view's own zero-specular reference.
        byte[]? previous = null;
        foreach (var eye in new[] { new Vector3(0, 0, -5), new Vector3(3, 0, -5), new Vector3(-3, 1, -5) })
        {
            var actual = session.Capture(Frame([0, 0, 128, 9], [166, 18, 37, 81], eye: eye), Width, Height, surfaceStyles: Map(family, binding));
            var baseline = session.Capture(Frame([0, 0, 128, 9], [166, 18, 37, 81], eye: eye, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(family, binding));
            AssertIncrement(baseline, actual, Oracle(family, binding, 128 / 255d, 166 / 255d, 1) * 255);
            var increment = Pixel(actual).Zip(Pixel(baseline), (a, b) => (byte)(a - b)).Take(3).ToArray();
            if (previous is not null) Assert.Equal(previous, increment);
            previous = increment;
        }
    }

    [Theory, InlineData(D3D12SurfaceStyle.StarRailPmx), InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [Trait("Category", "Gpu")]
    public void CoefficientClampBrightnessAndFinalPaletteSaturationDoNotAddTheOldLobe(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        foreach (var parameters in new (float C, float B, float I)[] { (0f, -1f, .1f), (.8f, 0f, .1f), (.8f, 10f, .1f), (1f, 10f, 2f) })
        {
            var binding = new D3D12StarMcHairBinding(0, 1, HighlightCoefficient: parameters.C,
                HighlightBrightness: parameters.B, Intensity: parameters.I);
            var actual = session.Capture(Frame([20, 91, 128, 0], [255, 0, 0, 255]), Width, Height, surfaceStyles: Map(family, binding));
            var baseline = session.Capture(Frame([20, 91, 128, 0], [255, 0, 0, 255], specular: Vector3.Zero), Width, Height, surfaceStyles: Map(family, binding));
            AssertIncrement(baseline, actual, Oracle(family, binding, 128 / 255d, 1, 1) * 255);
            if (parameters.B == -1) Assert.Equal(baseline.Rgba, actual.Rgba);
        }
        // k=0 suppresses every selected response even at NdotH=1. The ordinary
        // shader remains observably bright, catching an accidentally retained lobe.
        var zero = new D3D12StarMcHairBinding(0, 1);
        var black = Frame([0, 200, 0, 255], [255, 0, 0, 255]);
        Assert.Equal(session.Capture(Frame([0, 200, 0, 255], [255, 0, 0, 255], specular: Vector3.Zero), Width, Height, surfaceStyles: Map(family, zero)).Rgba,
            session.Capture(black, Width, Height, surfaceStyles: Map(family, zero)).Rgba);
        Assert.NotEqual(Pixel(session.Capture(black, Width, Height, surfaceStyles: Map(family, null))),
            Pixel(session.Capture(black, Width, Height, surfaceStyles: Map(family, zero))));
        var zeroSpec = Frame([255, 255, 255, 255], [255, 255, 255, 255], specular: Vector3.Zero);
        Assert.Equal(session.Capture(zeroSpec, Width, Height, surfaceStyles: Map(family, null)).Rgba,
            session.Capture(zeroSpec, Width, Height, surfaceStyles: Map(family, zero)).Rgba);
    }

    [Theory, InlineData(D3D12SurfaceStyle.StarRailPmx), InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [Trait("Category", "Gpu")]
    public void SeparateHighlightAndShadowImagesUseExplicitLinearRepeatAndShadowSoftness(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        byte[] highlight = [11, 91, 8, 255, 31, 81, 247, 32, 51, 71, 118, 64, 71, 61, 198, 128];
        byte[] shadow = [241, 5, 41, 13, 17, 15, 51, 23, 83, 25, 61, 33, 191, 35, 71, 43];
        const float d = .4f;
        var light = new Vector3(MathF.Sqrt(1 - d * d), 0, -d);
        foreach (float softness in new[] { .2f, .8f })
        foreach (var uv in new[] { new Vector2(.23f, .42f), new Vector2(1.23f, -.58f), new Vector2(-.08f, 1.07f) })
        {
            var binding = new D3D12StarMcHairBinding(0, 1, HighlightBrightness: 2,
                ShadowCoefficient: .7f, ShadowSoftness: softness);
            var actual = session.Capture(Frame(highlight, shadow, uv: uv, textureWidth: 2, textureHeight: 2, light: light), Width, Height, surfaceStyles: Map(family, binding));
            var baseline = session.Capture(Frame(highlight, shadow, uv: uv, textureWidth: 2, textureHeight: 2, light: light, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(family, binding));
            AssertIncrement(baseline, actual, Oracle(family, binding, LinearChannel(highlight, uv, 2), LinearChannel(shadow, uv, 0), d) * 255);
        }
    }

    [Theory, InlineData(D3D12HairHighlightChannel.Blue), InlineData(D3D12HairHighlightChannel.Alpha)]
    [Trait("Category", "Gpu")]
    public void McCanExplicitlySelectBlueOrNumericalAlphaWithoutChangingCoverage(D3D12HairHighlightChannel channel)
    {
        using var session = new D3D12FrameSession();
        var binding = new D3D12StarMcHairBinding(0, 1, channel);
        var actual = session.Capture(Frame([0, 0, 51, 204], [255, 0, 0, 0]), Width, Height, surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, binding));
        var baseline = session.Capture(Frame([0, 0, 51, 204], [255, 0, 0, 0], specular: Vector3.Zero), Width, Height, surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, binding));
        AssertIncrement(baseline, actual, Oracle(D3D12SurfaceStyle.WutheringPmx, binding, (channel == D3D12HairHighlightChannel.Blue ? 51 : 204) / 255d, 1, 1) * 255);
        Assert.Equal(255, Pixel(actual)[3]);
    }

    [Theory, InlineData(D3D12SurfaceStyle.StarRailPmx, false), InlineData(D3D12SurfaceStyle.StarRailPmx, true)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx, false), InlineData(D3D12SurfaceStyle.WutheringPmx, true)]
    [Trait("Category", "Gpu")]
    public void OriginalCAndOutsideProjectionRetainTheirExistingComposition(D3D12SurfaceStyle family, bool outside)
    {
        using var session = new D3D12FrameSession();
        const float d = .175f;
        var normal = new Vector3(d, 0, -MathF.Sqrt(1 - d * d));
        var binding = new D3D12StarMcHairBinding(0, 1, ShadowCoefficient: .5f, ShadowSoftness: 1);
        var actual = session.Capture(Frame([0, 0, 128, 255], [255, 0, 0, 255], normal: normal, light: Vector3.UnitX, outside: outside), Width, Height, surfaceStyles: Map(family, binding));
        var baseline = session.Capture(Frame([0, 0, 128, 255], [255, 0, 0, 255], normal: normal, light: Vector3.UnitX, outside: outside, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(family, binding));
        AssertIncrement(baseline, actual, Oracle(family, binding, 128 / 255d, 1, d) * (outside ? 1 : 3 * d) * 255);
    }

    [Theory, InlineData(0f), InlineData(1.0004f), InlineData(1.001f)]
    [Trait("Category", "Gpu")]
    public void ProjectedShadowContrastStillAttenuatesTheSelectedHighlight(float depth)
    {
        using var session = new D3D12FrameSession();
        foreach (var family in Families())
        {
            var binding = new D3D12StarMcHairBinding(0, 1);
            var actual = session.Capture(Frame([0, 0, 128, 255], [255, 0, 0, 255], projectedDepth: depth), Width, Height, surfaceStyles: Map(family, binding));
            var baseline = session.Capture(Frame([0, 0, 128, 255], [255, 0, 0, 255], projectedDepth: depth, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(family, binding));
            AssertIncrement(baseline, actual, Oracle(family, binding, 128 / 255d, 1, 1) * OriginalFrameMath.Visibility(depth, 1, .5f, false) * 255);
        }
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void NativeGiRootsUseTheNumericResponseAndWarmParameterChangesKeepBoundedCachesWithoutReads(bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 17,
            Composition: RayGiComposition.ToonPalette) : null;
        foreach (var family in Families())
        {
            var binding = new D3D12StarMcHairBinding(0, 1);
            var frame = Frame([0, 77, 128, 0], [255, 1, 2, 0], native: native);
            var actual = session.Capture(frame, Width, Height, options, surfaceStyles: Map(family, binding));
            var baseline = session.Capture(Frame([0, 77, 128, 0], [255, 1, 2, 0], native: native, specular: Vector3.Zero), Width, Height, options, surfaceStyles: Map(family, binding));
            AssertIncrement(baseline, actual, Oracle(family, binding, 128 / 255d, 1, 1) * 255);
            var warm = session.Statistics!; long readbacks = session.Runtime.Readbacks;
            for (int i = 0; i < 8; i++)
            {
                using var rendered = session.Render(frame, Width, Height, options,
                    surfaceStyles: Map(family, binding with { HighlightBrightness = -1 + i, Intensity = .03f + i * .01f }));
                Assert.Equal(0, rendered.Statistics.ReadbackCount);
                Assert.Equal(warm.ShaderCompilations, rendered.Statistics.ShaderCompilations);
                Assert.Equal(warm.PipelineCreations, rendered.Statistics.PipelineCreations);
                Assert.InRange(rendered.Statistics.CachedResources, 0, warm.CachedResources + 2);
            }
            Assert.Equal(readbacks, session.Runtime.Readbacks);
        }
    }

    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void NumericalMasksPreserveMorphSphereSubTextureAndStraightCoverage(bool straight, bool sub)
    {
        using var session = new D3D12FrameSession();
        foreach (var family in Families())
        {
            var frame = Frame([128, 255, 255, 0], [255, 0, 0, 255], transparent: true, straight: straight, sub: sub, sphere: !sub);
            var ordinary = session.Capture(frame, Width, Height, surfaceStyles: Map(family, null));
            var binding = new D3D12StarMcHairBinding(2, 3);
            var selected = session.Capture(frame, Width, Height, surfaceStyles: Map(family, binding));
            for (int i = 3; i < ordinary.Rgba.Length; i += 4) Assert.Equal(ordinary.Rgba[i], selected.Rgba[i]);
            var changedUnused = Frame([0, 0, 255, 255], [255, 255, 255, 0], transparent: true, straight: straight, sub: sub, sphere: !sub);
            Assert.Equal(selected.Rgba, session.Capture(changedUnused, Width, Height, surfaceStyles: Map(family, binding)).Rgba);
            Assert.Equal(ordinary.Rgba, session.Capture(frame, Width, Height, surfaceStyles: Map(family, null)).Rgba);
            Assert.InRange(Pixel(selected)[3], 15, 64);
            if (family == D3D12SurfaceStyle.WutheringPmx)
            {
                var alpha = session.Capture(frame, Width, Height, surfaceStyles: Map(family, binding with { HighlightChannel = D3D12HairHighlightChannel.Alpha }));
                for (int i = 3; i < alpha.Rgba.Length; i += 4) Assert.Equal(selected.Rgba[i], alpha.Rgba[i]);
                Assert.NotEqual(Pixel(selected), Pixel(alpha));
            }
        }
    }

    [Theory, InlineData(false, false, false), InlineData(true, false, false)]
    [InlineData(false, true, false), InlineData(true, true, false)]
    [InlineData(false, false, true), InlineData(true, false, true)]
    [InlineData(false, true, true), InlineData(true, true, true)]
    [Trait("Category", "Gpu")]
    public void AllFamiliesFaceGenshinHairAndBothNewHairDrawsInitializeIndependentRoots(bool both, bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var source = Frame([0, 0, 128, 255], [255, 0, 0, 255], native: native);
        var old = source.Surface.Snapshot;
        const int count = 7;
        var vertices = new List<SceneVertex>();
        for (int i = 0; i < count; i++)
            vertices.AddRange(old.Vertices.ToArray().Select(v => v with { Position = new(v.Position.X, v.Position.Y * .12f + .72f - i * .24f, 0) }));
        var indices = Enumerable.Range(0, count).SelectMany(i => new[] { 0, 2, 1, 0, 3, 2 }.Select(x => (uint)(x + i * 4))).ToArray();
        var materials = Enumerable.Range(0, count).Select(i => old.Materials[0] with { StartIndex = i * 6 }).ToArray();
        var textures = old.Textures.Concat(new SceneTexture[] { new("original face RGB", 1, 1, [255, 255, 255, 0]),
            new("original face GA", 1, 1, [0, 255, 0, 255]), new("original Genshin R/B", 1, 1, [128, 0, 255, 0]) }).ToArray();
        var scene = new SceneSnapshot(vertices.ToArray(), indices, materials, textures, camera: old.Camera, lightDirection: old.LightDirection);
        var frame = new OriginalFrameScene(new(scene, Enumerable.Repeat(source.Surface.Materials[0] with { DoubleSided = both }, count).ToArray(), source.Surface.Profile),
            options: source.Options, lighting: source.Lighting);
        D3D12SurfaceStyle[] families = [D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.GenshinPmx,
            D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx, D3D12SurfaceStyle.StarRailPmx];
        D3D12CharacterMaterialStyle[] ordinary = [new(), new(D3D12CharacterMaterialRole.Face, 1, 1, D3D12FaceSdfBinding.GenshinSoft(2, 3, -Vector3.UnitZ, Vector3.UnitX)),
            new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(4)), new(), new(), new(), new()];
        var selected = ordinary.ToArray();
        selected[3] = new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 1));
        selected[4] = new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 1, D3D12HairHighlightChannel.Alpha));
        var giOptions = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 17, Composition: RayGiComposition.ToonPalette) : null;
        var before = session.Capture(frame, Width, Height, giOptions, surfaceStyles: new(families, ordinary));
        var after = session.Capture(frame, Width, Height, giOptions, surfaceStyles: new(families, selected));
        for (int row = 0; row < count; row++)
        {
            var clip = Vector4.Transform(new Vector4(0, .72f - row * .24f, 0, 1), old.Camera.ViewProjection(Width, Height));
            int y = (int)((.5f - .5f * clip.Y / clip.W) * Height);
            var a = before.Rgba.AsSpan((y * Width + Width / 2) * 4, 4).ToArray();
            var b = after.Rgba.AsSpan((y * Width + Width / 2) * 4, 4).ToArray();
            if (row is 3 or 4) Assert.NotEqual(a, b); else Assert.Equal(a, b);
        }
        for (int i = 3; i < after.Rgba.Length; i += 4) Assert.Equal(before.Rgba[i], after.Rgba[i]);
        Assert.Equal(before.Rgba, session.Capture(frame, Width, Height, giOptions, surfaceStyles: new(families, ordinary)).Rgba);
    }

    private static D3D12SurfaceStyle[] Families() => [D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx];
    private static D3D12SurfaceStyleMap Map(D3D12SurfaceStyle family, D3D12StarMcHairBinding? binding)
        => new([family], [new(D3D12CharacterMaterialRole.Hair, StarMcHair: binding)]);

    private static Vector3 Oracle(D3D12SurfaceStyle family, D3D12StarMcHairBinding binding, double highlight, double shadow, double d)
    {
        // Independent double-precision evaluation of the source's scalar math;
        // the exact stored float ramp coefficients are intentionally preserved.
        double k = Math.Clamp(highlight + binding.HighlightCoefficient, 0, 1);
        double edge = 1 - Math.Clamp(shadow * binding.ShadowCoefficient, 0, 1);
        double u = Math.Clamp((d * .5 + .5 - (edge - binding.ShadowSoftness)) / binding.ShadowSoftness, 0, 1);
        double q = .05000000074505806 + (1 - .05000000074505806) * u;
        double response = family == D3D12SurfaceStyle.StarRailPmx ? Math.Clamp(q * 3.333333969116211 - 2.000000476837158, 0, 1) * (.2126 + .7152 + .0722) : q;
        double brightness = Math.Clamp(response * k, 0, 1) * (k + binding.HighlightBrightness);
        return new((float)Math.Clamp(Specular.X * brightness * binding.Intensity, 0, 1),
            (float)Math.Clamp(Specular.Y * brightness * binding.Intensity, 0, 1),
            (float)Math.Clamp(Specular.Z * brightness * binding.Intensity, 0, 1));
    }

    private static OriginalFrameScene Frame(byte[] highlight, byte[] shadow, Vector3? specular = null, Vector2? uv = null,
        int textureWidth = 1, int textureHeight = 1, Vector3? normal = null, Vector3? light = null, Vector3? eye = null,
        bool outside = false, bool native = false, bool transparent = false, bool straight = false, bool sub = false, bool sphere = false,
        float projectedDepth = 0)
    {
        var n = normal ?? -Vector3.UnitZ; var coordinates = uv ?? new(.25f, .25f);
        SceneVertex[] vertices = [new(new(-.8f, -.8f, 0), n, coordinates), new(new(.8f, -.8f, 0), n, coordinates),
            new(new(.8f, .8f, 0), n, coordinates), new(new(-.8f, .8f, 0), n, coordinates)];
        var textures = new List<SceneTexture>();
        if (transparent) textures.AddRange([new("original diffuse coverage", 1, 1, [220, 200, 180, 128]),
            new("original sphere coverage", 1, 1, [128, 128, 128, 128])]);
        textures.Add(new("original numerical highlight", textureWidth, textureHeight, highlight));
        textures.Add(new("original numerical shadow", textureWidth, textureHeight, shadow));
        var material = new OriginalStyleMaterialBinding(new(.2f), transparent ? 1 : -1,
            sub ? OriginalSphereMode.SubTexture : sphere ? OriginalSphereMode.Multiply : OriginalSphereMode.None, true);
        if (transparent) material = material with { TextureFactors = OriginalMaterialTextureFactors.Identity with
            { Diffuse = new(new(.8f, .7f, .6f, 1), Vector4.Zero), Toon = new(new(1, 1, 1, .2f), Vector4.Zero) } };
        var additional = sub ? new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 4).ToArray()) : null;
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("explicit numerical hair", 0, 6, new(.04f, .03f, .02f, transparent ? .5f : 1), specular ?? Specular, Vector3.Zero, 4, transparent ? 0 : -1)],
            textures, camera: new(eye ?? new Vector3(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: light ?? -Vector3.UnitZ,
            additionalUvs: additional);
        return new(new(scene, [material], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero,
                SelfShadow: new(Matrix4x4.CreateScale(.2f) * Matrix4x4.CreateTranslation(outside ? 3 : 0, 0, projectedDepth), 32, 32, false, false,
                    OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual),
                AlphaOutput: straight ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax),
            lighting: native ? new([new NativeSceneLight(Guid.NewGuid(), "original neutral fill", SceneLightKind.Directional,
                Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), new(.2f), 1, 30, CastShadows: false)]) : null);
    }

    private static void AssertIncrement(RenderedFrame baseline, RenderedFrame actual, Vector3 expected)
    {
        var before = Pixel(baseline); var after = Pixel(actual);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(after[channel] - before[channel] - Math.Min(expected[channel], 255 - before[channel])), 0, 1.1f);
        for (int i = 3; i < actual.Rgba.Length; i += 4) Assert.Equal(baseline.Rgba[i], actual.Rgba[i]);
    }
    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan(((Height / 2) * Width + Width / 2) * 4, 4).ToArray();
    private static double LinearChannel(byte[] pixels, Vector2 uv, int channel)
    {
        double x = uv.X * 2 - .5, y = uv.Y * 2 - .5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double fx = x - ix, fy = y - iy, result = 0;
        for (int j = 0; j <= 1; j++) for (int i = 0; i <= 1; i++)
        {
            int px = ((ix + i) % 2 + 2) % 2, py = ((iy + j) % 2 + 2) % 2;
            result += pixels[(py * 2 + px) * 4 + channel] / 255d * (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
        }
        return result;
    }
}
