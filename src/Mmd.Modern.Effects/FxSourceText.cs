using System.Text;

namespace Mmd.Modern.Effects;

/// <summary>
/// Decodes effect sources exactly like the shipped runtime has to: the corpus is a mix of
/// UTF-8-with-BOM (2025 era editors) and Shift-JIS (everything from the 2010s), and the
/// Shift-JIS files carry Japanese comments and paths.
/// </summary>
public static class FxSourceText
{
    // Static field initialisers run before the static constructor, so registration happens in the
    // factory: otherwise the Shift-JIS lookup throws NotSupportedException before the provider exists.
    private static readonly Encoding Sjis = CreateShiftJis();

    public static Encoding ShiftJis => Sjis;

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public static (string Text, bool UsedFallback) Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes[3..]), false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes[2..]), false);
        }

        try
        {
            // Throwing decoder: invalid UTF-8 means the file is Shift-JIS, which is lossy to detect
            // from valid-looking bytes, so only accept a strict decode.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), false);
        }
        catch (DecoderFallbackException)
        {
            return (ShiftJis.GetString(bytes), true);
        }
    }
}
