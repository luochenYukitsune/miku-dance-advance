using System.Buffers.Binary;
using System.IO.Compression;

namespace Mmd.Modern.ViewOutput.Images;

/// <summary>Streaming PNG RGBA8 encoder. Uses the runtime zlib implementation; IDAT buffering is bounded to 64KiB.</summary>
internal static class PngEncoder
{
    public static async ValueTask WriteAsync(string path, FrameBufferLease frame, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        output.Write(new byte[] {137,80,78,71,13,10,26,10});
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), frame.Height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR"u8, header); Chunk(output, "sRGB"u8, new byte[] {0});
        using (var chunks = new IdatStream(output))
        {
            using (var zlib = new ZLibStream(chunks, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int row = 0; row < frame.Height; row++)
                {
                    token.ThrowIfCancellationRequested();
                    await zlib.WriteAsync(new byte[] {0}, token).ConfigureAwait(false);
                    await zlib.WriteAsync(frame.Pixels.Slice(row * frame.Stride, frame.Width * 4), token).ConfigureAwait(false);
                }
            }
        }
        token.ThrowIfCancellationRequested(); Chunk(output, "IEND"u8, []);
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> integer = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(integer, data.Length); output.Write(integer);
        output.Write(type); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in type) crc = Update(crc, value);
        foreach (byte value in data) crc = Update(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc); output.Write(integer);
    }
    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        return crc;
    }

    private sealed class IdatStream(Stream output) : Stream
    {
        private readonly byte[] buffer = new byte[65536];
        private int count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { if (count > 0) { Chunk(output, "IDAT"u8, buffer.AsSpan(0, count)); count = 0; } }
        public override void Write(byte[] data, int offset, int length) => Write(data.AsSpan(offset, length));
        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int length = Math.Min(buffer.Length - count, data.Length); data[..length].CopyTo(buffer.AsSpan(count));
                count += length; data = data[length..]; if (count == buffer.Length) Flush();
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) Flush(); base.Dispose(disposing); }
        public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
