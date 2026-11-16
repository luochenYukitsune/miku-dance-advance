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
        IReadOnlyDictionary<Guid, int>? materialCounts = null, CancellationToken cancellationToken = default)
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
        if (addedIndices.Length != 0)
        {
            using var source = new D3D9EffectDependencyFileSource();
            var collected = new D3D9EffectDependencyCollector(source, inspector)
                .Collect(addedIndices.Select(index => roots.Roots[index]), cancellationToken);
            if (collected.Status != D3D9EffectDependencyStatus.Complete)
                return new(collected.Status, null, collected.Diagnostics);
            added = D3D9EffectResourceTree.FromClosure(collected.Closure!, cancellationToken: cancellationToken);
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
}
