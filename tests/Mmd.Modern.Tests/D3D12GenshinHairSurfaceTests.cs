using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Original numerical fixtures for the explicitly selected packed R/B response.
/// These are a PMX palette adapter, not a complete Genshin material or imported asset.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12GenshinHairSurfaceTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector3 Specular = new(.8f, .4f, .2f);

    public static IEnumerable<object[]> Variants() => D3D12GenshinSurfaceTests.SurfaceVariants();

    [Theory, MemberData(nameof(Variants))]
    public void AllTwelveVariantsCompileWithAnIndependentRootAbiAndOneReplacedLobe(string entry, bool native, bool gi, bool morph, bool sub)
    {
        var shader = T8bTestHelpers.CompileGenshinPixel("hair", entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, shader[..4]);
        string source = T8bTestHelpers.AssembleGenshinPixelSource("hair", entry, native, gi, morph, sub);
        Assert.Contains("cbuffer PluginStyleConstants : register(b0, space2)", source);
        Assert.Contains("GenshinHairParameters;", source);
        Assert.Contains("GenshinHairMap : register(t0, space2)", source);
        Assert.Contains("MdaLinearWrapSampler : register(s0, space2)", source);
        Assert.Contains("GenshinCubicSample(GenshinHairMap, v.Uv)", source);
        Assert.Contains("(1 - packed.b) < pow(halfResponse, GenshinHairParameters.y)", source);
        Assert.DoesNotContain("pow(max(0, dot(n, h)), SpecularPower.w)", source);
        Assert.Contains("float3 highlight = MdaStyleSpecular(v, n, h, l, baseColor);", source);
        Assert.Contains("float3 highlight = GenshinHairHighlight(v, n, h);", source);
        Assert.Contains("highlight * originalC", source);
        Assert.Contains("color = baseColor * c + baseColor * ToonShadowColor.rgb * (1 - c) + highlight;", source);
        Assert.DoesNotContain("FaceSdfMap", source);
        if (sub) Assert.Contains("originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC)", source);
    }

    [Fact, Trait("Category", "Gpu")]
    public void RedControlsAmplitudeBlueControlsTheStrictBoundaryAndZeroRedRemovesTheOldLobe()
    {
        using var session = new D3D12FrameSession();
        foreach (byte red in new byte[] { 0, 64, 128, 255 })
        foreach (byte blue in new byte[] { 0, 127, 128, 255 })
        {
            var frame = Frame([red, 93, blue, 17]);
            var binding = TestStylePlugins.GenshinHair(0, globalSpecularScale: .5f, shininess: 1);
            var actual = session.Capture(frame, Width, Height, surfaceStyles: Map(binding));
            var baseline = session.Capture(Frame([red, 93, blue, 17], specular: Vector3.Zero), Width, Height, surfaceStyles: Map(binding));
            float factor = blue >= 128 ? red / 255f * .1f : 0;
            AssertIncrement(baseline, actual, Specular * factor * 255);
            if (factor == 0) Assert.Equal(baseline.Rgba, actual.Rgba);
        }
        // NdotH=1 and scale=1 produce power=1. B=0 gives exact equality,
        // and must stay dark rather than using an inclusive threshold.
        var equality = TestStylePlugins.GenshinHair(0, globalSpecularScale: 1, shininess: 1);
        Assert.Equal(session.Capture(Frame([255, 0, 0, 255], specular: Vector3.Zero), Width, Height, surfaceStyles: Map(equality)).Rgba,
            session.Capture(Frame([255, 0, 0, 255]), Width, Height, surfaceStyles: Map(equality)).Rgba);
        var ordinaryFrame = Frame([128, 0, 255, 255]);
        var ordinary = session.Capture(ordinaryFrame, Width, Height, surfaceStyles: Map(null));
        var selected = session.Capture(ordinaryFrame, Width, Height, surfaceStyles: Map(TestStylePlugins.GenshinHair(0)));
        Assert.NotEqual(Pixel(ordinary), Pixel(selected));
        Assert.Equal(ordinary.Rgba, session.Capture(ordinaryFrame, Width, Height, surfaceStyles: Map(null)).Rgba);
        foreach (string styleId in (string[])["mmd", .. TestStylePlugins.OfficialStyleIds])
        {
            var hair = T8bTestHelpers.CreateDirectMap([styleId], (D3D12CharacterMaterialRole.Hair, null, 0.35f, 1f, 0.2f));
            var body = T8bTestHelpers.CreateDirectMap(styleId);
            Assert.Equal(session.Capture(ordinaryFrame, Width, Height, surfaceStyles: body).Rgba,
                session.Capture(ordinaryFrame, Width, Height, surfaceStyles: hair).Rgba);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void TwoByTwoFractionalAndWrappedUvsUseCubicRepeatRatherThanTheDiffuseSampler()
    {
        using var session = new D3D12FrameSession();
        byte[] packed = [0, 20, 255, 0, 255, 40, 255, 64, 48, 60, 255, 128, 128, 80, 255, 255];
        var binding = TestStylePlugins.GenshinHair(0, globalSpecularScale: 1, shininess: 0, multiplier: .6f);
        foreach (var uv in new[] { new Vector2(.23f, .42f), new Vector2(1.23f, -.58f), new Vector2(-.08f, 1.07f) })
        {
            var actual = session.Capture(Frame(packed, uv: uv, textureWidth: 2, textureHeight: 2), Width, Height, surfaceStyles: Map(binding));
            var baseline = session.Capture(Frame(packed, uv: uv, textureWidth: 2, textureHeight: 2, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(binding));
            float red = CubicChannel(packed, 2, 2, uv, 0);
            AssertIncrement(baseline, actual, Specular * (red * .6f * 255));
            Assert.True(Math.Abs(red - LinearChannel(packed, 2, 2, uv, 0)) * .6f * Specular.X * 255 > 2,
                "The numerical oracle must distinguish four-fetch cubic reconstruction from one linear sample.");
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    [Trait("Category", "Gpu")]
    public void ExistingOriginalCShadowAttenuationAndOutsideProjectionCompositionAreRetained(bool outside)
    {
        using var session = new D3D12FrameSession();
        const float d = .175f;
        var normal = new Vector3(d, 0, -MathF.Sqrt(1 - d * d));
        var binding = TestStylePlugins.GenshinHair(0, globalSpecularScale: 1, shininess: 0);
        var actual = session.Capture(Frame([128, 0, 255, 255], normal: normal, light: Vector3.UnitX, outside: outside), Width, Height, surfaceStyles: Map(binding));
        var baseline = session.Capture(Frame([128, 0, 255, 255], normal: normal, light: Vector3.UnitX, outside: outside, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(binding));
        AssertIncrement(baseline, actual, Specular * (128 / 255f * .1f * (outside ? 1 : 3 * d) * 255));
    }

    [Theory, InlineData(0f), InlineData(1.0004f), InlineData(1.001f)]
    [Trait("Category", "Gpu")]
    public void ProjectedShadowVisibilityAttenuatesTheDedicatedHighlightWithTheOriginalContrast(float depth)
    {
        using var session = new D3D12FrameSession();
        var binding = TestStylePlugins.GenshinHair(0, globalSpecularScale: 1, shininess: 0);
        var actual = session.Capture(Frame([128, 0, 255, 255], projectedDepth: depth), Width, Height, surfaceStyles: Map(binding));
        var baseline = session.Capture(Frame([128, 0, 255, 255], projectedDepth: depth, specular: Vector3.Zero), Width, Height, surfaceStyles: Map(binding));
        float visibility = OriginalFrameMath.Visibility(depth, 1, .5f, false);
        AssertIncrement(baseline, actual, Specular * (128 / 255f * .1f * visibility * 255));
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void NativeAndGiRootsRetainTheSinglePrimaryHighlightAndWarmRenderingDoesNotReadBack(bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var binding = TestStylePlugins.GenshinHair(0);
        var frame = Frame([128, 200, 255, 0], native: native);
        var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 17,
            Composition: RayGiComposition.ToonPalette) : null;
        var actual = session.Capture(frame, Width, Height, options, surfaceStyles: Map(binding));
        var baseline = session.Capture(Frame([128, 200, 255, 0], native: native, specular: Vector3.Zero), Width, Height, options, surfaceStyles: Map(binding));
        AssertIncrement(baseline, actual, Specular * (128 / 255f * .1f * 255));
        var warm = session.Statistics!; long readbacks = session.Runtime.Readbacks;
        for (int i = 0; i < 4; i++)
        {
            using var rendered = session.Render(frame, Width, Height, options,
                surfaceStyles: Map(TestStylePlugins.GenshinHair(0, intensity: .5f + i * .1f)));
            Assert.Equal(0, rendered.Statistics.ReadbackCount);
            Assert.Equal(warm.ShaderCompilations, rendered.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, rendered.Statistics.PipelineCreations);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);
    }

    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void PackedAlphaNeverChangesCoverageThroughMorphSphereSubTextureOrStraightOutput(bool straight, bool sub)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame([128, 255, 255, 0], transparent: true, straight: straight, sub: sub, sphere: !sub);
        var ordinary = session.Capture(frame, Width, Height, surfaceStyles: Map(null));
        var binding = TestStylePlugins.GenshinHair(2);
        var selected = session.Capture(frame, Width, Height, surfaceStyles: Map(binding));
        for (int i = 3; i < ordinary.Rgba.Length; i += 4) Assert.Equal(ordinary.Rgba[i], selected.Rgba[i]);
        var changedUnused = Frame([128, 0, 255, 255], transparent: true, straight: straight, sub: sub, sphere: !sub);
        Assert.Equal(selected.Rgba, session.Capture(changedUnused, Width, Height, surfaceStyles: Map(binding)).Rgba);
        Assert.Equal(ordinary.Rgba, session.Capture(frame, Width, Height, surfaceStyles: Map(null)).Rgba);
        Assert.InRange(Pixel(selected)[3], 15, 64);
    }

    [Theory]
    [InlineData(false, false, false), InlineData(true, false, false)]
    [InlineData(false, true, false), InlineData(true, true, false)]
    [InlineData(false, false, true), InlineData(true, false, true)]
    [InlineData(false, true, true), InlineData(true, true, true)]
    [Trait("Category", "Gpu")]
    public void MixedFaceHairAndOrdinaryDrawsBindIndependentDescriptorsAndSupportBothCullingModes(bool both, bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var source = Frame([128, 0, 255, 0], native: native); var old = source.Surface.Snapshot;
        var vertices = new List<SceneVertex>();
        foreach (float y in new[] { .65f, 0f, -.65f })
            vertices.AddRange(old.Vertices.ToArray().Select(v => v with { Position = new(v.Position.X, v.Position.Y * .4f + y, 0) }));
        var materials = Enumerable.Range(0, 3).Select(i => old.Materials[0] with { StartIndex = i * 6 }).ToArray();
        var textures = old.Textures.Concat(new SceneTexture[] { new("original face RGB", 1, 1, [255, 255, 255, 0]),
            new("original face GA", 1, 1, [0, 255, 0, 255]) }).ToArray();
        var scene = new SceneSnapshot(vertices.ToArray(), [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6, 8, 10, 9, 8, 11, 10], materials,
            textures, camera: old.Camera, lightDirection: old.LightDirection);
        var surfaceBinding = source.Surface.Materials[0] with { DoubleSided = both };
        var frame = new OriginalFrameScene(new(scene, [surfaceBinding, surfaceBinding, surfaceBinding], source.Surface.Profile), options: source.Options, lighting: source.Lighting);
        var giOptions = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 17,
            Composition: RayGiComposition.ToonPalette) : null;
        var face = TestStylePlugins.FaceSdf("genshin", 1, 2, "GenshinSoftFace", .001f, .55f);
        var ordinary = T8bTestHelpers.CreateDirectMap(
            ["genshin", "genshin", "mmd"],
            (D3D12CharacterMaterialRole.Face, [face], 1f, 1f, 0.2f),
            (D3D12CharacterMaterialRole.Body, null, 0.35f, 1f, 0.2f),
            (D3D12CharacterMaterialRole.Body, null, 0.35f, 1f, 0.2f));
        var mixed = T8bTestHelpers.CreateDirectMap(
            ["genshin", "genshin", "mmd"],
            (D3D12CharacterMaterialRole.Face, [face], 1f, 1f, 0.2f),
            (D3D12CharacterMaterialRole.Hair, [TestStylePlugins.GenshinHair(0)], 0.35f, 1f, 0.2f),
            (D3D12CharacterMaterialRole.Body, null, 0.35f, 1f, 0.2f));
        var before = session.Capture(frame, Width, Height, giOptions, surfaceStyles: ordinary);
        var after = session.Capture(frame, Width, Height, giOptions, surfaceStyles: mixed);
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int p = (y * Width + x) * 4;
            Assert.Equal(before.Rgba[p + 3], after.Rgba[p + 3]);
            if (y < Height / 3 || y > 2 * Height / 3) Assert.Equal(before.Rgba.AsSpan(p, 4).ToArray(), after.Rgba.AsSpan(p, 4).ToArray());
        }
        Assert.NotEqual(Pixel(before), Pixel(after));
        Assert.Equal(before.Rgba, session.Capture(frame, Width, Height, giOptions, surfaceStyles: ordinary).Rgba);
    }

    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding)
        => T8bTestHelpers.CreateGenshinMap(D3D12CharacterMaterialRole.Hair, binding);

    private static OriginalFrameScene Frame(byte[] packed, Vector3? specular = null, Vector2? uv = null,
        int textureWidth = 1, int textureHeight = 1, Vector3? normal = null, Vector3? light = null,
        bool outside = false, bool native = false, bool transparent = false, bool straight = false, bool sub = false, bool sphere = false,
        float projectedDepth = 0)
    {
        var n = normal ?? -Vector3.UnitZ; var coordinates = uv ?? new(.25f, .25f);
        SceneVertex[] vertices = [new(new(-.8f, -.8f, 0), n, coordinates), new(new(.8f, -.8f, 0), n, coordinates),
            new(new(.8f, .8f, 0), n, coordinates), new(new(-.8f, .8f, 0), n, coordinates)];
        var textures = new List<SceneTexture>();
        if (transparent) textures.AddRange([new("original diffuse coverage", 1, 1, [220, 200, 180, 128]),
            new("original sphere coverage", 1, 1, [128, 128, 128, 128])]);
        textures.Add(new("original packed numerical R/B", textureWidth, textureHeight, packed));
        var material = new OriginalStyleMaterialBinding(new(.2f), transparent ? 1 : -1,
            sub ? OriginalSphereMode.SubTexture : sphere ? OriginalSphereMode.Multiply : OriginalSphereMode.None, true);
        if (transparent) material = material with { TextureFactors = OriginalMaterialTextureFactors.Identity with
            { Diffuse = new(new(.8f, .7f, .6f, 1), Vector4.Zero), Toon = new(new(1, 1, 1, .2f), Vector4.Zero) } };
        var additional = sub ? new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 4).ToArray()) : null;
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("explicit numerical hair", 0, 6, new(.04f, .03f, .02f, transparent ? .5f : 1), specular ?? Specular, Vector3.Zero, 4, transparent ? 0 : -1)],
            textures, camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: light ?? -Vector3.UnitZ,
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
        for (int channel = 0; channel < 3; channel++) Assert.InRange(Math.Abs(after[channel] - before[channel] - expected[channel]), 0, 1.1f);
        for (int i = 3; i < actual.Rgba.Length; i += 4) Assert.Equal(baseline.Rgba[i], actual.Rgba[i]);
    }
    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan(((Height / 2) * Width + Width / 2) * 4, 4).ToArray();

    private static float CubicChannel(byte[] pixels, int width, int height, Vector2 uv, int channel)
    {
        double x = uv.X * width - .5, y = uv.Y * height - .5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double result = 0;
        static double Weight(double distance)
        {
            double d = Math.Abs(distance);
            return d < 1 ? (4 - 6 * d * d + 3 * d * d * d) / 6 : d < 2 ? Math.Pow(2 - d, 3) / 6 : 0;
        }
        for (int j = -1; j <= 2; j++) for (int i = -1; i <= 2; i++)
        {
            int px = ((ix + i) % width + width) % width, py = ((iy + j) % height + height) % height;
            result += pixels[(py * width + px) * 4 + channel] / 255.0 * Weight(x - ix - i) * Weight(y - iy - j);
        }
        return (float)result;
    }
    private static float LinearChannel(byte[] pixels, int width, int height, Vector2 uv, int channel)
    {
        float x = uv.X * width - .5f, y = uv.Y * height - .5f;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y); float fx = x - ix, fy = y - iy, result = 0;
        for (int j = 0; j <= 1; j++) for (int i = 0; i <= 1; i++)
        {
            int px = ((ix + i) % width + width) % width, py = ((iy + j) % height + height) % height;
            result += pixels[(py * width + px) * 4 + channel] / 255f * (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
        }
        return result;
    }
}
