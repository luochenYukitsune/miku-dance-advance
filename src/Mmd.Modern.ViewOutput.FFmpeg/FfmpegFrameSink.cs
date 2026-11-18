using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.FFmpeg;

/// <summary>Single-use CPU pipe sink. Borrows each frame only during WriteAsync, which composites it into a pooled
/// packed frame and queues it; a dedicated writer thread pipes queued frames to FFmpeg in order, one write per frame.
/// Straight alpha is composited against the configured sRGB background in linear light.
/// Optional frozen PCM audio uses the video plan's selected source interval and a private bounded input.</summary>
public sealed class FfmpegFrameSink(FfmpegToolchain tools, FfmpegOutputOptions options) : IFrameSink
{
    // Two queued frames plus the one being piped: the producer composites frame N+1 while FFmpeg reads frame N, and the
    // extra slot absorbs encoder bursts. Memory stays bounded at three packed frames (24 MiB at 1080p).
    private const int QueuedFrames = 2;
    private FfmpegProcess? process;
    private OfflineFramePlan? plan;
    private string? temporary;
    private string? audioTemporary;
    private Channel<byte[]>? frames;
    private FrameBufferPool? buffers;
    private Task? writer;
    private CancellationTokenSource? writerStop;
    private Exception? writerFailure;
    private long written;
    private bool ended;
    public VideoValidation? Validation { get; private set; }
    internal FrameBufferPool? BufferPoolForTesting => buffers;
    internal Action<CancellationToken>? BeforePipeWriteForTesting { get; set; }

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
            buffers = new(checked(plan.Width * 4 * plan.Height), QueuedFrames + 1);
            frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueuedFrames) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
            writerStop = new();
            writer = StartWriter(process, frames, buffers, writerStop.Token);
        }
        catch (Exception error) { await PreserveFailureAsync(error).ConfigureAwait(false); throw; }
    }

    public async ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
    {
        if (process is null || plan is null || frames is null || buffers is null || ended) throw new InvalidOperationException("The sink is not accepting frames.");
        if (sample != plan.GetSample(written)) throw new ArgumentException("Frames must match the plan and arrive in order.", nameof(sample));
        if (frame.Width != plan.Width || frame.Height != plan.Height) throw new ArgumentException("Frame dimensions differ from the export plan.", nameof(frame));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        byte[]? buffer = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            // Reserve a queue slot before compositing, so no more than QueuedFrames + 1 packed frames ever exist.
            if (!await frames.Writer.WaitToWriteAsync(timeout.Token).ConfigureAwait(false)) ThrowEncoderStopped();
            if (Volatile.Read(ref writerFailure) is not null) ThrowEncoderStopped();
            if (process.HasExited) throw new IOException("The encoder exited before it received every frame.");
            buffer = buffers.Rent();
            Composite(frame, buffer, options, timeout.Token);
            if (!frames.Writer.TryWrite(buffer)) ThrowEncoderStopped();
            buffer = null;
            written++;
        }
        catch (Exception error)
        {
            if (buffer is not null) buffers.Return(buffer);
            var failure = await FailAsync(error, timeout, encoderInput: true).ConfigureAwait(false);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (process is null || plan is null || temporary is null || frames is null || writer is null || ended)
            throw new InvalidOperationException("The sink cannot complete.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            if (written != plan.FrameCount) throw new InvalidOperationException($"Expected {plan.FrameCount} frames; received {written}.");
            frames.Writer.TryComplete();
            // The writer cannot observe this token inside a pipe write; killing the encoder releases it.
            using (timeout.Token.Register(process.Kill)) await writer.ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref writerFailure) is not null) ThrowEncoderStopped();
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
        catch (Exception error)
        {
            var failure = await FailAsync(error, timeout, ReferenceEquals(error, Volatile.Read(ref writerFailure))).ConfigureAwait(false);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }

    public async ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default)
    {
        ended = true;
        Exception? failure = null;
        // Stopping the writer also kills the encoder, which releases a pipe write blocked on a stalled FFmpeg.
        try { writerStop?.Cancel(); }
        catch (Exception cleanupError) { failure = cleanupError; }
        frames?.Writer.TryComplete();
        try { if (process is not null) await process.AbortAsync().ConfigureAwait(false); }
        catch (Exception cleanupError)
        {
            if (failure is null) failure = cleanupError;
            else AddCleanupError(failure, cleanupError);
        }
        // The writer never faults; awaiting it guarantees no thread still uses the pipe or a pooled frame.
        if (writer is not null) { await writer.ConfigureAwait(false); writer = null; }
        writerStop?.Dispose(); writerStop = null;
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

    private Task StartWriter(FfmpegProcess encoder, Channel<byte[]> queue, FrameBufferPool pool, CancellationToken stop)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A dedicated thread, because each pipe write blocks for as long as the encoder applies backpressure.
        var thread = new Thread(() =>
        {
            try { WriteFrames(encoder, queue.Reader, pool, stop); }
            catch (Exception error) { Volatile.Write(ref writerFailure, error); queue.Writer.TryComplete(); }
            finally
            {
                while (queue.Reader.TryRead(out var rest)) pool.Return(rest);
                done.SetResult();
            }
        }) { IsBackground = true, Name = "MDA FFmpeg frame writer" };
        thread.Start();
        return done.Task;
    }

    private void WriteFrames(FfmpegProcess encoder, ChannelReader<byte[]> reader, FrameBufferPool pool, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var registration = timeout.Token.Register(encoder.Kill);
        var input = encoder.Input;
        while (reader.WaitToReadAsync(stop).AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var buffer))
            {
                try
                {
                    timeout.CancelAfter(options.Timeout);
                    BeforePipeWriteForTesting?.Invoke(timeout.Token);
                    // The packed frame has no row padding, so the whole frame is a single contiguous write.
                    input.Write(buffer, 0, pool.BufferLength);
                    timeout.CancelAfter(Timeout.Infinite);
                    timeout.Token.ThrowIfCancellationRequested();
                }
                catch (Exception error) when (timeout.IsCancellationRequested && !stop.IsCancellationRequested && error is not OperationCanceledException)
                {
                    // A killed encoder surfaces as a broken pipe; report the expired write as the cancellation it was.
                    throw new OperationCanceledException("Encoding was cancelled.", error, timeout.Token);
                }
                finally { pool.Return(buffer); }
            }
        }
    }

    private void ThrowEncoderStopped()
    {
        if (Volatile.Read(ref writerFailure) is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        throw new IOException("The encoder stopped accepting frame data.");
    }

    private async Task<Exception> FailAsync(Exception error, CancellationTokenSource timeout, bool encoderInput)
    {
        await PreserveFailureAsync(error).ConfigureAwait(false);
        if (!encoderInput) return error;
        if (timeout.IsCancellationRequested && error is not OperationCanceledException)
        {
            // Killing a cancelled encoder can surface as a broken pipe before the write observes its token.
            var cancellation = new OperationCanceledException("Encoding was cancelled.", error, timeout.Token);
            foreach (System.Collections.DictionaryEntry item in error.Data) cancellation.Data[item.Key] = item.Value;
            return cancellation;
        }
        if (error is IOException and not FfmpegException)
        {
            var failure = new FfmpegException("The encoder stopped accepting frame data.", (string?)error.Data["FfmpegDiagnostic"] ?? "", error);
            foreach (System.Collections.DictionaryEntry item in error.Data) failure.Data[item.Key] = item.Value;
            return failure;
        }
        return error;
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
    private static void Composite(FrameBufferLease frame, byte[] output, FfmpegOutputOptions options, CancellationToken token)
    {
        ReadOnlySpan<byte> pixels = frame.Pixels.Span; int row = frame.Width * 4;
        for (int y = 0; y < frame.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            CompositeRow(pixels.Slice(y * frame.Stride, row), output.AsSpan(y * row, row), options);
        }
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
