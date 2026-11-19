using System.Text.Json;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.FFmpeg;

/// <summary>Uses the same explicit FFmpeg toolchain as export. Source files are private copies of
/// immutable project bytes; decoding never revisits an original asset path. No codec is installed.</summary>
public sealed class FfmpegSceneVideoDecoder : ISceneVideoDecoder
{
    private readonly FfmpegToolchain tools;
    private readonly TimeSpan operationTimeout;
    private readonly int maxFrameBytes;

    public FfmpegSceneVideoDecoder(FfmpegToolchain tools, TimeSpan? operationTimeout = null,
        int maxFrameBytes = 256 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(tools);
        this.tools = tools; this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (this.operationTimeout <= TimeSpan.Zero || this.operationTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        if (maxFrameBytes < 4) throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        this.maxFrameBytes = maxFrameBytes;
    }

    public async Task<ISceneVideoDecodeSession> OpenAsync(SceneMediaAsset asset, string absoluteScratchRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteScratchRoot);
        if (!Path.IsPathFullyQualified(absoluteScratchRoot))
            throw new ArgumentException("Media decoding requires an explicit absolute scratch directory.", nameof(absoluteScratchRoot));
        string directory = Path.Combine(Path.GetFullPath(absoluteScratchRoot), "media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source" + Path.GetExtension(asset.Name));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(operationTimeout);
            await File.WriteAllBytesAsync(path, asset.CopyBytes(), timeout.Token).ConfigureAwait(false);
            string json = await FfmpegToolchain.RunStdoutAsync(tools.FfprobePath,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,start_pts,time_base,color_transfer,color_primaries,color_space",
                    "-of", "json", path], timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() != 1) throw new FfmpegException("Captured media has no decodable video/image stream.", "");
            var stream = streams[0];
            int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height * 4 > maxFrameBytes)
                throw new InvalidDataException("Decoded media dimensions exceed the configured frame budget.");
            static string? Metadata(JsonElement stream, string name)
                => stream.TryGetProperty(name, out var value) ? value.GetString() : null;
            // Seeking keeps source timestamps; the stream start rebases them onto the same zero as an unseeked decode.
            // Still images and streams without a start time cannot seek and always decode from the beginning.
            long? originTicks = OriginTicks(stream.TryGetProperty("start_pts", out var startPts) ? startPts : default,
                Metadata(stream, "time_base"));
            return new Session(tools.FfmpegPath, directory, path, asset.Sha256, width, height, operationTimeout,
                Metadata(stream, "color_transfer"), Metadata(stream, "color_primaries"), Metadata(stream, "color_space"), originTicks);
        }
        catch
        {
            File.Delete(path);
            Directory.Delete(directory);
            throw;
        }
    }

    /// <summary>The stream origin in the filter's 1/10,000,000 time base, computed exactly from <c>start_pts</c> and
    /// <c>time_base</c> with av_rescale_q's round-to-nearest (ties away from zero), as <c>settb</c> rescales every frame.
    /// ffprobe's formatted <c>start_time</c> has six decimals and loses ticks (1/30 s became 333330 instead of 333333),
    /// which made a seeked decode pick a different frame than a decode from the start.</summary>
    internal static long? OriginTicks(JsonElement startPts, string? timeBase)
    {
        if (startPts.ValueKind != JsonValueKind.Number || !startPts.TryGetInt64(out long pts) || pts < 0) return null;
        if (timeBase is null || timeBase.Split('/') is not [var n, var d]
            || !long.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long numerator)
            || !long.TryParse(d, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long denominator)
            || numerator <= 0 || denominator <= 0) return null;
        var scaled = (System.Numerics.BigInteger)pts * numerator * TimeSpan.TicksPerSecond;
        var ticks = (scaled + denominator / 2) / denominator;
        return ticks < TimeSpan.TicksPerDay ? (long)ticks : null;
    }

    private sealed class Session(string executable, string directory, string path, string sha256, int width, int height,
        TimeSpan operationTimeout, string? transfer, string? primaries, string? colorSpace, long? originTicks) : ISceneVideoDecodeSession
    {
        /// <summary>Requests are truncated to ticks while FFmpeg rounds frame timestamps; frame n of a 30 fps clip
        /// can land one tick after the request for output frame n. Without this a 30/30 pairing repeats every
        /// third frame.</summary>
        internal const long PresentationToleranceTicks = 1;
        /// <summary>A target further ahead than this restarts with an input seek instead of decoding forward, and a
        /// restart (backwards seek, timeout recovery) seeks too, so deep positions no longer re-decode from frame 0.</summary>
        internal static readonly long SeekAheadTicks = TimeSpan.FromSeconds(5).Ticks;
        /// <summary>Seek this far before the target so the frame covering it (which may start earlier) is decoded.</summary>
        internal static readonly long SeekPrerollTicks = TimeSpan.FromSeconds(1).Ticks;
        private readonly SemaphoreSlim gate = new(1, 1);
        private FfmpegVideoDecodePipe? pipe;
        private FfmpegDecodedVideoFrame? current, next;
        private bool ended, disposed;
        public string SourceSha256 => sha256;
        public int Width => width;
        public int Height => height;

        public async ValueTask<SceneVideoFrame> DecodeAsync(long mediaTicks, CancellationToken cancellationToken = default)
        {
            if (mediaTicks < 0) throw new ArgumentOutOfRangeException(nameof(mediaTicks));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(operationTimeout);
                try
                {
                    if (current is not null && (mediaTicks + PresentationToleranceTicks < current.PresentationTicks
                        || (!ended && originTicks is not null && mediaTicks - current.PresentationTicks > SeekAheadTicks)))
                        await ResetAsync().ConfigureAwait(false);
                    long seek = originTicks is not null ? Math.Max(0, mediaTicks - SeekPrerollTicks) : 0;
                    if (pipe is null)
                    {
                        pipe = FfmpegVideoDecodePipe.Start(executable, path, width, height, seek, originTicks ?? 0);
                        current = await pipe.ReadAsync(timeout.Token).ConfigureAwait(false);
                        if (seek > 0 && (current is null || current.PresentationTicks > mediaTicks + PresentationToleranceTicks))
                        {
                            // A sparse or variable-rate stream had no frame in the preroll window: decode from the start.
                            await ResetAsync().ConfigureAwait(false);
                            pipe = FfmpegVideoDecodePipe.Start(executable, path, width, height);
                            current = await pipe.ReadAsync(timeout.Token).ConfigureAwait(false);
                        }
                    }
                    if (current is null) throw new FfmpegException("Captured media produced no image/video frames.", "");
                    while (!ended)
                    {
                        next ??= await pipe.ReadAsync(timeout.Token).ConfigureAwait(false);
                        if (next is null) { ended = true; break; }
                        if (next.PresentationTicks < current.PresentationTicks)
                            throw new FfmpegException("Decoded presentation timestamps are not monotonic.", "");
                        if (next.PresentationTicks > mediaTicks + PresentationToleranceTicks) break;
                        current = next; next = null;
                    }
                    return new(width, height, current.Pixels, current.PresentationTicks, ended, transfer, primaries, colorSpace);
                }
                catch
                {
                    await ResetAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally { gate.Release(); }
        }

        private async ValueTask ResetAsync()
        {
            if (pipe is not null) { await pipe.DisposeAsync().ConfigureAwait(false); pipe = null; }
            current = next = null; ended = false;
        }

        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                disposed = true;
                await ResetAsync().ConfigureAwait(false);
                // Keep failed cleanup retryable. This removes only this session's generated file and directory.
                File.Delete(path);
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
            finally { gate.Release(); }
        }
    }
}
