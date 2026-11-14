namespace Mmd.Modern.Formats;

/// <summary>Streaming output-byte budget, measured from the start of this PMX write.
/// The legacy byte-array and two-argument write APIs retain their 512 MiB budget.</summary>
public sealed record PmxWriteOptions
{
    public long MaxOutputBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxOutputBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxOutputBytes), "PMX output budget must be positive.");
    }
}
