using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12CaptureTimingTests
{
    [Fact]
    public void ConvertsActualQueueTicksToMilliseconds()
        => Assert.Equal(1.5, D3D12CaptureTiming.ToMilliseconds(4_000, 7_000, 2_000_000));

    [Fact]
    public void RejectsZeroQueueFrequencyInsteadOfFabricatingZero()
        => Assert.Throws<ArgumentOutOfRangeException>(() => D3D12CaptureTiming.ToMilliseconds(4, 7, 0));

    [Fact]
    public void RejectsReversedTimestampTicks()
        => Assert.Throws<InvalidOperationException>(() => D3D12CaptureTiming.ToMilliseconds(8, 7, 1_000));

    [Fact]
    public void EndBeforeBeginIsRejected()
    {
        var state = new D3D12CaptureTimingState(3, 7);
        Assert.Throws<InvalidOperationException>(() => state.End());
    }

    [Fact]
    public void ReadBeforeEndIsRejectedEvenIfFenceCompleted()
    {
        var state = new D3D12CaptureTimingState(3, 7);
        state.Begin();
        Assert.Throws<InvalidOperationException>(() => state.RequireReadable(3, false, 7, 7));
    }

    [Fact]
    public void ReadBeforeFrameFenceCompletionIsRejected()
    {
        var state = new D3D12CaptureTimingState(3, 7);
        state.Begin();
        state.End();
        Assert.Throws<InvalidOperationException>(() => state.RequireReadable(3, false, 7, 6));
    }

    [Fact]
    public void ReadOfUnsubmittedOrReusedFrameIsRejected()
    {
        var state = new D3D12CaptureTimingState(3, 7);
        state.Begin();
        state.End();
        Assert.Throws<InvalidOperationException>(() => state.RequireReadable(3, false, 6, 6));
        Assert.Throws<InvalidOperationException>(() => state.RequireReadable(4, false, 7, 7));
        Assert.Throws<InvalidOperationException>(() => state.RequireReadable(3, true, 7, 7));
    }

    [Fact]
    public void ValidCompletedFrameIsReadableButDuplicateBeginAndEndAreRejected()
    {
        var state = new D3D12CaptureTimingState(3, 7);
        state.Begin();
        Assert.Throws<InvalidOperationException>(() => state.Begin());
        state.End();
        Assert.Throws<InvalidOperationException>(() => state.End());
        state.RequireReadable(3, false, 7, 7);
    }
}
