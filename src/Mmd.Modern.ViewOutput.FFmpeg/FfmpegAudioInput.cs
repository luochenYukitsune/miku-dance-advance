using System.Globalization;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.FFmpeg;

internal static class FfmpegAudioInput
{
    public static string Format(ScenePcmAudio audio) => audio.BitsPerSample switch
    { 8 => "u8", 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => throw new NotSupportedException("Unsupported integer PCM bit depth.") };
    public static string Codec(ScenePcmAudio audio) => "pcm_" + Format(audio);

    public static void Validate(SceneAudioExportInput input, OfflineFramePlan plan, bool rawVideo)
    {
        _ = input.GetRange(plan); _ = Format(input.Audio);
        // These are the AAC sampling-frequency-index values accepted without resampling.
        // Preserve the project sample grid; other rates remain available to PCM AVI and preview.
        if (!rawVideo && input.Audio.SampleRate is not (8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 64000 or 88200 or 96000))
            throw new NotSupportedException("MP4 AAC audio requires a supported 8..96kHz AAC sample rate; no implicit audio resampling is performed.");
    }

    public static string Filter(SceneAudioExportInput input, OfflineFramePlan plan)
    {
        var range = input.GetRange(plan);
        // An empty intersection is materialized as one silent PCM frame. Padding is finite, even for an empty/ended source.
        long leading = range.SourceSampleCount == 0 ? 0 : range.LeadingSilenceSamples;
        return FormattableString.Invariant($"[1:a:0]asetpts=PTS-STARTPTS,adelay=delays={leading}S:all=1,apad=whole_len={range.OutputSampleCount},atrim=end_sample={range.OutputSampleCount},asetpts=PTS-STARTPTS[exportaudio]");
    }

    public static async Task WritePrivateAsync(string path, SceneAudioExportInput input, OfflineFramePlan plan, CancellationToken token)
    {
        var range = input.GetRange(plan);
        byte[] bytes;
        if (range.SourceSampleCount > 0) bytes = input.Audio.CopyFrames(range.SourceStartSample, range.SourceSampleCount);
        else
        {
            bytes = new byte[input.Audio.BlockAlign];
            if (input.Audio.BitsPerSample == 8) Array.Fill(bytes, (byte)128);
        }
        token.ThrowIfCancellationRequested();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    public static void AddInputArguments(List<string> args, SceneAudioExportInput input, OfflineFramePlan plan, string path)
    {
        args.AddRange(["-f", Format(input.Audio), "-ar", input.Audio.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", input.Audio.Channels.ToString(CultureInfo.InvariantCulture), "-i", path,
            "-filter_complex", Filter(input, plan)]);
    }
}
