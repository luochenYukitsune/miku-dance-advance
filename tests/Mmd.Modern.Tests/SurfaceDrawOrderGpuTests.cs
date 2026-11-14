using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class SurfaceDrawOrderGpuTests
{
    private const int Size = 65;

    [Theory]
    [InlineData("material")]
    [InlineData("diffuse")]
    [InlineData("sphere-add")]
    [InlineData("sphere-multiply")]
    [InlineData("sub-texture")]
    public void AutomaticOrderRetainsTheOpaqueBackgroundThroughTransparentSurface(string coverage)
    {
        using var session = new D3D12FrameSession();
        var source = Fixture(coverage, false, SurfaceDrawOrder.Original);
        var old = session.Capture(source, Size, Size);
        var automatic = session.Capture(Mode(source, SurfaceDrawOrder.OpaqueFirst), Size, Size);
        var reference = session.Capture(Fixture(coverage, true, SurfaceDrawOrder.Original), Size, Size);
        Assert.Equal(reference.Rgba, automatic.Rgba);
        Assert.NotEqual(old.Rgba, automatic.Rgba);
        int center = ((Size / 2) * Size + Size / 2) * 4;
        Assert.InRange((int)automatic.Rgba[center], 0, 1);
        Assert.InRange((int)automatic.Rgba[center + 1], 0, 1);
        Assert.InRange((int)automatic.Rgba[center + 2], 126, 129);
        Assert.Equal(255, automatic.Rgba[center + 3]);
        Assert.Equal(old.Rgba, session.Capture(source, Size, Size).Rgba);
    }

    [Fact]
    public void OriginalModeAndOpaqueOnlyDrawsRemainByteIdenticalAcrossModeChanges()
    {
        using var session = new D3D12FrameSession();
        var source = Fixture("opaque", false, SurfaceDrawOrder.Original);
        var old = session.Capture(source, Size, Size);
        Assert.Equal(old.Rgba, session.Capture(Mode(source, SurfaceDrawOrder.OpaqueFirst), Size, Size).Rgba);
        Assert.Equal(old.Rgba, session.Capture(source, Size, Size).Rgba);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void GiAndSpatialAntialiasingKeepTheSameBackgroundAsAnAuthoredBackFirstReference(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        if (mode == LightingMode.HardwareRayTracing)
            Assert.True(session.QueryRayGiCapabilities().HardwareAvailable);
        var gi = new RayGiOptions(mode, Strength: 1, SamplesPerPixel: 16, DirectionalIrradiance: Vector3.Zero);
        OriginalFrameScene Quality(OriginalFrameScene frame) => new(frame.Surface, options: frame.Options! with
        {
            SpatialSamples = 2, Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.Ultra
        });
        var source = Quality(Fixture("diffuse", false, SurfaceDrawOrder.OpaqueFirst));
        var reference = Quality(Fixture("diffuse", true, SurfaceDrawOrder.Original));
        var actual = session.Capture(source, Size, Size, rayGiOptions: gi);
        Assert.Equal(mode, session.RayGiStatistics!.Effective);
        Assert.Equal(session.Capture(reference, Size, Size, rayGiOptions: gi).Rgba, actual.Rgba);
        using var submitted = session.Render(source, Size, Size, rayGiOptions: gi);
        Assert.Equal(0, submitted.Statistics.ReadbackCount);
    }

    private static OriginalFrameScene Mode(OriginalFrameScene frame, SurfaceDrawOrder mode) =>
        new(frame.Surface, options: frame.Options! with { SurfaceOrder = mode });

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void PositiveGiKeepsOriginalTriangleIdsWhenAClippedSurfaceMovesBehindReceivers(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        if (mode == LightingMode.HardwareRayTracing)
            Assert.True(session.QueryRayGiCapabilities().HardwareAvailable);
        var bounce = D3D12RayGiTests.Bounce();
        var mesh = bounce.Surface.Snapshot;
        SceneVertex[] invisible = [new(new(-1, -1, -4), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, -4), -Vector3.UnitZ, Vector2.Zero), new(new(1, 1, -4), -Vector3.UnitZ, Vector2.Zero),
            new(new(-1, 1, -4), -Vector3.UnitZ, Vector2.Zero)];
        var vertices = invisible.Concat(mesh.Vertices.ToArray()).ToArray();
        var indices = new uint[] { 0, 1, 2, 0, 2, 3 }.Concat(mesh.Indices.ToArray().Select(i => i + 4)).ToArray();
        var materials = new[] { new SceneMaterial("clipped first", 0, 6, new(1, 1, 1, 0),
            Vector3.Zero, Vector3.Zero, 1) }.Concat(mesh.Materials.Select(m => m with { StartIndex = m.StartIndex + 6 }));
        var scene = new SceneSnapshot(vertices, indices, materials, mesh.Textures, mesh.World, mesh.Camera, mesh.LightDirection);
        var surface = new OriginalStyleScene(scene,
            new[] { new OriginalStyleMaterialBinding(Vector3.One, DoubleSided: true) }.Concat(bounce.Surface.Materials),
            bounce.Surface.Profile);
        var frame = new OriginalFrameScene(surface, options: bounce.Options! with { SurfaceOrder = SurfaceDrawOrder.Original },
            materials: new[] { new OriginalFrameMaterialFlags(false, false, false) }.Concat(bounce.Materials));
        var gi = new RayGiOptions(mode, Strength: 4, SamplesPerPixel: 64, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectionalIrradiance: new(3));
        var off = session.Capture(frame, Size, Size);
        var originalGi = session.Capture(frame, Size, Size, rayGiOptions: gi);
        Assert.NotEqual(off.Rgba, originalGi.Rgba);
        var automatic = ModeWithFlags(frame, SurfaceDrawOrder.OpaqueFirst);
        Assert.Equal(new[] { 1, 2, 0 }, SurfaceDrawOrderPlanner.Plan(automatic));
        Assert.Equal(originalGi.Rgba, session.Capture(automatic, Size, Size, rayGiOptions: gi).Rgba);
    }

    private static OriginalFrameScene ModeWithFlags(OriginalFrameScene frame, SurfaceDrawOrder mode) =>
        new(frame.Surface, options: frame.Options! with { SurfaceOrder = mode }, materials: frame.Materials);

    private static OriginalFrameScene Fixture(string coverage, bool backFirst, SurfaceDrawOrder mode)
    {
        var vertices = new List<SceneVertex>();
        foreach (float z in new[] { 0f, 1f })
            foreach (var xy in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
                vertices.Add(new(new(xy.X, xy.Y, z), -Vector3.UnitZ, Vector2.Zero));
        uint[] front = [0, 1, 2, 0, 2, 3], back = [4, 5, 6, 4, 6, 7];
        var indices = backFirst ? back.Concat(front).ToArray() : front.Concat(back).ToArray();
        var black = new SceneMaterial("transparent-front", 0, 6,
            new(0, 0, 0, coverage == "material" ? .5f : 1), Vector3.Zero, Vector3.Zero, 1,
            DiffuseTexture: coverage is "diffuse" or "sub-texture" ? 0 : -1);
        var blue = new SceneMaterial("opaque-back", 0, 6, new(0, 0, 1, 1), Vector3.Zero, Vector3.Zero, 1);
        var frontBinding = new OriginalStyleMaterialBinding(Vector3.One,
            coverage.StartsWith("sphere-", StringComparison.Ordinal) || coverage == "sub-texture" ? 1 : -1,
            coverage switch
            {
                "sphere-add" => OriginalSphereMode.Add,
                "sphere-multiply" => OriginalSphereMode.Multiply,
                "sub-texture" => OriginalSphereMode.SubTexture,
                _ => OriginalSphereMode.None
            }, DoubleSided: true);
        if (coverage == "sub-texture")
            frontBinding = frontBinding with { TextureFactors = OriginalMaterialTextureFactors.Identity with
                { Toon = new(new(1, 1, 1, .5f), Vector4.Zero) } };
        var blueBinding = new OriginalStyleMaterialBinding(Vector3.One, DoubleSided: true);
        var materials = backFirst ? new[] { blue, black with { StartIndex = 6 } }
            : new[] { black, blue with { StartIndex = 6 } };
        var bindings = backFirst ? new[] { blueBinding, frontBinding } : new[] { frontBinding, blueBinding };
        SceneTexture[] textures = [new("diffuse", 1, 1, [0, 0, 0, coverage == "diffuse" ? (byte)128 : (byte)255]),
            new("sphere", 1, 1, [0, 0, 0, coverage.StartsWith("sphere-", StringComparison.Ordinal) ? (byte)128 : (byte)255])];
        var uv = new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(Vector4.Zero,
            Vector4.Zero, Vector4.Zero, Vector4.Zero), vertices.Count).ToArray());
        var camera = new SceneCamera(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.5f, .1f, 20);
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices, materials, textures, camera: camera,
            lightDirection: coverage == "sub-texture" ? Vector3.UnitX : -Vector3.UnitZ, additionalUvs: uv);
        return new(new(snapshot, bindings, new(Vector3.One, Vector3.Zero, Matrix4x4.Identity,
            OriginalTextureSampling.PointClamp)), options: new(OriginalAlphaBlendMode.Normal, true, false,
            BackgroundColor: Vector4.One, EnableFrustumCulling: false, SurfaceOrder: mode));
    }
}
