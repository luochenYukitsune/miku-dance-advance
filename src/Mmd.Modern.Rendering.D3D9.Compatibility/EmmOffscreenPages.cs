namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>一次分配编辑中特效值发生变化的一个显式行，以及它作为命名页 Owner 时的写法。
/// 值不是文件（none／hide／main_default）或被重置为继承时 OriginAbsoluteFxPath 为 null。</summary>
public sealed record EmmOffscreenPageOwner(EmmOwnerReference Owner, string? OriginAbsoluteFxPath);

/// <summary>命名页同步结果。Document 与输入相同时 Added 和 Removed 都为空。</summary>
public sealed record EmmOffscreenPageResult(EmmDocument Document, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

/// <summary>被替换或移除的 FX 留下了用户改过的命名页。不静默删除，交给用户处理。</summary>
public sealed class EmmOffscreenPageConflictException : InvalidOperationException
{
    public EmmOffscreenPageConflictException(IEnumerable<string> sections)
        : base("Named EMM sections edited by the user no longer match the replaced effect.")
        => Sections = Array.AsReadOnly(sections.ToArray());
    public IReadOnlyList<string> Sections { get; }
}

/// <summary>
/// 按原版 MME 的方式维护 FX 声明的离屏渲染目标对应的命名分配页（[Effect@名称]）。只处理本次编辑中
/// 特效值变化的显式行：新指定的 FX 声明了哪些 OFFSCREENRENDERTARGET，就为缺少的生成只有 Owner 行的页；
/// 被替换或移除的 FX 留下的页，未改动过的一并删除，用户改过的则拒绝本次编辑。已有页和其中的内容不会被覆盖。
/// 生成的页不写对象行，各对象的默认分配仍由 FX 的 DefaultEffect 在宿主运行时决定。
/// 纯 CPU 元数据操作，不读文件、不编译 FX。
/// </summary>
public static class EmmOffscreenPages
{
    public static IReadOnlyList<EmmOffscreenPageOwner> ChangedOwners(EmmDocument previous, EmmDocument current,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        var before = Values(previous);
        var result = new List<EmmOffscreenPageOwner>();
        foreach (var section in current.EffectSections)
        {
            string? parent = section.IsDefault ? null : section.Name["Effect@".Length..];
            foreach (var (key, subset, value) in Rows(section))
            {
                if (before.GetValueOrDefault((section.Name, key, subset)) == value) continue;
                result.Add(Owner(section, key, subset, value, parent));
            }
        }
        // 当前文档中已不存在的行（重置为继承）也要检查它原来的页。
        var now = Values(current);
        foreach (var section in previous.EffectSections)
        {
            if (!current.EffectSections.Any(item => item.Name == section.Name)) continue;
            string? parent = section.IsDefault ? null : section.Name["Effect@".Length..];
            foreach (var (key, subset, _) in Rows(section))
                if (!now.ContainsKey((section.Name, key, subset))) result.Add(new(new(key, subset, parent), null));
        }
        return result;

        EmmOffscreenPageOwner Owner(EmmEffectSection section, string key, int subset, string value, string? parent)
            => new(new(key, subset, parent), D3D9EffectProjectRootPaths.EffectKind(value, !section.IsDefault) == D3D9EmmRuntimeEffectKind.File
                ? D3D9EffectProjectRootPaths.OriginalPath(value, workingDirectory) : null);
    }

    /// <param name="declaredTargets">返回一个 FX 原件绝对路径声明的离屏目标参数名（按序数排序）。</param>
    /// <param name="removeEditedPages">为 true 时，失效页即使有用户内容（包括 MME 自己写满对象行的页）也一并删除；
    /// 只在用户看过 <see cref="EmmOffscreenPageConflictException"/> 列出的页并确认后传入。</param>
    public static EmmOffscreenPageResult Synchronize(EmmDocument previous, EmmDocument current, string workingDirectory,
        Func<string, IReadOnlyList<string>> declaredTargets, bool removeEditedPages = false)
    {
        ArgumentNullException.ThrowIfNull(declaredTargets);
        var owners = ChangedOwners(previous, current, workingDirectory);
        var document = current;
        var removed = new List<string>();
        var added = new List<string>();
        if (owners.Count == 0) return new(document, added, removed);
        var declared = owners.ToDictionary(value => value.Owner,
            value => value.OriginAbsoluteFxPath is { } path ? declaredTargets(path) : (IReadOnlyList<string>)[]);

        // 先收集失效页，并连带收集以它们为父页的嵌套页。
        var stale = current.EffectSections.Where(section => !section.IsDefault && section.Owner is { } owner
            && declared.TryGetValue(owner, out var names) && !names.Contains(section.BaseName, StringComparer.Ordinal))
            .Select(section => section.Name).ToHashSet(StringComparer.Ordinal);
        for (bool grown = true; grown;)
        {
            grown = false;
            foreach (var section in current.EffectSections)
                if (!section.IsDefault && section.Owner?.ParentTargetLabel is { } parent && stale.Contains("Effect@" + parent))
                    grown |= stale.Add(section.Name);
        }
        var edited = current.EffectSections.Where(section => stale.Contains(section.Name)
            && !EmmDocumentEditor.IsOwnerOnly(current, section.Name)).Select(section => section.Name).ToArray();
        if (edited.Length != 0 && !removeEditedPages) throw new EmmOffscreenPageConflictException(edited);
        while (stale.Count != 0)
        {
            // 先删子页，父页不再被引用后才能删除。
            var leaf = document.EffectSections.First(section => stale.Contains(section.Name) && !document.EffectSections.Any(child =>
                child.Owner?.ParentTargetLabel is { } parent && "Effect@" + parent == section.Name));
            document = EmmDocumentEditor.RemoveNamedSection(document, leaf.Name);
            stale.Remove(leaf.Name); removed.Add(leaf.Name);
        }

        foreach (var (owner, names) in declared)
        {
            // 父页已被删除的嵌套行不再生成页（该行本身仍保留，由宿主报告）。
            if (owner.ParentTargetLabel is { } parent && !document.EffectSections.Any(section => section.Name == "Effect@" + parent)) continue;
            foreach (var name in names)
            {
                if (document.EffectSections.Any(section => !section.IsDefault && section.BaseName == name && section.Owner == owner)) continue;
                var before = document.EffectSections.Select(section => section.Name).ToHashSet(StringComparer.Ordinal);
                document = EmmDocumentEditor.AddNamedSection(document, name, owner);
                added.Add(document.EffectSections.Single(section => !before.Contains(section.Name)).Name);
            }
        }
        return new(document, added, removed);
    }

    private static Dictionary<(string Section, string Key, int Subset), string> Values(EmmDocument document)
    {
        var values = new Dictionary<(string, string, int), string>();
        foreach (var section in document.EffectSections)
            foreach (var (key, subset, value) in Rows(section)) values[(section.Name, key, subset)] = value;
        return values;
    }

    private static IEnumerable<(string Key, int Subset, string Value)> Rows(EmmEffectSection section)
    {
        foreach (var entry in section.Entries)
        {
            if (entry.EffectPath is { } whole) yield return (entry.ObjectKey, -1, whole);
            foreach (var subset in entry.SubsetPaths) yield return (entry.ObjectKey, subset.Key, subset.Value);
        }
    }
}
