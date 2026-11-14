namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// One frozen live instance and the absolute lexical inputs for exact host implicit-effect selection.
/// The resolver must preserve same-basename, embedded-name, then default priority and the host's native
/// encoding/CRT behavior. Optional candidates may miss; after a winner is selected, its capture is required.
/// </summary>
public sealed class D3D9EffectImplicitRootRequest
{
    internal D3D9EffectImplicitRootRequest(EmmObjectBinding value, string originAbsoluteObjectPath,
        string workingDirectory, string? defaultEffectAbsolutePath)
    {
        Object = value; OriginAbsoluteObjectPath = originAbsoluteObjectPath;
        WorkingDirectory = workingDirectory; DefaultEffectAbsolutePath = defaultEffectAbsolutePath;
    }

    public EmmObjectBinding Object { get; }
    public string OriginAbsoluteObjectPath { get; }
    public string WorkingDirectory { get; }
    /// <summary>The base section's file default, resolved against WorkingDirectory; null for absent or symbolic none.</summary>
    public string? DefaultEffectAbsolutePath { get; }
}

/// <summary>
/// Caller-supplied exact implicit selection. Return an absolute original winner, or null when all optional
/// candidates miss. The neutral planner never infers candidates, probes files, or substitutes another winner.
/// </summary>
public delegate string? D3D9EffectImplicitRootResolver(D3D9EffectImplicitRootRequest request);

public enum D3D9EffectProjectRootKind { Implicit, EmmDefault, EmmObject, EmmSubset }

/// <summary>
/// The source of one required dependency root. RootIndex relates this row to Roots without collapsing
/// shared paths. SectionOwner is parsed owner/target metadata, never another filename to discover.
/// An unmatched EMM record retains its required file and record kind, with a null live ObjectId.
/// </summary>
public sealed class D3D9EffectProjectRootOrigin
{
    internal D3D9EffectProjectRootOrigin(int rootIndex, D3D9EffectProjectRootKind kind, string originalValue,
        string originAbsolutePath, Guid? objectId, bool? isModel, string? recordKey, string? emmSection,
        int? subset, EmmOwnerReference? sectionOwner)
    {
        RootIndex = rootIndex; Kind = kind; OriginalValue = originalValue; OriginAbsolutePath = originAbsolutePath;
        ObjectId = objectId; IsModel = isModel; RecordKey = recordKey; EmmSection = emmSection;
        Subset = subset; SectionOwner = sectionOwner;
    }

    public int RootIndex { get; }
    public D3D9EffectProjectRootKind Kind { get; }
    public string OriginalValue { get; }
    public string OriginAbsolutePath { get; }
    public Guid? ObjectId { get; }
    public bool? IsModel { get; }
    public string? RecordKey { get; }
    public string? EmmSection { get; }
    public int? Subset { get; }
    public EmmOwnerReference? SectionOwner { get; }
}

/// <summary>
/// Immutable pre-resource-map roots and one implicit seed per frozen live instance. This CPU planner uses
/// only the existing reader's accepted state and a caller-provided exact implicit resolver. It performs no
/// file reads, native/device work, scene sampling or current-directory reads. Hidden/unmatched live objects,
/// explicit overrides and inactive subsets remain represented. Collect Roots, materialize the complete
/// graph, then pass RootSeeds and that resource map to D3D9EmmRuntimeConfiguration.Create. This is not a
/// runtime topology validation or a claim of whole-project portability.
/// </summary>
public sealed class D3D9EffectProjectRoots
{
    private D3D9EffectProjectRoots(EmmImportPlan plan, IEnumerable<D3D9EffectDependencyRoot> roots,
        IEnumerable<D3D9EmmRuntimeRootSeed> seeds, IEnumerable<D3D9EffectProjectRootOrigin> origins)
    {
        ImportPlan = plan; Roots = Array.AsReadOnly(roots.ToArray());
        RootSeeds = Array.AsReadOnly(seeds.ToArray()); Origins = Array.AsReadOnly(origins.ToArray());
    }

    public EmmImportPlan ImportPlan { get; }
    public IReadOnlyList<D3D9EffectDependencyRoot> Roots { get; }
    public IReadOnlyList<D3D9EmmRuntimeRootSeed> RootSeeds { get; }
    public IReadOnlyList<D3D9EffectProjectRootOrigin> Origins { get; }

