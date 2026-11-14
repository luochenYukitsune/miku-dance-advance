using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.Rendering.Veldrid;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class EmptySceneGpuTests
{
    [Theory]
    [InlineData(LightingMode.Legacy)]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void AllHiddenClearsOnGpuWithoutGeometryOrGiAndCanReshow(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        var visible = D3D12OriginalFrameTests.Alpha(false, 0, 0);
        var before = session.Capture(visible, 65, 49);
        var empty = new OriginalFrameScene(new OriginalStyleScene(SceneSnapshot.CreateEmpty(), []),
            options: new(OriginalAlphaBlendMode.Normal, true, false) { RayGi = new(Mode: mode) });
        var blank = session.Capture(empty, 65, 49);
        Assert.Equal(0, session.Statistics!.SceneDrawCalls);
        AssertBackground(blank);
        using (var gpu = session.Render(empty, 65, 49))
        {
            Assert.Equal(0, gpu.Statistics.SceneDrawCalls);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
        }
        Assert.Equal(before.Rgba, session.Capture(visible, 65, 49).Rgba);
    }

    [Fact]
    public void LegacyMeshBackendsAcceptExplicitEmptySnapshot()
    {
        using var d3d12 = new D3D12MeshRenderer();
        var scene = SceneSnapshot.CreateEmpty();
        AssertBackground(d3d12.Render(scene, 65, 49));
        using var veldrid = new VeldridRenderBackend();
        AssertBackground(veldrid.Render(scene, 65, 49));
    }

    [Fact]
    public void HiddenSceneClearsPreviousNativeLightStatistics()
    {
        using var session = new D3D12FrameSession();
        var source = D3D12OriginalFrameTests.Alpha(false, 0, 0);
        var lights = new SceneLightingSnapshot([new(Guid.NewGuid(), "sun", SceneLightKind.Directional,
            Vector3.Zero, Quaternion.Identity, Vector3.One)]);
        session.Capture(new(source.Surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials, lights), 65, 49);
        Assert.NotNull(session.NativeLightStatistics);
        var hidden = new OriginalFrameScene(new OriginalStyleScene(SceneSnapshot.CreateEmpty(), []), lighting: lights);
        AssertBackground(session.Capture(hidden, 65, 49));
        Assert.Null(session.NativeLightStatistics);
        Assert.Null(session.RayGiStatistics);
    }

    private static void AssertBackground(RenderedFrame frame)
    {
        Assert.Equal(0, frame.DrawCalls);
        for(int pixel = 0; pixel < frame.Width * frame.Height; pixel++)
            Assert.Equal(new byte[] {20, 24, 32, 255}, frame.Rgba.AsSpan(pixel * 4, 4).ToArray());
    }
}
