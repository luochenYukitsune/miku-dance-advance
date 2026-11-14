using System.Collections.ObjectModel;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

public enum D3D9EmmRuntimeEffectKind { File = 0, None = 1, Hide = 2, MainDefault = 3 }

/// <summary>A scoped EMM value. File values carry an original identity and its package-local path.</summary>
public sealed class D3D9EmmRuntimeEffectValue
{
    internal D3D9EmmRuntimeEffectValue(D3D9EmmRuntimeEffectKind kind, string originalValue,
        string? originAbsolutePath = null, string? materializedAbsolutePath = null)
    { Kind = kind; OriginalValue = originalValue; OriginAbsolutePath = originAbsolutePath; MaterializedAbsolutePath = materializedAbsolutePath; }

    public D3D9EmmRuntimeEffectKind Kind { get; }
    public string OriginalValue { get; }
    public string? OriginAbsolutePath { get; }
    public string? MaterializedAbsolutePath { get; }
}

/// <summary>
/// A caller-frozen implicit root decision for one live instance. A null path means no effect. The caller
/// performs sibling/embedded/default discovery during collection; this type never discovers or opens files.
/// Supply a decision for every live object, even when an explicit whole-object row will override it.
/// </summary>
public sealed class D3D9EmmRuntimeRootSeed
{
    public D3D9EmmRuntimeRootSeed(Guid objectId, bool isModel, string? originAbsoluteFxPath = null)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("A live object identity is required.", nameof(objectId));
        ObjectId = objectId;
        IsModel = isModel;
        OriginAbsoluteFxPath = originAbsoluteFxPath is null ? null
            : D3D9EffectResourcePaths.Canonicalize(originAbsoluteFxPath, nameof(originAbsoluteFxPath));
    }

    public Guid ObjectId { get; }
    public bool IsModel { get; }
    public string? OriginAbsoluteFxPath { get; }
}

/// <summary>A detached live instance. RecordKey is metadata, never a runtime ID or render-order slot.</summary>
public sealed class D3D9EmmRuntimeObject
{
    internal D3D9EmmRuntimeObject(EmmObjectBinding binding, string originAbsolutePath,
        EmmObjectRecord? record, D3D9EmmRuntimeEffectValue rootSeed)
    {
        ObjectId = binding.ObjectId; IsModel = binding.IsModel; SourcePath = binding.SourcePath;
        OriginAbsolutePath = originAbsolutePath; RecordKey = record?.Key; RecordSourcePath = record?.Path;
        RootSeed = rootSeed;
    }

    public Guid ObjectId { get; }
    public bool IsModel { get; }
    public string SourcePath { get; }
    public string OriginAbsolutePath { get; }
    public string? RecordKey { get; }
    public string? RecordSourcePath { get; }
    /// <summary>Used only for an absent base whole-object effect row; explicit none suppresses this seed.</summary>
    public D3D9EmmRuntimeEffectValue RootSeed { get; }
}

public sealed class D3D9EmmRuntimeOwner
{
    internal D3D9EmmRuntimeOwner(D3D9EmmRuntimeObject value, EmmOwnerReference owner)
    {
        RecordKey = owner.ObjectKey; ObjectId = value.ObjectId; IsModel = value.IsModel;
        Subset = owner.Subset; ParentTargetLabel = owner.ParentTargetLabel;
    }

    public string RecordKey { get; }
    public Guid ObjectId { get; }
    public bool IsModel { get; }
    public int Subset { get; }
    public string? ParentTargetLabel { get; }
}

public sealed class D3D9EmmRuntimeEntry
{
    internal D3D9EmmRuntimeEntry(D3D9EmmRuntimeObject value, string recordKey,
        D3D9EmmRuntimeEffectValue? effect, bool? shown, IDictionary<int, D3D9EmmRuntimeEffectValue> subsets,
        IDictionary<int, bool> subsetShown)
    {
        RecordKey = recordKey; ObjectId = value.ObjectId; IsModel = value.IsModel; Effect = effect; Shown = shown;
        SubsetEffects = new ReadOnlyDictionary<int, D3D9EmmRuntimeEffectValue>(new Dictionary<int, D3D9EmmRuntimeEffectValue>(subsets));
        SubsetShown = new ReadOnlyDictionary<int, bool>(new Dictionary<int, bool>(subsetShown));
    }

