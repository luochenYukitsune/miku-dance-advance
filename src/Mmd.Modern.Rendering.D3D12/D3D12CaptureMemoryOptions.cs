namespace Mmd.Modern.Rendering.D3D12;

public enum D3D12CaptureMemoryMode { Default, Auto, LowMemory }

/// <summary>Changes capture resource lifetime only. Resolution, samples, jitter, filtering and history semantics stay unchanged.</summary>
public sealed record D3D12CaptureMemoryOptions(D3D12CaptureMemoryMode Mode = D3D12CaptureMemoryMode.Default)
{
    /// <summary>The established three-slot capture resource lifetime.</summary>
    public static D3D12CaptureMemoryOptions Default { get; } = new();
    /// <summary>Use the smaller lifetime window when the normal GI reservation exceeds current adapter headroom.</summary>
    public static D3D12CaptureMemoryOptions Auto { get; } = new(D3D12CaptureMemoryMode.Auto);
    /// <summary>Retire completed frame resources before recording and reuse GI receivers between sequential spatial samples.</summary>
    public static D3D12CaptureMemoryOptions LowMemory { get; } = new(D3D12CaptureMemoryMode.LowMemory);

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
    }

    internal D3D12CaptureMemoryMode Select(ulong? defaultGiReservationBytes, ulong availableBytes)
        => Mode == D3D12CaptureMemoryMode.Auto
            ? defaultGiReservationBytes is {} estimate && estimate > availableBytes ? D3D12CaptureMemoryMode.LowMemory : D3D12CaptureMemoryMode.Default
            : Mode;
}

/// <summary>Selected capture policy and conservative GI preflight, not total process or adapter memory usage.</summary>
public sealed record D3D12CaptureMemoryStatistics(D3D12CaptureMemoryMode Requested, D3D12CaptureMemoryMode Selected,
    ulong? DefaultEstimatedGiReservationBytes, ulong? SelectedEstimatedGiReservationBytes,
    ulong AvailableReservationBytes, int InFlightFrames, double RetirementMilliseconds);
