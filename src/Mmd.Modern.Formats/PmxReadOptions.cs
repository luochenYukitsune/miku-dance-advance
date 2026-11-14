namespace Mmd.Modern.Formats;

/// <summary>Input-byte budget applied from the caller stream's current position.
/// Hosts may explicitly raise it for unusually large trusted PMX models.</summary>
public sealed record PmxReadOptions
{
    public long MaxInputBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxInputBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxInputBytes), "PMX input budget must be positive.");
    }
}
