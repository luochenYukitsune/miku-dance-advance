using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class GenshinMetallicSpecularGpuTests
{
    private const int W = 97, H = 73;
    private static readonly Vector3 Diffuse = new(.18f, .15f, .12f);
    private static readonly Vector3 PmxSpecular = new(.16f, .12f, .08f);
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30);
    private static readonly D3D12GenshinMetallicSpecularBinding Lobe = new(new(.65f, .5f, .75f), new(.3f, .7f, .4f),
        Shininess: 0, Scale: .45f, Attenuation: .25f);
    // Identity palette isolates the new additive endpoint from the already accepted MatCap.
    private static readonly D3D12GenshinMetallicBinding Metal = new(0, 1,
        D3D12MatcapColorEncoding.SrgbRgb, Vector3.One, Vector3.One, Vector3.One,
        LitMode: D3D12GenshinMetallicLitMode.Uniform, UniformLitFactor: 1);

    [Fact, Trait("Category", "Gpu")]
    public void ZeroMetalWeightRestoresPreviousPmxAndRbFramesExactly()
    {
        using var session = new D3D12FrameSession();
        foreach (bool rb in new[] { false, true })
        foreach (byte red in new byte[] { 229, 230, 255 })
        foreach (float enable in new[] { -1f, 0f, .35f, 1f })
        {
            if (red > 229 && enable > 0) continue;
            var frame = Frame(red: red, pmx: PmxSpecular);
            var previous = Metal with { EnableMetallics = enable };
            var before = session.Capture(frame, W, H, surfaceStyles: Map(previous, rb));
            var after = session.Capture(frame, W, H,
                surfaceStyles: Map(previous with { Specular = Lobe }, rb));
            Assert.Equal(before.Rgba, after.Rgba);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void FractionalMetalWeightReplacesPmxOrRbOnceAndAddsIndependentMetalEndpoint()
    {
        using var session = new D3D12FrameSession();
        foreach (bool rb in new[] { false, true })
        foreach (float enable in new[] { .35f, 1f, 2f })
        {
            var frame = Frame(pmx: PmxSpecular);
            var binding = Metal with { EnableMetallics = enable, Specular = Lobe };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding, rb));
            float m = Math.Clamp(enable, 0, 1);
            Vector3 nonmetal = PmxSpecular * (rb ? .35f : 1);
            Vector3 expected = Diffuse + nonmetal * (1 - m) + Local(1, 1, Lobe) * m;
            AssertPixel(actual, W / 2, H / 2, expected);
            // Previous binding leaves ordinary PMX unchanged; only the new explicit endpoint replaces it.
            var previous = session.Capture(frame, W, H, surfaceStyles: Map(binding with { Specular = null }, rb));
            AssertPixel(previous, W / 2, H / 2, Diffuse + nonmetal * (rb ? 1 - m : 1));
            AssertCoverage(previous, actual);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void BlueScalesTheMetalLobeWhileStrictSharpReplacementBypassesBlue()
    {
        using var session = new D3D12FrameSession();
        var lobe = Lobe with { Scale = .5f, SharpOffset = .5f };
        foreach (byte blue in new byte[] { 0, 96, 255 })
        {
            var frame = Frame(blue: blue);
            var ordinary = session.Capture(frame, W, H, surfaceStyles: Map(Metal with { Specular = lobe }));
            AssertPixel(ordinary, W / 2, H / 2, Diffuse + Local(1, blue / 255f, lobe));
            var sharp = lobe with { SharpOffset = MathF.BitDecrement(.5f) };
            var replaced = session.Capture(frame, W, H, surfaceStyles: Map(Metal with { Specular = sharp }));
            AssertPixel(replaced, W / 2, H / 2, Diffuse + GenshinMetallicOracle.Gamma(sharp.SharpColor));
            Assert.NotEqual(Pixel(ordinary), Pixel(replaced));
        }
        var zeroScaleSharp = lobe with { Scale = 0, SharpOffset = 0 };
        AssertPixel(session.Capture(Frame(blue: 0), W, H,
            surfaceStyles: Map(Metal with { Specular = zeroScaleSharp })), W / 2, H / 2, Diffuse);
        // Authored signed/HDR values survive Gamma and local composition until the final target.
        var signed = Lobe with { Color = new(-.5f, 1.3f, .5f), Scale = .2f };
        AssertPixel(session.Capture(Frame(), W, H, surfaceStyles: Map(Metal with { Specular = signed })),
            W / 2, H / 2, Diffuse + Local(1, 1, signed));
    }

    [Fact, Trait("Category", "Gpu")]
    public void RampUsesNonColorRgbBilinearRowsRepeatSeamAndIgnoresChannelPackedAlpha()
    {
        using var session = new D3D12FrameSession();
        var ramp = Ramp();
        // The same image owner serves an sRGB MatCap and a numerical RGB ramp.
        // Identity palette removes the MatCap color so an incorrect shared decoded role is visible.
        var frame = Frame(image: ramp);
        foreach (float p in new[] { .2f, .63f, 1f, 2f })
        foreach (float mix in new[] { 0f, .35f, 1f })
        {
            var lobe = Lobe with { Scale = p, RampCombinedTextureIndex = 1, RampMix = mix };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(Metal with { Specular = lobe }));
            Vector3 expected = Diffuse + Local(1, 1, lobe,
                q => GenshinMetallicSpecularOracle.LinearRepeat(ramp.Rgba, ramp.Width, ramp.Height, q));
            AssertPixel(actual, W / 2, H / 2, expected);
            var changed = ramp.Rgba.ToArray();
            for (int i = 3; i < changed.Length; i += 4) changed[i] ^= 255;
            var alphaOnly = Frame(image: new("same RGB opposite alpha", ramp.Width, ramp.Height, changed), lightmapAlpha: 255);
            Assert.Equal(actual.Rgba, session.Capture(alphaOnly, W, H,
                surfaceStyles: Map(Metal with { Specular = lobe })).Rgba);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void UniformLitOwnsAttenuationEvenWhenTheOriginalHighlightVisibilityIsZero()
    {
        using var session = new D3D12FrameSession();
        // n dot l=0 forces originalC=0, while shininess0 makes p independent of NdotH.
        var frame = Frame(light: Vector3.UnitX);
        foreach (float lit in new[] { -1f, 0f, .35f, 1f, 2f })
        foreach (float attenuation in new[] { 0f, .25f, 1f })
        {
            var lobe = Lobe with { Attenuation = attenuation };
            var binding = Metal with { UniformLitFactor = lit, Specular = lobe };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
            AssertPixel(actual, W / 2, H / 2, Diffuse + Local(1, 1, lobe, lit: lit));
        }
        foreach (float lit in new[] { 0f, 1f })
        {
            var lobe = Lobe with { Attenuation = 0 };
            var binding = Metal with { LitMode = D3D12GenshinMetallicLitMode.PrimaryResponse,
                UniformLitFactor = -2, Specular = lobe };
            var endpoint = Frame(light: lit == 0 ? Vector3.UnitZ : -Vector3.UnitZ);
            var actual = session.Capture(endpoint, W, H, surfaceStyles: Map(binding));
            AssertPixel(actual, W / 2, H / 2, Diffuse + Local(0, 1, lobe, lit: lit));
            Assert.Equal(actual.Rgba, session.Capture(endpoint, W, H,
                surfaceStyles: Map(binding with { UniformLitFactor = 3 })).Rgba);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void SameCaptureEyePositionAndGeometryNormalDrivePerspectiveAndOrthographicHalfDirection()
    {
        using var session = new D3D12FrameSession();
        var lobe = Lobe with { Shininess = 7, Scale = .8f };
        foreach (bool perspective in new[] { false, true })
        {
            var camera = Camera with { Eye = new(.4f, .2f, -5), Target = new(.4f, .2f, 0),
                Up = new(.25f, 1, 0), VerticalFovDegrees = perspective ? 35 : null };
            Vector3 normal = Vector3.Normalize(new(.4f, -.1f, -.85f));
            Vector3 light = Vector3.Normalize(new(-.35f, .2f, -1));
            var frame = Frame(camera: camera, normal: normal, light: light);
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(Metal with { Specular = lobe }));
            foreach (int x in new[] { 38, 48, 57 })
            foreach (int y in new[] { 29, 36, 43 })
            {
                Vector3 incoming = Vector3.Normalize(camera.Eye - WorldAtPixel(frame.Surface.Snapshot, x, y));
                float nh = Vector3.Dot(normal, Vector3.Normalize(incoming + light));
                AssertPixel(actual, x, y, Diffuse + Local(nh, 1, lobe));
            }
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void NativeAndGiEntriesAddOnlyOnePrimaryMetalLobe()
    {
        using var session = new D3D12FrameSession();
        foreach (bool native in new[] { false, true })
        foreach (bool gi in new[] { false, true })
        {
            // Identity palette spends the entire diffuse budget, so the GI entry cannot re-emit this primary
            // specular endpoint. A strong extra light only adds diffuse light under the shared shoulder.
            var frame = Frame(nativeIntensity: native ? 20 : null);
            var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing,
                SamplesPerPixel: 4, Seed: 13, Composition: RayGiComposition.ToonPalette) : null;
            var actual = session.Capture(frame, W, H, options, surfaceStyles: Map(Metal with { Specular = Lobe }));
            var expected = Diffuse + Local(1, 1, Lobe);
            if (native)
                for (int c = 0; c < 3; c++) Assert.InRange((int)Pixel(actual)[c], (int)(Math.Min(expected[c], 1) * 255) - 1, 254);
            else AssertPixel(actual, W / 2, H / 2, expected);
            Assert.Equal(255, Pixel(actual)[3]);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void StraightAlphaSsaaSmaaAndFinalDiffusionKeepCoverageAndMatchIndependentFilter()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(alpha: .43f, straight: true, samples: 2, smaa: true,
            normal: Vector3.Normalize(new(.5f, .3f, -.8f)));
        var styles = Map(Metal with { Specular = Lobe });
        var previous = session.Capture(frame, W, H, surfaceStyles: Map(Metal));
        var before = session.Capture(frame, W, H, surfaceStyles: styles);
        var after = session.Capture(frame, W, H, surfaceStyles: styles, diffusion: new(true, .7f, .01f));
        AssertCoverage(previous, before); AssertCoverage(before, after);
        Assert.NotEqual(Pixel(previous), Pixel(before));
        var expected = TransparentDiffusionReferenceImage.Apply(before.Rgba, W, H, .7, .01);
        for (int i = 0; i < expected.Length; i++) if ((i & 3) != 3)
            Assert.InRange(Math.Abs(expected[i] - after.Rgba[i]), 0, 2);
        Assert.Equal(new[] { "diffusion:x", "diffusion:y", "alpha:straight" },
            session.LastRecordings.TakeLast(3).Select(r => r.Channel));
        Assert.Equal(before.Rgba, session.Capture(frame, W, H, surfaceStyles: styles).Rgba);
        Assert.Equal(previous.Rgba, session.Capture(frame, W, H, surfaceStyles: Map(Metal)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void WarmParameterEditsReusePipelinesAndAbortedGiChangesDoNotCommitMaterialHistory()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(); var binding = Metal with { Specular = Lobe };
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 31,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
        Render(binding, 1, "FirstFrame"); Render(binding, 2, "None");
        var warm = session.Statistics!; long reads = session.Runtime.Readbacks;
        var changed = binding with { Specular = Lobe with { Color = new(.4f), Scale = .7f, Attenuation = .6f } };
        Render(changed, 1, "ExplicitReset"); Render(changed, 2, "None");
        Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
        Assert.Equal(warm.PipelineCreations, session.Statistics!.PipelineCreations);
        long submissions = session.Runtime.Submissions;
        var future = changed with { Specular = Lobe with { SharpOffset = .1f } };
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, W, H, gi,
            (_, _) => throw new OperationCanceledException("Abort future metallic specular parameters."),
            surfaceStyles: Map(future)));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Render(changed, 3, "None");
        Render(future, 1, "ExplicitReset"); Render(future, 2, "None");
        Assert.Equal(reads, session.Runtime.Readbacks);

        void Render(D3D12GenshinMetallicBinding value, ulong frames, string reason)
        {
            using var output = session.Render(frame, W, H, gi, surfaceStyles: Map(value));
            Assert.Equal(0, output.Statistics.ReadbackCount);
            var stats = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
            Assert.Equal(LightingMode.SoftwareRayTracing, stats.Effective);
            Assert.Equal(frames, stats.TemporalHistoryFrames);
            Assert.Equal(reason, stats.TemporalResetReason);
        }
    }

    private static Vector3 Local(float nh, float blue, D3D12GenshinMetallicSpecularBinding lobe,
        Func<float, Vector3>? ramp = null, float lit = 1)
        => GenshinMetallicSpecularOracle.Local(nh, blue, lobe, lit, ramp);

    private static D3D12SurfaceStyleMap Map(D3D12GenshinMetallicBinding binding, bool rb = false)
        => new([D3D12SurfaceStyle.GenshinPmx], [new(rb?D3D12CharacterMaterialRole.Hair:D3D12CharacterMaterialRole.Body,
            GenshinNonMetallicSpecular: rb ? new(0, 1, 0, .35f) : null, GenshinMetallic: binding)]);

    private static SceneTexture Ramp() => new("asymmetric numeric ramp", 3, 2,
        [16, 180, 70, 0, 210, 80, 160, 17, 100, 20, 220, 50,
         230, 35, 120, 190, 90, 155, 240, 200, 180, 215, 65, 255]);

    private static OriginalFrameScene Frame(byte red = 255, byte blue = 255, byte lightmapAlpha = 0,
        SceneTexture? image = null, Vector3? pmx = null, Vector3? normal = null, Vector3? light = null,
        SceneCamera? camera = null, float? nativeIntensity = null, float alpha = 1, bool straight = false,
        int samples = 1, bool smaa = false)
    {
        Vector3 n = normal ?? -Vector3.UnitZ;
        SceneVertex[] vertices = [new(new(-1, -1, 0), n, new(0, 1)), new(new(1, -1, 0), n, new(1, 1)),
            new(new(1, 1, 0), n, new(1, 0)), new(new(-1, 1, 0), n, new(0, 0))];
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("explicit metallic receiver", 0, 6, new(Diffuse, alpha), pmx ?? Vector3.Zero, Vector3.Zero, 4)],
            [new("shared numerical R gate B amplitude", 1, 1, [red, 128, blue, lightmapAlpha]),
             image ?? new SceneTexture("palette placeholder", 1, 1, [128, 64, 192, 0])],
            camera: camera ?? Camera, lightDirection: light ?? -Vector3.UnitZ);
        return new(new(scene, [new(new(.2f), DoubleSided: true)],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero,
                AlphaOutput: straight ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax,
                SpatialSamples: samples, Antialiasing: smaa ? FrameAntialiasing.Smaa1X : FrameAntialiasing.Off),
            lighting: nativeIntensity is { } intensity ? new([new NativeSceneLight(Guid.NewGuid(), "controlled neutral fill",
                SceneLightKind.Directional, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
                new(.2f), intensity, 30, CastShadows: false)]) : null);
    }

    private static Vector3 WorldAtPixel(SceneSnapshot scene, int x, int y)
    {
        Matrix4x4.Invert(scene.Camera.ViewProjection(W, H), out var inverseVp);
        Vector3 Point(float z)
        {
            var p = Vector4.Transform(new Vector4(2 * (x + .5f) / W - 1, 1 - 2 * (y + .5f) / H, z, 1), inverseVp);
            return new(p.X / p.W, p.Y / p.W, p.Z / p.W);
        }
        var a = Point(0); var direction = Point(1) - a;
        return a - direction * (a.Z / direction.Z);
    }

    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan(((H / 2) * W + W / 2) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame frame, int x, int y, Vector3 expected)
    {
        int at = (y * W + x) * 4;
        Assert.True(frame.Rgba[at + 3] > 0, "Oracle sample must lie inside the receiver.");
        for (int c = 0; c < 3; c++)
            Assert.InRange(Math.Abs(frame.Rgba[at + c] - Math.Clamp(expected[c] * 255, 0, 255)), 0, 1.4);
    }
    private static void AssertCoverage(RenderedFrame before, RenderedFrame after)
    {
        for (int i = 3; i < before.Rgba.Length; i += 4) Assert.Equal(before.Rgba[i], after.Rgba[i]);
    }
}
