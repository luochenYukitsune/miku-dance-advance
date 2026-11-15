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
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,color_transfer,color_primaries,color_space",
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
            return new Session(tools.FfmpegPath, directory, path, asset.Sha256, width, height, operationTimeout,
                Metadata(stream, "color_transfer"), Metadata(stream, "color_primaries"), Metadata(stream, "color_space"));
        }
        catch
        {
            File.Delete(path);
            Directory.Delete(directory);
            throw;
        }
    }

    private sealed class Session(string executable, string directory, string path, string sha256, int width, int height,
        TimeSpan operationTimeout, string? transfer, string? primaries, string? colorSpace) : ISceneVideoDecodeSession
    {
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
                    if (current is not null && mediaTicks < current.PresentationTicks) await ResetAsync().ConfigureAwait(false);
                    pipe ??= FfmpegVideoDecodePipe.Start(executable, path, width, height);
                    current ??= await pipe.ReadAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw new FfmpegException("Captured media produced no image/video frames.", "");
                    while (!ended)
                    {
                        next ??= await pipe.ReadAsync(timeout.Token).ConfigureAwait(false);
                        if (next is null) { ended = true; break; }
                        if (next.PresentationTicks < current.PresentationTicks)
                            throw new FfmpegException("Decoded presentation timestamps are not monotonic.", "");
                        if (next.PresentationTicks > mediaTicks) break;
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
