namespace Mmd.Modern.App;

/// <summary>Explicit composition for hosts with separately owned preferences and package caches.
/// The ordinary App constructor retains machine-local preference loading and persistence.</summary>
internal sealed record EditorStartupConfiguration(EditorPreferences Preferences, string PackageCacheDirectory,
    bool PersistPreferences = false)
{
    // Explicit hosts keep their existing engine without a modal prompt. Ordinary App()
    // has no configuration and always asks before constructing the editor or a device.
    internal IEditorRenderEngine RenderEngine { get; init; } = EditorModernRenderEngine.Instance;
    internal bool PromptForRenderEngine { get; init; }
    internal EditorPluginService? Plugins { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Preferences);
        Preferences.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(PackageCacheDirectory);
        if (!Path.IsPathFullyQualified(PackageCacheDirectory))
            throw new ArgumentException("The startup package cache requires an absolute path.", nameof(PackageCacheDirectory));
        _ = Path.GetFullPath(PackageCacheDirectory);
        ArgumentNullException.ThrowIfNull(RenderEngine);
        _ = EditorRenderEngineBinding.Require(RenderEngine, RenderEngine.EmptyBinding);
    }
}
