namespace Mmd.Modern.App;

internal sealed record EditorRecoveryDiscovery(
    IReadOnlyList<EditorRecoverySessionInfo> Sessions,
    IReadOnlyDictionary<string, EditorRecoveryStore> Sources,
    IReadOnlyList<Exception> Errors)
{
    /// <param name="includeDeferred">Also list interrupted sessions whose startup offer was already shown and deferred
    /// ("Later"). The startup prompt leaves them out; the explicit Recover command includes them, so a deferred copy
    /// stays reachable instead of only occupying the recovery budget.</param>
    internal static async Task<EditorRecoveryDiscovery> ScanAsync(EditorPreferences preferences, CancellationToken token = default,
        bool includeDeferred = false)
    {
        var sessions = new List<EditorRecoverySessionInfo>();
        var sources = new Dictionary<string, EditorRecoveryStore>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<Exception>();
        foreach (string root in preferences.RecoverySearchDirectories.Prepend(preferences.RecoveryDirectory)
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Historical locations can be removed or remapped since they were chosen. An
                // unavailable old location must not hide healthy copies in another location.
                if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network)
                    throw new IOException("A recovery search location is now a network drive.");
                var store = new EditorRecoveryStore(root, new(preferences.RecoveryMaxVersions, preferences.RecoveryMaxTotalBytes));
                var listed = includeDeferred ? await store.ListRecoverableAsync(token).ConfigureAwait(false)
                    : await store.ListStartupRecoverableAsync(token).ConfigureAwait(false);
                foreach (var session in listed)
                {
                    sessions.Add(session);
                    foreach (var checkpoint in session.Checkpoints) sources.Add(checkpoint.PackagePath, store);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error.Data["Recovery.SearchDirectory"] = root;
                errors.Add(error);
            }
        }
        return new(sessions, sources, errors);
    }
}
