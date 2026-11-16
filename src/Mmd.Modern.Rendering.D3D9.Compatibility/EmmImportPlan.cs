using System.Collections.ObjectModel;
using System.Text;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>A detached editor identity and source path. It contains no renderer or native object handle.</summary>
public sealed class EmmObjectBinding
{
    public Guid ObjectId { get; }
    public bool IsModel { get; }
    public string SourcePath { get; }
    public int RenderOrder { get; }

    public EmmObjectBinding(Guid objectId, bool isModel, string sourcePath, int renderOrder)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("An object identity is required.", nameof(objectId));
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.Contains('\0'))
            throw new ArgumentException("A nonempty source path without NUL characters is required.", nameof(sourcePath));
        ObjectId = objectId;
        IsModel = isModel;
        SourcePath = sourcePath;
        RenderOrder = renderOrder;
    }
}

public enum EmmObjectMatchReason
{
    RawPath,
    WorkingDirectoryPath,
    EmmDirectoryPath,
    PathSuffix,
    FileName
}

/// <summary>One consumed EMM object row and its distinct editor instance.</summary>
public sealed record EmmObjectMatch(string RecordKey, Guid ObjectId, EmmObjectMatchReason Reason);

/// <summary>The original lexical input that established a record-to-instance match. A later model
/// replacement changes its source while keeping that same Guid's authored EMM assignment.</summary>
public sealed record EmmStableMatchProvenance(string RecordKey, Guid ObjectId, bool IsModel,
    string OriginalSourcePath, int OriginalRenderOrder, EmmObjectMatchReason Reason);

public enum EmmImportDiagnosticCode
{
    UnsupportedVersion,
    UnresolvedPath,
    UnmatchedObject,
    UnmatchedRecord
}

public sealed record EmmImportDiagnostic(EmmImportDiagnosticCode Code, string Message,
    Guid? ObjectId = null, string? RecordKey = null);

/// <summary>
/// The original caller inputs and their absolute lexical context, captured once while creating the plan.
/// FX paths remain as written in Document; this context does not select a future FX loader policy.
/// </summary>
public sealed class EmmImportPathContext
{
    public string OriginalEmmPath { get; }
    public string OriginalWorkingDirectory { get; }
    public string EmmPath { get; }
    public string EmmDirectory { get; }
    public string WorkingDirectory { get; }

    internal EmmImportPathContext(string emmPath, string workingDirectory)
    {
        OriginalEmmPath = emmPath;
        OriginalWorkingDirectory = workingDirectory;
        EmmPath = AbsoluteContext(emmPath, nameof(emmPath));
        WorkingDirectory = Path.TrimEndingDirectorySeparator(AbsoluteContext(workingDirectory, nameof(workingDirectory)));
        EmmDirectory = Path.GetDirectoryName(EmmPath)
            ?? throw new ArgumentException("The EMM path must include a file name.", nameof(emmPath));
        if (Path.GetFileName(EmmPath).Length == 0)
            throw new ArgumentException("The EMM path must include a file name.", nameof(emmPath));
    }

    private static string AbsoluteContext(string value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("An absolute path without NUL characters is required.", parameter);
        return Path.GetFullPath(value);
    }
}

/// <summary>
/// Traditional-engine-only CPU import metadata. Creating a plan performs no file access, workspace mutation,
/// effect discovery, FX loading or GPU work. Default, named, owner and visibility scopes stay in Document.
/// A plan is not an executable effect configuration or a claim that its effects are supported.
/// </summary>
public sealed class EmmImportPlan
{
    public EmmDocument Document { get; }
    public EmmImportPathContext PathContext { get; }
    public IReadOnlyList<EmmObjectBinding> LiveObjects { get; }
    public IReadOnlyList<EmmObjectMatch> Matches { get; }
    public IReadOnlyDictionary<string, EmmObjectMatch> MatchesByRecordKey { get; }
    public IReadOnlyList<EmmObjectBinding> UnmatchedObjects { get; }
    public IReadOnlyList<EmmObjectRecord> UnmatchedRecords { get; }
    public IReadOnlyList<EmmImportDiagnostic> Diagnostics { get; }
    /// <summary>Null for the original path-matched plan. Non-null after an explicit live-object edit;
    /// each surviving assignment retains and validates its original matching inputs.</summary>
    public IReadOnlyList<EmmStableMatchProvenance>? StableMatchProvenance { get; private init; }

