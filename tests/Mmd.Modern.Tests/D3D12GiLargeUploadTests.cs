using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12GiLargeUploadTests
{
    private const int LargeBytes = 16 * 1024 * 1024 + 4;

    [Fact]
    public void ChangedGiTrianglesReuseThreeMappingsAndKeepEveryQueuedPayloadExact()
    {
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        var reads = new List<(ID3D12Resource Read, byte[] Expected)>();
        try
        {
            for (int frame = 0; frame < 12; frame++)
            {
                runtime.Begin(); cache.BeginFrame(true);
                byte[] bytes = Payload(LargeBytes, frame);
                var gpu = cache.BufferByIdentity("gi:triangles", bytes, ResourceStates.CopySource, new object());
                reads.Add((Copy(runtime, gpu, bytes.Length), bytes)); runtime.Submit();
                if (frame >= 2) Assert.Equal(3, cache.BufferUploadStatistics.LargePageCreations);
            }
            runtime.WaitForIdle();
            foreach (var read in reads) Assert.Equal(read.Expected, Read(read.Read, read.Expected.Length));
            var stats = cache.BufferUploadStatistics;
            Assert.Equal(3, stats.RetainedLargePages);
            Assert.Equal(3L * LargeBytes, stats.RetainedLargeBytes);
            Assert.Equal(12L * LargeBytes, stats.CopiedBytes);
            Assert.Equal(0, stats.LiveTransientPages);
            using var diagnostic = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(stats));
            Assert.Equal(3, diagnostic.RootElement.GetProperty("RetainedLargePages").GetInt32());
            Assert.Equal(3L * LargeBytes, diagnostic.RootElement.GetProperty("RetainedLargeBytes").GetInt64());
        }
        finally { runtime.Abort(); runtime.WaitForIdle(); foreach (var read in reads) read.Read.Dispose(); }
    }

    [Fact]
    public void ParallelUploadCopiesWholeBlocksAndUnalignedTailExactly()
    {
        using var runtime = new D3D12GpuRuntime(); using var cache = new D3D12GpuCache(runtime);
        byte[] bytes = new byte[32 * 1024 * 1024 + 12];
        new Random(173).NextBytes(bytes);
        runtime.Begin();
        var gpu = cache.BufferByIdentity("gi:triangles", bytes, ResourceStates.CopySource, new object());
        using var read = Copy(runtime, gpu, bytes.Length);
        runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(bytes, Read(read, bytes.Length));
    }

    [Fact]
    public void TwoLargeUploadsInOneListKeepDistinctMappedSources()
    {
        using var runtime = new D3D12GpuRuntime(); using var cache = new D3D12GpuCache(runtime);
        runtime.Begin();
        byte[] first = Payload(LargeBytes, 1), second = Payload(LargeBytes, 2);
        var a = cache.BufferByIdentity("gi:triangles", first, ResourceStates.CopySource, new object());
        var b = cache.BufferByIdentity("gi:triangles", second, ResourceStates.CopySource, new object());
        using var ar = Copy(runtime, a, first.Length); using var br = Copy(runtime, b, second.Length);
        Assert.Equal(1, cache.BufferUploadStatistics.RetainedLargePages);
        Assert.Equal(1, cache.BufferUploadStatistics.LiveTransientPages);
        runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(first, Read(ar, first.Length)); Assert.Equal(second, Read(br, second.Length));
    }

    [Fact]
    public void AbortedWritesReuseTheSameMappingAfterTheSlotReturns()
    {
        using var runtime = new D3D12GpuRuntime(); using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin(); staging.WriteLargeRetained(Payload(LargeBytes, 1)); runtime.Abort();
        for (int i = 0; i < 2; i++) { runtime.Begin(); runtime.Submit(); }
        runtime.Begin(); staging.WriteLargeRetained(Payload(LargeBytes, 2));
        Assert.Equal(1, staging.Statistics.LargePageCreations);
        runtime.Abort(); Assert.Equal(1, staging.Statistics.LiveLargePages);
    }

    [Fact]
    public void ResizeDropsAllIncompatibleOwnersWithoutDroppingTheirQueuedCopies()
    {
        using var runtime = new D3D12GpuRuntime(); using var cache = new D3D12GpuCache(runtime);
        var reads = new List<(ID3D12Resource Read, byte[] Expected)>();
        try
        {
            foreach (int length in new[] { LargeBytes, LargeBytes, LargeBytes, LargeBytes + 4 })
            {
                runtime.Begin(); var bytes = Payload(length, reads.Count);
                var gpu = cache.BufferByIdentity("gi:triangles", bytes, ResourceStates.CopySource, new object());
                reads.Add((Copy(runtime, gpu, length), bytes)); runtime.Submit();
            }
            Assert.Equal(1, cache.BufferUploadStatistics.RetainedLargePages);
            runtime.WaitForIdle();
            foreach (var read in reads) Assert.Equal(read.Expected, Read(read.Read, read.Expected.Length));
            Assert.Equal(1, cache.BufferUploadStatistics.LiveLargePages);
        }
        finally { runtime.Abort(); runtime.WaitForIdle(); foreach (var read in reads) read.Read.Dispose(); }
    }

    [Theory]
    [InlineData(16 * 1024 * 1024, 0)]
    [InlineData(128 * 1024 * 1024 + 1, 1)]
    public void SmallOrOversizedInputsReleaseTheLargeCacheAndUseTheEstablishedPolicy(int bytes, int transients)
    {
        using var runtime = new D3D12GpuRuntime(); using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin(); staging.WriteLargeRetained(new byte[LargeBytes]); runtime.Submit();
        runtime.Begin(); staging.WriteLargeRetained(new byte[bytes]);
        Assert.Equal(0, staging.Statistics.RetainedLargePages);
        Assert.Equal(transients, staging.Statistics.LiveTransientPages);
        runtime.Abort(); runtime.WaitForIdle(); Assert.Equal(0, staging.Statistics.LiveLargePages);
    }

    [Fact]
    public void UnusedLargePagesRetireEvenWhenAFrameDoesNotUploadAnyBytes()
    {
        using var runtime = new D3D12GpuRuntime(); using var staging = new D3D12BufferUploadStaging(runtime);
        for (int i = 0; i < 3; i++) { runtime.Begin(); staging.WriteLargeRetained(new byte[LargeBytes]); runtime.Submit(); }
        for (int i = 0; i < 6; i++) { runtime.Begin(); staging.BeginFrame(); runtime.Submit(); }
        runtime.WaitForIdle(); Assert.Equal(0, staging.Statistics.RetainedLargePages);
        Assert.Equal(0, staging.Statistics.LiveLargePages);
    }

    [Fact]
    public void HeldGeometryCacheHitsAlsoVisitAndReleaseUnusedUploadPages()
    {
        using var runtime = new D3D12GpuRuntime(); using var cache = new D3D12GpuCache(runtime);
        object identity = new(); ID3D12Resource? held = null;
        for (int i = 0; i < 3; i++)
        {
            runtime.Begin(); cache.BeginFrame(true);
            held = cache.BufferByIdentity("gi:triangles", new byte[LargeBytes], ResourceStates.CopySource, i == 2 ? identity : new object());
            runtime.Submit();
        }
        for (int i = 0; i < 6; i++)
        {
            runtime.Begin(); cache.BeginFrame(true);
            Assert.Same(held, cache.BufferByIdentity("gi:triangles", new byte[LargeBytes], ResourceStates.CopySource, identity));
            runtime.Submit();
        }
        runtime.WaitForIdle(); Assert.Equal(0, cache.BufferUploadStatistics.RetainedLargePages);
        Assert.Equal(3, cache.BufferUploadStatistics.Copies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameListClearOrResizeDoesNotPermitOverwritingAnEarlierCopySource(bool resize)
    {
        using var runtime = new D3D12GpuRuntime(); using var cache = new D3D12GpuCache(runtime);
        runtime.Begin(); var a = Payload(LargeBytes, 3); var b = Payload(resize ? LargeBytes + 4 : LargeBytes, 4);
        var first = cache.BufferByIdentity("gi:triangles", a, ResourceStates.CopySource, new object());
        if (!resize) cache.BufferByIdentity("gi:triangles", new byte[32], ResourceStates.CopySource, new object());
        var second = cache.BufferByIdentity("gi:triangles", b, ResourceStates.CopySource, new object());
        using var ar = Copy(runtime, first, a.Length); using var br = Copy(runtime, second, b.Length);
        Assert.Equal(1, cache.BufferUploadStatistics.LargePageCreations);
        Assert.Equal(0, cache.BufferUploadStatistics.RetainedLargePages);
        Assert.Equal(1, cache.BufferUploadStatistics.LiveTransientPages);
        runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(a, Read(ar, a.Length)); Assert.Equal(b, Read(br, b.Length));
    }

    [Fact]
    public void TheExactLargeRetentionLimitIsAccepted()
    {
        using var runtime = new D3D12GpuRuntime(); using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin(); staging.WriteLargeRetained(new byte[128 * 1024 * 1024]);
        Assert.Equal(128L * 1024 * 1024, staging.Statistics.RetainedLargeBytes);
        Assert.Equal(0, staging.Statistics.LiveTransientPages); runtime.Abort();
    }

    [Fact]
    public void DisabledRetentionDoesNotReappearDuringThatFramesGiUploads()
    {
        using var runtime = new D3D12GpuRuntime(); using var cache = new D3D12GpuCache(runtime);
        runtime.Begin(); cache.BeginFrame(false);
        var first = cache.BufferByIdentity("gi:triangles", new byte[LargeBytes], ResourceStates.CopySource, new object());
        Assert.Equal(0, cache.BufferUploadStatistics.RetainedLargePages);
        Assert.Equal(1, cache.BufferUploadStatistics.LiveTransientPages);
        runtime.Submit();
        runtime.Begin(); cache.BeginFrame(true);
        cache.BufferByIdentity("gi:triangles", new byte[LargeBytes], ResourceStates.CopySource, new object());
        Assert.Equal(1, cache.BufferUploadStatistics.RetainedLargePages);
        runtime.Abort(); runtime.WaitForIdle();
    }

    [Fact]
    public void GiDisableAndCacheDisposalKeepUsedUploadsAliveUntilFenceRetirement()
    {
        using var runtime = new D3D12GpuRuntime(); using var cache = new D3D12GpuCache(runtime);
        runtime.Begin(); byte[] bytes = Payload(LargeBytes, 8);
        var gpu = cache.BufferByIdentity("gi:triangles", bytes, ResourceStates.CopySource, new object());
        using var read = Copy(runtime, gpu, bytes.Length);
        cache.BeginFrame(false);
        Assert.Equal(0, cache.BufferUploadStatistics.RetainedLargePages);
        Assert.Equal(1, cache.BufferUploadStatistics.LiveLargePages);
        cache.Dispose(); runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(0, cache.BufferUploadStatistics.LiveLargePages);
        Assert.Equal(bytes, Read(read, bytes.Length));
    }

    private static byte[] Payload(int bytes, int seed)
    {
        var result = new byte[bytes];
        for (int i = 0; i < result.Length; i++) result[i] = unchecked((byte)(i * 17 + seed * 31));
        return result;
    }
    private static ID3D12Resource Copy(D3D12GpuRuntime runtime, ID3D12Resource source, int bytes)
    {
        var read = runtime.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer((ulong)bytes), ResourceStates.CopyDest);
        runtime.Commands.CopyBufferRegion(read, 0, source, 0, (ulong)bytes); return read;
    }
    private static byte[] Read(ID3D12Resource read, int bytes)
    {
        try { return read.Map<byte>(0, bytes).ToArray(); } finally { read.Unmap(0); }
    }
}
