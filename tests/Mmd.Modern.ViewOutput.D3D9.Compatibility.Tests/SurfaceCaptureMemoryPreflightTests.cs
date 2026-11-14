using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class SurfaceCaptureMemoryPreflightTests
{
    [Fact]
    public void DefaultAutomaticPolicyPreservesClassicRequest()
    {
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off,
            SpatialSamples: 1, SurfaceOrder: SurfaceDrawOrder.Original);
        var request = new ViewRenderRequest(193, 145, quality);

        var frozen = D3D9SurfacePreflight.FreezeRequest(request, Vector4.One,
            D3D9SurfaceIsolation.NoConfiguredMmeOrEmm);

        Assert.Equal(ViewCaptureMemoryMode.Auto, frozen.CaptureMemory);
        Assert.Equal(request.Width, frozen.Width);
        Assert.Equal(request.Height, frozen.Height);
        Assert.Equal(quality, frozen.EffectiveQuality);
    }

    [Theory]
    [InlineData(ViewCaptureMemoryMode.PreferSpeed)]
    [InlineData(ViewCaptureMemoryMode.LowMemory)]
    public void ExplicitModernPolicyIsRejectedBeforeClassicCapture(ViewCaptureMemoryMode mode)
    {
        var request = new ViewRenderRequest(193, 145, CaptureMemory: mode);

        Assert.Throws<NotSupportedException>(() => D3D9SurfacePreflight.FreezeRequest(request,
            Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
    }
}
