namespace Mmd.Modern.Application;

/// <summary>Failure-only observation of the existing offline clock. Indices are zero-based; ticks count
/// successfully returned 1/120-second Advance calls after Seek. An attempted tick may have failed partway
/// through the solver and is not counted as completed. No scene or live owner is retained.</summary>
public sealed record OfflineFrameFailureContext(string Phase, long CompletedFrames, OfflineFrameSample? Sample,
    long SimulationTicks, long? AttemptingSimulationTick, uint SimulationStartFrame, double? ObservedSourceFrame)
{
    public const string DataKey = "MDA.OfflineFrame";
}
