using System.Buffers.Binary;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Strict legacy encodings, including Microsoft's documented duplicate CP932 spellings.</summary>
public static class LegacyAnsiEncoding
{
    private static readonly Lazy<IReadOnlyDictionary<ushort, char>> Aliases = new(ReadAliases);

    public static Encoding Create(int codePage)
    {
        if (codePage is not (932 or 936)) throw new ArgumentOutOfRangeException(nameof(codePage));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback,
            codePage == 932 ? new MicrosoftDuplicateFallback() : DecoderFallback.ExceptionFallback);
    }

    private static IReadOnlyDictionary<ushort, char> ReadAliases()
    {
        using var stream = typeof(LegacyAnsiEncoding).Assembly.GetManifestResourceStream("Mmd.Modern.Formats.cp932-duplicates.bin")
            ?? throw new InvalidDataException("The documented CP932 duplicate mapping resource is missing.");
        if (stream.Length != 3176) throw new InvalidDataException("Invalid CP932 duplicate mapping resource length.");
        var result = new Dictionary<ushort, char>();
        Span<byte> pair = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(pair);
            result.Add(BinaryPrimitives.ReadUInt16LittleEndian(pair), (char)BinaryPrimitives.ReadUInt16LittleEndian(pair[2..]));
        }
        return result;
    }

    private sealed class MicrosoftDuplicateFallback : DecoderFallback
    {
        public override int MaxCharCount => 1;
        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer();

        private sealed class Buffer : DecoderFallbackBuffer
        {
            private char value;
            private int position = 1;
            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                if (Remaining != 0 || bytesUnknown.Length != 2
                    || !Aliases.Value.TryGetValue((ushort)((bytesUnknown[0] << 8) | bytesUnknown[1]), out value))
                    throw new DecoderFallbackException("Invalid CP932 byte sequence.", bytesUnknown, index);
                position = 0;
                return true;
            }
            public override char GetNextChar() => position++ == 0 ? value : '\0';
            public override bool MovePrevious() { if (position != 1) return false; position = 0; return true; }
            public override int Remaining => position == 0 ? 1 : 0;
            public override void Reset() { position = 1; base.Reset(); }
        }
    }
}
