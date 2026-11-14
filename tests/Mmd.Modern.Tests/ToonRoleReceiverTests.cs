using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// The root runs actual GPU tests serially. A successful compile is not GPU execution.
[Trait("Category", "Gpu")]
public sealed class ToonRoleReceiverTests(ITestOutputHelper output)
{
    private const int Size = 65;
    private static RayGiOptions Gi(LightingMode mode) => new(mode, Strength: 4, SamplesPerPixel: 128, Seed: 17,
        MaxRayDistance: 50, RayEpsilon: .001f, DirectionalLightDirection: Vector3.UnitZ,
        DirectionalIrradiance: new(3), Composition: RayGiComposition.ToonPalette)
        { ToonResponse = RayGiToonResponse.Continuous };

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void MixedReceiversOverrideGlobalContinuousWithoutColoringTheCharacter(LightingMode mode)
    {
        using var session = new D3D12FrameSession(); Require(session, mode);
        var frame = Fixture(); var gi = Gi(mode);
        var off = session.Capture(frame, Size, Size);
        var mixed = session.Capture(frame, Size, Size, rayGiOptions: gi);
        var stats = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
        // The global statistic is still Continuous; the per-material count reports the override.
        Assert.Equal(RayGiToonResponse.Continuous, stats.ToonResponse);
        Assert.Equal(1, stats.PaletteBandReceiverMaterials);
        byte[] characterOff = Pixel(off, frame, -.75f), character = Pixel(mixed, frame, -.75f);
        byte[] environmentOff = Pixel(off, frame, .75f), environment = Pixel(mixed, frame, .75f);
        int red = character[0] - characterOff[0], green = character[1] - characterOff[1], blue = character[2] - characterOff[2];
        Assert.True(red > 3, "A neutral character response must still contain actual traced fill.");
        Assert.InRange(Math.Abs(red - green), 0, 1); Assert.InRange(Math.Abs(red - blue), 0, 1);
        // Eg=.5 and toon=.4. The only permitted fill levels are 0, 1/16, 2/16, 3/16, 4/16.
        Assert.Contains(Enumerable.Range(0, 5), level =>
            Math.Abs(character[0] - 255 * .5f * (.4f + .6f * level / 16)) <= 1);
        Assert.InRange(red, 1, 20);
        Assert.True(environment[1] - environmentOff[1] > environment[0] - environmentOff[0] + 10,
            "The environment receiver must retain the shared green bounce as colored light.");
        var continuous = session.Capture(WithReceiverResponse(frame, RayGiToonResponse.Continuous), Size, Size, rayGiOptions: gi);
        Assert.Equal(environment, Pixel(continuous, frame, .75f));
        Assert.True(Pixel(continuous, frame, -.75f)[1] > character[1] + 10);
        for (int i = 3; i < off.Rgba.Length; i += 4) Assert.Equal(off.Rgba[i], mixed.Rgba[i]);
        Write(session, "mixed-receivers", character, environment);
        long readbacks = session.Runtime.Readbacks;
        using var gpu = session.Render(frame, Size, Size, rayGiOptions: gi);
        Assert.Equal(0, gpu.Statistics.ReadbackCount); Assert.Equal(readbacks, session.Runtime.Readbacks);
        Assert.Equal(1, session.RayGiStatistics!.PaletteBandReceiverMaterials);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void AdditiveCharacterRejectionDoesNotBeginOrDiscardTemporalHistoryOrSeed(LightingMode mode)
    {
        using var session = new D3D12FrameSession(); using var control = new D3D12FrameSession();
        Require(session, mode); Require(control, mode);
        var frame = Fixture(); var gi = Gi(mode) with { Denoiser = RayGiDenoiser.TemporalSpatial };
        Assert.Equal(control.Capture(frame, Size, Size, gi).Rgba, session.Capture(frame, Size, Size, gi).Rgba);
        using (session.Render(frame, Size, Size, gi)) { }
        using (control.Render(frame, Size, Size, gi)) { }
        var oldStats = session.RayGiStatistics;
        long frameId = session.Runtime.FrameId, submissions = session.Runtime.Submissions;
        long compilations = session.Runtime.ShaderCompilations, readbacks = session.Runtime.Readbacks;
        // Disable denoising so rejection is specifically the receiver policy, not the old denoiser validation.
        var additive = gi with { Composition = RayGiComposition.Additive, Denoiser = RayGiDenoiser.Off };
        var rejection = Assert.Throws<NotSupportedException>(() => session.Render(frame, Size, Size, additive));
        Assert.Contains("Character", rejection.Message);
        Assert.False(session.Runtime.IsRecording); Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions); Assert.Equal(compilations, session.Runtime.ShaderCompilations);
        Assert.Equal(readbacks, session.Runtime.Readbacks); Assert.Same(oldStats, session.RayGiStatistics);
        Assert.Equal(control.Capture(frame, Size, Size, gi).Rgba, session.Capture(frame, Size, Size, gi).Rgba);
        Assert.Equal(3UL, session.RayGiStatistics!.TemporalHistoryFrames);
        Assert.False(session.RayGiStatistics.TemporalResetHistory);
        Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(control.RayGiStatistics!.Seed, session.RayGiStatistics.Seed);
        Write(session, "additive-rejection-recovered");
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ChangingOneReceiverResponseResetsTemporalGiToFreshBytesAndKeepsRenderReadbackFree(LightingMode mode)
    {
        using var session = new D3D12FrameSession(); using var fresh = new D3D12FrameSession();
        Require(session, mode); Require(fresh, mode);
        var frame = Fixture(); var continuous = WithReceiverResponse(frame, RayGiToonResponse.Continuous);
        var gi = Gi(mode) with { Denoiser = RayGiDenoiser.TemporalSpatial };
        using (session.Render(continuous, Size, Size, gi)) { }
        using (session.Render(continuous, Size, Size, gi)) { }
        Assert.Equal(2UL, session.RayGiStatistics!.TemporalHistoryFrames);
        long readbacks = session.Runtime.Readbacks;
        using (var gpu = session.Render(frame, Size, Size, gi))
        {
            Assert.Equal(0, gpu.Statistics.ReadbackCount); Assert.Equal(readbacks, session.Runtime.Readbacks);
            Assert.True(session.RayGiStatistics!.TemporalResetHistory);
            Assert.Equal("ReceiverResponseChanged", session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames); Assert.Equal(gi.Seed, session.RayGiStatistics.Seed);
        }
        // Compare matching first and second phases; Capture itself also submits a frame.
        using (fresh.Render(frame, Size, Size, gi)) { }
        Assert.Equal(fresh.Capture(frame, Size, Size, gi).Rgba, session.Capture(frame, Size, Size, gi).Rgba);
        Assert.Equal(2UL, session.RayGiStatistics!.TemporalHistoryFrames);
        Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(fresh.RayGiStatistics!.Seed, session.RayGiStatistics.Seed);
        Assert.Equal(1, session.RayGiStatistics.PaletteBandReceiverMaterials);
        Write(session, "receiver-response-reset-recovered");
    }

    private static OriginalFrameScene WithReceiverResponse(OriginalFrameScene frame, RayGiToonResponse response)
    {
        var flags = frame.Materials.ToArray(); flags[0] = flags[0] with { GiToonResponseOverride = response };
        return new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options, flags, frame.Lighting, frame.EditorOverlay);
    }

