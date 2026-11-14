// ALTERED C# CPU-only adaptation of MikuDanceStudio's MIT EmmFile parser.
// Source: third_party/mmeffect/src/mmeffect/emm_manager.{cpp,h}, commit
// bfb6297aa63a37770efcd3f7a599de5dd39be12b. See LICENSE.MikuDanceStudio.txt.
// This is a third-party reconstruction contract, not a claim of original MME equivalence.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

public sealed record EmmReadOptions
{
    /// <summary>Explicit legacy encoding; no system-locale or UTF-8 autodetection.</summary>
    public int AnsiCodePage { get; init; } = 936;
    public long MaxInputBytes { get; init; } = 16L * 1024 * 1024;
    /// <summary>Maximum fgets-equivalent buffers, including blanks and continuation buffers.</summary>
    public int MaxRows { get; init; } = 100_000;

    internal void Validate()
    {
        if (AnsiCodePage is not (932 or 936)) throw new ArgumentOutOfRangeException(nameof(AnsiCodePage));
        if (MaxInputBytes is < 1 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
        if (MaxRows < 1) throw new ArgumentOutOfRangeException(nameof(MaxRows));
    }
}

/// <summary>
/// Reads the full EMM metadata grammar without filesystem, model, effect or GPU access.
/// It preserves the pinned host's first-key registration, rejected-header continuation and 1023-byte text scanner.
/// Strict decoding and explicit budgets are intentional safety boundaries; native locale/CRT edge cases are not claimed equivalent.
/// </summary>
public static class EmmReader
{
    public const int ScannerChunkBytes = 1023;
    private const RegexOptions PatternOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex NamedSection = new(@"^Effect@(([A-Za-z0-9_]+)(\([0-9]+\))?)$", PatternOptions);
    private static readonly Regex ObjectKey = new(@"^(Pmd|Acs)([0-9]+)$", PatternOptions);
    private static readonly Regex OwnerValue = new(@"^((?:Pmd|Acs)[0-9]+)(?:\[([0-9]+)\])?(?:@([A-Za-z0-9_]+(?:\([0-9]+\))?))?$", PatternOptions);
    private static readonly Regex AssignmentKey = new(@"^((?:Pmd|Acs)[0-9]+)(?:\[([0-9]+)\])?(\.show)?$", PatternOptions);

    public static EmmDocument Read(Stream stream, EmmReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new EmmReadOptions();
        options.Validate();
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            // Read at most one byte beyond the budget, including from non-seekable streams.
            int requested = (int)Math.Min(buffer.Length, options.MaxInputBytes - data.Length + 1);
            int count = stream.Read(buffer, 0, requested);
            if (count == 0) break;
            if (data.Length + count > options.MaxInputBytes)
                throw Error("InputTooLarge", "EMM input exceeds the caller's byte budget.");
            data.Write(buffer, 0, count);
        }
        return Parse(data.ToArray(), options);
    }

    public static EmmDocument Read(ReadOnlySpan<byte> bytes, EmmReadOptions? options = null)
    {
        options ??= new EmmReadOptions();
        options.Validate();
        if (bytes.Length > options.MaxInputBytes)
            throw Error("InputTooLarge", "EMM input exceeds the caller's byte budget.");
        return Parse(bytes.ToArray(), options);
    }

