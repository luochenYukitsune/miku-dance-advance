using Mmd.Modern.Application;

namespace Mmd.Modern.App;

internal enum EditorFileOpenKind { Project, Pmm, Model, Motion, Pose }

/// <summary>One OS/command-line request. Native identity is checked before any renderer or editor is created.</summary>
internal sealed record EditorFileOpenRequest(string FullPath, EditorFileOpenKind Kind, EditorProjectFormat? ProjectFormat)
{
    internal static EditorFileOpenRequest? Prepare(IReadOnlyList<string> args, EditorPreferences preferences,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(preferences);
        token.ThrowIfCancellationRequested();
        if (args.Count == 0) return null;
        string raw = args.Count switch
        {
            1 when !args[0].StartsWith("--", StringComparison.Ordinal) => args[0],
            2 when args[0] == "--open" => args[1],
            _ => throw new ArgumentException(EditorLocalization.Text("Mda.Text.FileOpen.OneFile"))
        };
        string full = Path.GetFullPath(raw);
        if (!File.Exists(full)) throw new FileNotFoundException(EditorLocalization.Text("Mda.Text.FileOpen.NotFound"), full);
        var kind = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".pma" or ".pmc" => EditorFileOpenKind.Project,
            ".pmm" => EditorFileOpenKind.Pmm,
            ".pmx" or ".pmd" => EditorFileOpenKind.Model,
            ".vmd" => EditorFileOpenKind.Motion,
            ".vpd" => EditorFileOpenKind.Pose,
            _ => throw new NotSupportedException(EditorLocalization.Text("Mda.Text.FileOpen.Unsupported"))
        };
        var format = kind == EditorFileOpenKind.Project
            ? EditorProjectFormat.FromPackageFormat(PmaProjectStore.InspectPackageFormat(full, new()
            {
                MaxPackageBytes = preferences.PmaPackageMaxBytes,
                MaxExpandedBytes = preferences.PmaExpandedMaxBytes,
                MaxEntryBytes = preferences.PmaEntryMaxBytes
            }, token)) : null;
        return new(full, kind, format);
    }
}
