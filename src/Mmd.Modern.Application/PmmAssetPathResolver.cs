using System.Text;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed class PmmImportOptions
{
    public string? MmdDirectory { get; init; }
    public IReadOnlyList<string> SearchRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<int, string> ModelPathOverrides { get; init; } = new Dictionary<int, string>();
    /// <summary>Explicit user selections keyed by stored PMM model index, including an explicitly selected Unspecified.</summary>
    public IReadOnlyDictionary<int, ModelPurpose> ModelPurposes { get; init; } = new Dictionary<int, ModelPurpose>();
}

public enum PmmAssetResolutionStatus { Resolved, Missing, Ambiguous, Invalid }

public sealed record PmmAssetResolution(PmmAssetResolutionStatus Status, string? Path,
    IReadOnlyList<string> References, IReadOnlyList<string> Candidates, IReadOnlyList<string> Diagnostics);

/// <summary>Read-only project resource lookup. Only caller-selected search roots are indexed.</summary>
public sealed class PmmAssetPathResolver
{
    private readonly string projectDirectory;
    private readonly PmmImportOptions options;
    private readonly Dictionary<string, List<string>> filenameIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> indexDiagnostics = [];
    private bool indexed;

    public PmmAssetPathResolver(string projectPath, PmmImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        this.options = options ?? new();
    }

    public PmmAssetResolution Resolve(PmmString reference, string? overridePath = null)
        => Resolve(DecodeCandidates(reference), overridePath);

    public PmmAssetResolution Resolve(IEnumerable<string> references, string? overridePath = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        var names = references.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
        var diagnostics = new List<string>();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string candidate)
        {
            try
            {
                string path = Path.GetFullPath(candidate);
                if (File.Exists(path)) existing.Add(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            { diagnostics.Add($"Invalid path candidate '{candidate}': {e.Message}"); }
        }
        PmmAssetResolution Result()
        {
            string[] candidates = existing.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            return new(candidates.Length switch { 0 => PmmAssetResolutionStatus.Missing, 1 => PmmAssetResolutionStatus.Resolved, _ => PmmAssetResolutionStatus.Ambiguous },
                candidates.Length == 1 ? candidates[0] : null, Array.AsReadOnly(names), Array.AsReadOnly(candidates), diagnostics.AsReadOnly());
        }
        // An explicit repair is authoritative, including when it is invalid/missing.
        if (overridePath is not null) { Add(overridePath); return Result(); }
        foreach (string reference in names)
        {
            string normalized = reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(normalized)) Add(normalized);
            else
            {
                Add(Path.Combine(projectDirectory, normalized));
                if (!string.IsNullOrWhiteSpace(options.MmdDirectory)) Add(Path.Combine(options.MmdDirectory, normalized));
            }
        }
        if (existing.Count > 0) return Result();
        EnsureIndex();
        diagnostics.AddRange(indexDiagnostics);
        foreach (string reference in names)
        {
            string name = Path.GetFileName(reference.Replace('\\', '/'));
            if (filenameIndex.TryGetValue(name, out var candidates))
                foreach (string candidate in candidates) Add(candidate);
        }
        return Result();
    }

    /// <summary>PMM ANSI fields can mix Japanese and Chinese encodings within one project.</summary>
    public static IReadOnlyList<string> DecodeCandidates(PmmString value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(value.Value)) names.Add(value.Value);
        byte[] bytes = value.RawBytes.ToArray();
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        foreach (int page in new[] { 936, 932 })
        {
            try
            {
                string text = LegacyAnsiEncoding.Create(page).GetString(bytes, 0, length);
                if (!string.IsNullOrWhiteSpace(text) && !names.Contains(text, StringComparer.Ordinal)) names.Add(text);
            }
            catch (DecoderFallbackException) { }
        }
        return names.AsReadOnly();
    }

    private void EnsureIndex()
    {
        if (indexed) return;
        indexed = true;
        foreach (string root in options.SearchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) { indexDiagnostics.Add($"Search root is missing: {root}"); continue; }
                var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string file in Directory.EnumerateFiles(root, "*", enumeration))
                {
                    string extension = Path.GetExtension(file);
                    if (!extension.Equals(".pmx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".pmd", StringComparison.OrdinalIgnoreCase)) continue;
                    string key = Path.GetFileName(file);
                    if (!filenameIndex.TryGetValue(key, out var list)) filenameIndex[key] = list = [];
                    list.Add(Path.GetFullPath(file));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { indexDiagnostics.Add($"Cannot index search root '{root}': {e.Message}"); }
        }
    }
}