    private static EmmDocument Parse(byte[] source, EmmReadOptions options)
    {
        if (source.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })
            || source.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })
            || source.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            throw Error("UnsupportedBom", "EMM requires explicitly selected ANSI bytes; a Unicode BOM is unsupported.");

        var state = new ParseState();
        Encoding encoding = LegacyAnsiEncoding.Create(options.AnsiCodePage);
        int position = 0;
        int rowNumber = 0;
        var translated = new byte[ScannerChunkBytes];
        while (position < source.Length && source[position] != 0x1a)
        {
            if (rowNumber >= options.MaxRows)
                throw Error("TooManyRows", "EMM input exceeds the caller's scanner-buffer budget.", state.Diagnostics, rowNumber + 1, position);
            int start = position;
            int count = 0;
            while (position < source.Length && count < ScannerChunkBytes && source[position] != 0x1a)
            {
                byte current = source[position++];
                if (current == 13 && position < source.Length && source[position] == 10)
                { position++; current = 10; }
                translated[count++] = current;
                if (current == 10) break;
            }
            rowNumber++;
            string raw;
            string text;
            try
            {
                raw = encoding.GetString(source.AsSpan(start, position - start));
                // std::string(char*) truncates an fgets buffer at an embedded NUL.
                int nul = Array.IndexOf(translated, (byte)0, 0, count);
                if (nul >= 0)
                {
                    state.Diagnostics.Add(new("EmbeddedNull", "An embedded NUL truncates this scanner buffer; original bytes remain preserved.", rowNumber, start + nul));
                    count = nul;
                }
                text = encoding.GetString(translated, 0, count);
            }
            catch (DecoderFallbackException ex)
            {
                throw Error("InvalidEncoding", "Invalid ANSI bytes, including a multibyte character split at a 1023-byte scanner boundary.",
                    state.Diagnostics, rowNumber, start, ex);
            }
            text = Trim(StripComment(text));
            state.ReadRow(rowNumber, start, position - start, raw, text);
        }
        if (position < source.Length)
            state.Diagnostics.Add(new("TextModeEndOfFile", "A DOS text-mode EOF marker stops scanning; remaining original bytes remain preserved.", rowNumber + 1, position));

        state.Validate(rowNumber, position);
        return new EmmDocument(source, state.Version, options.AnsiCodePage,
            state.Objects.Select(o => new EmmObjectRecord(o.IsModel, o.Slot, o.Path)),
            state.Sections.Select(s => new EmmEffectSection(s.Name, s.BaseName, s.Owner, s.DefaultPath,
                s.Entries.Select(e => new EmmEffectEntry(e.Key, e.Path, e.Shown, e.Paths, e.Shows)))), state.Diagnostics, state.Rows);
    }

    private static string StripComment(string text)
    {
        int index = text.IndexOfAny([';', '#']);
        return index < 0 ? text : text[..index];
    }
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\v' or '\f';
    private static string Trim(string text)
    {
        int first = 0, last = text.Length;
        while (first < last && IsSpace(text[first])) first++;
        while (last > first && IsSpace(text[last - 1])) last--;
        return text[first..last];
    }
    private static bool TryNativeInt(string text, out int value)
    {
        value = 0;
        int start = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        if (start == text.Length || text.Length == 0) return false;
        long result = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9') return false;
            result = result * 10 + text[i] - '0';
            if (result > int.MaxValue) return false;
        }
        value = (int)(text[0] == '-' ? -result : result);
        return true;
    }
    private static EmmFormatException Error(string code, string message, IEnumerable<EmmDiagnostic>? earlier = null,
        int row = 0, long offset = 0, Exception? inner = null)
    {
        var diagnostics = earlier?.ToList() ?? [];
        diagnostics.Add(new(code, message, row, offset, EmmDiagnosticSeverity.Error));
        return new EmmFormatException(message, diagnostics, inner);
    }

    private sealed class ParseState
    {
        public int Version;
        private bool versionParsed;
        private string currentName = "";
        private SectionBuilder? currentEffect;
        private readonly HashSet<string> sectionNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> seenKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<(bool IsModel, int Slot), ObjectBuilder> objectsBySlot = [];
        public readonly List<ObjectBuilder> Objects = [];
        public readonly List<SectionBuilder> Sections = [];
        public readonly List<EmmDiagnostic> Diagnostics = [];
        public readonly List<EmmSourceRow> Rows = [];

        public void ReadRow(int number, int offset, int length, string raw, string text)
        {
            string? key = null, value = null;
            bool accepted = false;
            var kind = EmmSourceRowKind.Blank;
            string? rejection = null;
            if (text.Length > 0)
            {
                if (text.Length >= 2 && text[0] == '[' && text[^1] == ']' && !text.AsSpan(1, text.Length - 2).ContainsAny('[', ']'))
                {
                    kind = EmmSourceRowKind.Section;
                    rejection = ReadSection(text[1..^1]);
                    accepted = rejection is null;
                }
                else
                {
                    int equals = text.IndexOf('=');
                    if (equals >= 0)
                    {
                        key = Trim(text[..equals]);
                        if (key.Length > 0 && key.IndexOfAny([' ', '\t']) < 0)
                        {
                            kind = EmmSourceRowKind.KeyValue;
                            value = Trim(text[(equals + 1)..]);
                            rejection = ReadValue(key, value);
                            accepted = rejection is null;
                        }
                        else { kind = EmmSourceRowKind.Malformed; rejection = "MalformedLine"; }
                    }
                    else { kind = EmmSourceRowKind.Malformed; rejection = "MalformedLine"; }
                }
            }
            if (rejection is not null)
                Diagnostics.Add(new(rejection, Describe(rejection), number, offset));
            Rows.Add(new(number, offset, length, raw, text, currentName, key, value, kind, accepted));
        }

        private string? ReadSection(string name)
        {
            // Unknown names occupy the registry as in the pinned host, but leave the active section unchanged.
            if (!sectionNames.Add(name)) return "DuplicateSection";
            if (name is "Info" or "Object") { currentName = name; return null; }
            string baseName;
            if (name == "Effect") baseName = "";
            else
            {
                var match = NamedSection.Match(name);
                if (!match.Success) return "UnknownSection";
                baseName = match.Groups[2].Value;
            }
            currentName = name;
            currentEffect = new SectionBuilder(name, baseName);
            Sections.Add(currentEffect);
            return null;
        }

        private string? ReadValue(string key, string value)
        {
            if (!seenKeys.TryGetValue(currentName, out var seen))
                seenKeys.Add(currentName, seen = new HashSet<string>(StringComparer.Ordinal));
            if (!seen.Add(key)) return "DuplicateKey";
            if (value.Length == 0) return "EmptyValue";
            if (currentName == "Info")
            {
                if (key != "Version") return "UnknownKey";
                if (!TryNativeInt(value, out int version)) return "InvalidVersion";
                Version = version; versionParsed = true;
                return version is >= 1 and <= 3 ? null : "UnsupportedVersion";
            }
            if (currentName == "Object")
            {
                var match = ObjectKey.Match(key);
                if (!match.Success) return "InvalidObjectKey";
                if (!TryNativeInt(match.Groups[2].Value, out int slot)) return "InvalidObjectSlot";
                bool isModel = match.Groups[1].Value == "Pmd";
                if (objectsBySlot.TryGetValue((isModel, slot), out var record)) record.Path = value;
                else
                {
                    record = new ObjectBuilder(isModel, slot, value);
                    objectsBySlot.Add((isModel, slot), record);
                    Objects.Add(record);
                }
                return null;
            }
            if (!currentName.StartsWith("Effect", StringComparison.Ordinal) || currentEffect is null) return "UnknownKey";
            if (key == "Default")
            {
                if (currentEffect.BaseName.Length != 0) return "DefaultInNamedSection";
                currentEffect.DefaultPath = value;
                return null;
            }
            if (key == "Owner")
            {
                var owner = OwnerValue.Match(value);
                if (!owner.Success) return "InvalidOwner";
                int subset = -1;
                if (owner.Groups[2].Success && !TryNativeInt(owner.Groups[2].Value, out subset)) return "InvalidSubset";
                currentEffect.Owner = new(owner.Groups[1].Value, subset, owner.Groups[3].Success ? owner.Groups[3].Value : null);
                return null;
            }
            var assignment = AssignmentKey.Match(key);
            if (!assignment.Success) return "InvalidAssignmentKey";
            int subsetIndex = -1;
            if (assignment.Groups[2].Success && !TryNativeInt(assignment.Groups[2].Value, out subsetIndex)) return "InvalidSubset";
            string objectKey = assignment.Groups[1].Value;
            if (!currentEffect.EntriesByKey.TryGetValue(objectKey, out var entry))
            {
                entry = new EntryBuilder(objectKey);
                currentEffect.EntriesByKey.Add(objectKey, entry);
                currentEffect.Entries.Add(entry);
            }
            if (assignment.Groups[3].Success)
            {
                if (value is not ("true" or "false")) return "InvalidShown";
                bool shown = value == "true";
                if (subsetIndex < 0) entry.Shown = shown;
                else entry.Shows[subsetIndex] = shown;
            }
            else if (subsetIndex < 0) entry.Path = value;
            else entry.Paths[subsetIndex] = value;
            return null;
        }

        public void Validate(int row, int offset)
        {
            if (!sectionNames.Contains("Info")) Fail("MissingInfo", "Required section 'Info' was not found.");
            if (!versionParsed || Version == 0) Fail("MissingVersion", "A nonzero Version was not parsed.");
            if (!sectionNames.Contains("Object")) Fail("MissingObject", "Required section 'Object' was not found.");
            if (!sectionNames.Contains("Effect")) Fail("MissingEffect", "Required section 'Effect' was not found.");
            var objectKeys = Objects.Select(o => (o.IsModel ? "Pmd" : "Acs") + o.Slot.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            foreach (var section in Sections)
            {
                if (section.BaseName.Length == 0)
                {
                    if (section.DefaultPath is null) Fail("MissingDefault", "Required key 'Default' was not found.");
                }
                else
                {
                    if (section.Owner is null) Fail("MissingOwner", $"Required Owner was not found in '{section.Name}'.");
                    if (!objectKeys.Contains(section.Owner!.ObjectKey)) Fail("UnknownOwner", $"Unknown owner object '{section.Owner.ObjectKey}'.");
                }
                foreach (var entry in section.Entries)
                    if (!objectKeys.Contains(entry.Key)) Fail("UnknownObject", $"Unknown assignment object '{entry.Key}'.");
            }
            return;
            void Fail(string code, string message) => throw Error(code, message, Diagnostics, row, offset);
        }

        private static string Describe(string code) => code switch
        {
            "UnsupportedVersion" => "The pinned host retains this nonzero version after a failed version gate. Runtime use must reject unsupported versions.",
            "DuplicateSection" => "A section name was already registered; the previous active section remains in use.",
            "UnknownSection" => "An unknown section name was registered but rejected; the previous active section remains in use.",
            "DuplicateKey" => "A key was already registered in the active section; its first row wins, including rejected first rows.",
            "EmptyValue" => "An empty value was rejected after its key was registered.",
            _ => "The scanner row was rejected (" + code + "); its original bytes remain preserved."
        };
    }

    private sealed class ObjectBuilder(bool isModel, int slot, string path)
    { public readonly bool IsModel = isModel; public readonly int Slot = slot; public string Path = path; }
    private sealed class SectionBuilder(string name, string baseName)
    {
        public readonly string Name = name;
        public readonly string BaseName = baseName;
        public EmmOwnerReference? Owner;
        public string? DefaultPath;
        public readonly List<EntryBuilder> Entries = [];
        public readonly Dictionary<string, EntryBuilder> EntriesByKey = new(StringComparer.Ordinal);
    }
    private sealed class EntryBuilder(string key)
    {
        public readonly string Key = key;
        public string? Path;
        public bool? Shown;
        public readonly Dictionary<int, string> Paths = [];
        public readonly Dictionary<int, bool> Shows = [];
    }
}
