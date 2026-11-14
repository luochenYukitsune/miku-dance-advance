using System.Numerics;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>A frozen enabled PCM WAV track for an offline output. It never retains an original path.
/// Disabled tracks stay in the project but are excluded by the caller from audio output.</summary>
public sealed class SceneAudioExportInput
{
    public Guid TrackId { get; }
    public long OffsetTicks { get; }
    public ScenePcmAudio Audio { get; }

    public SceneAudioExportInput(SceneMediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track); track.Validate();
        if (track.Kind != SceneMediaKind.Audio || !track.Enabled)
            throw new ArgumentException("Audio output requires one explicitly enabled audio track.", nameof(track));
        TrackId = track.Id; OffsetTicks = track.OffsetTicks; Audio = ScenePcmAudio.FromAsset(track.Asset);
    }

    /// <summary>The output begins at StartFrame/30 seconds and lasts FrameCount/outputFPS.
    /// Quantize the source start down once to its preceding PCM frame, with at most one sample of error;
    /// ceil the total output length once so no per-video-frame rounding accumulates. Unavailable samples are silence.</summary>
    public SceneAudioExportRange GetRange(OfflineFramePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        BigInteger numerator = ((BigInteger)plan.StartFrame * TimeSpan.TicksPerSecond
            - (BigInteger)OffsetTicks * OfflineFramePlan.SourceFramesPerSecond) * Audio.SampleRate;
        BigInteger denominator = (BigInteger)OfflineFramePlan.SourceFramesPerSecond * TimeSpan.TicksPerSecond;
        BigInteger source = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (remainder < 0) source--;
        BigInteger duration = (BigInteger)plan.FrameCount * plan.FrameRate.Denominator * Audio.SampleRate;
        long total = checked((long)((duration + plan.FrameRate.Numerator - 1) / plan.FrameRate.Numerator));
        long leading = (long)BigInteger.Min(total, BigInteger.Max(BigInteger.Zero, -source));
        long start = (long)BigInteger.Min(Audio.SampleCount, BigInteger.Max(BigInteger.Zero, source));
        long count = Math.Min(Audio.SampleCount - start, total - leading);
        return new(start, count, leading, total);
    }
}

/// <summary>Counts interleaved PCM frames (one sample per channel), not bytes or video frames.</summary>
public sealed record SceneAudioExportRange(long SourceStartSample, long SourceSampleCount,
    long LeadingSilenceSamples, long OutputSampleCount);
