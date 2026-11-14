using System.Text;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>One strict interpretation of original PMM bytes. Page zero means external PMM v1 Unicode metadata.</summary>
public sealed record PmmNameCandidate(int AnsiCodePage, string Value);

/// <summary>Display is separate from the byte-preserving PMM document. A bound name always comes from the selected PMX.</summary>
public sealed record PmmNameBinding(string? BoundName, string DisplayName, IReadOnlyList<PmmNameCandidate> Candidates);

/// <summary>Resolves unmarked Japanese/Chinese PMM names against exact Unicode model names, without statistical encoding guesses.</summary>
public static class PmmNameResolver
{
    /// <summary>Only a unique exact PMX name may bind. Reordered indices, replacement text and encoding popularity are never evidence.</summary>
    public static IReadOnlyList<PmmNameBinding> ResolveTable(IReadOnlyList<PmmString> source, IEnumerable<string> target,
        string kind, ICollection<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var counts = target.GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var results = new PmmNameBinding[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            IReadOnlyList<PmmNameCandidate> candidates = DecodeCandidates(source[i]);
            string[] matches = candidates.Select(c => c.Value).Where(counts.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
            string display = Describe(source[i], candidates);
            if (matches.Length > 1 || (matches.Length == 1 && (counts[matches[0]] != 1 || !bound.Add(matches[0]))))
                throw new InvalidDataException($"PMM {kind} name '{display}' has an ambiguous PMX binding.");
            string? name = matches.Length == 1 ? matches[0] : null;
            results[i] = new(name, name ?? display, candidates);
            if (name is null)
                diagnostics?.Add($"Unmatched PMM {kind} '{display}' at name index {i}; retained as metadata and not applied to a PMX index.");
        }
        return Array.AsReadOnly(results);
    }

    /// <summary>Corrects instance display only when exactly one interpretation agrees with the loaded PMX header.
    /// A custom alias is never replaced with an unrelated model name; uncertain aliases remain explicit alternatives.</summary>
    public static PmmNameBinding ResolveDisplay(PmmString source, IEnumerable<string> modelNames)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(modelNames);
        IReadOnlyList<PmmNameCandidate> candidates = DecodeCandidates(source);
        var targets = modelNames.ToHashSet(StringComparer.Ordinal);
        string[] matches = candidates.Select(c => c.Value).Where(targets.Contains).Distinct(StringComparer.Ordinal).ToArray();
        string? name = matches.Length == 1 ? matches[0] : null;
        return new(name, name ?? Describe(source, candidates), candidates);
    }

    /// <summary>Examines raw bytes independently for each field, because one PMM may contain both encodings.</summary>
    public static IReadOnlyList<PmmNameCandidate> DecodeCandidates(PmmString source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // PMM v1 has no serialized track names; these values came from the explicit external model metadata resolver.
        if (source.RawBytes.Count == 0)
            return string.IsNullOrWhiteSpace(source.Value) ? [] : [new(0, source.Value)];
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Payload(source);
        var candidates = new List<PmmNameCandidate>();
        foreach (int page in new[] { 932, 936 })
        {
            try
            {
                string text = LegacyAnsiEncoding.Create(page).GetString(bytes);
                // Do not re-encode to validate: CP932 has valid duplicate byte spellings whose canonical encoding differs.
                // The strict decoder verifies meaning; the original payload remains authoritative for byte preservation.
                if (!string.IsNullOrWhiteSpace(text)) candidates.Add(new(page, text));
            }
            catch (DecoderFallbackException) { }
        }
        return candidates.AsReadOnly();
    }

    /// <summary>Without a model match, shows all differing strict interpretations rather than choosing a language.</summary>
    public static string Describe(PmmString source)
        => Describe(source, DecodeCandidates(source));

    private static string Describe(PmmString source, IReadOnlyList<PmmNameCandidate> candidates)
    {
        string[] meanings = candidates.Select(c => c.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (meanings.Length == 1) return Visible(meanings[0]);
        if (meanings.Length > 1)
            return string.Join(" / ", candidates.Select(c => $"CP{c.AnsiCodePage}: {Visible(c.Value)}"));
        return source.RawBytes.Count == 0 ? Visible(source.Value) : "0x" + Convert.ToHexString(Payload(source));
    }

    private static byte[] Payload(PmmString source)
        => source.RawBytes.TakeWhile(b => b != 0).ToArray();

    private static string Visible(string name)
        => string.Concat(name.Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()));
}
