namespace Mmd.Modern.App;

/// <summary>Preflights one bounded file drop before any editor operation can change the workspace.</summary>
internal sealed record EditorFileDropPlan
{
    private const int MaximumPaths = 32;

    private EditorFileDropPlan(EditorFileOpenRequest[] requests) => Requests = Array.AsReadOnly(requests);

    internal IReadOnlyList<EditorFileOpenRequest> Requests { get; }

    /// <summary>Drag feedback checks names only; package inspection and file reads belong to Prepare.</summary>
    internal static bool CanAcceptPaths(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count is < 1 or > MaximumPaths) return false;
        return paths.All(path => !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path).ToLowerInvariant() is
            ".pma" or ".pmc" or ".pmm" or ".pmx" or ".pmd" or ".vmd" or ".vpd");
    }

    internal static EditorFileDropPlan Prepare(IReadOnlyList<string> paths, EditorPreferences preferences,
        EditorProjectFormat currentFormat, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(currentFormat);
        token.ThrowIfCancellationRequested();
        if (!CanAcceptPaths(paths))
            throw EditorLocalization.Error("Mda.Text.FileDrop.InvalidSelection");

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var unique = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(path);
            if (seen.Add(full)) unique.Add(full);
        }
        if (unique.Count > 1 && unique.Any(path => Path.GetExtension(path).ToLowerInvariant() is ".pma" or ".pmc" or ".pmm"))
            throw EditorLocalization.Error("Mda.Text.FileDrop.ProjectAlone");

        var requests = new List<EditorFileOpenRequest>(unique.Count);
        foreach (string path in unique)
        {
            token.ThrowIfCancellationRequested();
            var request = EditorFileOpenRequest.Prepare([path], preferences, token)!;
            if (request.Kind == EditorFileOpenKind.Project && request.ProjectFormat != currentFormat)
                throw new NotSupportedException(EditorLocalization.Text("Mda.Text.FileOpen.EngineMismatch"));
            requests.Add(request);
        }
        token.ThrowIfCancellationRequested();
        return new(requests.Where(request => request.Kind == EditorFileOpenKind.Model)
            .Concat(requests.Where(request => request.Kind != EditorFileOpenKind.Model)).ToArray());
    }
}
