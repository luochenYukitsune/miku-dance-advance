namespace Mmd.Modern.Formats;

/// <summary>Malformed VPD text; line numbers are one-based, or zero for encoding/input limits.</summary>
public sealed class VpdFormatException : FormatException
{
    public VpdFormatException(string field, int lineNumber, string detail, Exception? innerException = null)
        : base($"VPD format error (line {lineNumber}, field '{field}'): {detail}", innerException)
    {
        Field = field;
        LineNumber = lineNumber;
    }
    public string Field { get; }
    public int LineNumber { get; }
}
