using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12BufferUploadPackingTests
{
    private const int MiB = 1024 * 1024;
    private const int PositionBytes = 439532 * 36;

    [Fact]
    public void WarmFullPoolsReuseEarlierTailsForSmallCopiesAfterThePositionUpload()
    {
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        var reads = new List<QueuedCopy>();
        try
        {
            Warm(runtime, staging, [16 * MiB, 16 * MiB]);
            // A 1MiB header leaves less room than the actual 15,823,152B DXR payload.
            // Odd lengths also require separate four-byte-aligned source ranges.
            int[] lengths = [MiB + 1, PositionBytes, .. Enumerable.Repeat(MiB - 3, 12)];
            Assert.True(lengths.Sum() < D3D12BufferUploadStaging.MaximumRetainedBytesPerSlot);
            for (int frame = 0; frame < 6; frame++)
            {
                runtime.Begin();
                reads.Add(Record(runtime, staging, lengths, frame));
                runtime.Submit();
            }
            runtime.WaitForIdle();
            foreach (var read in reads) AssertExact(read);
            Assert.Equal(6, staging.Statistics.PageCreations);
            Assert.Equal(6, staging.Statistics.PageMappings);
            Assert.Equal(0, staging.Statistics.LiveTransientPages);
            Assert.Equal(3L * D3D12BufferUploadStaging.MaximumRetainedBytesPerSlot, staging.Statistics.RetainedBytes);
        }
        finally { runtime.Abort(); runtime.WaitForIdle(); foreach (var read in reads) read.Resource.Dispose(); }
    }

    [Fact]
    public void ReusedTailsKeepTheExactPageAndSlotLimitsAndExcessCopiesStayIndependent()
    {
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        Warm(runtime, staging, [16 * MiB, 16 * MiB]);
        runtime.Begin();
        // The first sixteen writes fill exactly two 16MiB pages, including both tails.
        int[] lengths = [MiB, PositionBytes, .. Enumerable.Repeat(MiB, 12),
            16 * MiB - PositionBytes, 3 * MiB, 4, 16 * MiB + 1];
        using var read = Record(runtime, staging, lengths, 11);
        Assert.Equal(16 * MiB, D3D12BufferUploadStaging.MaximumRetainedPageBytes);
        Assert.Equal(32L * MiB, D3D12BufferUploadStaging.MaximumRetainedBytesPerSlot);
        Assert.Equal(3L * 32 * MiB, staging.Statistics.RetainedBytes);
        Assert.Equal(0, staging.Statistics.RetainedLargePages);
        Assert.Equal(2, staging.Statistics.LiveTransientPages);
        Assert.Equal(8, staging.Statistics.PageCreations);
        runtime.Submit(); runtime.WaitForIdle();
        AssertExact(read);
        Assert.Equal(0, staging.Statistics.LiveTransientPages);
    }

    [Fact]
    public void ATransientRequestDoesNotDiscardEarlierTailsNeededByLaterCopies()
    {
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        Warm(runtime, staging, [16 * MiB, 16 * MiB]);
        runtime.Begin();
        // 13MiB fits neither current tail; the later 8MiB still fits the first page.
        using var read = Record(runtime, staging, [4 * MiB, PositionBytes, 13 * MiB, 8 * MiB], 23);
        Assert.Equal(1, staging.Statistics.LiveTransientPages);
        Assert.Equal(7, staging.Statistics.PageCreations);
        Assert.Equal(3L * 32 * MiB, staging.Statistics.RetainedBytes);
        runtime.Submit(); runtime.WaitForIdle(); AssertExact(read);
    }

    [Fact]
    public void AbortedPackingResetsBothCursorsWhenItsFenceSlotReturns()
    {
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        Warm(runtime, staging, [16 * MiB, 16 * MiB]);
        // This nearly fills both pages; resetting only one cursor would exhaust
        // the other page's tail and force a new mapping during recovery.
        int[] lengths = [MiB + 1, PositionBytes, .. Enumerable.Repeat(MiB - 3, 12)];
        runtime.Begin();
        using var aborted = Record(runtime, staging, lengths, 31);
        runtime.Abort();
        for (int i = 0; i < 2; i++) { runtime.Begin(); staging.BeginFrame(); runtime.Submit(); }
        runtime.Begin();
        using var recovered = Record(runtime, staging, lengths, 37);
        runtime.Submit(); runtime.WaitForIdle(); AssertExact(recovered);
        Assert.Equal(6, staging.Statistics.PageCreations);
        Assert.Equal(6, staging.Statistics.PageMappings);
        Assert.Equal(0, staging.Statistics.LiveTransientPages);
    }

    [Fact]
    public void BestFitUsesTheSmallerPageAndTrimsAnUnusedPageBeforeIt()
    {
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        var reads = new List<QueuedCopy>();
        try
        {
            Warm(runtime, staging, [16 * MiB, MiB]);
            for (int frame = 0; frame < 6; frame++)
            {
                runtime.Begin(); reads.Add(Record(runtime, staging, [513], 41 + frame)); runtime.Submit();
            }
            runtime.WaitForIdle(); foreach (var read in reads) AssertExact(read);
            Assert.Equal(3, staging.Statistics.RetainedPages);
            Assert.Equal(3L * MiB, staging.Statistics.RetainedBytes);
            Assert.Equal(6, staging.Statistics.PageMappings);
            Assert.Equal(0, staging.Statistics.LiveTransientPages);
        }
        finally { runtime.Abort(); runtime.WaitForIdle(); foreach (var read in reads) read.Resource.Dispose(); }
    }

    [Fact]
    public void ReplacingAnUnusedPageAndDisposingThePoolPreserveEveryQueuedSource()
    {
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin(); staging.Write(new byte[MiB]); staging.Write(new byte[MiB]); runtime.Submit();
        for (int i = 0; i < 2; i++) { runtime.Begin(); runtime.Submit(); }
        runtime.Begin();
        using var read = Record(runtime, staging, [513, 16 * MiB, 1021], 53);
        Assert.Equal(17L * MiB, staging.Statistics.RetainedBytes);
        Assert.Equal(3, staging.Statistics.PageCreations);
        Assert.Equal(0, staging.Statistics.LiveTransientPages);
        staging.Dispose();
        Assert.Equal(0, staging.Statistics.RetainedPages);
        Assert.Equal(2, staging.Statistics.LivePages);
        runtime.Submit(); runtime.WaitForIdle();
        AssertExact(read); Assert.Equal(0, staging.Statistics.LivePages);
    }

    private sealed record QueuedCopy(ID3D12Resource Resource, byte[] Expected) : IDisposable
    {
        public void Dispose() => Resource.Dispose();
    }

    private static void Warm(D3D12GpuRuntime runtime, D3D12BufferUploadStaging staging, int[] lengths)
    {
        var bytes = new byte[lengths.Max()];
        for (int slot = 0; slot < 3; slot++)
        {
            runtime.Begin();
            foreach (int length in lengths) staging.Write(bytes.AsSpan(0, length));
            runtime.Submit();
        }
    }

    private static QueuedCopy Record(D3D12GpuRuntime runtime, D3D12BufferUploadStaging staging, int[] lengths, int seed)
    {
        int total = lengths.Sum(); var expected = new byte[total];
        var read = runtime.Device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer((ulong)total), ResourceStates.CopyDest);
        try
        {
            int destination = 0;
            for (int part = 0; part < lengths.Length; part++)
            {
                var payload = new byte[lengths[part]];
                for (int i = 0; i < payload.Length; i++) payload[i] = unchecked((byte)(i * 17 + seed * 31 + part * 43));
                var source = staging.Write(payload);
                Assert.Equal(0UL, source.Offset & 3UL);
                runtime.Commands.CopyBufferRegion(read, (ulong)destination, source.Resource, source.Offset, (ulong)payload.Length);
                payload.CopyTo(expected, destination); destination += payload.Length;
            }
            return new(read, expected);
        }
        catch { read.Dispose(); throw; }
    }

    private static void AssertExact(QueuedCopy read)
    {
        var data = read.Resource.Map<byte>(0, read.Expected.Length);
        try { Assert.True(read.Expected.AsSpan().SequenceEqual(data), "Queued buffer-copy source ranges must remain byte-exact."); }
        finally { read.Resource.Unmap(0); }
    }
}
