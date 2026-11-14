namespace Mmd.Modern.Formats;

public sealed class PmmFormatException(string field, long offset, string detail)
    : FormatException($"PMM {field} at offset {offset} (0x{offset:X}): {detail}")
{
    public string Field { get; } = field;
    public long Offset { get; } = offset;
}