    private EmmImportPlan(EmmDocument document, EmmImportPathContext context, EmmObjectBinding[] liveObjects,
        EmmObjectMatch[] matches, EmmObjectBinding[] unmatchedObjects, EmmObjectRecord[] unmatchedRecords,
        EmmImportDiagnostic[] diagnostics)
    {
        Document = document;
        PathContext = context;
        LiveObjects = Array.AsReadOnly(liveObjects);
        Matches = Array.AsReadOnly(matches);
        MatchesByRecordKey = new ReadOnlyDictionary<string, EmmObjectMatch>(
            matches.ToDictionary(m => m.RecordKey, StringComparer.Ordinal));
        UnmatchedObjects = Array.AsReadOnly(unmatchedObjects);
        UnmatchedRecords = Array.AsReadOnly(unmatchedRecords);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public static EmmImportPlan Create(EmmDocument document, string absoluteEmmPath,
        IEnumerable<EmmObjectBinding> liveObjects, string? absoluteWorkingDirectory = null)
        => CreateCore(document, absoluteEmmPath, liveObjects, absoluteWorkingDirectory, null, null, false);

    /// <summary>Changes authored assignments without rematching identical model instances by path.
    /// Existing rows remain unchanged; appended exact-path rows can name previously implicit objects.
    /// Object-set edits use RebindLiveObjects instead.</summary>
    public static EmmImportPlan WithAssignments(EmmImportPlan previous, EmmDocument document)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(document);
        if (previous.Document.Objects.Any(oldRow => !document.Objects.Any(row => row.Key == oldRow.Key && row.Path == oldRow.Path)))
            throw new ArgumentException("Assignment edits must retain the exact EMM object table.", nameof(document));
        var old = previous.LiveObjects.ToDictionary(value => value.ObjectId);
        var proof = (previous.StableMatchProvenance ?? previous.Matches.Select(match =>
        {
            var binding = old[match.ObjectId];
            return new EmmStableMatchProvenance(match.RecordKey, match.ObjectId, binding.IsModel,
                binding.SourcePath, binding.RenderOrder, match.Reason);
        }).ToArray()).ToList();
        var free = previous.UnmatchedObjects.OrderBy(value => value.RenderOrder).ToList();
        foreach (var row in document.Objects.Where(row => !previous.Document.Objects.Any(oldRow => oldRow.Key == row.Key)))
        {
            var binding = free.FirstOrDefault(value => value.IsModel == row.IsModel && value.SourcePath == row.Path)
                ?? throw new ArgumentException("New assignment rows must explicitly identify an unmatched live object by its exact source path.", nameof(document));
            free.Remove(binding);
            proof.Add(new(row.Key, binding.ObjectId, binding.IsModel, binding.SourcePath, binding.RenderOrder, EmmObjectMatchReason.RawPath));
        }
        return CreateCore(document, previous.PathContext.OriginalEmmPath, previous.LiveObjects,
            previous.PathContext.OriginalWorkingDirectory, proof, old.Keys.ToHashSet(), false);
    }

