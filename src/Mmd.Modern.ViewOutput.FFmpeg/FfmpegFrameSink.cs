using System.Buffers;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.FFmpeg;

/// <summary>Sequential, single-use CPU pipe sink. Borrows each frame only during WriteAsync.
/// Straight alpha is composited against the configured sRGB background in linear light.
/// Optional frozen PCM audio uses the video plan's selected source interval and a private bounded input.</summary>
public sealed class FfmpegFrameSink(FfmpegToolchain tools, FfmpegOutputOptions options) : IFrameSink
{
    private FfmpegProcess? process;
    private OfflineFramePlan? plan;
    private string? temporary;
    private string? audioTemporary;
    private long written;
    private bool ended;
    public VideoValidation? Validation { get; private set; }

    public async ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan is not null || ended) throw new InvalidOperationException("The sink is single-use.");
        request.Validate(); options.Validate(request.Plan);
        string destination = Path.GetFullPath(options.OutputPath);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".partial");
        plan = request.Plan;
        try
        {
            if (options.AudioInput is not null)
            {
                audioTemporary = temporary + ".pcm";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                await FfmpegAudioInput.WritePrivateAsync(audioTemporary, options.AudioInput, plan, timeout.Token).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            process = FfmpegProcess.Start(tools.FfmpegPath, options.CreateArguments(plan, temporary, audioTemporary));
        }
        catch (Exception error) { await PreserveFailureAsync(error).ConfigureAwait(false); throw; }
    }

    public async ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
    {
        if (process is null || plan is null || ended) throw new InvalidOperationException("The sink is not accepting frames.");
        if (sample != plan.GetSample(written)) throw new ArgumentException("Frames must match the plan and arrive in order.", nameof(sample));
        if (frame.Width != plan.Width || frame.Height != plan.Height) throw new ArgumentException("Frame dimensions differ from the export plan.", nameof(frame));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        using var registration = timeout.Token.Register(process.Kill);
        byte[] row = ArrayPool<byte>.Shared.Rent(checked(frame.Width * 4));
        try
        {
            for (int y = 0; y < frame.Height; y++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                CompositeRow(frame.Pixels.Span.Slice(y * frame.Stride, frame.Width * 4), row.AsSpan(0, frame.Width * 4), options);
                await process.Input.WriteAsync(row.AsMemory(0, frame.Width * 4), timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            written++;
        }
        catch (Exception error)
        {
            await PreserveFailureAsync(error).ConfigureAwait(false);
            if (timeout.IsCancellationRequested && error is not OperationCanceledException)
            {
                // Killing a cancelled encoder can surface as a broken pipe before WriteAsync observes its token.
                var cancellation = new OperationCanceledException("Encoding was cancelled.", error, timeout.Token);
                foreach (System.Collections.DictionaryEntry item in error.Data) cancellation.Data[item.Key] = item.Value;
                throw cancellation;
            }
            if (error is IOException and not FfmpegException)
            {
                var failure = new FfmpegException("The encoder stopped accepting frame data.", (string?)error.Data["FfmpegDiagnostic"] ?? "", error);
                foreach (System.Collections.DictionaryEntry item in error.Data) failure.Data[item.Key] = item.Value;
                throw failure;
            }
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(row); }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (process is null || plan is null || temporary is null || ended) throw new InvalidOperationException("The sink cannot complete.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            if (written != plan.FrameCount) throw new InvalidOperationException($"Expected {plan.FrameCount} frames; received {written}.");
            await process.FinishAsync(timeout.Token).ConfigureAwait(false);
            Validation = await tools.ValidateAsync(temporary, plan, options, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            DeletePrivateAudio();
            string destination = Path.GetFullPath(options.OutputPath);
            // Both files are in one directory. File.Replace publishes atomically and preserves the old file on failure.
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            temporary = null;
            ended = true;
        }
        catch (Exception error) { await PreserveFailureAsync(error).ConfigureAwait(false); throw; }
    }

    public async ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default)
    {
        ended = true;
        Exception? failure = null;
        try { if (process is not null) await process.AbortAsync().ConfigureAwait(false); }
        catch (Exception cleanupError) { failure = cleanupError; }
        try
        {
            // Retain the path until deletion succeeds: a sharing violation may be retried after disposal.
            if (temporary is not null) { File.Delete(temporary); temporary = null; }
        }
        catch (Exception cleanupError)
        {
            if (failure is null) failure = cleanupError;
            else AddCleanupError(failure, cleanupError);
        }
        try { DeletePrivateAudio(); }
        catch (Exception cleanupError)
        {
            if (failure is null) failure = cleanupError;
            else AddCleanupError(failure, cleanupError);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void DeletePrivateAudio()
    {
        // Retain this path on a sharing failure, just like the unpublished video, so Dispose can retry.
        if (audioTemporary is not null) { File.Delete(audioTemporary); audioTemporary = null; }
    }

    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try { await AbortAsync(null).ConfigureAwait(false); }
        catch (Exception cleanupError) { failure = cleanupError; }
        finally
        {
            try { if (process is not null) await process.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                if (failure is null) failure = cleanupError;
                else AddCleanupError(failure, cleanupError);
            }
            finally { process = null; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task PreserveFailureAsync(Exception error)
    {
        try { await AbortAsync(error).ConfigureAwait(false); }
        catch (Exception cleanupError) { AddCleanupError(error, cleanupError); }
        try { error.Data["FfmpegDiagnostic"] = process is null ? "" : await process.DiagnosticAsync().ConfigureAwait(false); }
        catch (Exception diagnosticError) { AddCleanupError(error, diagnosticError); }
    }

    private static void AddCleanupError(Exception error, Exception cleanupError)
    {
        var errors = error.Data["FfmpegCleanupErrors"] as List<Exception> ?? [];
        errors.Add(cleanupError); error.Data["FfmpegCleanupErrors"] = errors;
    }

    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(value =>
    {
        double v = value / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }).ToArray();
    private static byte Encode(double linear)
    {
        double value = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
    }
    internal static void CompositeRow(ReadOnlySpan<byte> input, Span<byte> output, FfmpegOutputOptions options)
    {
        for (int x = 0; x < input.Length; x += 4)
        {
            if (input[x + 3] == 255) { input.Slice(x, 4).CopyTo(output.Slice(x, 4)); continue; }
            if (input[x + 3] == 0)
            { output[x] = options.BackgroundRed; output[x + 1] = options.BackgroundGreen; output[x + 2] = options.BackgroundBlue; output[x + 3] = 255; continue; }
            double alpha = input[x + 3] / 255.0;
            output[x] = Encode(Linear[input[x]] * alpha + Linear[options.BackgroundRed] * (1 - alpha));
            output[x + 1] = Encode(Linear[input[x + 1]] * alpha + Linear[options.BackgroundGreen] * (1 - alpha));
            output[x + 2] = Encode(Linear[input[x + 2]] * alpha + Linear[options.BackgroundBlue] * (1 - alpha));
            output[x + 3] = 255;
        }
    }
}
