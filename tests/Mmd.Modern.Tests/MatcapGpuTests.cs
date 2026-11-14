using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class MatcapGpuTests
{
    private const int W = 97, H = 97;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);
    private static readonly Vector3 Ni = new(0, 0, 1.7f);
    private static readonly D3D12EndfieldMatcapBinding Binding = new(0, 1, 2,
        D3D12MatcapColorEncoding.LinearRgb, 0, new(.085f, .07f, .06f), new(.2f, .45f, .12f),
        NormalStrength: 1.4f, HighlightCoefficient: .08f);

    [Fact, Trait("Category", "Gpu")]
    public void NonwhiteHighlightProducesVisibleDeltaMatchingIndependentPaletteOracle()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame();
        var baseline = session.Capture(frame, W, H, surfaceStyles: Map(null));
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(Binding));
        var delta = Expected(frame.Surface.Snapshot, Binding, W / 2, H / 2);
        Assert.True(delta.Length() * 255 > 6, "Fixture must visibly exercise MatCap rather than white-highlight identity.");
        foreach (int y in new[] { 40, 48, 56 }) foreach (int x in new[] { 40, 48, 56 })
            AssertPixel(baseline, actual, x, y, Expected(frame.Surface.Snapshot, Binding, x, y));
        AssertCoverage(baseline, actual);
    }

    [Fact, Trait("Category", "Gpu")]
    public void NormalRgAndPRedAlphaRemainNumericalAndUseBilinearRepeat()
    {
        using var session = new D3D12FrameSession();
        SceneTexture[] textures = [
            new("numerical N", 2, 1, [150, 100, 0, 0, 210, 150, 255, 255]),
            new("numerical P", 2, 1, [13, 255, 255, 10, 48, 0, 0, 24]),
            ColorTexture()];
        var frame = Frame(textures: textures);
        var binding = Binding with { NormalStrength = 1.8f, RotationRadians = .45f };
        var baseline = session.Capture(frame, W, H, surfaceStyles: Map(null));
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
        foreach (int x in new[] { 32, 48, 64 })
            AssertPixel(baseline, actual, x, 44, Expected(frame.Surface.Snapshot, binding, x, 44));
        // B/A in N and G/B in P have no meaning in this source contract.
        var ignored = Frame(textures: [
            new("same N.rg", 2, 1, [150, 100, 255, 255, 210, 150, 0, 0]),
            new("same P.ra", 2, 1, [13, 0, 0, 10, 48, 255, 255, 24]), ColorTexture()]);
        Assert.Equal(actual.Rgba, session.Capture(ignored, W, H, surfaceStyles: Map(binding)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void SrgbColorIsDecodedPerTexelBeforeFilteringAndAlphaRemainsLinear()
    {
        using var session = new D3D12FrameSession();
        var source = Frame(textures: [NormalTexture(), PTexture(),
            new("sRGB nonendpoint colors", 2, 1, [32, 72, 128, 51, 224, 180, 192, 204])]);
        var brighter=source.Surface.Snapshot.WithMaterials([source.Surface.Snapshot.Materials[0] with { Diffuse=new(.8f,.7f,.6f,1) }]);
        var frame=new OriginalFrameScene(new(brighter,[source.Surface.Materials[0] with { ToonShadowColor=Vector3.One }],source.Surface.Profile),options:source.Options);
        var binding = Binding with { MatcapEncoding = D3D12MatcapColorEncoding.SrgbRgb,
            NormalStrength = 0, HighlightCoefficient = 1, BaseColor = new(.4f), HighlightColor = new(.1f) };
        var baseline = session.Capture(frame, W, H, surfaceStyles: Map(null));
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
        var expected = Expected(frame.Surface.Snapshot, binding, 48, 48);
        AssertPixel(baseline, actual, 48, 48, expected);
        for(int channel=0;channel<3;channel++)
            Assert.InRange(baseline.Rgba[(48*W+48)*4+channel]+255*expected[channel],1,254);
        // Non-endpoint texels detect BOTH a missing sRGB decode and decode-after-filter.
        var encodedMid=new Vector3(256,252,320)/510;
        var wrongImage = new Vector4(MatcapOracle.Srgb(encodedMid.X), MatcapOracle.Srgb(encodedMid.Y), MatcapOracle.Srgb(encodedMid.Z), .5f);
        var wrongDelta = MatcapOracle.Delta(binding.BaseColor,
            MatcapOracle.MaterialColor(binding.BaseColor, binding.HighlightColor, wrongImage, 1), 1);
        Assert.True(Vector3.Distance(expected, wrongDelta) * 255 > 6,
            "Fixture must reject decoding an already-filtered sRGB value.");
        var missingDecode=MatcapOracle.Delta(binding.BaseColor,
            MatcapOracle.MaterialColor(binding.BaseColor,binding.HighlightColor,new(encodedMid,.5f),1),1);
        Assert.True(Vector3.Distance(expected,missingDecode)*255>4,
            "Fixture must also reject a numerical UNorm resource used in place of authored sRGB.");
        AssertCoverage(baseline, actual);
    }

    [Fact, Trait("Category", "Gpu")]
    public void NonuniformReflectedWorldCameraRollAndMirroredUv1MatchAnalyticTangentFrame()
    {
        using var session = new D3D12FrameSession();
        foreach (float sx in new[] { 1.15f, -1.15f })
        foreach (bool mirroredUv1 in new[] { false, true })
        {
            var world = Matrix4x4.CreateScale(sx, .85f, .6f) * Matrix4x4.CreateRotationY(.24f);
            var camera = Camera with { Eye = new(.3f, .15f, -5), Up = new(.4f, 1, .1f) };
            var frame = Frame(world: world, camera: camera, mirroredUv1: mirroredUv1);
            Matrix4x4.Invert(world, out var inverse);
            Vector3 control = Vector3.TransformNormal(Ni, Matrix4x4.Transpose(inverse));
            if (!Front(frame.Surface.Snapshot, 0)) control = -control;
            var binding = Binding with { UvChannel = mirroredUv1 ? 1 : 0, NormalStrength = 2,
                RotationRadians = .71f, LightControlDirection = control, HighlightCoefficient = .1f };
            var baseline = session.Capture(frame, W, H, surfaceStyles: Map(null));
            var actual = session.Capture(frame, W, H, surfaceStyles: Map(binding));
            foreach (int y in new[] { 42, 54 }) foreach (int x in new[] { 42, 54 })
                AssertPixel(baseline, actual, x, y, Expected(frame.Surface.Snapshot, binding, x, y));
            AssertCoverage(baseline, actual);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void StraightCoverageSurvivesImageAlphaAndRemovingOrZeroingDeltaRestoresExactFrame()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(alpha: .43f, straight: true);
        var original = session.Capture(frame, W, H, surfaceStyles: Map(null));
        var actual = session.Capture(frame, W, H, surfaceStyles: Map(Binding));
        AssertCoverage(original, actual);
        Assert.InRange(actual.Rgba[(48 * W + 48) * 4 + 3], 108, 111);
        AssertPixel(original, actual, 48, 48, Expected(frame.Surface.Snapshot, Binding, 48, 48), 2.1);
        Assert.Equal(original.Rgba, session.Capture(frame, W, H,
            surfaceStyles: Map(Binding with { BaselineStrength = 0 })).Rgba);
        Assert.Equal(original.Rgba, session.Capture(frame, W, H, surfaceStyles: Map(null)).Rgba);
        var transparentImage = Frame(alpha: .43f, straight: true,
            textures: [NormalTexture(), PTexture(), new("zero image alpha", 1, 1, [255, 255, 255, 0])]);
        var zeroImage = session.Capture(transparentImage, W, H, surfaceStyles: Map(Binding));
        AssertCoverage(original, zeroImage);
        AssertPixel(original, zeroImage, 48, 48, Expected(transparentImage.Surface.Snapshot, Binding, 48, 48), 2.1);
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void DeltaIsComposedOnceAfterActualNativeAndGiEntries(bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(native: native, alpha: .61f, straight: true);
        var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4,
            Seed: 13, Composition: RayGiComposition.ToonPalette) : null;
        var baseline = session.Capture(frame, W, H, options, surfaceStyles: Map(null));
        var actual = session.Capture(frame, W, H, options, surfaceStyles: Map(Binding));
        AssertPixel(baseline, actual, 48, 48, Expected(frame.Surface.Snapshot, Binding, 48, 48), 2.1);
        AssertCoverage(baseline, actual);
    }

    [Fact, Trait("Category", "Gpu")]
    public void MixedMaterialsUseDrawLocalPrimitiveCornersAfterNonzeroStartIndex()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(split: true);
        D3D12SurfaceStyleMap Styles(bool enabled) => new(
            [D3D12SurfaceStyle.EndfieldPmx, D3D12SurfaceStyle.EndfieldPmx],
            [new(), new(EndfieldMatcap: enabled ? Binding : null)]);
        var baseline = session.Capture(frame, W, H, surfaceStyles: Styles(false));
        var actual = session.Capture(frame, W, H, surfaceStyles: Styles(true));
        Assert.Equal(6, frame.Surface.Snapshot.Materials[1].StartIndex);
        var first = Pixel(frame.Surface.Snapshot, new(-.45f, 0, 0));
        var second = Pixel(frame.Surface.Snapshot, new(.45f, 0, 0));
        foreach (int dy in new[] { -6, 6 }) foreach (int dx in new[] { -6, 6 })
        {
            AssertPixel(baseline, actual, first.X + dx, first.Y + dy, Vector3.Zero, 0);
            AssertPixel(baseline, actual, second.X + dx, second.Y + dy,
                Expected(frame.Surface.Snapshot, Binding, second.X + dx, second.Y + dy, material: 1));
        }
        AssertCoverage(baseline, actual);
    }

    private static D3D12SurfaceStyleMap Map(D3D12EndfieldMatcapBinding? binding)
        => new([D3D12SurfaceStyle.EndfieldPmx], [new(EndfieldMatcap: binding)]);
    private static SceneTexture NormalTexture() => new("numerical normal", 1, 1, [179, 153, 13, 0]);
    private static SceneTexture PTexture() => new("numerical gate", 1, 1, [40, 255, 255, 24]);
    private static SceneTexture ColorTexture() => new("authored color", 2, 2,
        [40, 180, 70, 180, 210, 80, 160, 220, 100, 20, 220, 130, 240, 210, 30, 200]);

    private static OriginalFrameScene Frame(SceneTexture[]? textures = null, Matrix4x4? world = null,
        SceneCamera? camera = null, bool mirroredUv1 = false, float alpha = 1,
        bool straight = false, bool native = false, bool split = false)
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>(); var materials = new List<SceneMaterial>();
        foreach (float center in split ? new[] { -.45f, .45f } : new[] { 0f })
        {
            float half = split ? .34f : .8f; uint start = (uint)vertices.Count;
            foreach (var p in new[] { new Vector2(-half, -half), new(half, -half), new(half, half), new(-half, half) })
                vertices.Add(new(new(center + p.X, p.Y, 0), Ni, new((p.X / half + 1) * .5f, (p.Y / half + 1) * .5f)));
            int index = indices.Count;
            indices.AddRange([start, start + 2, start + 1, start, start + 3, start + 2]);
            materials.Add(new("explicit receiver", index, 6, new(.2f, .18f, .16f, alpha), Vector3.Zero, new(.02f), 4));
        }
        SceneAdditionalUvs? additional = mirroredUv1 ? new(1, vertices.Select(v =>
            new SceneAdditionalUvVertex(new(v.Uv.X, 1 - v.Uv.Y, 0, 0), default, default, default)).ToArray()) : null;
        var scene = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials,
            textures ?? [NormalTexture(), PTexture(), ColorTexture()], world: world, camera: camera ?? Camera,
            lightDirection: -Vector3.UnitZ, additionalUvs: additional);
        return new(new(scene, materials.Select(_ => new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true)).ToArray(),
                new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero,
                AlphaOutput: straight ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax),
            lighting: native ? new([new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Directional, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Vector3.One, 2, 30, CastShadows: false)]) : null);
    }

    // Analytic planar frame: dP/du=+X. Clockwise source triangles have negative
    // Mikk UV orientation; UV1 reverses V and therefore reverses that corner sign.
    // Never read the product tangent buffer or binding-derived constants for the oracle.
    private static Vector3 Expected(SceneSnapshot scene, D3D12EndfieldMatcapBinding b, int x, int y, int material = 0)
    {
        Matrix4x4.Invert(scene.World, out var inverse); var normalWorld = Matrix4x4.Transpose(inverse);
        Vector3 ni = Vector3.TransformNormal(Ni, normalWorld);
        Vector3 tangent = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, normalWorld));
        Vector2 uv = UvAt(scene, x, y, material);
        if (b.UvChannel == 1) uv.Y = 1 - uv.Y;
        var normal = scene.Textures[b.NormalCombinedTextureIndex]; var p = scene.Textures[b.PCombinedTextureIndex];
        var color = scene.Textures[b.MatcapCombinedTextureIndex];
        Vector4 ns = MatcapOracle.BilinearRepeat(normal.Rgba, normal.Width, normal.Height, uv, false);
        Vector4 ps = MatcapOracle.BilinearRepeat(p.Rgba, p.Width, p.Height, uv, false);
        Vector3 mapped = MatcapOracle.PackedNormal(new(ns.X, ns.Y), ni,
            new(tangent, b.UvChannel == 1 ? 1 : -1), scene.World.GetDeterminant() < 0 ? -1 : 1,
            Front(scene, material), b.NormalStrength);
        var view = Matrix4x4.CreateLookAt(scene.Camera.Eye, scene.Camera.Target, scene.Camera.Up);
        Vector2 colorUv = MatcapOracle.Coordinates(mapped, new(view.M11, view.M21, view.M31),
            new(view.M12, view.M22, view.M32), new(view.M13, view.M23, view.M33), b.RotationRadians);
        Vector4 image = MatcapOracle.BilinearRepeat(color.Rgba, color.Width, color.Height, colorUv,
            b.MatcapEncoding == D3D12MatcapColorEncoding.SrgbRgb);
        float gate = MatcapOracle.SourceGate(mapped, b.LightControlDirection ?? Vector3.UnitZ,
            b.ShadowHardness, ps.X, ps.W, b.HighlightCoefficient).Gate;
        return MatcapOracle.Delta(b.BaseColor, MatcapOracle.MaterialColor(b.BaseColor, b.HighlightColor, image, gate), b.BaselineStrength);
    }

    private static Vector2 UvAt(SceneSnapshot scene, int x, int y, int material)
    {
        Matrix4x4.Invert(scene.Camera.ViewProjection(W, H), out var invVp);
        Matrix4x4.Invert(scene.World, out var invWorld);
        Vector3 Point(float z)
        {
            var p = Vector4.Transform(new Vector4(2 * (x + .5f) / W - 1, 1 - 2 * (y + .5f) / H, z, 1), invVp);
            return Vector3.Transform(new(p.X / p.W, p.Y / p.W, p.Z / p.W), invWorld);
        }
        Vector3 a = Point(0), d = Point(1) - a, local = a - d * (a.Z / d.Z);
        var range = scene.Materials[material]; var vertices = scene.Vertices; var indices = scene.Indices;
        Vector3 min = vertices[(int)indices[range.StartIndex]].Position;
        Vector3 max = vertices[(int)indices[range.StartIndex + 1]].Position;
        return new((local.X - min.X) / (max.X - min.X), (local.Y - min.Y) / (max.Y - min.Y));
    }

    private static bool Front(SceneSnapshot scene, int material)
    {
        var vp = scene.World * scene.Camera.ViewProjection(W, H); int start = scene.Materials[material].StartIndex;
        Vector2 Project(int i)
        {
            var p = Vector4.Transform(new Vector4(scene.Vertices[(int)scene.Indices[start + i]].Position, 1), vp);
            return new(p.X / p.W, -p.Y / p.W);
        }
        Vector2 a = Project(0), b = Project(1), c = Project(2);
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0;
    }

    private static (int X, int Y) Pixel(SceneSnapshot scene, Vector3 local)
    {
        var p = Vector4.Transform(new Vector4(local, 1), scene.World * scene.Camera.ViewProjection(W, H));
        return ((int)((p.X / p.W + 1) * W / 2), (int)((1 - p.Y / p.W) * H / 2));
    }
    private static void AssertPixel(RenderedFrame before, RenderedFrame after, int x, int y, Vector3 delta, double tolerance = 1.6)
    {
        int at = (y * W + x) * 4;
        Assert.True(before.Rgba[at + 3] > 0, "Oracle sample must lie inside the receiver.");
        for (int c = 0; c < 3; ++c)
            Assert.InRange(Math.Abs(after.Rgba[at + c] - Math.Clamp(before.Rgba[at + c] + 255 * delta[c], 0, 255)), 0, tolerance);
    }
    private static void AssertCoverage(RenderedFrame before, RenderedFrame after)
    {
        for (int i = 3; i < before.Rgba.Length; i += 4) Assert.Equal(before.Rgba[i], after.Rgba[i]);
    }
}
