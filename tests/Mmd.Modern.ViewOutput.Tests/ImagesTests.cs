using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput.Images;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ImagesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task PngPreservesPaddedRgbaRowsAlphaAndSrgbAndPublishesOnlyOnComplete()
    {
        using var directory = new ImageTestDirectory();
        string target = Path.Combine(directory.Path, "still.png");
        await File.WriteAllTextAsync(target, "old", Token);
        await using var sink = new PngFrameSink(target);
        var request = new ExportRequest(new(2, 2, new(30), 0, 0));
        await sink.BeginAsync(request, Token);
        using var lease = new FrameBufferLease(2, 2, 12, new byte[] { 255,0,0,128,0,255,0,0,91,92,93,94,0,0,255,255,40,50,60,64,95,96,97,98 });
        await sink.WriteAsync(request.Plan.GetSample(0), lease, Token);
        Assert.Equal("old", await File.ReadAllTextAsync(target, Token));
        await sink.CompleteAsync(Token);
        var decoded = Decode(await File.ReadAllBytesAsync(target, Token));
        Assert.Equal(2, decoded.Width); Assert.Equal(2, decoded.Height); Assert.True(decoded.Srgb);
        Assert.Equal(new byte[] {255,0,0,128,0,255,0,0,0,0,255,255,40,50,60,64}, decoded.Pixels);
        Assert.False(lease.IsDisposed);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task SequenceRecordsExactRationalSamplesAndCannotReplaceOldDirectory()
    {
        using var directory = new ImageTestDirectory();
        string target = Path.Combine(directory.Path, "sequence");
        var request = new ExportRequest(new(1, 1, new(30000, 1001), 3, 5));
        await using (var sink = new PngSequenceSink(target))
        {
            await sink.BeginAsync(request, Token);
            using var frame = new FrameBufferLease(1, 1, 4, new byte[4]);
            for (long i = 0; i < request.Plan.FrameCount; i++) await sink.WriteAsync(request.Plan.GetSample(i), frame, Token);
            Assert.False(Directory.Exists(target));
            await sink.CompleteAsync(Token);
        }
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(target, "manifest.json"), Token));
        var frames = manifest.RootElement.GetProperty("frames");
        Assert.Equal(request.Plan.FrameCount, frames.GetArrayLength());
        Assert.Equal("1001", frames[1].GetProperty("presentationTime").GetProperty("numerator").GetString());
        Assert.Equal("30000", frames[1].GetProperty("presentationTime").GetProperty("denominator").GetString());
        Assert.Equal("frame-00000001.png", frames[1].GetProperty("file").GetString());
        await using var collision = new PngSequenceSink(target);
        await Assert.ThrowsAsync<IOException>(async () => await collision.BeginAsync(request, Token));
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
    }

    [Fact]
    public async Task InvalidOrderAndCancellationDiscardNewJobAndPreserveOldFile()
    {
        using var directory = new ImageTestDirectory();
        var request = new ExportRequest(new(1, 1, new(30), 0, 2));
        string target = Path.Combine(directory.Path, "sequence");
        await using (var sink = new PngSequenceSink(target))
        {
            await sink.BeginAsync(request, Token);
            using var frame = new FrameBufferLease(1, 1, 4, new byte[4]);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.WriteAsync(request.Plan.GetSample(1), frame, Token));
            await sink.AbortAsync(null, Token); await sink.AbortAsync(null, Token);
        }
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        string still = Path.Combine(directory.Path, "old.png"); await File.WriteAllTextAsync(still, "old", Token);
        await using (var sink = new PngFrameSink(still))
        {
            var single = new ExportRequest(new(1, 1, new(30), 0, 0));
            await sink.BeginAsync(single, Token);
            using var frame = new FrameBufferLease(1, 1, 4, new byte[4]);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sink.WriteAsync(single.Plan.GetSample(0), frame, cancelled.Token));
        }
        Assert.Equal("old", await File.ReadAllTextAsync(still, Token)); Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task IncompleteOrWrongSizedImageCannotPublish()
    {
        using var directory = new ImageTestDirectory();
        await using var sink = new PngSequenceSink(Path.Combine(directory.Path, "sequence"));
        var request = new ExportRequest(new(2, 1, new(30), 0, 1)); await sink.BeginAsync(request, Token);
        using var wrong = new FrameBufferLease(1, 1, 4, new byte[4]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.WriteAsync(request.Plan.GetSample(0), wrong, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.CompleteAsync(Token));
    }

    // Independent PNG chunk reader, CRC verifier and zlib decoder for the emitted noninterlaced filter-0 subset.
    private static (int Width, int Height, bool Srgb, byte[] Pixels) Decode(byte[] png)
    {
        Assert.Equal(new byte[] {137,80,78,71,13,10,26,10}, png[..8]);
        int width = 0, height = 0; bool srgb = false; using var compressed = new MemoryStream();
        for (int offset = 8; offset < png.Length;)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, count);
            uint crc = uint.MaxValue;
            foreach (byte b in png.AsSpan(offset + 4, count + 4))
            { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
            Assert.Equal(~crc, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + count)));
            if (type == "IHDR") { width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]); Assert.Equal(8, data[8]); Assert.Equal(6, data[9]); }
            if (type == "sRGB") srgb = true;
            if (type == "IDAT") compressed.Write(data);
            offset += count + 12;
        }
        compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress); using var raw = new MemoryStream(); zlib.CopyTo(raw);
        var scanlines = raw.ToArray(); var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++) { Assert.Equal(0, scanlines[row * (width * 4 + 1)]); scanlines.AsSpan(row * (width * 4 + 1) + 1, width * 4).CopyTo(pixels.AsSpan(row * width * 4)); }
        return (width, height, srgb, pixels);
    }

    private sealed class ImageTestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "mmd-view-images-" + Guid.NewGuid().ToString("N"));
        public ImageTestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
