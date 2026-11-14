using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Capture continuity is committed after a successful capture only, independently per renderer.</summary>
internal sealed class D3D12ViewTemporalClock
{
    private OfflineFrameSample? previous;
    internal ViewTemporalFrameState Resolve(OfflineFrameSample sample, ViewTemporalFrameState? supplied = null)
    {
        supplied ??= new(); supplied.Validate();
        bool discontinuity = previous is not null && (sample.Index != previous.Index + 1
            || sample.PresentationTime.ToDouble() <= previous.PresentationTime.ToDouble()
            || !SourceDeltaMatchesPresentation(sample, previous));
        float? duration = supplied.FrameTimeMilliseconds;
        if (duration is null && !discontinuity && previous is not null)
            duration = (float)((sample.PresentationTime.ToDouble() - previous.PresentationTime.ToDouble()) * 1000);
        return supplied with { FrameTimeMilliseconds = duration,
            ResetHistory = supplied.ResetHistory || discontinuity };
    }
    internal void Commit(OfflineFrameSample sample) => previous = sample;
    private static bool SourceDeltaMatchesPresentation(OfflineFrameSample current, OfflineFrameSample previous)
    {
        var source = current.RequestedSourceFrame; var oldSource = previous.RequestedSourceFrame;
        var time = current.PresentationTime; var oldTime = previous.PresentationTime;
        var sourceNumerator = source.Numerator * oldSource.Denominator - oldSource.Numerator * source.Denominator;
        var timeNumerator = time.Numerator * oldTime.Denominator - oldTime.Numerator * time.Denominator;
        return sourceNumerator * time.Denominator * oldTime.Denominator
            == timeNumerator * source.Denominator * oldSource.Denominator * OfflineFramePlan.SourceFramesPerSecond;
    }
}