    public string RecordKey { get; }
    public Guid ObjectId { get; }
    public bool IsModel { get; }
    /// <summary>Null means absent, unlike an explicit None value.</summary>
    public D3D9EmmRuntimeEffectValue? Effect { get; }
    /// <summary>Null means absent, unlike an explicit false value.</summary>
    public bool? Shown { get; }
    public IReadOnlyDictionary<int, D3D9EmmRuntimeEffectValue> SubsetEffects { get; }
    public IReadOnlyDictionary<int, bool> SubsetShown { get; }
}

public sealed class D3D9EmmRuntimeSection
{
    internal D3D9EmmRuntimeSection(string name, string baseName, D3D9EmmRuntimeOwner? owner,
        D3D9EmmRuntimeEffectValue? defaultEffect, IEnumerable<D3D9EmmRuntimeEntry> entries)
    {
        Name = name; BaseName = baseName; Owner = owner; DefaultEffect = defaultEffect;
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public string Name { get; }
    public string BaseName { get; }
    public bool IsDefault => BaseName.Length == 0;
    public D3D9EmmRuntimeOwner? Owner { get; }
    public D3D9EmmRuntimeEffectValue? DefaultEffect { get; }
    public IReadOnlyList<D3D9EmmRuntimeEntry> Entries { get; }
}

/// <summary>
/// Bounded immutable projection of the existing reader's accepted sections and import plan's instance map.
/// This is CPU metadata for a distinct EMM session policy, not an MME executor, resource collector or file lease.
/// Original bytes, selected code page and rejected rows remain in ImportPlan.Document. The session must map
/// these Guids through its single native identity map, validate material subsets and retain the complete frozen
/// resource lease until native destruction succeeds. Native loading additionally validates ACP/path limits.
/// </summary>
public sealed class D3D9EmmRuntimeConfiguration
{
    private D3D9EmmRuntimeConfiguration(EmmImportPlan plan, D3D9EffectResourceMap resources,
        List<D3D9EmmRuntimeObject> objects, Dictionary<string, D3D9EmmRuntimeObject> byKey,
        List<D3D9EmmRuntimeSection> sections)
    {
        ImportPlan = plan; Resources = resources;
        Objects = objects.AsReadOnly();
        ObjectsByRecordKey = new ReadOnlyDictionary<string, D3D9EmmRuntimeObject>(byKey);
        Sections = sections.AsReadOnly();
    }

    public EmmImportPlan ImportPlan { get; }
    public D3D9EffectResourceMap Resources { get; }
    public IReadOnlyList<D3D9EmmRuntimeObject> Objects { get; }
    public IReadOnlyDictionary<string, D3D9EmmRuntimeObject> ObjectsByRecordKey { get; }
    public IReadOnlyList<D3D9EmmRuntimeSection> Sections { get; }

