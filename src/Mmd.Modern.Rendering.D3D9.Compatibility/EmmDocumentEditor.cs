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

    /// <summary>追加一个只有 Owner 行的命名分配页，对应 FX 中声明的一个 OFFSCREENRENDERTARGET。
    /// 标签沿用宿主写出 EMM 时的编号规则：同一基名第一次为 baseName，之后依次为 baseName(1)、baseName(2)……
    /// 原有字节全部保留；新页追加在最后一个扫描行之后、文本模式 EOF 标记之前。不写对象行，
    /// 默认分配由 FX 自身的 DefaultEffect 在宿主运行时决定。</summary>
    public static EmmDocument AddNamedSection(EmmDocument document, string baseName, EmmOwnerReference owner)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrEmpty(baseName) || !baseName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("A named EMM section requires an ASCII identifier base name.", nameof(baseName));
        RequireObject(document, owner.ObjectKey);
        if (owner.Subset < -1) throw new ArgumentOutOfRangeException(nameof(owner));
        if (owner.ParentTargetLabel is { } parent && !document.EffectSections.Any(item => !item.IsDefault && item.Name == "Effect@" + parent))
            throw new ArgumentException("The parent target label must name an existing EMM section.", nameof(owner));
        // 被拒绝的页头也占用了宿主的页名登记表，因此按全部扫描到的页头避让。
        var used = document.Rows.Where(row => row.Kind == EmmSourceRowKind.Section && row.Text.Length >= 2)
            .Select(row => row.Text[1..^1]).ToHashSet(StringComparer.Ordinal);
        string label = baseName;
        for (int index = 1; used.Contains("Effect@" + label); index = checked(index + 1))
            label = baseName + "(" + index.ToString(CultureInfo.InvariantCulture) + ")";
        string value = Key(owner.ObjectKey, owner.Subset) + (owner.ParentTargetLabel is null ? "" : "@" + owner.ParentTargetLabel);
        var encoding = LegacyAnsiEncoding.Create(document.AnsiCodePage);
        byte[] header = encoding.GetBytes("[Effect@" + label + "]\r\n"), ownerRow = encoding.GetBytes("Owner=" + value + "\r\n");
        if (header.Length > 1023 || ownerRow.Length > 1023)
            throw new ArgumentException("The named section exceeds the original MME text-line limit.", nameof(baseName));
        byte[] source = document.CopySourceBytes();
        int insertion = ScannedEnd(document);
        using var output = new MemoryStream();
        output.Write(source.AsSpan(0, insertion));
        if (insertion > 0 && source[insertion - 1] != (byte)'\n') output.Write("\r\n"u8);
        output.Write(header); output.Write(ownerRow);
        output.Write(source.AsSpan(insertion));
        return EmmReader.Read(output.ToArray(), new() { AnsiCodePage = document.AnsiCodePage });
    }

    /// <summary>页内除页头外只有已接受的 Owner 行（可夹空行和注释）时为 true。
    /// 任何分配、显示、未知或被拒绝的行都视为用户内容。</summary>
    public static bool IsOwnerOnly(EmmDocument document, string section)
    {
        var (start, end) = NamedSectionRange(document, section);
        return document.Rows.Where(row => row.SourceByteOffset > start && row.SourceByteOffset < end).All(row =>
            row.Kind == EmmSourceRowKind.Blank || row.Kind == EmmSourceRowKind.KeyValue && row.IsAccepted && row.Key == "Owner");
    }

    /// <summary>删除一个命名页的全部字节（页头到下一个已接受页头之间）。仍被其他页当作父页时拒绝。</summary>
    public static EmmDocument RemoveNamedSection(EmmDocument document, string section)
    {
        var (start, end) = NamedSectionRange(document, section);
        string label = section["Effect@".Length..];
        if (document.EffectSections.Any(item => item.Owner?.ParentTargetLabel == label))
            throw new InvalidOperationException("Another named EMM section still uses this section as its parent target.");
        byte[] source = document.CopySourceBytes();
        byte[] output = [.. source.AsSpan(0, start), .. source.AsSpan(end)];
        return EmmReader.Read(output, new() { AnsiCodePage = document.AnsiCodePage });
    }

    private static (int Start, int End) NamedSectionRange(EmmDocument document, string section)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.EffectSections.Any(item => !item.IsDefault && item.Name == section))
            throw new ArgumentException("Select an existing named EMM section.", nameof(section));
        var header = document.Rows.First(row => row.IsAccepted && row.Kind == EmmSourceRowKind.Section && row.SectionName == section);
        long end = document.Rows.Where(row => row.SourceByteOffset > header.SourceByteOffset && row.IsAccepted && row.Kind == EmmSourceRowKind.Section)
            .Select(row => row.SourceByteOffset).DefaultIfEmpty(ScannedEnd(document)).Min();
        return (checked((int)header.SourceByteOffset), checked((int)end));
    }

    private static int ScannedEnd(EmmDocument document)
        => checked((int)document.Rows.Max(row => row.SourceByteOffset + row.SourceByteLength));

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
