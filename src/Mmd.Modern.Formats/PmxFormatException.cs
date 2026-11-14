namespace Mmd.Modern.Formats;

/// <summary>
/// Raised when a PMX file is truncated, malformed, or declares values outside
/// the range allowed by the PMX 2.0/2.1 format. The message always names the
/// field and the byte offset (relative to the start of the PMX stream) so a
/// corrupt asset can be located precisely.
/// </summary>
public sealed class PmxFormatException : Exception
{
    public PmxFormatException(string field, long offset, string detail)
        : this(field, offset, detail, filePath: null, innerException: null)
    {
    }

    public PmxFormatException(
        string field,
        long offset,
        string detail,
        string? filePath,
        Exception? innerException = null)
        : base(BuildMessage(field, offset, detail, filePath), innerException)
    {
        Field = field;
        Offset = offset;
        FilePath = filePath;
    }

    /// <summary>Dotted PMX field path, for example <c>vertices[3].weightType</c>.</summary>
    public string Field { get; }

    /// <summary>Byte offset from the first byte of the PMX stream.</summary>
    public long Offset { get; }

    /// <summary>Path passed to <see cref="PmxReader.Read(string)"/>, if any.</summary>
    public string? FilePath { get; }

    private static string BuildMessage(string field, long offset, string detail, string? filePath)
    {
        string location = filePath is null
            ? $"offset 0x{offset:X8}"
            : $"{filePath} at offset 0x{offset:X8}";
        return $"PMX format error ({location}, field '{field}'): {detail}";
    }
}
