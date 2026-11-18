using System.Runtime.ExceptionServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Formats;

/// <summary>Decoded images for one load operation (for example one project open). Entries are keyed by the
/// resolved file path, size and last write time, so one shared file is decoded once while edited files miss.
/// Background work is bounded by <see cref="DefaultParallelism"/>; results and failures are only surfaced when
/// the caller asks for that file, in the caller's own order. Dispose cancels and joins all background work and
/// drops every entry; decoded images stay alive only through the scenes that reference them.</summary>
public sealed class SceneTextureCache : IDisposable
{
    /// <summary>Leaves one logical processor for the UI and render threads.</summary>
    public static int DefaultParallelism => Math.Max(1, Environment.ProcessorCount - 1);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly record struct Key(string Path, long Length, long WriteTicks);
    private sealed record Entry(string Name, Task<SceneTexture?> Decode);
    private readonly Dictionary<Key, Entry> entries;
    private readonly SemaphoreSlim budget;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Task> background = [];
    private int disposed;

    public SceneTextureCache(int maxParallelism = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelism);
        MaxParallelism = maxParallelism == 0 ? DefaultParallelism : maxParallelism;
        budget = new(MaxParallelism, MaxParallelism);
        entries = new(new KeyComparer());
    }

    public int MaxParallelism { get; }
    /// <summary>Number of decode callbacks that actually ran (background or inline).</summary>
    public int DecodeCount => Volatile.Read(ref decodes);
    private int decodes;

    /// <summary>Runs work under this cache's shared processor budget. Cancelled when the cache is disposed.</summary>
    public Task<T> Schedule<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (background)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            var token = linked.Token;
            var task = Task.Run(async () =>
            {
                await budget.WaitAsync(token).ConfigureAwait(false);
                try { token.ThrowIfCancellationRequested(); return work(token); }
                finally { budget.Release(); }
            }, token);
            background.Add(task.ContinueWith(done => { _ = done.Exception; linked.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            return task;
        }
    }

    /// <summary>Starts decoding one file in the background unless an entry for the same file already exists.
    /// <paramref name="decode"/> must be thread-safe and side-effect free apart from reading the file.</summary>
    public void Prefetch(string path, string name, Func<CancellationToken, SceneTexture?> decode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decode);
        if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested) return;
        if (Identify(path) is not { } key) return;
        try
        {
            lock (entries)
            {
                if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested || entries.ContainsKey(key)) return;
                entries.Add(key, new(name, Schedule(token => { Interlocked.Increment(ref decodes); return decode(token); }, cancellationToken)));
            }
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { }
    }

    /// <summary>Returns the image for <paramref name="path"/>, joining a background decode of the same file.
    /// A missing file is never cached. A decode requested under a different name, or one that was cancelled
    /// while the caller is not, runs inline so names, messages and exception types match a direct call.</summary>
    public SceneTexture? Load(string path, string name, Func<CancellationToken, SceneTexture?> decode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decode);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Identify(path) is not { } key) return Inline(decode, cancellationToken);
        Entry? entry; TaskCompletionSource<SceneTexture?>? created = null;
        lock (entries)
        {
            if (!entries.TryGetValue(key, out entry))
            {
                created = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entries.Add(key, entry = new(name, created.Task));
            }
        }
        if (created is not null)
        {
            // Concurrent requests for the same file join this inline decode; a failure is not retained.
            try { var value = Inline(decode, cancellationToken); created.SetResult(value); return value; }
            catch (Exception error) { lock (entries) { if (entries.TryGetValue(key, out var cur) && ReferenceEquals(cur, entry)) entries.Remove(key); } created.SetException(error); _ = created.Task.Exception; throw; }
        }
        try { entry.Decode.Wait(cancellationToken); }
        catch (AggregateException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Decode.IsCanceled || entry.Decode.IsFaulted && entry.Decode.Exception!.InnerException is OperationCanceledException)
            return Inline(decode, cancellationToken);
        if (entry.Decode.IsFaulted)
        {
            if (!string.Equals(entry.Name, name, StringComparison.Ordinal)) return Inline(decode, cancellationToken);
            ExceptionDispatchInfo.Capture(entry.Decode.Exception!.InnerException!).Throw();
        }
        var texture = entry.Decode.Result;
        if (texture is null || string.Equals(texture.Name, name, StringComparison.Ordinal)) return texture;
        return new SceneTexture(name, texture.Width, texture.Height, texture.Rgba, texture.Kind);
    }

    private SceneTexture? Inline(Func<CancellationToken, SceneTexture?> decode, CancellationToken token)
    {
        Interlocked.Increment(ref decodes);
        return decode(token);
    }

    private static Key? Identify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        return file.Exists ? new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks) : null;
    }

    public void Dispose()
    {
        Task[] pending;
        lock (background)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            pending = [.. background];
        }
        lifetime.Cancel();
        try { Task.WaitAll(pending); } catch (AggregateException) { }
        lock (entries) entries.Clear();
        lifetime.Dispose();
        budget.Dispose();
    }

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key x, Key y) => x.Length == y.Length && x.WriteTicks == y.WriteTicks && PathComparer.Equals(x.Path, y.Path);
        public int GetHashCode(Key key) => HashCode.Combine(PathComparer.GetHashCode(key.Path), key.Length, key.WriteTicks);
    }
}
