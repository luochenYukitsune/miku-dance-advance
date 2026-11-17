using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

public sealed partial class D3D9ClassicEffectsPreparation
{
    /// <summary>A transient bypass. The authored preparation remains owned by the document.</summary>
    public D3D9ClassicEffectsPreparation WithoutEffects()
    {
        var text = new System.Text.StringBuilder("[Info]\r\nVersion=3\r\n[Object]\r\n");
        foreach (var obj in Emm.Document.Objects) text.Append(obj.Key).Append('=').Append(obj.Path).Append("\r\n");
        text.Append("[Effect]\r\nDefault=none\r\n");
        var document = EmmReader.Read(Mmd.Modern.Formats.LegacyAnsiEncoding.Create(Emm.Document.AnsiCodePage).GetBytes(text.ToString()),
            new() { AnsiCodePage = Emm.Document.AnsiCodePage });
        var plan = EmmImportPlan.WithAssignments(FrozenEffects.ImportPlan, document);
        var roots = D3D9EffectProjectRoots.Create(plan, _ => null);
        return new(D3D9EmmPma.Capture(plan, Emm.Assets), D3D9FrozenEffectProject.Capture(roots, null));
    }

    /// <summary>Prepares edited assignments off the live document. Existing dependency closures
    /// remain usable after their original source directory disappears. Only new roots are read.</summary>
    public D3D9ClassicEffectsCaptureResult PrepareAssignments(EmmDocument document,
        ID3D9EffectDependencyInspector inspector, bool reloadAll = false,
        IReadOnlyDictionary<Guid, int>? materialCounts = null, CancellationToken cancellationToken = default,
        bool removeEditedPages = false)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = EmmImportPlan.WithAssignments(FrozenEffects.ImportPlan, document);
        var implicitValues = FrozenEffects.Origins.Where(value => value.Kind == D3D9EffectProjectRootKind.Implicit)
            .ToDictionary(value => value.ObjectId!.Value, value => value.OriginalValue);
        var roots = D3D9EffectProjectRoots.Create(plan,
            request => implicitValues.GetValueOrDefault(request.Object.ObjectId), cancellationToken);
        var retained = Tree?.DependencyGraph?.Roots.Select((root, index) => (root, index))
            .GroupBy(value => value.root.OriginAbsolutePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var addedIndices = Enumerable.Range(0, roots.Roots.Count)
            .Where(index => reloadAll || !retained.ContainsKey(roots.Roots[index].OriginAbsolutePath)).ToArray();
        D3D9EffectResourceTree? added = null;
        IReadOnlyDictionary<string, IReadOnlyList<D3D9EffectOffscreenDeclaration>>? addedOffscreen = null;
        if (addedIndices.Length != 0)
        {
            using var source = new D3D9EffectDependencyFileSource();
            var collected = new D3D9EffectDependencyCollector(source, inspector)
                .Collect(addedIndices.Select(index => roots.Roots[index]), cancellationToken);
            if (collected.Status != D3D9EffectDependencyStatus.Complete)
                return new(collected.Status, null, collected.Diagnostics);
            added = D3D9EffectResourceTree.FromClosure(collected.Closure!, cancellationToken: cancellationToken);
            addedOffscreen = collected.Closure!.OffscreenTargets;
        }
        // 新指定或替换的 FX 声明了离屏目标时，补上对应的命名分配页，并清理被替换 FX 留下的未改动页。
        // 新 FX 的声明直接取自上面同一次依赖收集；已冻结的 FX 只用冻结树里的字节重新检查，不碰原件。
        // 命名页只有 Owner 行，不增减依赖根，因此同步后根表不变。
        var declarations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var synchronized = EmmOffscreenPages.Synchronize(Emm.Document, document, plan.PathContext.WorkingDirectory,
            path => declarations.TryGetValue(path, out var names) ? names : declarations[path] =
                addedOffscreen?.TryGetValue(path, out var raw) == true
                    ? D3D9EffectDependencyInspection.MainOffscreenTargetNames(raw)
                    : DeclaredOffscreenTargets(path, inspector, cancellationToken), removeEditedPages);
        if (!ReferenceEquals(synchronized.Document, document))
        {
            plan = EmmImportPlan.WithAssignments(FrozenEffects.ImportPlan, synchronized.Document);
            var synchronizedRoots = D3D9EffectProjectRoots.Create(plan,
                request => implicitValues.GetValueOrDefault(request.Object.ObjectId), cancellationToken);
            if (!synchronizedRoots.Roots.Select(root => root.OriginAbsolutePath)
                    .SequenceEqual(roots.Roots.Select(root => root.OriginAbsolutePath), StringComparer.Ordinal))
                throw new InvalidOperationException("Generated named EMM sections unexpectedly changed the effect roots.");
            roots = synchronizedRoots;
        }
        D3D9EffectResourceTree? composed = null;
        if (roots.Roots.Count != 0)
        {
            var addedMap = addedIndices.Select((root, index) => (root, index)).ToDictionary(value => value.root, value => value.index);
            var sources = Enumerable.Range(0, roots.Roots.Count).Select(index =>
                addedMap.TryGetValue(index, out var addedIndex) ? (added!, addedIndex)
                    : (Tree!, retained[roots.Roots[index].OriginAbsolutePath])).ToArray();
            composed = D3D9EffectResourceTree.ComposeDependencies(roots.Roots, sources, null, cancellationToken);
        }
        var prepared = new D3D9ClassicEffectsPreparation(D3D9EmmPma.Capture(plan, Emm.Assets),
            D3D9FrozenEffectProject.Capture(roots, composed, cancellationToken));
        prepared.ValidateObjectEditBindings(materialCounts, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(D3D9EffectDependencyStatus.Complete, prepared, composed?.DependencyGraph?.Diagnostics ?? []);
    }

    /// <summary>只读取声明，不建立依赖闭包：用冻结树里的 FX 字节和已记录的 include 重新检查，不读原件。</summary>
    private IReadOnlyList<string> DeclaredOffscreenTargets(string originAbsolutePath,
        ID3D9EffectDependencyInspector inspector, CancellationToken cancellationToken)
    {
        var graph = Tree?.DependencyGraph;
        var frozen = graph?.Nodes.FirstOrDefault(node => (node.Category & D3D9EffectDependencyCategory.Effect) != 0
            && StringComparer.OrdinalIgnoreCase.Equals(node.OriginAbsolutePath, originAbsolutePath))
            ?? throw new InvalidOperationException("An assigned effect is neither newly collected nor frozen in the effect tree.");
        var byId = graph!.Nodes.ToDictionary(node => node.ResourceId, StringComparer.Ordinal);
        var includes = graph.Edges.Where(edge => edge.Kind is D3D9EffectDependencyEdgeKind.IncludeLocal or D3D9EffectDependencyEdgeKind.IncludeSystem
            && StringComparer.OrdinalIgnoreCase.Equals(edge.TopFxOriginAbsolutePath, frozen.OriginAbsolutePath)).ToArray();
        D3D9EffectDependencySnapshot Snapshot(D3D9EffectResourceTreeDependencyNode node) => new(node.OriginAbsolutePath, Tree!.CopyFileBytes(node.RelativePath));
        var inspection = inspector.Inspect(Snapshot(frozen), request =>
        {
            string parent = request.ParentOriginAbsolutePath is { } raw
                ? D3D9EffectDependencySnapshot.CanonicalizeOrigin(raw) : frozen.OriginAbsolutePath;
            byte[] bytes = request.Reference.CopyBytes();
            var edge = includes.FirstOrDefault(value => value.ProfileId == request.ProfileId
                && value.Kind == (request.IsSystem ? D3D9EffectDependencyEdgeKind.IncludeSystem : D3D9EffectDependencyEdgeKind.IncludeLocal)
                && StringComparer.OrdinalIgnoreCase.Equals(value.ParentOriginAbsolutePath, parent)
                && value.Reference.CodePage == request.Reference.CodePage && value.Reference.CopyBytes().AsSpan().SequenceEqual(bytes))
                ?? throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported, "offscreen-frozen-include",
                    "A frozen effect requested an include absent from its saved graph.", frozen.OriginAbsolutePath);
            return Snapshot(byId[edge.TargetResourceId!]);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection.Status != D3D9EffectDependencyStatus.Complete)
            throw new D3D9EffectDependencyException(inspection.Status == D3D9EffectDependencyStatus.Unsupported
                ? D3D9EffectDependencyStatus.Unsupported : D3D9EffectDependencyStatus.Incomplete, "offscreen-inspection",
                inspection.Diagnostics.FirstOrDefault()?.Message ?? "The effect could not be inspected for offscreen render targets.", originAbsolutePath);
        return inspection.MainOffscreenTargetNames();
    }
}
