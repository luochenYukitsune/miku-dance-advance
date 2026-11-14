namespace Mmd.Modern.Formats;

/// <summary>Malformed VMD data, with field and byte offset relative to the starting stream position.</summary>
public sealed class VmdFormatException : FormatException
{
    public VmdFormatException(string field, long offset, string detail, Exception? innerException = null)
        : base($"VMD format error (offset 0x{offset:X8}, field '{field}'): {detail}", innerException)
    {
        Field = field;
        Offset = offset;
    }

    public string Field { get; }
    public long Offset { get; }
}
