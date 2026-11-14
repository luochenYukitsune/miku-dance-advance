using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12RayGiEmissionTests
{
    private const int Size = 97;
    private static RayGiOptions Options(LightingMode mode) => new(mode, Strength: .5f, SamplesPerPixel: 256,
        Seed: 29, MaxRayDistance: 20, RayEpsilon: .001f, DirectionalIrradiance: Vector3.Zero,
        DirectScale: 0, Composition: RayGiComposition.ToonPalette);

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void EmissionLightsReceiverWithNoDirectionalEnergyAndRespectsOcclusionAndSidedness(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        var options = Options(mode);
        byte[] Capture(OriginalFrameScene frame) => Center(session.Capture(frame, Size, Size, rayGiOptions: options));
        var zero = Fixture(Vector3.Zero);
        var original = Center(session.Capture(zero, Size, Size));
        Assert.Equal(original, Capture(zero));
        var red = Fixture(new(.3f, .015f, .015f));
        var lit = Capture(red);
        Assert.True(lit[0] - original[0] > 8);
        Assert.InRange(lit[1] - original[1], 0, 2);
        Assert.InRange(lit[2] - original[2], 0, 2);
        Assert.All(lit.Take(3), value => Assert.InRange(value, (byte)0, (byte)128));
        Assert.Equal(original[3], lit[3]);
        Assert.Equal(original, Capture(Fixture(new(.3f, .015f, .015f), blocker: true)));
        Assert.Equal(original, Capture(Fixture(new(.3f, .015f, .015f), backFacing: true)));
        Assert.True(Capture(Fixture(new(.3f, .015f, .015f), backFacing: true, doubleSided: true))[0] > original[0] + 8);
        Assert.Equal(original, Capture(Fixture(new(.3f, .015f, .015f), emitterAlpha: 0)));
        Assert.Equal(original, Center(session.Capture(red, Size, Size, rayGiOptions: options with { Strength = 0 })));
        Assert.Equal(mode, session.RayGiStatistics!.Effective);
        Assert.False(session.IsWarp);
    }

    [Fact]
    public void MaterialChangesDoNotReuseOldEmissionAndSoftwareMatchesActualDxr()
    {
        using var session = new D3D12FrameSession();
        var red = Fixture(new(.3f, .015f, .015f));
        var blue = Fixture(new(.015f, .015f, .3f));
        var software = session.Capture(red, Size, Size, rayGiOptions: Options(LightingMode.SoftwareRayTracing));
        var hardware = session.Capture(red, Size, Size, rayGiOptions: Options(LightingMode.HardwareRayTracing));
        Assert.StartsWith("DXR-raw-", session.RayGiBackendIdentity);
        Assert.InRange(software.Rgba.Zip(hardware.Rgba, (a, b) => Math.Abs(a - b)).Max(), 0, 1);
        var blueFrame = session.Capture(blue, Size, Size, rayGiOptions: Options(LightingMode.HardwareRayTracing));
        Assert.True(Center(hardware)[0] > Center(blueFrame)[0] + 8);
        Assert.True(Center(blueFrame)[2] > Center(hardware)[2] + 8);
        Assert.Equal(hardware.Rgba, session.Capture(red, Size, Size, rayGiOptions: Options(LightingMode.HardwareRayTracing)).Rgba);
        var off = session.Capture(red, Size, Size);
        Assert.Equal(off.Rgba, session.Capture(red, Size, Size, rayGiOptions: RayGiOptions.Off).Rgba);
        using var rendered = session.Render(red, Size, Size, rayGiOptions: Options(LightingMode.HardwareRayTracing));
        Assert.Equal(1, rendered.Statistics.SubmissionCount); Assert.Equal(0, rendered.Statistics.ReadbackCount);
    }

    private static byte[] Center(RenderedFrame frame) => frame.Rgba.AsSpan(((Size / 2) * Size + Size / 2) * 4, 4).ToArray();
    private static OriginalFrameScene Fixture(Vector3 emission, bool blocker = false, bool backFacing = false,
        bool doubleSided = false, float emitterAlpha = 1)
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        var materials = new List<SceneMaterial>(); var bindings = new List<OriginalStyleMaterialBinding>();
        void Quad(string name, float half, float z, bool forward, Vector4 diffuse, Vector3 ambient,
            Vector3 emitted, bool twoSided = false, Vector4? rectangle = null)
        {
            uint start = (uint)vertices.Count; int first = indices.Count;
            var normal = forward ? Vector3.UnitZ : -Vector3.UnitZ;
            var bounds = rectangle ?? new(-half, -half, half, half);
            foreach (var p in new Vector3[] { new(bounds.X, bounds.Y, z), new(bounds.Z, bounds.Y, z), new(bounds.Z, bounds.W, z), new(bounds.X, bounds.W, z) })
                vertices.Add(new(p, normal, Vector2.Zero));
            indices.AddRange(forward ? [start, start + 1, start + 2, start, start + 2, start + 3]
                : [start, start + 2, start + 1, start, start + 3, start + 2]);
            materials.Add(new(name, first, 6, diffuse, Vector3.Zero, ambient, 1) { Emission = emitted });
            bindings.Add(new(new(.2f), DoubleSided: twoSided));
        }
        Quad("receiver", .7f, 0, false, new(.5f, .5f, .5f, 1), new(.1f), Vector3.Zero);
        // Leave the center camera ray clear even when the emitter winding is reversed; the
        // sidedness control must continue to measure the receiver rather than the emitter.
        foreach (var rectangle in new Vector4[] { new(-4, -4, -.05f, 4), new(.05f, -4, 4, 4),
            new(-.05f, -4, .05f, -.05f), new(-.05f, .05f, .05f, 4) })
            Quad("emitter", 4, -1, !backFacing, new(0, 0, 0, emitterAlpha), Vector3.Zero, emission, doubleSided, rectangle);
        if (blocker) Quad("opaque blocker", 4, -.5f, true, new(0, 0, 0, 1), Vector3.Zero, Vector3.Zero);
        var camera = new SceneCamera(new(0, 0, -6), Vector3.Zero, Vector3.UnitY, 4.5f, .1f, 30);
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials, camera: camera, lightDirection: Vector3.UnitX);
        return new(new(snapshot, bindings, new(new(.8f), Vector3.Zero, Matrix4x4.Identity)),
            options: new(OriginalAlphaBlendMode.Normal, true, false),
            materials: materials.Select(_ => new OriginalFrameMaterialFlags(false, false, false)));
    }
}
