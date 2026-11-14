namespace Mmd.Modern.Effects;

/// <summary>Result of translating a legacy effect into the C# effect API.</summary>
public sealed record ImportResult(
    string Path,
    Neo.EffectDefinition? Definition,
    IReadOnlyList<MmeDiagnostic> Diagnostics)
{
    public bool Success => Definition is not null
        && Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
}

public sealed record EffectLoadResult(
    string Path,
    FxEffect? Effect,
    IReadOnlyList<MmeDiagnostic> Diagnostics,
    IReadOnlyList<string> Includes)
{
    public bool Success => Effect is not null
        && Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);

    public IEnumerable<MmeDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>Aggregate view of a corpus scan; the numbers a compatibility claim has to match.</summary>
public sealed record EffectScanSummary(
    int Files,
    int Techniques,
    IReadOnlyDictionary<string, int> MmdPassHistogram,
    IReadOnlyDictionary<string, int> AnnotationHistogram,
    IReadOnlyDictionary<string, int> ScriptCommandHistogram,
    IReadOnlyDictionary<string, int> ProfileHistogram,
    int Errors,
    int Warnings)
{
    public static EffectScanSummary From(IEnumerable<EffectLoadResult> results)
    {
        var list = results.ToList();
        var mmdPass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var annotations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var commands = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var profiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int techniques = 0;

        foreach (var result in list)
        {
            foreach (var technique in result.Effect?.Techniques ?? [])
            {
                techniques++;
                if (technique.MmdPass is { } mmdPassValue)
                {
                    Bump(mmdPass, mmdPassValue.Trim('"'));
                }

                foreach (var key in technique.Annotations.Keys)
                {
                    Bump(annotations, key);
                }

                foreach (var pass in technique.Passes)
                {
                    foreach (var profile in pass.Profiles)
                    {
                        Bump(profiles, profile);
                    }
                }

                foreach (var command in EffectScript.Parse(technique.Script))
                {
                    Bump(commands, command.Name);
                }
            }
        }

        return new EffectScanSummary(
            list.Count,
            techniques,
            mmdPass,
            annotations,
            commands,
            profiles,
            list.Sum(r => r.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)),
            list.Sum(r => r.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning)));
    }

    private static void Bump(Dictionary<string, int> map, string key) =>
        map[key] = map.TryGetValue(key, out int value) ? value + 1 : 1;
}

/// <summary>
/// Loads MME effects from disk: decode, preprocess, parse, and validate.
/// </summary>
public static class EffectLibrary
{
    private static readonly string[] EffectExtensions = [".fx", ".fxsub", ".fxm", ".fxh", ".inc"];

    public static EffectLoadResult Load(string path, bool preprocess = true)
    {
        var diagnostics = new List<MmeDiagnostic>();
        string? text = ReadSource(path, diagnostics);
        if (text is null)
        {
            return new EffectLoadResult(path, null, diagnostics, []);
        }

        string originalSource = text;
        IReadOnlyList<string> includes = [];
        if (preprocess)
        {
            var result = FxPreprocessor.Process(path, ReadFileDecoded, text);
            text = result.Text;
            includes = result.Includes;
            diagnostics.AddRange(result.Diagnostics);
        }

        var tokens = FxTokenizer.Tokenize(text);
        var effect = FxParser.Parse(tokens, path, diagnostics, text) with { OriginalSource = originalSource, Includes = includes };
        return new EffectLoadResult(path, effect, diagnostics, includes);
    }

    /// <summary>
    /// Translates a legacy <c>.fx</c> into the C# effect API.
    /// </summary>
    public static ImportResult Import(string path, bool preprocess = true)
    {
        var load = Load(path, preprocess);
        var diagnostics = new List<MmeDiagnostic>(load.Diagnostics);
        if (load.Effect is null)
        {
            return new ImportResult(path, null, diagnostics);
        }

        var definition = new Neo.ImportedEffect(load.Effect, diagnostics).Build();
        return new ImportResult(path, definition, diagnostics);
    }

    public static IReadOnlyList<EffectLoadResult> Scan(string directory, bool preprocess = true)
    {
        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => EffectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        return files.Select(f => Load(f, preprocess)).ToList();
    }

    private static string? ReadFileDecoded(string path) =>
        File.Exists(path) ? FxSourceText.Decode(File.ReadAllBytes(path)).Text : null;

    private static string? ReadSource(string path, ICollection<MmeDiagnostic> diagnostics)
    {
        if (!File.Exists(path))
        {
            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, MmeDiagnosticCodes.IncludeNotFound,
                "file not found", path));
            return null;
        }

        var (text, fallback) = FxSourceText.Decode(File.ReadAllBytes(path));
        if (fallback)
        {
            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Info, MmeDiagnosticCodes.EncodingFallback,
                "decoded as Shift-JIS (invalid UTF-8)", path));
        }

        return text;
    }
}
