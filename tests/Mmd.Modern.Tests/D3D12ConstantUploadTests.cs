using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12ConstantUploadTests
{
    [Fact]
    public void ConstantPagesStayMappedAcrossSlotReuseAndAbortWithoutReusingOldValues()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12OriginalFrameTests.Shadow(true);
        for (int i = 0; i < 6; i++) { using var output = session.Render(frame, 97, 79); }
        long mappings = session.Runtime.ConstantPageMappings;
        Assert.InRange(mappings, 1, 3);
        for (int i = 0; i < 7; i++)
        {
            var camera = frame.Surface.Snapshot.Camera with { Eye = frame.Surface.Snapshot.Camera.Eye + new System.Numerics.Vector3(.03f * i, 0, 0) };
            var changed = new OriginalFrameScene(frame.Surface, frame.Outline, frame.OutlineOptions,
                frame.Options! with { CameraOverride = camera }, frame.Materials, frame.Lighting);
            if (i == 3)
                Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changed, 97, 79, null,
                    (_, _) => throw new OperationCanceledException("Abort after uploading this frame's constants.")));
            var image = session.Capture(changed, 97, 79);
            using var reference = new D3D12FrameSession();
            Assert.Equal(reference.Capture(changed, 97, 79).Rgba, image.Rgba);
            Assert.Equal(mappings, session.Runtime.ConstantPageMappings);
        }
    }

    [Fact]
    public void ConstantPageRolloverKeepsAlignedAddressesAndReusesPagesAfterSlotFence()
    {
        using var runtime = new D3D12GpuRuntime();
        byte[] full = new byte[1024 * 1024];
        byte[] small = new byte[17];
        for (int frame = 0; frame < 6; frame++)
        {
            runtime.Begin();
            ulong first = runtime.UploadConstants(full);
            ulong second = runtime.UploadConstants(small);
            ulong third = runtime.UploadConstants(small);
            Assert.Equal(0UL, first & 255);
            Assert.Equal(0UL, second & 255);
            Assert.Equal(second + 256, third);
            Assert.NotEqual(first, second);
            Assert.Throws<ArgumentException>(() => runtime.UploadConstants(new byte[full.Length + 1]));
            Assert.Equal(3, runtime.ConstantAllocations);
            Assert.Equal(full.Length + 2L * small.Length, runtime.ConstantBytes);
            runtime.Submit();
        }
        runtime.WaitForIdle();
        Assert.Equal(6, runtime.ConstantPageMappings);
    }
}
