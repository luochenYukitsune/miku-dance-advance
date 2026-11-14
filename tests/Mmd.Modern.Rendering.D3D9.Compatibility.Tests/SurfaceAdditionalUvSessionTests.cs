using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Fake API and managed delegates only; no native library or GPU is loaded.
public sealed class SurfaceAdditionalUvSessionTests
{
    [Fact]
    public void AdditionalUvSnapshotFreezesTheV2TailAndItsDigestCoversEveryByte()
    {
        var (frame, visual) = UvPackets();
        var snapshot = Copy(frame, visual);
        frame[^1] ^= 1; visual[20] ^= 1;
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.FrameBytes.AsSpan(8)));
        Assert.NotEqual(frame[^1], snapshot.FrameBytes[^1]);
        var bad = UvPackets(); bad.Frame[^1] ^= 1;
        Assert.Contains("not bound", Assert.Throws<ArgumentException>(() => Copy(bad.Frame, bad.Visual)).Message);
    }

    [Fact]
    public async Task AdditionalUvOldDllRejectsBeforeCaptureAndTheLaneStillCapturesLegacyPackets()
    {
        var fake = new UvFakeApi(1);
        await using var session = await ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => fake, new(4, 2), TestContext.Current.CancellationToken);
        var packets = UvPackets();
        await Assert.ThrowsAsync<NotSupportedException>(() => session.CaptureAsync(Copy(packets.Frame, packets.Visual), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(fake.Captures);
        await session.CaptureAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1U }, fake.Captures.Select(call => call.Version));
    }

    [Fact]
    public async Task AdditionalUvCapableDllAcceptsBothPacketVersionsOnItsExistingOwnerThread()
    {
        var fake = new UvFakeApi(3);
        var session = await ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => fake, new(4, 2), TestContext.Current.CancellationToken);
        var packets = UvPackets();
        await session.CaptureAsync(Copy(packets.Frame, packets.Visual), cancellationToken: TestContext.Current.CancellationToken);
        await session.CaptureAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await session.DisposeAsync();
        Assert.Equal(new[] { 2U, 1U }, fake.Captures.Select(call => call.Version));
        Assert.Single(fake.Threads.Distinct());
        Assert.Equal(1, fake.QueryCount);
        Assert.Equal(1, fake.DestroyCount);
        Assert.Equal(1, fake.UnloadCount);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(3U)]
    [InlineData(uint.MaxValue)]
    public void AdditionalUvDoesNotAcceptUnknownFrameVersions(uint version)
    {
        var packets = UvPackets(); BinaryPrimitives.WriteUInt32LittleEndian(packets.Frame.AsSpan(8), version);
        SHA256.HashData(packets.Frame).CopyTo(packets.Visual.AsSpan(20));
        Assert.Throws<ArgumentException>(() => Copy(packets.Frame, packets.Visual));
    }

    [Fact]
    public void SubTextureSnapshotAcceptsVis3OnlyWhenBoundToAnAdditionalUvFrame()
    {
        var packets = UvPackets(); BinaryPrimitives.WriteUInt32LittleEndian(packets.Visual.AsSpan(8), 3);
        var snapshot = Copy(packets.Frame, packets.Visual);
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.VisualBytes.AsSpan(8)));
        var legacy = SurfaceSessionTests.Packets(); BinaryPrimitives.WriteUInt32LittleEndian(legacy.Visual.AsSpan(8), 3);
        Assert.Throws<ArgumentException>(() => Copy(legacy.Frame, legacy.Visual));
        packets.Frame[^1] ^= 1;
        Assert.Contains("not bound", Assert.Throws<ArgumentException>(() => Copy(packets.Frame, packets.Visual)).Message);
    }

    internal static (byte[] Frame, byte[] Visual) UvPackets()
    {
        var packets = SurfaceSessionTests.Packets();
        var frame = new byte[packets.Frame.Length + 8]; packets.Frame.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(packets.Frame.Length), 1);
        // An empty isolated scene still has an explicit channel declaration and a matching zero vertex count.
        SHA256.HashData(frame).CopyTo(packets.Visual.AsSpan(20));
        return (frame, packets.Visual);
    }

    internal static D3D9SurfaceSnapshot Copy(byte[] frame, byte[] visual) =>
        D3D9SurfaceSnapshot.CopyIsolatedSurface(frame, visual, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm);

    private sealed class UvFakeApi(ulong features) : ID3D9SurfaceNativeApi
    {
        internal readonly ConcurrentQueue<(uint Version, int Thread)> Captures = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal int QueryCount, DestroyCount, UnloadCount;
        private void Record() => Threads.Enqueue(Environment.CurrentManagedThreadId);
        public NativeAbi QueryAbi() { Record(); QueryCount++; return NativeAbi.Valid() with { SupportedFeatures = features }; }
        public ulong Create(D3D9SurfaceSessionOptions options) { Record(); return 1; }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
        {
            Record(); Captures.Enqueue((BinaryPrimitives.ReadUInt32LittleEndian(snapshot.FrameBytes.AsSpan(8)), Environment.CurrentManagedThreadId));
            return new(snapshot.Width, snapshot.Height, new byte[snapshot.Width * snapshot.Height * 4],
                new NativeCaptureResult { SourceFrame = snapshot.SourceFrame });
        }
        public void Resize(ulong handle, int width, int height) => Record();
        public void Destroy(ulong handle) { Record(); DestroyCount++; }
        public void Dispose() { Record(); UnloadCount++; }
    }
}
