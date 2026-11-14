using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

internal sealed record D3D12CaptureTimingResult(string Label, ulong BeginTicks, ulong EndTicks,
    ulong Frequency, double GpuMilliseconds, long FrameId, ulong SubmittedFence);

/// <summary>Explicit diagnostic capture only. Borrows the recording frame; never submits or waits.</summary>
internal sealed class D3D12CaptureTiming
{
    private readonly D3D12GpuRuntime runtime;
    private readonly ID3D12QueryHeap queries;
    private readonly ID3D12Resource readback;
    private readonly D3D12CaptureTimingState state;
    private readonly long frameId;
    private readonly ulong expectedFence, frequency;
    private readonly string label;
    private D3D12CaptureTimingResult? result;
    internal int ReadbackCount { get; private set; }

    internal D3D12CaptureTiming(D3D12RecordingContext context, string label)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        runtime = context.Runtime;
        if (!runtime.IsRecording) throw new InvalidOperationException("GPU timing requires an open frame.");
        this.label = label;
        frameId = runtime.FrameId;
        expectedFence = checked(runtime.SubmittedFence + 1);
        state = new(frameId, expectedFence);
        runtime.Queue.GetTimestampFrequency(out frequency).CheckError();
        if (frequency == 0) throw new NotSupportedException("The GPU queue returned zero timestamp frequency.");
        queries = context.Own(context.Device.CreateQueryHeap<ID3D12QueryHeap>(new(QueryHeapType.Timestamp, 2)));
        readback = context.Own(context.Device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer(16), ResourceStates.CopyDest));
    }
    internal void Begin()
    {
        RequireRecordingFrame(); state.Begin();
        runtime.Commands.EndQuery(queries, QueryType.Timestamp, 0);
    }
    internal void EndAndResolve()
    {
        RequireRecordingFrame(); state.End();
        runtime.Commands.EndQuery(queries, QueryType.Timestamp, 1);
        runtime.Commands.ResolveQueryData(queries, QueryType.Timestamp, 0, 2, readback, 0);
    }
    private void RequireRecordingFrame()
    {
        if (!runtime.IsRecording || runtime.FrameId != frameId)
            throw new InvalidOperationException("The GPU timing belongs to another or closed frame.");
    }
    internal D3D12CaptureTimingResult ReadResult()
    {
        state.RequireReadable(runtime.FrameId, runtime.IsRecording, runtime.SubmittedFence, runtime.CompletedFence);
        if (result is not null) return result;
        var ticks = readback.Map<ulong>(0, 2);
        try
        {
            result = new(label, ticks[0], ticks[1], frequency,
                ToMilliseconds(ticks[0], ticks[1], frequency), frameId, expectedFence);
            ReadbackCount++;
            return result;
        }
        finally { readback.Unmap(0); }
    }
    internal static double ToMilliseconds(ulong beginTicks, ulong endTicks, ulong frequency)
    {
        if (frequency == 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (endTicks < beginTicks) throw new InvalidOperationException("GPU timestamp end precedes its beginning.");
        return (endTicks - beginTicks) * (1000.0 / frequency);
    }
}

internal sealed class D3D12CaptureTimingState(long frameId, ulong submittedFence)
{
    private bool begun, ended;
    internal void Begin()
    {
        if (begun) throw new InvalidOperationException("The GPU timing was already begun.");
        begun = true;
    }
    internal void End()
    {
        if (!begun || ended) throw new InvalidOperationException("GPU timing must end once after beginning.");
        ended = true;
    }
    internal void RequireReadable(long currentFrameId, bool isRecording, ulong actualSubmittedFence, ulong completedFence)
    {
        if (!ended || currentFrameId != frameId || isRecording || actualSubmittedFence < submittedFence || completedFence < submittedFence)
            throw new InvalidOperationException("GPU timing requires its submitted frame fence to complete before reading.");
    }
}