    /// <summary>Edits the live identity table without reassigning any surviving instance's EMM row.
    /// Only newly added Guids enter the existing lexical matcher over the remaining free rows.</summary>
    public static EmmImportPlan RebindLiveObjects(EmmImportPlan previous, IEnumerable<EmmObjectBinding> liveObjects)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(liveObjects);
        var live = liveObjects.ToArray(); var liveIds = live.Select(value => value.ObjectId).ToHashSet();
        var old = previous.LiveObjects.ToDictionary(value => value.ObjectId);
        foreach (var value in live)
            if (old.TryGetValue(value.ObjectId, out var retained) && retained.IsModel != value.IsModel)
                throw new ArgumentException("A surviving EMM instance cannot change object kind.", nameof(liveObjects));
        var proof = previous.StableMatchProvenance ?? previous.Matches.Select(match =>
        {
            var binding = old[match.ObjectId];
            return new EmmStableMatchProvenance(match.RecordKey, match.ObjectId, binding.IsModel,
                binding.SourcePath, binding.RenderOrder, match.Reason);
        }).ToArray();
        return CreateCore(previous.Document, previous.PathContext.OriginalEmmPath, live,
            previous.PathContext.OriginalWorkingDirectory, proof.Where(value => liveIds.Contains(value.ObjectId)).ToArray(),
            old.Keys.Where(liveIds.Contains).ToHashSet(), false);
    }

    /// <summary>Restores only explicitly persisted stable-Guid assignments, validating every original
    /// match's source, kind and lexical reason. It cannot invent missing mappings on package reopen.</summary>
    public static EmmImportPlan RestoreStableBindings(EmmDocument document, string absoluteEmmPath,
        IEnumerable<EmmObjectBinding> liveObjects, string absoluteWorkingDirectory,
        IEnumerable<EmmStableMatchProvenance> provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return CreateCore(document, absoluteEmmPath, liveObjects, absoluteWorkingDirectory,
            provenance.ToArray(), null, true);
    }

    private static EmmImportPlan CreateCore(EmmDocument document, string absoluteEmmPath,
        IEnumerable<EmmObjectBinding> liveObjects, string? absoluteWorkingDirectory,
        IReadOnlyList<EmmStableMatchProvenance>? preserved, HashSet<Guid>? retainedIds, bool restoreOnly)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(liveObjects);
        // This is the only ambient directory read. Every comparison below uses the frozen context.
        var context = new EmmImportPathContext(absoluteEmmPath, absoluteWorkingDirectory ?? Environment.CurrentDirectory);
        var live = liveObjects.ToArray();
        var identities = new HashSet<Guid>();
        foreach (var binding in live)
        {
            if (binding is null) throw new ArgumentException("Live object bindings cannot contain null.", nameof(liveObjects));
            if (!identities.Add(binding.ObjectId))
                throw new ArgumentException("Live object identities must be unique, including across object kinds.", nameof(liveObjects));
        }

        // Adapted from MikuDanceStudio bfb6297aa63a37770efcd3f7a599de5dd39be12b,
        // third_party/mmeffect/src/mmeffect/emm_manager.cpp: ObjectPathMatches / MmeEmmLoad.
        // MIT copyright and permission notice: LICENSE.MikuDanceStudio.txt distributed with this assembly.
        // That third-party reconstruction is a reference implementation, not original MMD/MME source.
        // Preserve each caller instance and finish the entire exact-path pass before filename fallback.
        live = live.OrderBy(binding => binding.RenderOrder).ToArray();
        var records = document.Objects.OrderBy(record => record.Slot).ToArray();
        var diagnostics = new List<EmmImportDiagnostic>();
        if (!document.IsSupportedVersion)
            diagnostics.Add(new(EmmImportDiagnosticCode.UnsupportedVersion,
                $"EMM version {document.Version} is retained as metadata; executable import is not supported."));

        var normalizer = new MatchPathNormalizer(context, document.AnsiCodePage, diagnostics);
        var livePaths = live.Select(binding => normalizer.Live(binding)).ToArray();
        var recordPaths = records.Select(record => normalizer.Record(record)).ToArray();
        var matchedRecords = Enumerable.Repeat(-1, live.Length).ToArray();
        var reasons = new EmmObjectMatchReason[live.Length];
        var consumed = new bool[records.Length];
        var proofs = new Dictionary<Guid, EmmStableMatchProvenance>();
        foreach (var proof in preserved ?? [])
        {
            if (proof is null || !Enum.IsDefined(proof.Reason) || !proofs.TryAdd(proof.ObjectId, proof))
                throw new ArgumentException("Stable EMM provenance must identify distinct valid instances.", nameof(preserved));
            int i = Array.FindIndex(live, value => value.ObjectId == proof.ObjectId);
            int r = Array.FindIndex(records, value => value.Key == proof.RecordKey);
            if (i < 0 || r < 0 || consumed[r] || live[i].IsModel != proof.IsModel || records[r].IsModel != proof.IsModel)
                throw new ArgumentException("Stable EMM provenance has an unknown, duplicate or wrong-kind assignment.", nameof(preserved));
            var original = new EmmObjectBinding(proof.ObjectId, proof.IsModel, proof.OriginalSourcePath, proof.OriginalRenderOrder);
            var originalPath = normalizer.Live(original);
            bool provenMatch = proof.Reason == EmmObjectMatchReason.FileName
                ? Equal(FileName(records[r].Path), FileName(original.SourcePath))
                : ExactMatch(recordPaths[r], originalPath) == proof.Reason;
            if (!provenMatch) throw new ArgumentException("Stable EMM provenance does not prove its original lexical match.", nameof(preserved));
            matchedRecords[i] = r; reasons[i] = proof.Reason; consumed[r] = true;
        }
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < live.Length; i++)
            {
                if (matchedRecords[i] >= 0 || restoreOnly || retainedIds?.Contains(live[i].ObjectId) == true) continue;
                for (var r = 0; r < records.Length; r++)
                {
                    if (consumed[r] || records[r].IsModel != live[i].IsModel) continue;
                    var reason = pass == 0 ? ExactMatch(recordPaths[r], livePaths[i])
                        : Equal(FileName(records[r].Path), FileName(live[i].SourcePath))
                            ? EmmObjectMatchReason.FileName : (EmmObjectMatchReason?)null;
                    if (reason is null) continue;
                    matchedRecords[i] = r;
                    reasons[i] = reason.Value;
                    consumed[r] = true;
                    break;
                }
            }
        }

        var matches = new List<EmmObjectMatch>();
        var unmatchedObjects = new List<EmmObjectBinding>();
        for (var i = 0; i < live.Length; i++)
        {
            var r = matchedRecords[i];
            if (r >= 0) matches.Add(new(records[r].Key, live[i].ObjectId, reasons[i]));
            else
            {
                unmatchedObjects.Add(live[i]);
                diagnostics.Add(new(EmmImportDiagnosticCode.UnmatchedObject,
                    $"No EMM object row matched '{live[i].SourcePath}'.", live[i].ObjectId));
            }
        }
        var unmatchedRecords = new List<EmmObjectRecord>();
        for (var r = 0; r < records.Length; r++)
        {
            if (consumed[r]) continue;
            unmatchedRecords.Add(records[r]);
            diagnostics.Add(new(EmmImportDiagnosticCode.UnmatchedRecord,
                $"EMM object row '{records[r].Key}' matched no live object.", RecordKey: records[r].Key));
        }
        var result = new EmmImportPlan(document, context, live, matches.ToArray(), unmatchedObjects.ToArray(),
            unmatchedRecords.ToArray(), diagnostics.ToArray());
        if (preserved is not null)
            result = WithStableProvenance();
        return result;

        EmmImportPlan WithStableProvenance() => new(document, context, live, matches.ToArray(), unmatchedObjects.ToArray(),
            unmatchedRecords.ToArray(), diagnostics.ToArray())
        {
            StableMatchProvenance = Array.AsReadOnly(matches.Select(match => proofs.TryGetValue(match.ObjectId, out var proof)
                ? proof : new EmmStableMatchProvenance(match.RecordKey, match.ObjectId,
                    live.Single(value => value.ObjectId == match.ObjectId).IsModel,
                    live.Single(value => value.ObjectId == match.ObjectId).SourcePath,
                    live.Single(value => value.ObjectId == match.ObjectId).RenderOrder, match.Reason)).ToArray())
        };
    }

    private static EmmObjectMatchReason? ExactMatch(RecordPaths record, LivePaths live)
    {
        if (Equal(record.Raw, live.Raw)) return EmmObjectMatchReason.RawPath;
        if (Equal(record.Absolute, live.Absolute)) return EmmObjectMatchReason.WorkingDirectoryPath;
        if (Equal(record.JoinedAbsolute, live.Absolute)) return EmmObjectMatchReason.EmmDirectoryPath;
        return TailEqual(record.Absolute, live.Absolute) || TailEqual(record.JoinedAbsolute, live.Absolute)
            ? EmmObjectMatchReason.PathSuffix : null;
    }

    private static bool Equal(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool TailEqual(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0 || first.Length == second.Length) return Equal(first, second);
        return first.Length > second.Length
            ? first.EndsWith(second, StringComparison.OrdinalIgnoreCase)
            : second.EndsWith(first, StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(string path)
    {
        var separator = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        if (separator >= 0) return path[(separator + 1)..];
        return HasDrive(path) ? path[2..] : path;
    }

    private static bool HasDrive(string path) => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    private readonly record struct LivePaths(string Raw, string Absolute);
    private readonly record struct RecordPaths(string Raw, string Absolute, string JoinedAbsolute);

    private sealed class MatchPathNormalizer(EmmImportPathContext context, int ansiCodePage,
        List<EmmImportDiagnostic> diagnostics)
    {
        // Native _fullpath uses a 260-byte narrow-character buffer. Preserve its lexical fallback for
        // comparison only; this is not an I/O path-length policy or a restriction on retained document data.
        private readonly Encoding encoding = LegacyAnsiEncoding.Create(ansiCodePage);

        public LivePaths Live(EmmObjectBinding binding) => new(binding.SourcePath,
            Absolute(binding.SourcePath, binding.ObjectId, null));

        public RecordPaths Record(EmmObjectRecord record)
        {
            var raw = record.Path;
            var joined = HasDrive(raw) || raw.StartsWith('\\') || raw.StartsWith('/')
                ? raw : Path.Combine(context.EmmDirectory, raw);
            return new(raw, Absolute(raw, null, record.Key), Absolute(joined, null, record.Key));
        }

        private string Absolute(string path, Guid? objectId, string? recordKey)
        {
            var original = path;
            // A different drive's relative directory is not part of the supplied context. Do not ask
            // Windows for its ambient per-drive directory or silently substitute that drive's root.
            if (HasDrive(path) && (path.Length == 2 || path[2] is not ('\\' or '/')))
            {
                if (!HasDrive(context.WorkingDirectory) || !Equal(path[..2], context.WorkingDirectory[..2]))
                    return Unresolved(path, objectId, recordKey, "A drive-relative path needs an explicit directory for that drive.");
                path = Path.Combine(context.WorkingDirectory, path[2..]);
            }
            try
            {
                var absolute = Path.GetFullPath(path, context.WorkingDirectory);
                return encoding.GetByteCount(absolute) < 260 ? absolute : original;
            }
            catch (EncoderFallbackException)
            {
                return Unresolved(original, objectId, recordKey, "The path cannot be represented in the EMM ANSI code page.");
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Unresolved(original, objectId, recordKey, "The path could not be normalized lexically.");
            }
        }

        private string Unresolved(string raw, Guid? objectId, string? recordKey, string reason)
        {
            var diagnostic = new EmmImportDiagnostic(EmmImportDiagnosticCode.UnresolvedPath,
                $"{reason} Retained '{raw}' for comparison.", objectId, recordKey);
            if (!diagnostics.Contains(diagnostic)) diagnostics.Add(diagnostic);
            return raw;
        }
    }
}
