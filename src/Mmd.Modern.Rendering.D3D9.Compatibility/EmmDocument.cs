using System.Collections.ObjectModel;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>Immutable parsed EMM metadata. Parsing does not load objects, effects, or graphics devices.</summary>
public sealed class EmmDocument
{
    private readonly byte[] sourceBytes;

    internal EmmDocument(byte[] sourceBytes, int version, int ansiCodePage, IEnumerable<EmmObjectRecord> objects,
        IEnumerable<EmmEffectSection> sections, IEnumerable<EmmDiagnostic> diagnostics, IEnumerable<EmmSourceRow> rows)
    {
        this.sourceBytes = (byte[])sourceBytes.Clone();
        Version = version;
        AnsiCodePage = ansiCodePage;
        Objects = Array.AsReadOnly(objects.ToArray());
        EffectSections = Array.AsReadOnly(sections.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        Rows = Array.AsReadOnly(rows.ToArray());
    }

    public int Version { get; }
    /// <summary>Versions 1–3 are supported by the pinned reader. A higher layer must reject unsupported versions before use.</summary>
    public bool IsSupportedVersion => Version is >= 1 and <= 3;
    public bool SupportedVersion => IsSupportedVersion;
    public int AnsiCodePage { get; }
    public ReadOnlyCollection<EmmObjectRecord> Objects { get; }
    public ReadOnlyCollection<EmmEffectSection> EffectSections { get; }
    public ReadOnlyCollection<EmmDiagnostic> Diagnostics { get; }
    /// <summary>Scanner buffers in order, including blank, unknown, and rejected buffers. A long physical line spans buffers.</summary>
    public ReadOnlyCollection<EmmSourceRow> Rows { get; }
    /// <summary>Returns independent original bytes, including unsupported rows and bytes beyond a text-mode EOF marker.</summary>
    public byte[] CopySourceBytes() => (byte[])sourceBytes.Clone();
}

public sealed class EmmObjectRecord
{
    internal EmmObjectRecord(bool isModel, int slot, string path)
    { IsModel = isModel; Slot = slot; Path = path; }
    /// <summary>Canonical slot spelling; distinct input spellings such as Pmd00 and Pmd0 share the same object record.</summary>
    public string Key => (IsModel ? "Pmd" : "Acs") + Slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool IsModel { get; }
    public int Slot { get; }
    public string Path { get; }
}

public sealed record EmmOwnerReference(string ObjectKey, int Subset = -1, string? ParentTargetLabel = null);

public sealed class EmmEffectSection
{
    internal EmmEffectSection(string name, string baseName, EmmOwnerReference? owner, string? defaultPath,
        IEnumerable<EmmEffectEntry> entries)
    {
        Name = name; BaseName = baseName; Owner = owner; DefaultPath = defaultPath;
        Entries = Array.AsReadOnly(entries.ToArray());
    }
    public string Name { get; }
    public string BaseName { get; }
    public bool IsDefault => BaseName.Length == 0;
    public EmmOwnerReference? Owner { get; }
    /// <summary>Null means absent; the literal "none" remains an explicit row value.</summary>
    public string? DefaultPath { get; }
    public ReadOnlyCollection<EmmEffectEntry> Entries { get; }
}

public sealed class EmmEffectEntry
{
    internal EmmEffectEntry(string objectKey, string? effectPath, bool? shown,
        IDictionary<int, string> subsetPaths, IDictionary<int, bool> subsetShown)
    {
        ObjectKey = objectKey; EffectPath = effectPath; Shown = shown;
        SubsetPaths = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>(subsetPaths));
        SubsetShown = new ReadOnlyDictionary<int, bool>(new Dictionary<int, bool>(subsetShown));
    }
    public string ObjectKey { get; }
    /// <summary>Null means absent; "none", "hide", and "main_default" are preserved without interpretation.</summary>
    public string? EffectPath { get; }
    public bool? Shown { get; }
    public IReadOnlyDictionary<int, string> SubsetPaths { get; }
    public IReadOnlyDictionary<int, bool> SubsetShown { get; }
}

public enum EmmDiagnosticSeverity { Warning, Error }

public sealed record EmmDiagnostic(string Code, string Message, int RowNumber = 0,
    long SourceByteOffset = 0, EmmDiagnosticSeverity Severity = EmmDiagnosticSeverity.Warning);

public enum EmmSourceRowKind { Blank, Section, KeyValue, Malformed }

/// <summary>One fgets-equivalent buffer. RawText retains original CRLF bytes decoded using the explicitly selected ANSI page.</summary>
public sealed record EmmSourceRow(int Number, long SourceByteOffset, int SourceByteLength, string RawText,
    string Text, string SectionName, string? Key, string? Value, EmmSourceRowKind Kind, bool IsAccepted);
