namespace Mmd.Modern.App;

/// <summary>Immutable resource observations for one export. Null means unavailable, not zero.</summary>
public sealed record RenderResourceSnapshot
{
    public DateTimeOffset SampledAtUtc { get; init; }

    /// <summary>Current process CPU time divided by elapsed time and all available logical CPUs.</summary>
    public double? ProcessCpuPercent { get; init; }
    public ulong? ProcessWorkingSetBytes { get; init; }

    /// <summary>Largest working set observed during this monitor's lifetime, not the process lifetime.</summary>
    public ulong? ProcessPeakWorkingSetBytes { get; init; }
    public string? AdapterName { get; init; }
    public long? AdapterLuid { get; init; }

    /// <summary>Current PID's busiest GPU engine on AdapterLuid; not whole-system GPU utilization.</summary>
    public double? GpuUtilizationPercent { get; init; }

    /// <summary>DXGI local-segment CurrentUsage for this process on the renderer's adapter.</summary>
    public ulong? ProcessLocalVideoMemoryUsageBytes { get; init; }

    /// <summary>DXGI local-segment Budget for this process, not the adapter's physical capacity.</summary>
    public ulong? LocalVideoMemoryBudgetBytes { get; init; }
    public ulong? PeakProcessLocalVideoMemoryUsageBytes { get; init; }

    /// <summary>The selected adapter's memory query time; independently tracked from other resource samples.</summary>
    public DateTimeOffset? VideoMemorySampledAtUtc { get; init; }
    public string? CpuUnavailableReason { get; init; }
    public string? MemoryUnavailableReason { get; init; }
    public string? GpuUnavailableReason { get; init; }
    public string? VideoMemoryUnavailableReason { get; init; }
}

/// <summary>
/// Values from the selected renderer's adapter. A provider can query its own DXGI interfaces
/// using the actual adapter LUID; it never guesses a default adapter or shares renderer COM objects.
/// </summary>
public sealed record RenderGpuMemorySnapshot
{
    public DateTimeOffset SampledAtUtc { get; init; }
    public string? AdapterName { get; init; }
    public long? AdapterLuid { get; init; }
    public ulong? ProcessLocalVideoMemoryUsageBytes { get; init; }
    public ulong? LocalVideoMemoryBudgetBytes { get; init; }
    public string? UnavailableReason { get; init; }
}
