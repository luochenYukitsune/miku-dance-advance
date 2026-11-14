using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class GenshinMetallicGpuTests
{
    private const int W = 97, H = 73;
    private static readonly Vector3 C0 = new(.6f, .5f, .4f);
    private static readonly Vector3 Nonmetal = new(.2f, .25f, .3f);
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30);
    private static readonly D3D12GenshinMetallicBinding Binding = new(0, 1,
        D3D12MatcapColorEncoding.LinearRgb, new(.25f, .45f, .35f), new(.85f, .7f, .95f), new(.7f, .5f, .8f),
        LitMode: D3D12GenshinMetallicLitMode.Uniform, UniformLitFactor: 1.15f);

    [Fact, Trait("Category", "Gpu")]
    public void StrictRedThresholdAndDisabledBindingRecoverExactFrameWhileFractionalGateUsesBothMixes()
    {
        using var session = new D3D12FrameSession();
        foreach (byte red in new byte[] { 229, 230, 255 })
        {
            // UNorm8 brackets the float32 threshold; exact .9/either ULP is CPU-oracle coverage.
            // Fix the primary band to its dark endpoint; a perpendicular light lies
            // inside the accepted soft transition and is not the Nonmetal endpoint.
            var frame = Frame(red: red, light: Vector3.UnitZ);
            var off = session.Capture(frame, W, H, surfaceStyles: Map(null));
            Assert.Equal(off.Rgba, session.Capture(frame, W, H, surfaceStyles: Map(Binding with { EnableMetallics = 0 })).Rgba);
            foreach (float enable in new[] { -1f, .35f, 1f, 2f })
            {
                var binding = Binding with { EnableMetallics = enable };
                var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
                if (red == 229 || enable < 0) Assert.Equal(off.Rgba, actual.Rgba);
                else
                {
                    Vector3 expected = C0 * GenshinMetallicOracle.Palette(Nonmetal,
                        Tint(frame.Surface.Snapshot, binding, W / 2, H / 2), enable);
                    AssertPixel(actual, W / 2, H / 2, expected);
                    Assert.NotEqual(Pixel(off), Pixel(actual));
                }
                AssertCoverage(off, actual);
            }
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void PerspectiveRollXOnlyTilingAndReflectedNormalUseIndependentPerPixelCameraOracle()
    {
        using var session = new D3D12FrameSession();
        int witnesses = 0;
        foreach (bool perspective in new[] { false, true })
        foreach (float sx in new[] { 1.1f, -1.1f })
        foreach (float tile in new[] { -3.8f, 4.1f })
        foreach (var translation in new[] { new Vector3(.3f, .2f, 0), new Vector3(-.35f, -.15f, 0) })
        {
            var world = Matrix4x4.CreateScale(sx, .85f, 1.4f) * Matrix4x4.CreateRotationY(.23f);
            // Translate Eye and Target together: rotation/roll stays fixed while perspective Incoming changes.
            // The larger positive/negative X tiling also crosses Repeat seams with the asymmetric 4x3 image.
            var camera = Camera with { Eye = Camera.Eye + translation, Target = translation,
                Up = new(.45f, 1, .12f), VerticalFovDegrees = perspective ? 32 : null };
            var frame = Frame(world: world, camera: camera, normal: Vector3.Normalize(new(.5f, .35f, -.8f)),
                mirrorModelUv: sx < 0);
            var binding = Binding with { TileScale = tile };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
            foreach (int y in new[] { 29, 43 }) foreach (int x in new[] { 41, 55 })
            {
                var expected = C0 * Tint(frame.Surface.Snapshot, binding, x, y);
                AssertPixel(actual, x, y, expected);
                witnesses++;
            }
        }
        Assert.Equal(64, witnesses);
    }

    [Fact, Trait("Category", "Gpu")]
    public void PrimaryResponseUsesTheCurrentNormalBandRatherThanDormantUniformInput()
    {
        using var session = new D3D12FrameSession();
        foreach (float lit in new[] { 0f, 1f })
        {
            var frame = Frame(light: lit == 0 ? Vector3.UnitZ : -Vector3.UnitZ);
            var binding = Binding with { LitMode = D3D12GenshinMetallicLitMode.PrimaryResponse,
                UniformLitFactor = -3, DarkColor = new(.8f), LightColor = new(.8f), ShadowMultiplyColor = new(.2f) };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
            var expected = C0 * GenshinMetallicOracle.LocalTint(Vector3.Zero, 1,
                binding.DarkColor, binding.LightColor, binding.ShadowMultiplyColor, lit);
            AssertPixel(actual, 48, 36, expected);
            Assert.Equal(actual.Rgba, session.Capture(frame, W, H,
                surfaceStyles: Map(binding with { UniformLitFactor = 4 })).Rgba);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void NonendpointSrgbCubicFilteringUsesAllRgbChannelsAndIgnoresChannelPackedAlpha()
    {
        using var session = new D3D12FrameSession();
        foreach (var encoding in new[] { D3D12MatcapColorEncoding.LinearRgb, D3D12MatcapColorEncoding.SrgbRgb })
        {
            var frame = Frame(normal: Vector3.Normalize(new(.6f, -.3f, -.7f)));
            var binding = Binding with { MatcapEncoding = encoding, Brightness = 1.25f, TileScale = 1.7f };
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
            AssertPixel(actual, 48, 36, C0 * Tint(frame.Surface.Snapshot, binding, 48, 36));
            var changedAlpha = MetalImage();
            var pixels = changedAlpha.Rgba.ToArray();
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = (byte)(255 - pixels[i]);
            var ignored = Frame(normal: Vector3.Normalize(new(.6f, -.3f, -.7f)),
                image: new("same RGB opposite alpha", 4, 3, pixels), lightmapAlpha: 255);
            Assert.Equal(actual.Rgba, session.Capture(ignored, W, H, surfaceStyles: Map(binding)).Rgba);
        }
        // Alternating encoded endpoints produce .5 linear at this exact midpoint.
        // Decode-after-filter would produce ~.214 and is separated by tens of output bytes.
        var endpoints = Frame(image: new("encoded black white", 2, 1, [0, 0, 0, 0, 255, 255, 255, 255]));
        var midpoint = Binding with { MatcapEncoding = D3D12MatcapColorEncoding.SrgbRgb,
            DarkColor = Vector3.Zero, LightColor = Vector3.One, ShadowMultiplyColor = Vector3.One };
        AssertPixel(session.Capture(endpoints, W, H, surfaceStyles: Map(midpoint)), 48, 36, C0 * .5f);
    }

    [Fact, Trait("Category", "Gpu")]
    public void SameMaterialAtlasAndRbUseOneMetalPaletteAndAttenuateOnlyTheNonmetallicLobe()
    {
        using var session = new D3D12FrameSession();
        var atlas = GenshinHairAtlasSurfaceTests.Binding with { ShadowPosition = 1.1f };
        var rb = new D3D12GenshinNonMetallicSpecularBinding(0, 1, 0, .1f);
        var specular = new Vector3(.4f, .3f, .2f);
        OriginalFrameScene Fixture(Vector3 spec)
        {
            var source = GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas(), specular: spec, light: -Vector3.UnitZ);
            var old = source.Surface.Snapshot;
            var scene = new SceneSnapshot(old.Vertices, old.Indices, old.Materials,
                [new("shared numerical R G B", 1, 1, [255, 128, 255, 0]), old.Textures[1], old.Textures[2], MetalImage()],
                old.World, old.Camera, old.LightDirection);
            return new(new(scene, source.Surface.Materials, source.Surface.Profile), options: source.Options);
        }
        D3D12SurfaceStyleMap Styles(D3D12GenshinMetallicBinding? metal) => new([D3D12SurfaceStyle.GenshinPmx],
            [new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: rb, GenshinHairShadowAtlas: atlas, GenshinMetallic: metal)]);
        var frame = Fixture(specular); var noSpec = Fixture(Vector3.Zero);
        var off = session.Capture(frame, W, H, surfaceStyles: Styles(null));
        Assert.Equal(off.Rgba, session.Capture(frame, W, H,
            surfaceStyles: Styles(Binding with { MatcapCombinedTextureIndex = 3, EnableMetallics = 0 })).Rgba);
        var coord = GenshinHairAtlasOracle.Coordinates(1, 128 / 255f, atlas.ExplicitRowId, atlas.ShadowPosition,
            atlas.ShadowRampWidth, atlas.VertexWidth, atlas.VertexAo, atlas.UseLightmapAo, atlas.UseVertexColorAo);
        var bytes = GenshinHairAtlasSurfaceTests.Atlas();
        var palette = GenshinHairAtlasOracle.Palette(
            GenshinHairAtlasOracle.ClosestExtend(bytes, 4, 10, coord.Q, coord.DayY, false),
            GenshinHairAtlasOracle.ClosestExtend(bytes, 4, 10, coord.Q, coord.NightY, false), atlas.DayNight, coord.Gate);
        foreach (float enable in new[] { .4f, 1f })
        {
            var binding = Binding with { MatcapCombinedTextureIndex = 3, EnableMetallics = enable };
            var actual = session.Capture(frame, W, H, surfaceStyles: Styles(binding));
            var diffuse = session.Capture(noSpec, W, H, surfaceStyles: Styles(binding));
            var expected = C0 * GenshinMetallicOracle.Palette(palette, Tint(frame.Surface.Snapshot, binding, 48, 36), enable);
            AssertPixel(diffuse, 48, 36, expected);
            AssertPixel(actual, 48, 36, expected + specular * (.1f * (1 - enable)));
            if (enable == 1) Assert.Equal(diffuse.Rgba, actual.Rgba);
            AssertCoverage(off, actual);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void NativeAndGiPixelEntriesComposePrimaryMetalOnceAndSpendOnlyRemainingPalette()
    {
        using var session = new D3D12FrameSession();
        // Constant source palette isolates endpoint composition from camera sampling.
        var binding = Binding with { DarkColor = new(.65f), LightColor = new(.65f), ShadowMultiplyColor = Vector3.One };
        float palette = GenshinMetallicOracle.Gamma(.65f);
        foreach (bool native in new[] { false, true }) foreach (bool gi in new[] { false, true })
        {
            var frame = Frame(nativeIntensity: native ? .25f : null, light: -Vector3.UnitZ);
            var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 13,
                Composition: RayGiComposition.ToonPalette) : null;
            var actual = session.Capture(frame, W, H, options, surfaceStyles: Map(binding));
            // Native light has RGB=.2, intensity=.25 and a fully lit normal.
            float expected = palette + (native ? (1 - palette) * .05f : 0);
            AssertPixel(actual, 48, 36, C0 * expected);
            Assert.Equal(255, actual.Rgba[(36 * W + 48) * 4 + 3]);
        }
        var strong = Frame(nativeIntensity: 20, light: -Vector3.UnitZ);
        AssertPixel(session.Capture(strong, W, H, surfaceStyles: Map(binding)), 48, 36, C0);
    }

    [Fact, Trait("Category", "Gpu")]
    public void ActualNeutralBounceCanFillMetalContrastEvenWhenPrimaryNormalBandIsFullyLit()
    {
        using var session = new D3D12FrameSession();
        var old = D3D12RayGiTests.Bounce(); var source = old.Surface.Snapshot;
        var materials = source.Materials.ToArray(); materials[0] = materials[0] with { Diffuse = new(.6f, .6f, .6f, 1) };
        var scene = new SceneSnapshot(source.Vertices, source.Indices, materials, [Lightmap(), MetalImage()],
            source.World, source.Camera, -Vector3.UnitZ);
        var flags = old.Materials.ToArray(); flags[0] = flags[0] with { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands };
        var frame = new OriginalFrameScene(new(scene, [new(new(.2f)), old.Surface.Materials[1]],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero), materials: flags);
        var binding = Binding with { DarkColor = new(.6f), LightColor = new(.6f), ShadowMultiplyColor = Vector3.One };
        var styles = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd], [new(GenshinMetallic: binding), new()]);
        var options = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 20, SamplesPerPixel: 32, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectionalLightDirection: Vector3.UnitZ, DirectionalIrradiance: new(3),
            Composition: RayGiComposition.ToonPalette);
        var before = session.Capture(frame, W, H, surfaceStyles: styles);
        var after = session.Capture(frame, W, H, options, surfaceStyles: styles);
        float palette = GenshinMetallicOracle.Gamma(.6f), budget = .6f * (1 - palette) * .25f * 255;
        AssertPixel(before, 48, 36, new(.6f * palette));
        var a = Pixel(after); var b = Pixel(before);
        for (int c = 0; c < 3; c++) Assert.InRange(a[c] - b[c], 0, budget + 1.1f);
        Assert.True(a[0] > b[0], "Fixture must contain actual traced fill, including the c==1 metallic path.");
        Assert.InRange(Math.Abs((a[0] - b[0]) - (a[1] - b[1])), 0, 1);
        Assert.InRange(Math.Abs((a[1] - b[1]) - (a[2] - b[2])), 0, 1);
        AssertCoverage(before, after);
    }

    [Fact, Trait("Category", "Gpu")]
    public void StraightAlphaSsaaSmaaAndFinalDiffusionPreserveCoverageAndMatchIndependentFilter()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(alpha: .43f, straight: true, samples: 2, smaa: true,
            normal: Vector3.Normalize(new(.5f, .3f, -.8f)));
        var off = session.Capture(frame, W, H, surfaceStyles: Map(null));
        var before = session.Capture(frame, W, H, surfaceStyles: Map(Binding));
        var after = session.Capture(frame, W, H, surfaceStyles: Map(Binding), diffusion: new(true, .7f, .01f));
        AssertCoverage(off, before); AssertCoverage(before, after);
        var expected = TransparentDiffusionReferenceImage.Apply(before.Rgba, W, H, .7, .01);
        for (int i = 0; i < expected.Length; i++) if ((i & 3) != 3)
            Assert.InRange(Math.Abs(expected[i] - after.Rgba[i]), 0, 2);
        Assert.Equal(new[] { "diffusion:x", "diffusion:y", "alpha:straight" }, session.LastRecordings.TakeLast(3).Select(r => r.Channel));
        Assert.Equal(before.Rgba, session.Capture(frame, W, H, surfaceStyles: Map(Binding)).Rgba);
        Assert.Equal(off.Rgba, session.Capture(frame, W, H, surfaceStyles: Map(null)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void WarmAuthoredParametersDoNotGrowPipelinesOrReadBackAndRemovalRestoresExactOutput()
    {
        using var session = new D3D12FrameSession(); var frame = Frame();
        var original = session.Capture(frame, W, H, surfaceStyles: Map(null));
        using (var image = session.Render(frame, W, H, surfaceStyles: Map(Binding))) { }
        var warm = session.Statistics!; long reads = session.Runtime.Readbacks;
        for (int i = 0; i < 8; i++)
        {
            using var image = session.Render(frame, W, H, surfaceStyles: Map(Binding with
            { TileScale = .25f + i * .2f, Brightness = .4f + i * .1f, UniformLitFactor = .6f + i * .1f }));
            Assert.Equal(0, image.Statistics.ReadbackCount);
            Assert.Equal(warm.ShaderCompilations, image.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, image.Statistics.PipelineCreations);
            Assert.InRange(image.Statistics.CachedResources, 0, warm.CachedResources + 1);
        }
        Assert.Equal(reads, session.Runtime.Readbacks);
        Assert.Equal(original.Rgba, session.Capture(frame, W, H, surfaceStyles: Map(null)).Rgba);
    }

    private static D3D12SurfaceStyleMap Map(D3D12GenshinMetallicBinding? binding)
        => new([D3D12SurfaceStyle.GenshinPmx], [new(GenshinMetallic: binding)]);
    private static SceneTexture Lightmap(byte red = 255, byte alpha = 0) => new("numeric shared R gate", 1, 1, [red, 128, 255, alpha]);
    private static SceneTexture MetalImage() => new("asymmetric channel-packed color", 4, 3,
        [16, 180, 70, 0, 210, 80, 160, 17, 100, 20, 220, 50, 240, 210, 30, 120,
         130, 50, 200, 220, 45, 200, 90, 255, 190, 120, 50, 0, 75, 230, 165, 10,
         230, 35, 120, 190, 90, 155, 240, 200, 180, 215, 65, 30, 35, 85, 190, 254]);

    private static OriginalFrameScene Frame(byte red = 255, SceneTexture? image = null, byte lightmapAlpha = 0,
        Matrix4x4? world = null, SceneCamera? camera = null, Vector3? normal = null, bool mirrorModelUv = false,
        Vector3? light = null, float? nativeIntensity = null, float alpha = 1, bool straight = false, int samples = 1, bool smaa = false)
    {
        Vector3 n = normal ?? -Vector3.UnitZ;
        SceneVertex[] vertices = [new(new(-.8f, -.8f, 0), n, new(0, 1)), new(new(.8f, -.8f, 0), n, new(1, 1)),
            new(new(.8f, .8f, 0), n, new(1, 0)), new(new(-.8f, .8f, 0), n, new(0, 0))];
        if (mirrorModelUv) vertices = vertices.Select(v => v with { Uv = new(1 - v.Uv.X, v.Uv.Y) }).ToArray();
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("explicit metallic receiver", 0, 6, new(C0, alpha), Vector3.Zero, Vector3.Zero, 4)],
            [Lightmap(red, lightmapAlpha), image ?? MetalImage()], world, camera ?? Camera, light ?? Vector3.UnitX);
        return new(new(scene, [new(Nonmetal, DoubleSided: true)], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero,
                AlphaOutput: straight ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax,
                SpatialSamples: samples, Antialiasing: smaa ? FrameAntialiasing.Smaa1X : FrameAntialiasing.Off),
            lighting: nativeIntensity is { } intensity ? new([new NativeSceneLight(Guid.NewGuid(), "controlled neutral fill",
                SceneLightKind.Directional, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
                new(.2f), intensity, 30, CastShadows: false)]) : null);
    }

    private static Vector3 Tint(SceneSnapshot scene, D3D12GenshinMetallicBinding binding, int x, int y)
    {
        Matrix4x4.Invert(scene.World, out var inverse);
        Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(scene.Vertices[0].Normal, Matrix4x4.Transpose(inverse)));
        var view = Matrix4x4.CreateLookAt(scene.Camera.Eye, scene.Camera.Target, scene.Camera.Up);
        var uv = GenshinMetallicOracle.Coordinates(normal, WorldAtPixel(scene, x, y), scene.Camera.Eye,
            new(view.M12, view.M22, view.M32), Vector3.Normalize(scene.Camera.Eye - scene.Camera.Target),
            scene.Camera.VerticalFovDegrees is null, binding.TileScale);
        var image = scene.Textures[binding.MatcapCombinedTextureIndex];
        var sampled = GenshinMetallicOracle.CubicRepeat(image.Rgba, image.Width, image.Height, uv,
            binding.MatcapEncoding == D3D12MatcapColorEncoding.SrgbRgb);
        return GenshinMetallicOracle.LocalTint(sampled, binding.Brightness, binding.DarkColor, binding.LightColor,
            binding.ShadowMultiplyColor, binding.UniformLitFactor);
    }

    private static Vector3 WorldAtPixel(SceneSnapshot scene, int x, int y)
    {
        Matrix4x4.Invert(scene.Camera.ViewProjection(W, H), out var inverseVp);
        Matrix4x4.Invert(scene.World, out var inverseWorld);
        Vector3 Point(float z)
        {
            var p = Vector4.Transform(new Vector4(2 * (x + .5f) / W - 1, 1 - 2 * (y + .5f) / H, z, 1), inverseVp);
            return Vector3.Transform(new(p.X / p.W, p.Y / p.W, p.Z / p.W), inverseWorld);
        }
        var a = Point(0); var direction = Point(1) - a;
        return Vector3.Transform(a - direction * (a.Z / direction.Z), scene.World);
    }

    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan(((H / 2) * W + W / 2) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame frame, int x, int y, Vector3 expected, double tolerance = 1.4)
    {
        int at = (y * W + x) * 4;
        Assert.True(frame.Rgba[at + 3] > 0, "Oracle sample must lie inside the receiver.");
        for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(frame.Rgba[at + c] - Math.Clamp(expected[c] * 255, 0, 255)), 0, tolerance);
    }
    private static void AssertCoverage(RenderedFrame before, RenderedFrame after)
    {
        for (int i = 3; i < before.Rgba.Length; i += 4) Assert.Equal(before.Rgba[i], after.Rgba[i]);
    }
}
