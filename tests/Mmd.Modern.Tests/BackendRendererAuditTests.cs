using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using System.Numerics;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class BackendRendererAuditTests
{
    [Theory]
    [InlineData(SceneLightKind.Point)]
    [InlineData(SceneLightKind.Spot)]
    public void ShadowProjectionRejectsFinitePositionsThatCollapseLookDirection(SceneLightKind kind)
    {
        var light = new NativeSceneLight(Guid.NewGuid(), "distant", kind, new(1e20f),
            Quaternion.Identity, Vector3.One);
        _ = new SceneLightingSnapshot(new[] { light }); // Valid format-neutral input.
        var scene = D3D12CharacterFsrSessionTests.Character().Surface.Snapshot;
        Assert.Throws<ArgumentException>(() => NativeLightShadowProjection.Create(scene, light));
    }

    [Fact]
    public void GiResetRequestInvalidatesAaOnceWithoutBecomingPersistentConfiguration()
    {
        var frame = D3D12CharacterFsrSessionTests.Character();
        var history = new D3D12TemporalHistory();
        var options = new D3D12TemporalUpscalingOptions();
        var gi = new RayGiOptions { Mode = LightingMode.SoftwareRayTracing };
        history.Commit(frame, options, 0, 8, gi);
        Assert.Equal("None", history.ResetReason(frame, options, gi));

        var reset = gi with { ResetTemporalHistory = true };
        Assert.Equal("GiConfigurationChanged", history.ResetReason(frame, options, reset));
        history.Commit(frame, options, 0, 8, reset);
        Assert.Equal("None", history.ResetReason(frame, options, gi));
        Assert.Equal("GiConfigurationChanged", history.ResetReason(frame, options, reset));
    }
}

[Trait("Category", "Gpu")]
public sealed class BackendRendererAuditGpuTests
{
    [Fact]
    public void RejectedDisposeDuringRecordingDoesNotPoisonPersistentSession()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12CharacterFsrSessionTests.Character();
        using (var output = session.RenderForPresent(frame, 64, 48, null, (_, _) =>
            Assert.Throws<InvalidOperationException>(session.Dispose)))
            Assert.Equal(1, output.Statistics.SubmissionCount);
        Assert.Equal(64 * 48 * 4, session.Capture(frame, 64, 48).Rgba.Length);
    }

    [Fact]
    public void DisposedSessionRejectsCachedCapabilityQueries()
    {
        var session = new D3D12FrameSession();
        _ = session.QueryRayGiCapabilities();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.QueryRayGiCapabilities());
    }

    [Fact]
    public void WaitForIdleRejectsOpenFrameWithoutRetiringItsResources()
    {
        using var runtime = new D3D12GpuRuntime();
        var context = runtime.Begin();
        var resource = context.Own(new DisposalProbe());
        try
        {
            Assert.Throws<InvalidOperationException>(runtime.WaitForIdle);
            Assert.False(resource.Disposed);
            Assert.True(runtime.IsRecording);
            runtime.Wait(runtime.Submit());
            Assert.False(resource.Disposed);
            runtime.WaitForIdle();
            Assert.True(resource.Disposed);
        }
        finally { runtime.Abort(); }
    }

    [Fact]
    public void ReplacingDynamicStreamTwiceInOneFrameKeepsEarlierRecordedBufferAlive()
    {
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        var context = runtime.Begin();
        var first = cache.DynamicBuffer("audit", new byte[] { 1, 2, 3, 4 }, ResourceStates.CopySource);
        var readback = context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer(4), ResourceStates.CopyDest));
        context.Commands.CopyBufferRegion(readback, 0, first, 0, 4);
        try
        {
            var second = cache.DynamicBuffer("audit", new byte[] { 5, 6, 7, 8 }, ResourceStates.CopySource);
            Assert.NotEqual(IntPtr.Zero, first.NativePointer);
            Assert.NotEqual(first.NativePointer, second.NativePointer);
            runtime.Wait(runtime.Submit());
            var bytes = readback.Map<byte>(0, 4);
            try { Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes.ToArray()); }
            finally { readback.Unmap(0); }
            runtime.WaitForIdle();
            Assert.Equal(IntPtr.Zero, first.NativePointer);
            Assert.NotEqual(IntPtr.Zero, second.NativePointer);
        }
        finally { runtime.Abort(); }
    }

    private sealed class DisposalProbe : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
