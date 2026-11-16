using System.Globalization;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>Detached EMM authoring. Original bytes and unrelated/unknown rows are retained;
/// no source file, effect or device is opened. A failed edit leaves the input untouched.</summary>
public static class EmmDocumentEditor
{
    /// <summary>Creates an external mapping sidecar whose file references do not depend on the
    /// new sidecar directory. This does not export the dependency files carried by a PMC.</summary>
    public static byte[] ExportAbsolutePaths(EmmImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var document = plan.Document;
        foreach (var obj in plan.Document.Objects)
        {
            string path = plan.MatchesByRecordKey.TryGetValue(obj.Key, out var match)
                ? plan.LiveObjects.Single(value => value.ObjectId == match.ObjectId).SourcePath : obj.Path;
            document = SetValue(document, "Object", obj.Key,
                D3D9EffectProjectRootPaths.OriginalPath(path, plan.PathContext.WorkingDirectory));
        }
        foreach (var section in plan.Document.EffectSections)
        {
            if (section.DefaultPath is { } value && IsFile(value, section))
                document = SetValue(document, section.Name, "Default", Absolute(value));
            foreach (var entry in section.Entries)
            {
                if (entry.EffectPath is { } effect && IsFile(effect, section))
                    document = SetEffect(document, section.Name, entry.ObjectKey, -1, Absolute(effect));
                foreach (var subset in entry.SubsetPaths)
                    if (IsFile(subset.Value, section)) document = SetEffect(document, section.Name, entry.ObjectKey, subset.Key, Absolute(subset.Value));
            }
        }
        return document.CopySourceBytes();
        string Absolute(string value) => D3D9EffectProjectRootPaths.OriginalPath(value, plan.PathContext.WorkingDirectory);
        static bool IsFile(string value, EmmEffectSection section)
            => D3D9EffectProjectRootPaths.EffectKind(value, !section.IsDefault) == D3D9EmmRuntimeEffectKind.File;
    }

    public static EmmDocument SetEffect(EmmDocument document, string section, string objectKey,
        int subset, string? effect)
    {
        RequireObject(document, objectKey);
        RequireEffectSection(document, section);
        if (subset < -1) throw new ArgumentOutOfRangeException(nameof(subset));
        return SetValue(document, section, Key(objectKey, subset), effect);
    }

    public static EmmDocument SetShown(EmmDocument document, string section, string objectKey,
        int subset, bool? shown)
    {
        RequireObject(document, objectKey);
        RequireEffectSection(document, section);
        if (subset < -1) throw new ArgumentOutOfRangeException(nameof(subset));
        return SetValue(document, section, Key(objectKey, subset) + ".show", shown is null ? null : shown.Value ? "true" : "false");
    }

    public static EmmDocument AddObject(EmmDocument document, bool isModel, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        int slot = 0;
        var used = document.Objects.Where(value => value.IsModel == isModel).Select(value => value.Slot).ToHashSet();
        while (used.Contains(slot)) slot = checked(slot + 1);
        return SetValue(document, "Object", (isModel ? "Pmd" : "Acs") + slot.ToString(CultureInfo.InvariantCulture), sourcePath);
    }

    private static string Key(string key, int subset)
        => subset < 0 ? key : key + "[" + subset.ToString(CultureInfo.InvariantCulture) + "]";

    private static void RequireEffectSection(EmmDocument document, string section)
    {
        if (!document.EffectSections.Any(item => item.Name == section))
            throw new ArgumentException("Select an existing EMM effect section.", nameof(section));
    }

    private static void RequireObject(EmmDocument document, string key)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Objects.Any(value => value.Key == key))
            throw new ArgumentException("The EMM object row must exist before assigning an effect.", nameof(key));
    }

    private static EmmDocument SetValue(EmmDocument document, string section, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (section != "Object" && !document.EffectSections.Any(item => item.Name == section))
            throw new ArgumentException("Select an existing EMM effect section.", nameof(section));
        if (value is not null && (value.Length == 0 || value.Trim() != value || value.IndexOfAny(['\r', '\n', '\0', '\u001a', '#', ';']) >= 0))
            throw new ArgumentException("An EMM value must be nonempty and contain no line/control terminator.", nameof(value));
        var encoding = LegacyAnsiEncoding.Create(document.AnsiCodePage);
        byte[] replacement = value is null ? [] : encoding.GetBytes(key + "=" + value + "\r\n");
        if (replacement.Length > 1023)
            throw new ArgumentException("The assignment exceeds the original MME text-line limit.", nameof(value));
        byte[] source = document.CopySourceBytes();
        var rows = document.Rows.Where(row => row.SectionName == section && row.Kind == EmmSourceRowKind.KeyValue && row.Key == key).ToArray();
        using var output = new MemoryStream();
        if (rows.Length != 0)
        {
            int offset = 0;
            for (int index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                int start = checked((int)row.SourceByteOffset);
                output.Write(source.AsSpan(offset, start - offset));
                if (index == 0) output.Write(replacement);
                offset = checked(start + row.SourceByteLength);
            }
            output.Write(source.AsSpan(offset));
        }
        else
        {
            var header = document.Rows.FirstOrDefault(row => row.IsAccepted && row.Kind == EmmSourceRowKind.Section && row.SectionName == section)
                ?? throw new InvalidDataException("The EMM section has no accepted source header.");
            long end = document.Rows.Where(row => row.SourceByteOffset > header.SourceByteOffset && row.IsAccepted && row.Kind == EmmSourceRowKind.Section)
                .Select(row => row.SourceByteOffset).DefaultIfEmpty(document.Rows.Max(row => row.SourceByteOffset + row.SourceByteLength)).Min();
            int insertion = checked((int)end);
            output.Write(source.AsSpan(0, insertion));
            if (replacement.Length != 0 && insertion > 0 && source[insertion - 1] != (byte)'\n') output.Write("\r\n"u8);
            output.Write(replacement);
            output.Write(source.AsSpan(insertion));
        }
        return EmmReader.Read(output.ToArray(), new() { AnsiCodePage = document.AnsiCodePage });
    }
}
