// DirectX binary token layout references Microsoft Token Records and Assimp v5.4.3
// XFileParser (BSD-3-Clause). Independently written C#; no copied implementation.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Mmd.Modern.Formats;

internal enum XBinaryNumberKind { None, Integer, Float }

internal sealed class XBinaryTokenReader(byte[] data, int start, bool float64, Encoding encoding, XTextReadOptions options)
{
    private int position = start, remaining;
    private bool floating;
    internal int Position => position;
    internal XBinaryNumberKind NumberKind { get; private set; }

    internal string? ReadNext()
    {
        NumberKind = XBinaryNumberKind.None;
        while (true)
        {
            if (remaining != 0)
            {
                remaining--;
                if (!floating) { NumberKind = XBinaryNumberKind.Integer; return Dword().ToString(CultureInfo.InvariantCulture); }
                int width = float64 ? 8 : 4; Need(width);
                double value = float64 ? BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(position, 8))
                    : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(position, 4));
                position += width;
                float core = (float)value;
                if (!float.IsFinite(core)) throw Error("Expected a finite core float32 number.");
                NumberKind = XBinaryNumberKind.Float;
                return core.ToString("R", CultureInfo.InvariantCulture);
            }
            if (position == data.Length) return null;
            ushort token = Word();
            switch (token)
            {
                case 1: return Text(false);
                case 2:
                    string value = Text(true);
                    if (Word() is not (19 or 20)) throw Error("Binary string requires a comma or semicolon token.");
                    return value;
                case 3: NumberKind = XBinaryNumberKind.Integer; return Dword().ToString(CultureInfo.InvariantCulture);
                case 5: Need(16); position += 16; return "<guid>";
                case 6: case 7:
                    uint count = Dword(); int width = token == 7 && float64 ? 8 : 4;
                    if (count > options.MaxTokens || (long)count * width > data.Length - position)
                        throw Error("Truncated or excessive binary numeric list.");
                    remaining = (int)count; floating = token == 7; continue;
                case 19: case 20: continue;
                case 10: return "{";
                case 11: return "}";
                case 12: return "(";
                case 13: return ")";
                case 14: return "[";
                case 15: return "]";
                case 16: return "<";
                case 17: return ">";
                case 18: return ".";
                case 31: return "template";
                case 40: return "WORD";
                case 41: return "DWORD";
                case 42: return "FLOAT";
                case 43: return "DOUBLE";
                case 44: return "CHAR";
                case 45: return "UCHAR";
                case 46: return "SWORD";
                case 47: return "SDWORD";
                case 48: return "VOID";
                case 49: return "STRING";
                case 50: return "UNICODE";
                case 51: return "CSTRING";
                case 52: return "array";
                default: throw Error("Unknown binary token " + token + ".");
            }
        }
    }

    private string Text(bool quoted)
    {
        uint length = Dword();
        if (length > options.MaxTokenCharacters || length > data.Length - position) throw Error("Truncated or excessive binary string/name.");
        string value = encoding.GetString(data, position, (int)length); position += (int)length;
        if (value.IndexOf('\0') >= 0 || !quoted && (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c is '{' or '}' or '"')))
            throw Error("Invalid binary name/string.");
        return quoted ? "\"" + value : value;
    }
    private ushort Word() { Need(2); ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2)); position += 2; return value; }
    private uint Dword() { Need(4); uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4)); position += 4; return value; }
    private void Need(int count) { if (count > data.Length - position) throw Error("Truncated binary token."); }
    private InvalidDataException Error(string message) => new($".x binary at byte {position}: {message}");
}
