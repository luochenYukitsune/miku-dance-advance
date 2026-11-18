using System.Runtime.ExceptionServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>Background model parsing and image decoding for one project open. The project reader still asks
/// for every model in layout order; each request joins the prepared result or prepares it inline, so results,
/// diagnostics and the first reported failure are those of sequential loading. Disposing cancels and joins all
/// background work and releases every cached entry; nothing is retained after the open returns.</summary>
internal sealed class EditorAssetPrefetch : IDisposable
{
    private static readonly AsyncLocal<EditorAssetPrefetch?> current = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly EditorAssetPrefetch? previous;
    private readonly long modelMaxBytes;
    private readonly Dictionary<string, Task<PmxModel>?> models = new(PathComparer);

    private EditorAssetPrefetch(long modelMaxBytes, EditorAssetPrefetch? previous)
    { this.modelMaxBytes = modelMaxBytes; this.previous = previous; }

    internal static EditorAssetPrefetch? Current => current.Value;
    internal SceneTextureCache Textures { get; } = new();

    internal static EditorAssetPrefetch Begin(long modelMaxBytes)
    {
        var scope = new EditorAssetPrefetch(modelMaxBytes, current.Value);
        current.Value = scope;
        return scope;
    }

    /// <summary>Starts parsing each distinct model and decoding its images under the shared budget.</summary>
    internal void PrefetchModels(IReadOnlyList<string> paths, CancellationToken token)
    {
        lock (models)
            foreach (var path in paths)
                if (!models.ContainsKey(path))
                    models.Add(path, Textures.Schedule(cancellation =>
                    {
                        var model = EditorProjectWorkspace.ReadModel(path, modelMaxBytes, cancellation);
                        PrefetchTextures(Textures, model, Path.GetDirectoryName(Path.GetFullPath(path))!, cancellation);
                        return model;
                    }, token));
    }

    /// <summary>The project loader callback. A prepared parse is consumed once; repeated instances of the
    /// same file parse again, exactly as before, and reuse the decoded images.</summary>
    internal SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    {
        Task<PmxModel>? prepared = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            string normalized = Path.GetFullPath(path);
            lock (models) if (models.TryGetValue(normalized, out prepared)) models[normalized] = null;
        }
        PmxModel? model = null;
        if (prepared is not null)
        {
            try { prepared.Wait(token); }
            catch (AggregateException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();
            if (prepared.IsCompletedSuccessfully) model = prepared.Result;
            else if (prepared.IsFaulted && prepared.Exception!.InnerException is not OperationCanceledException)
                ExceptionDispatchInfo.Capture(prepared.Exception.InnerException!).Throw();
        }
        model ??= EditorProjectWorkspace.ReadModel(path!, modelMaxBytes, token);
        return EditorProjectWorkspace.CreateModelAsset(model, path!, token);
    }

    internal static void PrefetchTextures(SceneTextureCache cache, PmxModel model, string root, CancellationToken token,
        Func<string, bool>? retained = null)
    {
        foreach (var relative in PmxOriginalStyleAdapter.RequestedTextures(model))
        {
            string path;
            // Invalid tokens are reported by the ordered load, with its original message.
            try { path = EditorTextureLoader.ResolveLocalPath(root, relative); }
            catch (Exception) { continue; }
            if (retained?.Invoke(path) == true) continue;
            cache.Prefetch(path, relative, cancellation => EditorTextureLoader.Load(root, relative, cancellation), token);
        }
    }

    internal static SceneTexture? LoadTexture(SceneTextureCache cache, string root, string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = EditorTextureLoader.ResolveLocalPath(root, relative);
        return cache.Load(path, relative, cancellation => EditorTextureLoader.Load(root, relative, cancellation), token);
    }

    /// <summary>Uses the current open's images when one is active; otherwise decodes directly.</summary>
    internal static SceneTexture? LoadTexture(string root, string relative, CancellationToken token)
        => Current is { } scope ? LoadTexture(scope.Textures, root, relative, token) : EditorTextureLoader.Load(root, relative, token);

    public void Dispose()
    {
        if (ReferenceEquals(current.Value, this)) current.Value = previous;
        Textures.Dispose();
    }
}
