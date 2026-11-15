using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Mmd.Modern.ViewOutput.FFmpeg;

internal sealed record FfmpegDecodedVideoFrame(byte[] Pixels, long PresentationTicks);

/// <summary>Binary stdout companion to the export process wrapper. FFmpeg showinfo reports the
/// exact filtered PTS; rawvideo passthrough preserves one output frame per report. Neither nominal
/// FPS nor wall time selects a frame. See FFmpeg libavfilter/vf_showinfo.c (LGPL-2.1-or-later).
/// Only process argument construction is shared: the export wrapper drains stdout as text.</summary>
internal sealed class FfmpegVideoDecodePipe : IAsyncDisposable
{
    private static readonly Regex FrameInfo = new(@"\bn:\s*(?<index>\d+)\s+pts:\s*(?<pts>-?\d+).*?\bs:(?<width>\d+)x(?<height>\d+)", RegexOptions.CultureInvariant);
    private readonly Process process;
    private readonly int width, height, frameBytes;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<long> timestamps = Channel.CreateBounded<long>(new BoundedChannelOptions(32)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly StringBuilder diagnostic = new();
    private readonly Task readDiagnostic;
    private int frameIndex;

    private FfmpegVideoDecodePipe(Process process, int width, int height)
    {
        this.process = process; this.width = width; this.height = height; frameBytes = checked(width * height * 4);
        readDiagnostic = ReadDiagnosticAsync();
    }

    /// <param name="seekTicks">When positive, FFmpeg input-seeks to this media time instead of decoding every frame
    /// from the start. Timestamps then keep their source values (<c>-copyts</c>) and are rebased by
    /// <paramref name="originTicks"/>, the stream start time, so they match the unseeked timeline.</param>
    internal static FfmpegVideoDecodePipe Start(string executable, string path, int width, int height,
        long seekTicks = 0, long originTicks = 0)
    {
        Process process = new() { StartInfo = FfmpegProcess.StartInfo(executable, Arguments(path, seekTicks, originTicks)) };
        try
        {
            process.Start(); process.StandardInput.Close();
            return new(process, width, height);
        }
        catch { process.Dispose(); throw; }
    }

    internal static IReadOnlyList<string> Arguments(string path, long seekTicks, long originTicks)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "info", "-noautorotate" };
        string rebase = "PTS-STARTPTS";
        if (seekTicks > 0)
        {
            arguments.AddRange(["-ss", ((seekTicks + originTicks) / (decimal)TimeSpan.TicksPerSecond).ToString("0.0000000", CultureInfo.InvariantCulture), "-copyts"]);
            rebase = "PTS-" + originTicks.ToString(CultureInfo.InvariantCulture);
        }
        arguments.AddRange(["-i", path, "-map", "0:v:0", "-an", "-sn", "-dn", "-filter_threads", "1", "-vf",
            $"settb=1/10000000,setpts={rebase},format=rgba,showinfo=checksum=0",
            "-fps_mode", "passthrough", "-threads", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1"]);
        return arguments;
    }

    internal async ValueTask<FfmpegDecodedVideoFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(Kill);
        byte[] pixels = new byte[frameBytes];
        int length = 0;
        while (length < pixels.Length)
        {
            int count = await process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length == 0)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await readDiagnostic.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new FfmpegException($"Video decoder exited with code {process.ExitCode}.", diagnostic.ToString());
            if (timestamps.Reader.TryRead(out _)) throw new FfmpegException("Video timestamp has no corresponding RGBA frame.", diagnostic.ToString());
            return null;
        }
        if (length != pixels.Length) throw new FfmpegException("Video decoder returned a truncated RGBA frame.", "");
        long pts;
        try { pts = await timestamps.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException error) { throw new FfmpegException("Video decoder omitted presentation metadata.", diagnostic.ToString(), error); }
        return new(pixels, pts);
    }

    private async Task ReadDiagnosticAsync()
    {
        Exception? failure = null;
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(stop.Token).ConfigureAwait(false)) is not null)
            {
                diagnostic.AppendLine(line);
                if (diagnostic.Length > 32768) diagnostic.Remove(0, diagnostic.Length - 32768);
                if (!line.Contains("showinfo", StringComparison.Ordinal)) continue;
                var match = FrameInfo.Match(line);
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    || index != frameIndex++
                    || !int.TryParse(match.Groups["width"].Value, out int actualWidth) || actualWidth != width
                    || !int.TryParse(match.Groups["height"].Value, out int actualHeight) || actualHeight != height
                    || !long.TryParse(match.Groups["pts"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pts) || pts < 0)
                    throw new InvalidDataException("Video decoder changed frame geometry or returned invalid presentation metadata.");
                await timestamps.Writer.WriteAsync(pts, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { failure = error; Kill(); }
        finally { timestamps.Writer.TryComplete(failure); }
    }

    private void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); Kill();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await readDiagnostic.ConfigureAwait(false);
        }
        finally { process.Dispose(); stop.Dispose(); }
    }
}