    public static D3D9EffectProjectRoots Create(EmmImportPlan plan, D3D9EffectImplicitRootResolver implicitResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(implicitResolver);
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.Document.IsSupportedVersion)
            throw Invalid($"EMM version {plan.Document.Version} cannot supply executable effect roots.");
        if (plan.Document.Diagnostics.Any(d => d.Severity == EmmDiagnosticSeverity.Error))
            throw Invalid("EMM error diagnostics must be resolved before effect root planning.");
        if (plan.LiveObjects.Count > 65536 || plan.Document.Objects.Count > 65536 || plan.Document.EffectSections.Count > 65536)
            throw Invalid("EMM effect root object, record and section tables are each limited to 65536 rows.");
        return new Builder(plan, implicitResolver, cancellationToken).Create();
    }

    private static ArgumentException Invalid(string message) => new(message, "plan");

    private sealed class Builder(EmmImportPlan plan, D3D9EffectImplicitRootResolver implicitResolver,
        CancellationToken cancellationToken)
    {
        private readonly List<D3D9EffectDependencyRoot> roots = [];
        private readonly List<D3D9EmmRuntimeRootSeed> seeds = [];
        private readonly List<D3D9EffectProjectRootOrigin> origins = [];
        private readonly Dictionary<string, EmmObjectRecord> records = plan.Document.Objects.ToDictionary(r => r.Key, StringComparer.Ordinal);
        private readonly Dictionary<Guid, EmmObjectMatch> matchesByObject = plan.Matches.ToDictionary(r => r.ObjectId);
        private long copiedBytes;

        internal D3D9EffectProjectRoots Create()
        {
            var workingDirectory = plan.PathContext.WorkingDirectory;
            var baseDefault = plan.Document.EffectSections.FirstOrDefault(s => s.IsDefault)?.DefaultPath;
            var defaultPath = baseDefault is not null &&
                D3D9EffectProjectRootPaths.EffectKind(baseDefault, false) == D3D9EmmRuntimeEffectKind.File
                    ? D3D9EffectProjectRootPaths.OriginalPath(baseDefault, workingDirectory) : null;

            foreach (var value in plan.LiveObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = D3D9EffectProjectRootPaths.OriginalPath(value.SourcePath, workingDirectory);
                Text(value.SourcePath); Text(source);
                var selected = implicitResolver(new(value, source, workingDirectory, defaultPath));
                cancellationToken.ThrowIfCancellationRequested();
                var seed = new D3D9EmmRuntimeRootSeed(value.ObjectId, value.IsModel, selected);
                seeds.Add(seed);
                if (seed.OriginAbsoluteFxPath is { } path)
                {
                    matchesByObject.TryGetValue(value.ObjectId, out var match);
                    Add(D3D9EffectProjectRootKind.Implicit, selected!, path, value.ObjectId, value.IsModel,
                        match?.RecordKey, null, null, null);
                }
            }

            foreach (var section in plan.Document.EffectSections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (section.DefaultPath is { } defaultValue)
                    Effect(D3D9EffectProjectRootKind.EmmDefault, defaultValue, section, null, null);
                foreach (var entry in section.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.EffectPath is { } whole)
                        Effect(D3D9EffectProjectRootKind.EmmObject, whole, section, entry.ObjectKey, null);
                    foreach (var subset in entry.SubsetPaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Effect(D3D9EffectProjectRootKind.EmmSubset, subset.Value, section, entry.ObjectKey, subset.Key);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(plan, roots, seeds, origins);
        }

        private void Effect(D3D9EffectProjectRootKind kind, string value, EmmEffectSection section, string? recordKey, int? subset)
        {
            if (D3D9EffectProjectRootPaths.EffectKind(value, !section.IsDefault) != D3D9EmmRuntimeEffectKind.File) return;
            var path = D3D9EffectProjectRootPaths.OriginalPath(value, plan.PathContext.WorkingDirectory);
            Guid? objectId = recordKey is not null && plan.MatchesByRecordKey.TryGetValue(recordKey, out var match) ? match.ObjectId : null;
            bool? isModel = recordKey is not null && records.TryGetValue(recordKey, out var record) ? record.IsModel : null;
            Add(kind, value, path, objectId, isModel, recordKey, section.Name, subset, section.Owner);
        }

        private void Add(D3D9EffectProjectRootKind kind, string value, string path, Guid? objectId,
            bool? isModel, string? recordKey, string? section, int? subset, EmmOwnerReference? owner)
        {
            if (roots.Count >= 262144) throw Invalid("EMM effect roots exceed 262144 rows.");
            Text(value); Text(path);
            if (recordKey is not null) Text(recordKey);
            if (section is not null) Text(section);
            if (owner is not null)
            {
                Text(owner.ObjectKey);
                if (owner.ParentTargetLabel is { } label) Text(label);
            }
            var description = kind switch
            {
                D3D9EffectProjectRootKind.Implicit => "Frozen implicit effect",
                D3D9EffectProjectRootKind.EmmDefault => "EMM Default",
                D3D9EffectProjectRootKind.EmmObject => "EMM whole-object effect",
                _ => "EMM subset effect"
            };
            origins.Add(new(roots.Count, kind, value, path, objectId, isModel, recordKey, section, subset, owner));
            roots.Add(new(path, new(objectId, subset, section, Description: description)));
        }

        private void Text(string value)
        {
            copiedBytes += D3D9EffectProjectRootPaths.TextValidity(value);
            if (copiedBytes > 16L * 1024 * 1024)
                throw Invalid("EMM effect root strings exceed the combined 16 MiB copied-string budget.");
        }
    }
}
