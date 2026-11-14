// DirectX chunk framing: Microsoft MS-MCI, inspected Assimp/Wine behavior.
// DEFLATE is delegated to SharpZipLib 1.4.2 (MIT) and System.IO.Compression.
// No decompression algorithm or third-party parser code is copied here.
using System.Buffers.Binary;
using System.IO.Compression;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Mmd.Modern.Formats;

internal static class XMszipReader
{
    internal static byte[] Decode(byte[] input, XTextReadOptions options)
    {
        if (input.Length < 20) throw Invalid("Truncated compressed master header.");
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(16, 4));
        if (total < 16 || total > options.MaxDecompressedBytes) throw Invalid("Decoded file exceeds the decompression size limit.");
        byte[] output = new byte[(int)total - 16];
        int position = 20, written = 0, chunks = 0;
        while (position < input.Length)
        {
            if (++chunks > options.MaxCompressedChunks) throw Invalid("Compressed chunk count limit exceeded.");
            if (input.Length - position < 4) throw Invalid("Truncated compressed chunk header.");
            int decoded = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(position, 2));
            int compressed = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(position + 2, 2)); position += 4;
            if (decoded is < 1 or > 32768 || compressed is < 3 or > 32780 || compressed > input.Length - position)
                throw Invalid("Invalid or truncated MSZIP chunk size.");
            if (decoded > output.Length - written) throw Invalid("Chunk exceeds the declared decoded file length.");
            if (input[position] != 'C' || input[position + 1] != 'K') throw Invalid("Missing MSZIP CK signature.");

            int history = Math.Min(written, 32768);
            int seedLength = history == 0 ? 0 : history + 5;
            byte[] seeded = new byte[seedLength + compressed - 2];
            if (history != 0)
            {
                // Nonfinal stored block, byte aligned. It fills the mature decoder's
                // history window before the original final raw stream is parsed.
                seeded[0] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(seeded.AsSpan(1, 2), (ushort)history);
                BinaryPrimitives.WriteUInt16LittleEndian(seeded.AsSpan(3, 2), (ushort)~history);
                output.AsSpan(written - history, history).CopyTo(seeded.AsSpan(5, history));
            }
            input.AsSpan(position + 2, compressed - 2).CopyTo(seeded.AsSpan(seedLength));
            byte[] block = Inflate(seeded, history + decoded);
            // SharpZipLib's OutputWindow permits references to its zero-initialized
            // window before produced history. Native DEFLATE validates that case.
            // Once the full 32KiB history exists every legal distance is available.
            if (history < 32768) ValidateHistory(seeded, block);
            block.AsSpan(history, decoded).CopyTo(output.AsSpan(written, decoded));
            written += decoded; position += compressed;
        }
        if (written != output.Length || chunks == 0) throw Invalid("Decoded file length differs from the master header.");
        return output;
    }

    private static byte[] Inflate(byte[] input, int expected)
    {
        var inflater = new Inflater(noHeader: true); inflater.SetInput(input);
        // One extra byte exposes expansion past the declaration without unbounded allocation.
        byte[] output = new byte[expected + 1]; int written = 0;
        try
        {
            while (!inflater.IsFinished)
            {
                int count = inflater.Inflate(output, written, output.Length - written); written += count;
                if (written > expected) throw Invalid("Chunk inflates past its declared output size.");
                if (count == 0 && !inflater.IsFinished) throw Invalid("Truncated or unfinished DEFLATE stream.");
            }
        }
        catch (Exception error) when (error is SharpZipBaseException or FormatException or InvalidOperationException)
        { throw new InvalidDataException(".x MSZIP has invalid DEFLATE data.", error); }
        if (written != expected || inflater.RemainingInput != 0 || inflater.TotalIn != input.Length)
            throw Invalid("DEFLATE stream output size or input boundary mismatch.");
        return output;
    }

    private static void ValidateHistory(byte[] input, byte[] expected)
    {
        using var stream = new MemoryStream(input, writable: false);
        using var inflater = new DeflateStream(stream, CompressionMode.Decompress);
        byte[] buffer = new byte[4096]; int offset = 0;
        while (true)
        {
            int count = inflater.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
            if (count == 0) break;
            if (!buffer.AsSpan(0, count).SequenceEqual(expected.AsSpan(offset, count))) throw Invalid("DEFLATE history validation differs.");
            offset += count;
            if (offset == expected.Length) throw Invalid("DEFLATE history validation exceeds output size.");
        }
        // expected includes the one spare byte, which was never emitted by SharpZipLib.
        if (offset != expected.Length - 1) throw Invalid("DEFLATE history validation output size mismatch.");
    }
    private static InvalidDataException Invalid(string message) => new(".x MSZIP: " + message);
}
