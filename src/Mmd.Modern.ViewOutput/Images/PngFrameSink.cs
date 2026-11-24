using System.Globalization;
using System.Text.Json;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.Images;

/// <summary>Writes one PNG into a unique file beside the destination, replacing the old file only at Complete.</summary>
public sealed class PngFrameSink(string outputPath) : PngOutputSink(outputPath, sequence: false);

/// <summary>Publishes a new directory containing ordered PNGs and a complete manifest. An existing directory is never replaced.</summary>
public sealed class PngSequenceSink(string outputDirectory) : PngOutputSink(outputDirectory, sequence: true);

/// <summary>Single-use, serial PNG output lifecycle shared by still and sequence sinks.</summary>
public abstract class PngOutputSink : IFrameSink
{
    private readonly string destination;
    private readonly bool sequence;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ExportRequest? request;
    private string? temporary;
    private FileStream? manifestStream;
    private Utf8JsonWriter? manifest;
    private long written;
    private enum SinkState { Ready, Writing, Faulted, Completed, Aborted, Disposed }
    private SinkState state;
    protected PngOutputSink(string outputPath, bool sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        destination = Path.GetFullPath(outputPath); this.sequence = sequence;
    }

    public async ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(state == SinkState.Disposed, this);
            if (state != SinkState.Ready) throw new InvalidOperationException("A PNG sink can begin only once.");
            request.Validate(); cancellationToken.ThrowIfCancellationRequested();
            if (!sequence && request.Plan.FrameCount != 1) throw new ArgumentException("A single PNG requires exactly one planned frame.", nameof(request));
            this.request = request;
            string parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Output must name a file or job directory.");
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
            if (sequence && (Directory.Exists(destination) || File.Exists(destination))) throw new IOException("The PNG sequence destination already exists; choose a new job directory.");
            temporary = Path.Combine(parent, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".partial");
            if (sequence)
            {
                Directory.CreateDirectory(temporary);
                manifestStream = new FileStream(Path.Combine(temporary, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                manifest = new Utf8JsonWriter(manifestStream, new() { Indented = true });
                manifest.WriteStartObject(); manifest.WriteNumber("version", 1);
                manifest.WriteNumber("width", request.Plan.Width); manifest.WriteNumber("height", request.Plan.Height);
                manifest.WriteString("pixelLayout", "rgba8"); manifest.WriteString("colorSpace", "sRGB"); manifest.WriteString("alpha", "straight");
                manifest.WriteNumber("fpsNumerator", request.Plan.FrameRate.Numerator); manifest.WriteNumber("fpsDenominator", request.Plan.FrameRate.Denominator);
                manifest.WriteNumber("startFrame", request.Plan.StartFrame); manifest.WriteNumber("endFrame", request.Plan.EndFrame);
                manifest.WriteNumber("frameCount", request.Plan.FrameCount); manifest.WriteStartArray("frames");
            }
            state = SinkState.Writing;
        }
        catch { MarkFaulted(); throw; }
        finally { gate.Release(); }
    }

    public async ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWriting(); cancellationToken.ThrowIfCancellationRequested();
            if (written >= request!.Plan.FrameCount || sample != request.Plan.GetSample(written)) throw new InvalidOperationException("PNG frames must exactly follow the planned frame order and rational timestamps.");
            if (frame.Width != request.Plan.Width || frame.Height != request.Plan.Height) throw new InvalidOperationException("PNG dimensions differ from the output plan.");
            string name = "frame-" + written.ToString("D8", CultureInfo.InvariantCulture) + ".png";
            await PngEncoder.WriteAsync(sequence ? Path.Combine(temporary!, name) : temporary!, frame, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                manifest.WriteStartObject(); manifest.WriteNumber("index", sample.Index); manifest.WriteString("file", name);
                Time(manifest, "presentationTime", sample.PresentationTime); Time(manifest, "requestedSourceFrame", sample.RequestedSourceFrame); Time(manifest, "sampledSourceFrame", sample.SampledSourceFrame);
                manifest.WriteEndObject(); manifest.Flush();
            }
            written++;
        }
        catch { MarkFaulted(); throw; }
        finally { gate.Release(); }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWriting(); cancellationToken.ThrowIfCancellationRequested();
            if (written != request!.Plan.FrameCount) throw new InvalidOperationException("Cannot publish an incomplete PNG output.");
            if (manifest is not null)
            {
                manifest.WriteEndArray(); manifest.WriteBoolean("complete", true); manifest.WriteEndObject(); manifest.Flush();
                manifestStream!.Flush(flushToDisk: true); CloseManifest();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (sequence) Directory.Move(temporary!, destination); else File.Move(temporary!, destination, overwrite: true);
            temporary = null; state = SinkState.Completed;
        }
        catch { MarkFaulted(); throw; }
        finally { gate.Release(); }
    }

    public ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default) =>
        CleanupAsync(SinkState.Aborted);

    public ValueTask DisposeAsync() => CleanupAsync(SinkState.Disposed);

    private async ValueTask CleanupAsync(SinkState finalState)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state != SinkState.Disposed) state = finalState;
            // A sharing violation can leave an unpublished output behind. A later call retries deletion.
            CloseManifest();
            DeleteTemporary();
        }
        finally { gate.Release(); }
    }

    private void MarkFaulted()
    {
        if (state is SinkState.Ready or SinkState.Writing) state = SinkState.Faulted;
    }

    private void RequireWriting()
    {
        ObjectDisposedException.ThrowIf(state == SinkState.Disposed, this);
        if (state != SinkState.Writing) throw new InvalidOperationException("The PNG sink is not writable.");
    }

    private void CloseManifest()
    {
        try { manifest?.Dispose(); }
        finally
        {
            manifest = null;
            manifestStream?.Dispose();
            manifestStream = null;
        }
    }

    private void DeleteTemporary()
    {
        if (temporary is null) return;
        if (sequence) { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
        else if (File.Exists(temporary)) File.Delete(temporary);
        temporary = null;
    }
    private static void Time(Utf8JsonWriter writer, string name, OfflineFrameTime time)
    {
        writer.WriteStartObject(name); writer.WriteString("numerator", time.Numerator.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("denominator", time.Denominator.ToString(CultureInfo.InvariantCulture)); writer.WriteEndObject();
    }
}
