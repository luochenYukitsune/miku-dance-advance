using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Worker-owned decoder sessions over frozen project bytes. Media time comes exclusively
/// from each accepted OfflineFrame sample; this adapter never advances or queries a live scene.</summary>
public sealed class D3D9ClassicMediaDecoder : IAsyncDisposable
{
    private sealed record Entry(D3D9ClassicMediaSource Source, ISceneVideoDecodeSession Decoder);
    private readonly List<Entry> entries = [];
    private D3D9ClassicScreenCaptureMode? screenCaptureMode;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifetime = new();
    private bool closing, disposed;
    private Task? disposalAttempt;

    private D3D9ClassicMediaDecoder() { }

    public static async Task<D3D9ClassicMediaDecoder> OpenAsync(ISceneVideoDecoder decoder,
        IEnumerable<D3D9ClassicMediaSource> sources, string absoluteScratchRoot,
        CancellationToken cancellationToken = default,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(decoder); ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteScratchRoot);
        if (!Path.IsPathFullyQualified(absoluteScratchRoot))
            throw new ArgumentException("Classic media requires an explicit absolute scratch directory.", nameof(absoluteScratchRoot));
        if (screenCaptureMode is { } mode && !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(screenCaptureMode));
        var frozen = sources.ToArray();
        foreach (var source in frozen)
        { ArgumentNullException.ThrowIfNull(source); source.Validate(); }
        if (frozen.Length > 2 || frozen.Select(value => value.Kind).Distinct().Count() != frozen.Length
            || frozen.Select(value => value.Track.Id).Distinct().Count() != frozen.Length)
            throw new ArgumentException("Classic backgrounds require unique picture/video tracks.", nameof(sources));
        var owner = new D3D9ClassicMediaDecoder { screenCaptureMode = screenCaptureMode };
        try
        {
            foreach (var source in frozen.OrderBy(value => value.Kind))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!source.Track.Enabled) continue;
                var session = await decoder.OpenAsync(source.Track.Asset, absoluteScratchRoot, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The media decoder returned no session.");
                owner.entries.Add(new(source, session));
                if (!string.Equals(session.SourceSha256, source.Track.Asset.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The media decoder opened bytes from another captured asset.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return owner;
        }
        catch (Exception error)
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                // Preserve a retryable cleanup handle if an injected decoder cannot release its
                // private files/processes on the first attempt. The original failure remains primary.
                error.Data["ClassicMediaCleanupOwner"] = owner;
                error.Data["ClassicMediaCleanupFailure"] = cleanup;
            }
            throw;
        }
    }

    public async ValueTask<D3D9ClassicMediaInputs> DecodeAsync(OfflineFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ThrowIfClosing();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            // Reuse the neutral sampling arithmetic. This local stateless projection of an
            // accepted rational time never advances the scene or owns an independent clock.
            var sample = new SceneMediaClock(new(30, 1));
            sample.Seek(frame.Sample.SampledSourceFrame);
            var layers = new List<D3D9ClassicMediaLayer>(entries.Count);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var media = sample.Sample(entry.Source.Track);
                if (!media.Active) continue;
                using var decoded = await entry.Decoder.DecodeAsync(media.MediaTicks, cancellationToken).ConfigureAwait(false);
                if (decoded.Width != entry.Decoder.Width || decoded.Height != entry.Decoder.Height
                    || decoded.PresentationTicks > media.MediaTicks)
                    throw new InvalidDataException("Decoded background does not match the requested captured stream/sample.");
                var rectangle = entry.Source.Placement.Resolve(decoded.Width, decoded.Height, frame.Width, frame.Height);
                layers.Add(new(entry.Source.Kind, decoded.Width, decoded.Height, rectangle, decoded.Pixels.Span, entry.Source.DrawBackground));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(frame, layers, screenCaptureMode);
        }
        finally { gate.Release(); }
    }

    private void ThrowIfClosing()
    {
        lock (lifetime) ObjectDisposedException.ThrowIf(closing, this);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            if (disposed) return ValueTask.CompletedTask;
            closing = true;
            if (disposalAttempt is not { IsCompleted: false }) disposalAttempt = DisposeCoreAsync();
            return new(disposalAttempt);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<Exception>? failures = null;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                try { await entries[i].Decoder.DisposeAsync().ConfigureAwait(false); entries.RemoveAt(i); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException("Classic media cleanup requires a retry.", failures);
            lock (lifetime) disposed = true;
        }
        finally { gate.Release(); }
    }
}
