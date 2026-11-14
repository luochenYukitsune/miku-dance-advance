using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Numeric witnesses for the combined additional-diffuse budget, below UNorm clipping.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12StylizedLightBudgetTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector3 Diffuse = new(.4f, .24f, .16f);
    private static readonly Vector3 Toon = new(.2f, .3f, .4f);
    private static readonly Vector3 Lit = Diffuse * .5f;
    private static readonly Vector3 Dark = Lit * Toon;
    private static readonly Vector3 Budget = Lit * (Vector3.One - Toon);
    private static readonly Vector3 DarkNormal = new(-.6f, 0, -.8f);

    public static IEnumerable<object[]> Families()
    {
        foreach (var family in new[] { D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx,
            D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx }) yield return [family];
    }

    public static IEnumerable<object[]> StrongLightCases()
    {
        foreach (var family in Families())
        foreach (int count in new[] { 1, 4 }) yield return [family[0], count];
    }

    public static IEnumerable<object[]> GiCases()
    {
        foreach (var family in Families())
        foreach (var mode in new[] { LightingMode.SoftwareRayTracing, LightingMode.HardwareRayTracing })
            yield return [family[0], mode];
    }

    [Theory, MemberData(nameof(StrongLightCases))]
    public void OneAndFourStrongLightsStopAtTheVisibleEgEndpointRatherThanNativeDiffuse(D3D12SurfaceStyle family, int count)
    {
        using var session = new D3D12FrameSession();
        var frame = Surface(); var map = Map(family);
        var off = session.Capture(frame, Width, Height, surfaceStyles: map);
        var on = session.Capture(WithLights(frame, Lights(count, 16f / count)), Width, Height, surfaceStyles: map);
        AssertPixel(off, Dark); AssertPixel(on, Lit);
        AssertCoverage(off, on);
        Assert.Equal(count, session.NativeLightStatistics!.EnabledLights);
        Assert.Equal(off.Rgba, session.Capture(frame, Width, Height, surfaceStyles: map).Rgba);
    }

    [Theory, MemberData(nameof(Families))]
    public void OneScalarPreservesTheColoredIncrementAndUncappedLowLightValues(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession(); var frame = Surface(); var map = Map(family);
        var color = new Vector3(1, .25f, .125f);
        var low = session.Capture(WithLights(frame, Lights(4, .025f, color)), Width, Height, surfaceStyles: map);
        // NativeDiffuse is twice Eg: total intensity .1 therefore spends .2 B.
        AssertPixel(low, Dark + Budget * color * .2f);
        var high = session.Capture(WithLights(frame, Lights(4, .25f, color)), Width, Height, surfaceStyles: map);
        // Raw X=(2 B.r,.5 B.g,.25 B.b), so k=.5. Per-channel min would visibly
        // change green from 15 to 20 bytes and fails this independent expected value.
        AssertPixel(high, Dark + Budget * new Vector3(1, .25f, .125f));
        AssertCoverage(low, high);
        Assert.Equal(low.Rgba, session.Capture(WithLights(frame, Lights(4, .025f, color)), Width, Height, surfaceStyles: map).Rgba);
    }

    [Theory, MemberData(nameof(Families))]
    public void SaturatedAuthoredSpecularAboveOneKeepsItsOriginalGateBeforeAndAfterTheDiffuseCap(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession(); var map = Map(family);
        const float dot = .175f;
        var normal = new Vector3(dot, 0, -MathF.Sqrt(1 - dot * dot));
        float noH = Vector3.Dot(normal, Vector3.Normalize(Vector3.UnitX - Vector3.UnitZ));
        float shininess = MathF.Log(.5f) / MathF.Log(noH);
        var plain = Surface(normal, shininess: shininess);
        var specular = Surface(normal, specular: new(4), shininess: shininess);
        // Move the diffuse band above this normal signal while preserving the
        // original highlight's c=.525. Strong -Z fill must then trigger the cap.
        OriginalFrameMaterialFlags[] flags = [new(false, false, false) { CharacterToonResponse = new(.75f, .1f) }];
        plain = new(plain.Surface, options: plain.Options, materials: flags);
        specular = new(specular.Surface, options: specular.Options, materials: flags);
        // This is the uploaded authored value; saturating it on the host changes the lobe.
        var uploaded = OriginalStyleMath.ResolveHostColors(specular.Surface.Snapshot.Materials[0], specular.Surface.Profile).SpecularPower;
        Assert.Equal(new Vector3(4), new Vector3(uploaded.X, uploaded.Y, uploaded.Z));
        float highlight = family switch
        {
            D3D12SurfaceStyle.StarRailPmx => .525f * .5f,
            D3D12SurfaceStyle.WutheringPmx => .525f * .5f * SmoothStep(.10f, .30f, .25f),
            _ => .525f,
        };
        var darkPlain = session.Capture(plain, Width, Height, surfaceStyles: map);
        var darkSpecular = session.Capture(specular, Width, Height, surfaceStyles: map);
        var litPlain = session.Capture(WithLights(plain, Lights(4, 4)), Width, Height, surfaceStyles: map);
        var litSpecular = session.Capture(WithLights(specular, Lights(4, 4)), Width, Height, surfaceStyles: map);
        AssertPixel(darkPlain, Dark); AssertPixel(litPlain, Lit); AssertPixel(litSpecular, Lit + new Vector3(highlight));
        AssertIncrement(darkPlain, darkSpecular, new(highlight));
        AssertIncrement(litPlain, litSpecular, new(highlight));
        AssertCoverage(darkPlain, darkSpecular); AssertCoverage(litPlain, litSpecular);
    }

    [Theory, MemberData(nameof(GiCases))]
    public void ActualEmissiveBounceSpendsAQuarterAndCombinesWithNineTenthsDirectAtOneBudget(
        D3D12SurfaceStyle family, LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        if (mode == LightingMode.HardwareRayTracing) Assert.True(session.QueryRayGiCapabilities().HardwareAvailable);
        var frame = Bounce(); var map = new D3D12SurfaceStyleMap([family, D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.Mmd]);
        var options = Gi(mode);
        var off = session.Capture(frame, Width, Height, surfaceStyles: map);
        var gi = session.Capture(frame, Width, Height, options, surfaceStyles: map);
        // DirectScale=0, irradiance=0 and emissive wall=10 mean a nonzero signal
        // proves actual secondary hits. Require the saturated fifth fill level, not only an upper bound.
        AssertPixel(off, Dark); AssertPixel(gi, Dark + Budget * .25f);
        AssertIncrement(off, gi, Budget * .25f);
        Assert.Equal(mode, session.RayGiStatistics!.Effective);
        if (mode == LightingMode.HardwareRayTracing) Assert.StartsWith("DXR-raw-", session.RayGiBackendIdentity);
        else Assert.Equal("GPU-BVH", session.RayGiBackendIdentity);
        var nativeFrame = WithLights(frame, Lights(4, .1125f));
        var direct = session.Capture(nativeFrame, Width, Height, surfaceStyles: map);
        var combined = session.Capture(nativeFrame, Width, Height, options, surfaceStyles: map);
        AssertPixel(direct, Dark + Budget * .9f); AssertPixel(combined, Lit);
        Assert.True(Pixel(combined)[0] >= Pixel(direct)[0] + 3, "The combined cap must still retain measurable GI fill.");
        Assert.True(session.NativeLightStatistics!.NativeLightsContributeToGi);
        Assert.Equal(4, session.RayGiStatistics!.SecondaryNativeLights);
        AssertCoverage(off, gi); AssertCoverage(direct, combined);
        // The extra visible right-hand draw stays original MMD in the same frame.
        var mmdDirect = session.Capture(nativeFrame, Width, Height);
        var mmdCombined = session.Capture(nativeFrame, Width, Height, options);
        var control = Project(frame, new(2.45f, 0, 0));
        Assert.True(Pixel(mmdDirect, control.X, control.Y)[3] > 0, "MMD control must be visible.");
        Assert.Equal(Pixel(mmdDirect, control.X, control.Y), Pixel(direct, control.X, control.Y));
        Assert.Equal(Pixel(mmdCombined, control.X, control.Y), Pixel(combined, control.X, control.Y));
        Assert.Equal(off.Rgba, session.Capture(frame, Width, Height, surfaceStyles: map).Rgba);
        using var rendered = session.Render(nativeFrame, Width, Height, options, surfaceStyles: map);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
    }

    [Theory, MemberData(nameof(Families))]
    public void EnabledAdditiveCompositionRetainsItsExistingUncappedNativeArithmetic(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession(); var map = Map(family);
        var frame = WithLights(Surface(), Lights(4, .1375f));
        // An enabled GI variant with zero strength isolates its composition tag.
        // Additive's established native result is base+1.1B, safely below UNorm clipping.
        var additive = Gi(LightingMode.SoftwareRayTracing) with { Strength = 0, Composition = RayGiComposition.Additive };
        var result = session.Capture(frame, Width, Height, additive, surfaceStyles: map);
        AssertPixel(result, Dark + Budget * 1.1f);
        AssertPixel(session.Capture(frame, Width, Height, surfaceStyles: map), Lit);
    }

    [Theory, MemberData(nameof(Families))]
    public void TriggeredSubTextureCapKeepsRawToonAlphaTranspAndStraightSourceOrder(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var map = new D3D12SurfaceStyleMap([family, family]);
        foreach (bool transp in new[] { false, true })
        {
            var frame = Layers(transp); var off = session.Capture(frame, Width, Height, surfaceStyles: map);
            var on = session.Capture(WithLights(frame, Lights(4, 4)), Width, Height, surfaceStyles: map);
            float alpha = transp ? .5f : (200 / 255f) * (220 / 255f) * .8f;
            float composedAlpha = alpha * (2 - alpha);
            Vector3 texture = new Vector3(.8f, .7f, .6f) * new Vector3(128 / 255f, 192 / 255f, 64 / 255f);
            Vector3 first = new Vector3(.2f, .05f, .025f) * texture;
            Vector3 second = new Vector3(.025f, .2f, .05f) * texture;
            AssertPixel(on, (first * (1 - alpha) + second) / (2 - alpha), composedAlpha);
            AssertCoverage(off, on);
            // Keep the established GI input restriction: this source-order fixture
            // disables depth writes, and Transp is independently unsupported by GI.
            Assert.Throws<NotSupportedException>(() => session.Capture(WithLights(frame, Lights(4, 4)), Width, Height,
                Gi(LightingMode.SoftwareRayTracing), surfaceStyles: map));
            Assert.Equal(on.Rgba, session.Capture(WithLights(frame, Lights(4, 4)), Width, Height, surfaceStyles: map).Rgba);
            Assert.Equal(off.Rgba, session.Capture(frame, Width, Height, surfaceStyles: map).Rgba);
        }
    }

    [Theory, MemberData(nameof(Families))]
    public void ExplicitFaceSdfFeedsTheSameTriggeredBudgetAsTheVisibleSurface(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var source = Surface(); var mesh = source.Surface.Snapshot;
        // Deliberately numerical, constant masks. Their alpha never supplies coverage.
        SceneTexture[] textures = family == D3D12SurfaceStyle.EndfieldPmx
            ? [new("RGB SDF", 1, 1, [128, 255, 0, 0]), new("cm_M", 1, 1, [128, 0, 0, 0])]
            : [new("gray numerical SDF", 1, 1, [100, 100, 100, 0]), new("GA mask", 1, 1, [0, 255, 0, 0])];
        var scene = new SceneSnapshot(mesh.Vertices, mesh.Indices, mesh.Materials, textures,
            mesh.World, mesh.Camera, mesh.LightDirection);
        var frame = new OriginalFrameScene(new(scene, source.Surface.Materials, source.Surface.Profile), options: source.Options);
        var binding = family switch
        {
            D3D12SurfaceStyle.GenshinPmx => D3D12FaceSdfBinding.GenshinSoft(0, 1, -Vector3.UnitZ, Vector3.UnitX, .05f, .55f),
            D3D12SurfaceStyle.EndfieldPmx => new(0, -Vector3.UnitZ, Vector3.UnitX, .1f, 1,
                Encoding: D3D12FaceSdfEncoding.EndfieldRgbCmM, SecondaryCombinedTextureIndex: 1),
            _ => new D3D12FaceSdfBinding(0, -Vector3.UnitZ, Vector3.UnitX, family == D3D12SurfaceStyle.StarRailPmx ? 1 : .1f),
        };
        var map = new D3D12SurfaceStyleMap([family], [new(D3D12CharacterMaterialRole.Face, 1, 1, binding)]);
        var off = session.Capture(frame, Width, Height, surfaceStyles: map);
        var lights = Lights(4, 4, towardPositiveZ: family == D3D12SurfaceStyle.GenshinPmx);
        var on = session.Capture(WithLights(frame, lights), Width, Height, surfaceStyles: map);
        Assert.True(Pixel(off)[0] < Pixel(on)[0] - 3, "Explicit FaceSDF fixture must have a nonzero remaining budget.");
        AssertPixel(on, Lit); AssertCoverage(off, on);
    }

    private static OriginalFrameScene Surface(Vector3? normal = null, Vector3? specular = null, float shininess = 1)
    {
        var material = new SceneMaterial("authored ordinary PMX", 0, 6, new(Diffuse, 1), specular ?? Vector3.Zero, Vector3.Zero, shininess);
        var scene = new SceneSnapshot(Quad(-.8f, .8f, -.6f, .6f, 0, normal ?? DarkNormal), [0, 2, 1, 0, 3, 2], [material],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30), lightDirection: Vector3.UnitX);
        return new(new(scene, [new(Toon)], new(new(.5f), Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: Options());
    }

    private static OriginalFrameScene Bounce()
    {
        var source = D3D12RayGiTests.Bounce(); var mesh = source.Surface.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for (int i = 0; i < 4; i++) vertices[i] = vertices[i] with { Normal = DarkNormal };
        vertices = [.. vertices, .. Quad(2.15f, 2.75f, -.8f, .8f, 0, -Vector3.UnitZ)];
        var materials = mesh.Materials.ToArray();
        materials[0] = materials[0] with { Diffuse = new(Diffuse, 1), Ambient = Vector3.Zero, Specular = Vector3.Zero };
        materials[1] = materials[1] with { Emission = new(10) };
        var scene = new SceneSnapshot(vertices, [.. mesh.Indices, 8, 10, 9, 8, 11, 10],
            [.. materials, new("original MMD control", 12, 6, new(.2f, .16f, .12f, 1), Vector3.Zero, Vector3.Zero, 1)],
            mesh.Textures, mesh.World, mesh.Camera, Vector3.UnitX);
        return new(new(scene, [new(Toon), source.Surface.Materials[1], new(Toon)],
            new(new(.5f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: Options(),
            materials: [new(false, false, false) { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands },
                new(false, false, false), new(false, false, false)]);
    }

    private static OriginalFrameScene Layers(bool transp)
    {
        var quad = Quad(-.8f, .8f, -.6f, .6f, 0, DarkNormal);
        var additional = new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 8).ToArray());
        var scene = new SceneSnapshot([.. quad, .. quad], [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("red first", 0, 6, new(.4f, .1f, .05f, 1), Vector3.Zero, Vector3.Zero, 1, 0),
                new("green second", 6, 6, new(.05f, .4f, .1f, 1), Vector3.Zero, Vector3.Zero, 1, 0)],
            [new("raw coverage", 1, 1, [255, 255, 255, 200]), new("UV1 sphere", 2, 1, [128, 192, 64, 220, 255, 0, 255, 255])],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30), lightDirection: Vector3.UnitX, additionalUvs: additional);
        var binding = new OriginalStyleMaterialBinding(Toon, 1, OriginalSphereMode.SubTexture, true)
        {
            TextureFactors = OriginalMaterialTextureFactors.Identity with
            { Diffuse = new(new(.8f, .7f, .6f, 1), Vector4.Zero), Toon = new(new(1, 1, 1, .8f), Vector4.Zero) },
        };
        return new(new(scene, [binding, binding], new(new(.5f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: Options() with { WriteDepth = false, Transp = transp, AlphaOutput = FrameAlphaOutput.Straight });
    }

    private static RayGiOptions Gi(LightingMode mode) => new(mode, Strength: 20, SamplesPerPixel: 32, Seed: 17,
        MaxRayDistance: 50, RayEpsilon: .001f, DirectionalLightDirection: Vector3.UnitZ,
        DirectionalIrradiance: Vector3.Zero, DirectScale: 0, OcclusionStrength: 0, Composition: RayGiComposition.ToonPalette);
    private static OriginalFrameOptions Options() => new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero);
    private static D3D12SurfaceStyleMap Map(D3D12SurfaceStyle family) => new([family]);
    private static OriginalFrameScene WithLights(OriginalFrameScene frame, IEnumerable<NativeSceneLight> lights)
        => new(frame.Surface, options: frame.Options, materials: frame.Materials, lighting: new(lights));
    private static NativeSceneLight[] Lights(int count, float intensity, Vector3? color = null, bool towardPositiveZ = false)
        => Enumerable.Range(0, count).Select(i => new NativeSceneLight(Guid.NewGuid(), "budget light " + i,
            SceneLightKind.Directional, Vector3.Zero,
            towardPositiveZ ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
            color ?? Vector3.One, intensity, 30, CastShadows: false)).ToArray();
    private static SceneVertex[] Quad(float left, float right, float bottom, float top, float z, Vector3 normal)
        => [new(new(left, bottom, z), normal, new(.25f, .25f)), new(new(right, bottom, z), normal, new(.25f, .25f)),
            new(new(right, top, z), normal, new(.25f, .25f)), new(new(left, top, z), normal, new(.25f, .25f))];
    private static float SmoothStep(float a, float b, float value)
    { float t = Math.Clamp((value - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    private static (int X, int Y) Project(OriginalFrameScene frame, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), frame.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        return ((int)((clip.X / clip.W * .5f + .5f) * Width), (int)((.5f - clip.Y / clip.W * .5f) * Height));
    }
    private static byte[] Pixel(RenderedFrame frame) => Pixel(frame, Width / 2, Height / 2);
    private static byte[] Pixel(RenderedFrame frame, int x, int y) => frame.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame frame, Vector3 rgb, float alpha = 1)
    {
        var pixel = Pixel(frame); var expected = new Vector4(rgb, alpha);
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(pixel[channel] - expected[channel] * 255) <= 1.1f,
                $"channel {channel}: actual=({string.Join(',', pixel)}), expected={expected} * 255");
    }
    private static void AssertIncrement(RenderedFrame before, RenderedFrame after, Vector3 increment)
    {
        var a = Pixel(before); var b = Pixel(after);
        for (int channel = 0; channel < 3; channel++)
            Assert.True(Math.Abs((b[channel] - a[channel]) - increment[channel] * 255) <= 1.1f,
                $"channel {channel}: actual increment={b[channel] - a[channel]}, expected={increment[channel] * 255}");
    }
    private static void AssertCoverage(RenderedFrame before, RenderedFrame after)
    {
        for (int offset = 3; offset < before.Rgba.Length; offset += 4) Assert.Equal(before.Rgba[offset], after.Rgba[offset]);
    }
}
