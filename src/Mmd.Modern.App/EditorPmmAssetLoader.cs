using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>One import's immutable assets. No process-wide retention or shared solver state.</summary>
public sealed class EditorPmmAssetLoader(bool retainToonTextures = false,
    Func<int, SceneTexture?>? loadCommonToon = null, Func<SceneTexture?>? loadDefaultToon = null)
{
    private readonly Dictionary<string, (PmxModel Model, SceneAnimationModelAsset Asset)> assets =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, SceneTexture?> textures =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public SceneAnimationModelAsset Load(PreparedPmmModel prepared, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        token.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(prepared.Path);
        if (assets.TryGetValue(path, out var cached) && ReferenceEquals(cached.Model, prepared.Model)) return cached.Asset;
        string root = Path.GetDirectoryName(path)!;
        int textureFailures = 0;
        var asset = EditorProjectWorkspace.CreateModelAsset(prepared.Model, path, EditorTextureLoader.WithModelBudget(relative =>
        {
            // Use the same containment checks as ordinary imports before consulting the cache.
            string texturePath = EditorTextureLoader.ResolveLocalPath(root, relative);
            if (textures.TryGetValue(texturePath, out var texture)) return texture;
            texture = EditorTextureLoader.Load(root, relative, token);
            textures.Add(texturePath, texture);
            return texture;
        }, failures: message => { textureFailures++; System.Diagnostics.Trace.TraceWarning(message); }),
            token, retainToonTextures, loadCommonToon, loadDefaultToon);
        token.ThrowIfCancellationRequested();
        // A model whose texture degraded to the missing image is not cached, so repairing the file and loading
        // again picks up the texture instead of the degraded asset.
        if (textureFailures == 0) assets[path] = (prepared.Model, asset);
        return asset;
    }
}
