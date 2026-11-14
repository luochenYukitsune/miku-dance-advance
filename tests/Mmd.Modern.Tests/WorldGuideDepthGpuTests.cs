using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class WorldGuideDepthGpuTests
{
    public static IEnumerable<object[]> CoverageCases()
    {
        foreach (bool leftHanded in new[] { false, true })
        foreach (byte alpha in new byte[] { 0, 128, 255 })
        foreach (int quality in new[] { 0, 1, 2 })
            yield return [leftHanded, alpha, quality];
    }

    [Theory] [MemberData(nameof(CoverageCases))]
    public void OpaqueAndBlendedSurfacesHideWorldGuidesButAlphaHolesAndSelectionRemainVisible(bool leftHanded, byte alpha, int quality)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(leftHanded, alpha, quality);
        var overlay = new ViewportOverlaySnapshot([
            new(new(.1f, .5f), new(.9f, .5f), new(1, 0, 1, 1), 2, DeviceDepth: new(.5f)),
            new(new(.1f, .35f), new(.9f, .35f), new(0, 1, 1, 1), 2, DeviceDepth: new(.3f)),
            new(new(.1f, .65f), new(.9f, .65f), new(0, 1, 0, 1), 2)]);
        var editing = WithOverlay(frame, overlay);
        var upscale = quality == 2 ? new D3D12SpatialUpscalingOptions { Mode = D3D12SpatialUpscalingMode.Fsr1 } : null;
        var diffusion = new D3D12DiffusionOptions(true, .7f);
        var shown = ReadPreview(session, editing, upscale, diffusion: diffusion);
        AssertColor(shown, 20, 48, 255, 0, 255); // Visible background behind the model.
        if (alpha == 0) AssertColor(shown, 64, 48, 255, 0, 255);
        else Assert.False(IsColor(shown, 64, 48, 255, 0, 255));
        AssertColor(shown, 64, 34, 0, 255, 255); // Guide physically in front of the model.
        AssertColor(shown, 64, 62, 0, 255, 0); // Selection helpers are always on top.
        Assert.Equal(2, session.EditorOverlayDrawCalls);
        Assert.Contains(session.LastRecordings, r => r.Channel == "editor:world-guide-depth");
        Assert.Contains(session.LastTargets, t => t.Name == "$editor-guide-depth" && t.Width == 128 && t.Height == 96);
        Assert.True(session.LastRecordings.ToList().FindIndex(r => r.Channel == "diffusion:y")
            < session.LastRecordings.ToList().FindIndex(r => r.Channel == "editor:world-guides"));
        Assert.Equal(session.Capture(frame, 128, 96, upscaling: upscale, diffusion: diffusion).Rgba,
            session.Capture(editing, 128, 96, upscaling: upscale, diffusion: diffusion).Rgba);
        Assert.DoesNotContain(session.LastRecordings, r => r.Channel.StartsWith("editor:", StringComparison.Ordinal));
        Save($"guides-{leftHanded}-{alpha}-{quality}", shown);
    }

    [Fact]
    public void NoDepthGuidesCreatesNoAdditionalVisibilityPassAndNoStaleOcclusion()
    {
        using var session = new D3D12FrameSession(); var frame = Frame(true, 255, 0);
        var world = WithOverlay(frame, new([new(new(.1f, .5f), new(.9f, .5f), new(1, 0, 1, 1), 2, DeviceDepth: new(.5f))]));
        ReadPreview(session, world);
        using (session.Render(frame, 128, 96))
        {
            Assert.Equal(0, session.EditorOverlayDrawCalls);
            Assert.DoesNotContain(session.LastTargets, t => t.Name == "$editor-guide-depth");
        }
        var helpers = WithOverlay(frame, new([new(new(.1f, .5f), new(.9f, .5f), new(1, 0, 1, 1), 2)]));
        var shown = ReadPreview(session, helpers);
        AssertColor(shown, 64, 48, 255, 0, 255);
        Assert.Equal(1, session.EditorOverlayDrawCalls);
        Assert.DoesNotContain(session.LastTargets, t => t.Name == "$editor-guide-depth");
    }

    [Fact]
    public void DeviceDepthInterpolatesAlongSegmentsAndRespectsFrameDepthWritePolicy()
    {
        using var session = new D3D12FrameSession(); var frame = Frame(true, 255, 0);
        var overlay = new ViewportOverlaySnapshot([new(new(.1f, .5f), new(.9f, .5f), new(1, 0, 1, 1), 2, DeviceDepth: new(.3f, .6f))]);
        var shown = ReadPreview(session, WithOverlay(frame, overlay));
        AssertColor(shown, 40, 48, 255, 0, 255);
        Assert.False(IsColor(shown, 80, 48, 255, 0, 255));
        var noWrite = new OriginalFrameScene(frame.Surface, options: frame.Options! with { WriteDepth = false }, editorOverlay: overlay);
        AssertColor(ReadPreview(session, noWrite), 80, 48, 255, 0, 255);
    }

    [Fact]
    public void TemporalReconstructionKeepsGuideVisibilityIndependentOfJitterAndHistory()
    {
        // The same official runtime used by existing FSR integration tests is explicitly supplied by the runner.
        var loader = Environment.GetEnvironmentVariable("MMD_FSR_RUNTIME_PATH");
        Assert.False(string.IsNullOrWhiteSpace(loader), "Set MMD_FSR_RUNTIME_PATH to the official FidelityFX loader for the temporal guide test.");
        using var session = new D3D12FrameSession(); var original = Frame(true, 255, 0);
        var frame = new OriginalFrameScene(original.Surface, options: original.Options! with
        { CameraOverride = original.Surface.Snapshot.Camera with { VerticalFovDegrees = 22.619864f } });
        var overlay = new ViewportOverlaySnapshot([new(new(.1f, .5f), new(.9f, .5f), new(1, 0, 1, 1), 2, DeviceDepth: new(.92f))]);
        var temporal = new D3D12TemporalUpscalingOptions { Mode = D3D12TemporalUpscalingMode.Fsr,
            QualityPreset = D3D12FsrQualityPreset.Quality, LoaderPath = loader };
        ReadPreview(session, frame, temporal: temporal);
        for (int phase = 0; phase < 4; phase++)
        {
            var shown = ReadPreview(session, WithOverlay(frame, overlay), temporal: temporal);
            Assert.False(session.TemporalUpscalingStatistics!.ResetHistory);
            AssertColor(shown, 20, 48, 255, 0, 255);
            Assert.False(IsColor(shown, 64, 48, 255, 0, 255));
            Assert.Contains(session.LastTargets, t => t.Name == "$editor-guide-depth" && t.Width == 128 && t.Height == 96);
        }
        ReadPreview(session, frame, temporal: temporal);
        Assert.False(session.TemporalUpscalingStatistics!.ResetHistory);
        Assert.DoesNotContain(session.LastTargets, t => t.Name == "$editor-guide-depth");
    }

    private static OriginalFrameScene Frame(bool leftHanded, byte alpha, int quality)
    {
        SceneVertex[] vertices = [new(new(-.5f, -.7f, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(.5f, -.7f, 0), -Vector3.UnitZ, Vector2.UnitX),
            new(new(.5f, .7f, 0), -Vector3.UnitZ, Vector2.One),
            new(new(-.5f, .7f, 0), -Vector3.UnitZ, Vector2.UnitY)];
        var scene = new SceneSnapshot(vertices, new uint[] { 0, 1, 2, 0, 2, 3 },
            [new("occluder", 0, 6, Vector4.One, Vector3.Zero, Vector3.One, 1, DiffuseTexture: 0, AlphaCutoff: 0)],
            [new("alpha coverage", 1, 1, [255, 255, 255, alpha])],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2, 1, 11, LeftHanded: leftHanded));
        return new(new(scene, [new(Vector3.One, DoubleSided: true)]),
            options: new(OriginalAlphaBlendMode.Normal, true, false) { BackgroundColor = Vector4.One,
                Antialiasing = quality == 1 ? FrameAntialiasing.Smaa1X : FrameAntialiasing.Off,
                SpatialSamples = quality == 1 ? 4 : 1 });
    }

    private static OriginalFrameScene WithOverlay(OriginalFrameScene frame, ViewportOverlaySnapshot overlay)
        => new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options, frame.Materials, frame.Lighting, overlay);

    private static byte[] ReadPreview(D3D12FrameSession session, OriginalFrameScene frame,
        D3D12SpatialUpscalingOptions? upscale = null, D3D12TemporalUpscalingOptions? temporal = null, D3D12DiffusionOptions? diffusion = null)
    {
        ID3D12Resource? readback = null; var layout = new PlacedSubresourceFootPrint[1]; int bytes = 0;
        using var shown = session.RenderForPresent(frame, 128, 96, null, (context, output) =>
        {
            context.Device.GetCopyableFootprints(output.Resource.Description, 0, 1, 0, layout, new int[1], new ulong[1], out ulong size);
            bytes = checked((int)size);
            readback = context.Own(context.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
            context.Commands.CopyTextureRegion(new TextureCopyLocation(readback, layout[0]), 0, 0, 0, new TextureCopyLocation(output.Resource, 0));
        }, upscale, temporal, diffusion: diffusion);
        session.Runtime.Wait(shown.Statistics.SubmittedFence);
        var rgba = new byte[128 * 96 * 4]; var mapped = readback!.Map<byte>(0, bytes);
        try { for (int row = 0; row < 96; row++) mapped.Slice(row * layout[0].Footprint.RowPitch, 128 * 4).CopyTo(rgba.AsSpan(row * 128 * 4)); }
        finally { readback.Unmap(0); }
        Assert.Equal(0, shown.Statistics.ReadbackCount); // Only the explicit diagnostic callback reads back.
        return rgba;
    }

    private static bool IsColor(byte[] rgba, int x, int y, byte r, byte g, byte b)
    { int offset = (y * 128 + x) * 4; return rgba[offset] == r && rgba[offset + 1] == g && rgba[offset + 2] == b; }
    private static void AssertColor(byte[] rgba, int x, int y, byte r, byte g, byte b)
        => Assert.True(IsColor(rgba, x, y, r, g, b), $"Pixel ({x},{y}): {string.Join(',', rgba.AsSpan((y * 128 + x) * 4, 4).ToArray())}; expected {r},{g},{b}.");
    private static void Save(string name, byte[] rgba)
    {
        string? directory = Environment.GetEnvironmentVariable("MMD_GPU_RUNTIME_ARTIFACTS");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".rgba"), rgba);
    }
}
