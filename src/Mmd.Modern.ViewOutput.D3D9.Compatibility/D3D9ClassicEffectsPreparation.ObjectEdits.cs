using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

public sealed partial class D3D9ClassicEffectsPreparation
{
    /// <summary>Prepares a detached model/object-set edit. Unchanged Guids retain their exact original
    /// identity, positive/null choice and completed dependency bytes; only the explicitly changed/new
    /// set invokes the pinned resolver and collector. No caller/live state is changed on failure.</summary>
    public D3D9ClassicEffectsCaptureResult PrepareObjectEdit(IEnumerable<EmmObjectBinding> liveObjects,
        IReadOnlySet<Guid> changedObjectIds, D3D9EffectImplicitRootResolver implicitResolver,
        ID3D9EffectDependencyInspector inspector, D3D9EffectDependencyLimits? limits = null,
        D3D9EffectResourceTreeLimits? treeLimits = null, IReadOnlyDictionary<Guid, int>? materialCounts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveObjects); ArgumentNullException.ThrowIfNull(changedObjectIds);
        ArgumentNullException.ThrowIfNull(implicitResolver); ArgumentNullException.ThrowIfNull(inspector);
        cancellationToken.ThrowIfCancellationRequested();
        var live = liveObjects.ToArray();
        var previous = FrozenEffects.ImportPlan.LiveObjects.ToDictionary(value => value.ObjectId);
        var current = live.ToDictionary(value => value.ObjectId);
        if (changedObjectIds.Any(id => !current.ContainsKey(id)))
            throw new ArgumentException("Every explicitly changed effect object must be present in the candidate.", nameof(changedObjectIds));
        foreach (var value in live)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previous.TryGetValue(value.ObjectId, out var old))
            {
                if (value.IsModel != old.IsModel || !changedObjectIds.Contains(value.ObjectId) && value.SourcePath != old.SourcePath)
                    throw new ArgumentException("Unchanged effect instances must retain their exact original source identity and kind.", nameof(liveObjects));
            }
            else if (!changedObjectIds.Contains(value.ObjectId))
                throw new ArgumentException("Every added effect instance requires explicit discovery preparation.", nameof(changedObjectIds));
        }
        var plan = EmmImportPlan.RebindLiveObjects(FrozenEffects.ImportPlan, live);
        var retainedValues = FrozenEffects.Origins.Where(value => value.Kind == D3D9EffectProjectRootKind.Implicit)
            .ToDictionary(value => value.ObjectId!.Value, value => value.OriginalValue);
        var seeds = FrozenEffects.RootSeeds.ToDictionary(value => value.ObjectId);
        var roots = D3D9EffectProjectRoots.Create(plan, request =>
        {
            if (changedObjectIds.Contains(request.Object.ObjectId)) return implicitResolver(request);
            if (!seeds.TryGetValue(request.Object.ObjectId, out var seed) || seed.IsModel != request.Object.IsModel)
                throw new InvalidDataException("An unchanged effect instance is missing its frozen positive or null choice.");
            return seed.OriginAbsoluteFxPath is null ? null : retainedValues[request.Object.ObjectId];
        }, cancellationToken);
        var addedRootIndices = roots.Origins.Where(value => value.Kind == D3D9EffectProjectRootKind.Implicit &&
            value.ObjectId is { } id && changedObjectIds.Contains(id)).Select(value => value.RootIndex).ToArray();
        D3D9EffectResourceTree? added = null;
        if (addedRootIndices.Length != 0)
        {
            using var source = new D3D9EffectDependencyFileSource(limits);
            var collected = new D3D9EffectDependencyCollector(source, inspector, limits)
                .Collect(addedRootIndices.Select(index => roots.Roots[index]), cancellationToken);
            if (collected.Status != D3D9EffectDependencyStatus.Complete)
                return new(collected.Status, null, collected.Diagnostics);
            added = D3D9EffectResourceTree.FromClosure(collected.Closure!, treeLimits, collected.Diagnostics, cancellationToken);
        }
        D3D9EffectResourceTree? composed = null;
        if (roots.Roots.Count != 0)
        {
            var addedByIndex = addedRootIndices.Select((root, index) => (root, index)).ToDictionary(value => value.root, value => value.index);
            var retainedByOrigin = Tree?.DependencyGraph?.Roots.Select((root, index) => (root, index))
                .GroupBy(value => value.root.OriginAbsolutePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sources = new (D3D9EffectResourceTree Tree, int RootIndex)[roots.Roots.Count];
            for (int index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (addedByIndex.TryGetValue(index, out var addedIndex)) sources[index] = (added!, addedIndex);
                else if (retainedByOrigin.TryGetValue(roots.Roots[index].OriginAbsolutePath, out var retainedIndex))
                    sources[index] = (Tree!, retainedIndex);
                else throw new InvalidDataException("A candidate effect root has neither retained evidence nor explicit new capture.");
            }
            composed = D3D9EffectResourceTree.ComposeDependencies(roots.Roots, sources, treeLimits, cancellationToken);
        }
        var emm = D3D9EmmPma.Capture(plan, Emm.Assets);
        var frozen = D3D9FrozenEffectProject.Capture(roots, composed, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = new D3D9ClassicEffectsPreparation(emm, frozen);
        prepared.ValidateObjectEditBindings(materialCounts, cancellationToken);
        return new(D3D9EffectDependencyStatus.Complete, prepared, composed?.DependencyGraph?.Diagnostics ?? []);
    }

    /// <summary>Uses the existing runtime projection without materializing or opening a resource.
    /// Optional authored counts let the edit owner validate resolved effect/show/Owner subsets before
    /// committing the candidate; no snapshot is sampled and no renderer/session is created.</summary>
    public void ValidateObjectEditBindings(IReadOnlyDictionary<Guid, int>? materialCounts = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var map = new D3D9EffectResourceMap(Tree?.DependencyGraph?.Nodes.Select(value =>
            new D3D9EffectResourceBinding(value.OriginAbsolutePath, value.OriginAbsolutePath)) ?? []);
        var configuration = D3D9EmmRuntimeConfiguration.Create(FrozenEffects.ImportPlan, FrozenEffects.RootSeeds, map);
        if (materialCounts is null) return;
        if (materialCounts.Count != configuration.Objects.Count || configuration.Objects.Any(value =>
                !materialCounts.TryGetValue(value.ObjectId, out var count) || count < 0))
            throw new InvalidDataException("Candidate authored material counts must cover every live effect object exactly.");
        foreach (var section in configuration.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (section.Owner is { } owner) Check(owner.ObjectId, owner.Subset, allowWhole: true);
            foreach (var entry in section.Entries)
                foreach (int subset in entry.SubsetEffects.Keys.Concat(entry.SubsetShown.Keys))
                    Check(entry.ObjectId, subset, allowWhole: false);
        }

        void Check(Guid objectId, int subset, bool allowWhole)
        {
            if (allowWhole && subset == -1) return;
            if (subset < 0 || subset >= materialCounts[objectId])
                throw new InvalidDataException("Candidate EMM effect, visibility or Owner subset is outside its authored model/X material table.");
        }
    }
}