    private static OriginalFrameScene Fixture()
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        var materials = new List<SceneMaterial>();
        void Quad(string name, float x, float z, float half, Vector3 normal, Vector4 color, Vector3 ambient)
        {
            uint start = (uint)vertices.Count; int offset = indices.Count;
            vertices.AddRange([new(new(x-half,-half,z),normal,Vector2.Zero), new(new(x+half,-half,z),normal,Vector2.Zero),
                new(new(x+half,half,z),normal,Vector2.Zero), new(new(x-half,half,z),normal,Vector2.Zero)]);
            indices.AddRange((normal == Vector3.UnitZ ? new uint[] { 0,1,2,0,2,3 } : new uint[] { 0,2,1,0,3,2 }).Select(i => i + start));
            materials.Add(new(name, offset, 6, color, Vector3.Zero, ambient, 1));
        }
        Quad("manually marked character", -.75f, 0, .6f, -Vector3.UnitZ, new(.5f,.5f,.5f,1), new(.1f));
        Quad("manually marked environment", .75f, 0, .6f, -Vector3.UnitZ, new(.5f,.5f,.5f,1), new(.1f));
        Quad("shared green bounce", 0, -8, 15, Vector3.UnitZ, new(.02f,1,.02f,1), Vector3.Zero);
        var mesh = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials, [],
            camera: new(new(0,0,-6), Vector3.Zero, Vector3.UnitY, 4.5f, .1f, 30), lightDirection: Vector3.UnitX);
        var surface = new OriginalStyleScene(mesh, [new(new(.4f)), new(new(.4f)), new(Vector3.One, DoubleSided: true)],
            new(new(.8f), Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp));
        return new(surface, options: new(OriginalAlphaBlendMode.Normal, true, false), materials:
            [new(false,false,false) { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands },
             new(false,false,false) { GiToonResponseOverride = RayGiToonResponse.Continuous }, new(false,false,false)]);
    }

    private static byte[] Pixel(RenderedFrame frame, OriginalFrameScene scene, float worldX)
    {
        var clip = Vector4.Transform(new Vector4(worldX, 0, 0, 1), scene.Surface.Snapshot.Camera.ViewProjection(Size, Size));
        int x = (int)((clip.X / clip.W * .5f + .5f) * Size);
        return frame.Rgba.AsSpan((Size / 2 * Size + x) * 4, 4).ToArray();
    }

    private static void Require(D3D12FrameSession session, LightingMode mode)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? capabilities.HardwareAvailable : capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? $"Actual GPU backend unavailable: {session.AdapterName}.");
        Assert.False(session.IsWarp);
    }

    private void Write(D3D12FrameSession session, string stage, byte[]? character = null, byte[]? environment = null)
        => output.WriteLine(JsonSerializer.Serialize(new { stage, session.AdapterName, session.RayGiBackendIdentity,
            gi = session.RayGiStatistics, character, environment }));
}