    public static D3D9EmmRuntimeConfiguration Create(EmmImportPlan plan,
        IEnumerable<D3D9EmmRuntimeRootSeed> rootSeeds, D3D9EffectResourceMap resources)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rootSeeds);
        ArgumentNullException.ThrowIfNull(resources);
        if (!plan.Document.IsSupportedVersion)
            throw Invalid($"EMM version {plan.Document.Version} cannot be projected for execution.");
        if (plan.Document.Diagnostics.Any(d => d.Severity == EmmDiagnosticSeverity.Error))
            throw Invalid("EMM error diagnostics must be resolved before runtime projection.");
        // Scanner warnings preserve the accepted state of the existing pinned reader. Reinterpreting rejected
        // text here would change first-key/header-continuation semantics and create a second EMM interpreter.
        var projection = new Projection(plan, resources);
        return projection.Create(rootSeeds);
    }

    private static ArgumentException Invalid(string message) => new(message, "plan");

    private sealed class Projection(EmmImportPlan plan, D3D9EffectResourceMap resources)
    {
        private const int MaxRows = 65536;
        private const long MaxCopiedBytes = 16L * 1024 * 1024;
        private readonly Dictionary<string, D3D9EffectResourceBinding> files = resources.Bindings
            .ToDictionary(row => row.OriginAbsolutePath, StringComparer.OrdinalIgnoreCase);
        private long copiedBytes = resources.Utf8Bytes;
        private int entryCount;
        private int subsetCount;

        public D3D9EmmRuntimeConfiguration Create(IEnumerable<D3D9EmmRuntimeRootSeed> rootSeeds)
        {
            if (plan.LiveObjects.Count > MaxRows || plan.Document.Objects.Count > MaxRows ||
                plan.Document.EffectSections.Count > MaxRows)
                throw Invalid("EMM runtime object, record and section tables are each limited to 65536 rows.");
            var live = new Dictionary<Guid, EmmObjectBinding>();
            foreach (var binding in plan.LiveObjects)
            {
                if (binding is null || binding.ObjectId == Guid.Empty || !live.TryAdd(binding.ObjectId, binding))
                    throw Invalid("EMM runtime live object identities must be nonempty and unique across kinds.");
            }
            var seeds = new Dictionary<Guid, D3D9EmmRuntimeRootSeed>();
            foreach (var seed in rootSeeds)
            {
                if (seed is null || !live.TryGetValue(seed.ObjectId, out var binding) || binding.IsModel != seed.IsModel)
                    throw new ArgumentException("Every root seed must identify a live object of its known kind.", nameof(rootSeeds));
                if (!seeds.TryAdd(seed.ObjectId, seed))
                    throw new ArgumentException("Duplicate EMM root seed identity.", nameof(rootSeeds));
            }
            if (seeds.Count != live.Count)
                throw new ArgumentException("Exactly one frozen root seed is required for every live object.", nameof(rootSeeds));

            var records = new Dictionary<string, EmmObjectRecord>(StringComparer.Ordinal);
            foreach (var record in plan.Document.Objects)
                if (!records.TryAdd(record.Key, record)) throw Invalid($"Duplicate EMM record '{record.Key}'.");
            var matched = new Dictionary<Guid, EmmObjectRecord>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in plan.Matches)
            {
                if (!records.TryGetValue(match.RecordKey, out var record) ||
                    !live.TryGetValue(match.ObjectId, out var binding) || record.IsModel != binding.IsModel ||
                    !keys.Add(match.RecordKey) || !matched.TryAdd(match.ObjectId, record))
                    throw Invalid($"Invalid, duplicate or wrong-kind EMM instance mapping for '{match.RecordKey}'.");
            }

            var objects = new List<D3D9EmmRuntimeObject>();
            var byKey = new Dictionary<string, D3D9EmmRuntimeObject>(StringComparer.Ordinal);
            foreach (var binding in plan.LiveObjects)
            {
                matched.TryGetValue(binding.ObjectId, out var record);
                Text(binding.SourcePath);
                var source = OriginalPath(binding.SourcePath);
                Text(source);
                if (record is not null) { Text(record.Key); Text(record.Path); }
                var seed = seeds[binding.ObjectId];
                var root = seed.OriginAbsoluteFxPath is null
                    ? Special(D3D9EmmRuntimeEffectKind.None, "none")
                    : File(seed.OriginAbsoluteFxPath, seed.OriginAbsoluteFxPath, "implicit root seed");
                var value = new D3D9EmmRuntimeObject(binding, source, record, root);
                objects.Add(value);
                if (record is not null) byKey.Add(record.Key, value);
            }

            var sections = new List<D3D9EmmRuntimeSection>();
            var labels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in plan.Document.EffectSections)
            {
                Text(section.Name); Text(section.BaseName);
                if (!labels.Add(section.Name)) throw Invalid($"Duplicate EMM section '{section.Name}'.");
                D3D9EmmRuntimeOwner? owner = null;
                if (section.Owner is { } originalOwner)
                {
                    if (originalOwner.Subset < -1) throw Invalid($"Invalid Owner subset in [{section.Name}].");
                    Text(originalOwner.ObjectKey);
                    if (originalOwner.ParentTargetLabel is { } parent) Text(parent);
                    owner = new(Object(byKey, originalOwner.ObjectKey, section.Name), originalOwner);
                }
                else if (!section.IsDefault) throw Invalid($"Named EMM section [{section.Name}] requires an Owner.");

                var defaultEffect = section.DefaultPath is null ? null : Effect(section.DefaultPath, !section.IsDefault, section.Name);
                var entries = new List<D3D9EmmRuntimeEntry>();
                var entryKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in section.Entries)
                {
                    if (++entryCount > MaxRows) throw Invalid("EMM runtime entries exceed 65536 rows.");
                    Text(entry.ObjectKey);
                    if (!entryKeys.Add(entry.ObjectKey)) throw Invalid($"Duplicate EMM entry '{entry.ObjectKey}' in [{section.Name}].");
                    var value = Object(byKey, entry.ObjectKey, section.Name);
                    var effect = entry.EffectPath is null ? null : Effect(entry.EffectPath, !section.IsDefault, section.Name);
                    var subsets = new Dictionary<int, D3D9EmmRuntimeEffectValue>();
                    foreach (var row in entry.SubsetPaths)
                    {
                        Subset(row.Key, section.Name, entry.ObjectKey);
                        subsets.Add(row.Key, Effect(row.Value, !section.IsDefault, section.Name));
                    }
                    var shown = new Dictionary<int, bool>();
                    foreach (var row in entry.SubsetShown)
                    {
                        Subset(row.Key, section.Name, entry.ObjectKey);
                        shown.Add(row.Key, row.Value);
                    }
                    entries.Add(new(value, entry.ObjectKey, effect, entry.Shown, subsets, shown));
                }
                sections.Add(new(section.Name, section.BaseName, owner, defaultEffect, entries));
            }
            return new(plan, resources, objects, byKey, sections);
        }

        private static D3D9EmmRuntimeObject Object(Dictionary<string, D3D9EmmRuntimeObject> byKey, string key, string section) =>
            byKey.TryGetValue(key, out var value) ? value
                : throw Invalid($"EMM record '{key}' in [{section}] has no explicit matched live instance.");

        private void Subset(int index, string section, string key)
        {
            if (index < 0) throw Invalid($"Invalid subset index for '{key}' in [{section}].");
            if (++subsetCount > MaxRows) throw Invalid("EMM runtime subset effect/show rows exceed 65536 rows.");
        }

        private D3D9EmmRuntimeEffectValue Effect(string value, bool named, string section)
        {
            var kind = D3D9EffectProjectRootPaths.EffectKind(value, named);
            return kind == D3D9EmmRuntimeEffectKind.File
                ? File(value, OriginalPath(value), $"[{section}]") : Special(kind, value);
        }

        private D3D9EmmRuntimeEffectValue Special(D3D9EmmRuntimeEffectKind kind, string value)
        { Text(value); return new(kind, value); }

        private D3D9EmmRuntimeEffectValue File(string originalValue, string absoluteIdentity, string scope)
        {
            var canonical = D3D9EffectResourcePaths.Canonicalize(absoluteIdentity, nameof(absoluteIdentity));
            if (!files.TryGetValue(canonical, out var file))
                throw Invalid($"EMM effect '{originalValue}' in {scope} requires unmapped original resource '{canonical}'.");
            Text(originalValue); Text(file.OriginAbsolutePath); Text(file.MaterializedAbsolutePath);
            return new(D3D9EmmRuntimeEffectKind.File, originalValue, file.OriginAbsolutePath, file.MaterializedAbsolutePath);
        }

        private string OriginalPath(string path) =>
            D3D9EffectProjectRootPaths.OriginalPath(path, plan.PathContext.WorkingDirectory);

        private void Text(string value)
        {
            copiedBytes += D3D9EffectProjectRootPaths.TextValidity(value);
            if (copiedBytes > MaxCopiedBytes)
                throw Invalid("EMM runtime strings and resource paths exceed the combined 16 MiB copied-string budget.");
        }
    }
}
