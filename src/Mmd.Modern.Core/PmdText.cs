using System.Text;

namespace Mmd.Modern.Core;

/// <summary>
/// Decodes PMD fixed-width CP932 text consistently for the header and the tail. MMD truncates names to the field
/// width, which can leave half of a double-byte character before the NUL; that partial lead byte is dropped
/// instead of becoming a stray character. Microsoft's NEC/IBM duplicate spellings (for example ED 40) decode
/// to their characters instead of rejecting the whole model.
/// </summary>
internal static class PmdText
{
    private static readonly Encoding Strict = CodePagesEncodingProvider.Instance.GetEncoding(932,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;
    private static readonly Encoding BestFit = CodePagesEncodingProvider.Instance.GetEncoding(932)!;

    internal static string Decode(byte[] field)
    {
        int end = Array.IndexOf(field, (byte)0);
        int length = end < 0 ? field.Length : end;
        try { return Strict.GetString(field, 0, length); }
        catch (DecoderFallbackException)
        {
            // A trailing lead byte without its trail byte is a truncation artifact: drop it.
            if (length > 0 && IsLeadByte(field[length - 1]) && CompleteBefore(field, length - 1))
            {
                try { return Strict.GetString(field, 0, length - 1); }
                catch (DecoderFallbackException) { }
            }
            return BestFit.GetString(field, 0, length);
        }
    }

    private static bool IsLeadByte(byte value) => value is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;

    // True when bytes [0, count) end on a character boundary, i.e. the byte at count starts a character.
    private static bool CompleteBefore(byte[] field, int count)
    {
        int i = 0;
        while (i < count) i += IsLeadByte(field[i]) ? 2 : 1;
        return i == count;
    }
}
