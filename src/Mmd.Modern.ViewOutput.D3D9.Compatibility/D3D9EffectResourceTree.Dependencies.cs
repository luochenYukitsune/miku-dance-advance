using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>One original file identity, retained independently of its digest and private local name.</summary>
public sealed record D3D9EffectResourceTreeDependencyNode(string ResourceId, string OriginAbsolutePath,
    string RelativePath, D3D9EffectDependencyCategory Category);

/// <summary>Detached discovery evidence. This describes the captured compiler graph; it does not execute it.</summary>
public sealed class D3D9EffectResourceTreeDependencyGraph
{
    internal D3D9EffectResourceTreeDependencyGraph(D3D9EffectDependencyRoot[] roots,
        D3D9EffectResourceTreeDependencyNode[] nodes, D3D9EffectDependencyEdge[] edges,
        D3D9EffectDependencyCompilerIdentity compilerIdentity, D3D9EffectDependencyProfile[] profiles,
        D3D9EffectDependencyDiagnostic[] diagnostics)
    {
        Roots = Array.AsReadOnly(roots); Nodes = Array.AsReadOnly(nodes); Edges = Array.AsReadOnly(edges);
        CompilerIdentity = compilerIdentity; Profiles = Array.AsReadOnly(profiles); Diagnostics = Array.AsReadOnly(diagnostics);
    }
    public IReadOnlyList<D3D9EffectDependencyRoot> Roots { get; }
    public IReadOnlyList<D3D9EffectResourceTreeDependencyNode> Nodes { get; }
    public IReadOnlyList<D3D9EffectDependencyEdge> Edges { get; }
    public D3D9EffectDependencyCompilerIdentity CompilerIdentity { get; }
    public IReadOnlyList<D3D9EffectDependencyProfile> Profiles { get; }
    public IReadOnlyList<D3D9EffectDependencyDiagnostic> Diagnostics { get; }
}

public sealed partial class D3D9EffectResourceTree
{
    // Schema-three adapter bounds, separate from the neutral container's configurable project limits.
    private const int MaximumGraphRecords = 262_144;
    private const long MaximumReferenceBytes = 16L * 1024 * 1024;
    private readonly MetadataV3? dependencyMetadata;
    /// <summary>Null for explicit common-tree schemas one and two. All original paths are identity data only.</summary>
    public D3D9EffectResourceTreeDependencyGraph? DependencyGraph { get; }

    private D3D9EffectResourceTree(D3D9EffectResourceContent[] contents, MetadataV3 metadata)
        : this(contents, metadata.Directories, metadata.RequiredRelativeFiles, null)
    {
        dependencyMetadata = metadata;
        DependencyGraph = ToPublicGraph(metadata);
    }

    /// <summary>
    /// Copies a completed collector closure, without opening any original. Every distinct origin receives
    /// its own short ASCII local file even when bytes match. Optional diagnostics must be informational
    /// diagnostics from that same complete collection; failed/cancelled results have no admissible closure.
    /// </summary>
    public static D3D9EffectResourceTree FromClosure(D3D9EffectDependencyClosure closure,
        D3D9EffectResourceTreeLimits? limits = null,
        IEnumerable<D3D9EffectDependencyDiagnostic>? diagnostics = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(closure); limits ??= new(); limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (closure.Nodes.Count is < 1 || closure.Nodes.Count > limits.MaxFiles ||
            closure.Roots.Count is < 1 or > MaximumGraphRecords || closure.Edges.Count > MaximumGraphRecords ||
            closure.Profiles.Count > MaximumGraphRecords || closure.CompilerIdentity is null)
            throw new InvalidDataException("A nonempty complete compiler dependency closure is required.");
        var ordered = closure.Nodes.OrderBy(node => node.Snapshot.OriginAbsolutePath, StringComparer.Ordinal).ToArray();
        var contents = new D3D9EffectResourceContent[ordered.Length];
        var nodes = new NodeData[ordered.Length]; var files = new FileData[ordered.Length]; long total = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var node = ordered[index]; var snapshot = node.Snapshot;
            if (snapshot.ByteLength > limits.MaxFileBytes || snapshot.ByteLength > limits.MaxTotalBytes - total)
                throw new InvalidDataException("Dependency snapshot bytes exceed effect-tree adapter budgets.");
            total += snapshot.ByteLength;
            string local = DependencyLocalPath(index); byte[] bytes = snapshot.CopyBytes();
            files[index] = new(local, ResourcePath(index), snapshot.ByteLength, snapshot.Sha256);
            contents[index] = new(new(local, snapshot.ByteLength, snapshot.Sha256), bytes);
            nodes[index] = new(node.ResourceId, snapshot.OriginAbsolutePath, local, node.Category);
        }
        // Root order and duplicate origins with different owners are meaningful; only required local names
        // are deduplicated, since every root's individual provenance remains in Roots and its Root edge.
        var byOrigin = nodes.ToDictionary(node => node.OriginAbsolutePath, StringComparer.OrdinalIgnoreCase);
        string[] required = closure.Roots.Select(root => byOrigin.TryGetValue(root.OriginAbsolutePath, out var node)
            ? node.RelativePath : throw new InvalidDataException("A dependency root has no captured node."))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        long referenceBytes = 0;
        foreach (var edge in closure.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckReferenceBudget(edge.Reference); CheckReferenceBudget(edge.RawRow);
        }
        var savedDiagnostics = new List<DiagnosticData>();
        foreach (var diagnostic in diagnostics ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (savedDiagnostics.Count == MaximumGraphRecords) throw new InvalidDataException("Dependency diagnostics exceed their record budget.");
            ArgumentNullException.ThrowIfNull(diagnostic);
            CheckReferenceBudget(diagnostic.Reference); CheckReferenceBudget(diagnostic.RawMessage);
            savedDiagnostics.Add(ToData(diagnostic));
        }
        var metadata = new MetadataV3(3, D3D9EffectDependencyStatus.Complete, [], required, files, nodes,
            closure.Roots.Select(root => new RootData(root.OriginAbsolutePath, root.Provenance with { })).ToArray(),
            closure.CompilerIdentity with { }, closure.Profiles.Select(profile => new ProfileData(profile.Id, profile.Flags,
                profile.Macros.Select(macro => macro with { }).ToArray(), profile.EntryPoint, profile.Target)).ToArray(),
            closure.Edges.Select(ToData).ToArray(), savedDiagnostics.ToArray());
        new DependencyValidator(metadata, limits, cancellationToken).Validate();
        if (JsonSerializer.SerializeToUtf8Bytes(metadata, Json).Length > limits.MaxMetadataBytes)
            throw new InvalidDataException("Effect dependency metadata exceeds its byte budget.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(contents, metadata);

        void CheckReferenceBudget(D3D9EffectDependencyReference? reference)
        {
            if (reference is null) return;
            if (reference.ByteLength > MaximumReferenceBytes - referenceBytes)
                throw new InvalidDataException("Dependency references exceed their copied-byte budget.");
            referenceBytes += reference.ByteLength;
        }
    }

    private static D3D9EffectResourceTree ReadDependencyPackage(IPmaPackageResources package, string raw,
        D3D9EffectResourceTreeLimits limits, CancellationToken token)
    {
        var metadata = JsonSerializer.Deserialize<MetadataV3>(raw, Json)
            ?? throw new InvalidDataException("Missing effect dependency metadata.");
        new DependencyValidator(metadata, limits, token).Validate();
        // Check presence for the entire graph before reading the first blob. Neutral package admission
        // validates the archive; this also supports read-only preflight callers without live models.
        var resources = package.ResourcePaths.ToHashSet(StringComparer.Ordinal);
        if (metadata.Files.Any(file => !resources.Contains(file.ResourcePath)))
            throw new InvalidDataException("A required effect dependency resource is absent.");
        var contents = new D3D9EffectResourceContent[metadata.Files.Length];
        for (int index = 0; index < contents.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var file = metadata.Files[index];
            byte[] bytes = package.ReadResource(file.ResourcePath, file.ByteLength);
            if (bytes.LongLength != file.ByteLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                throw new InvalidDataException("An effect dependency blob disagrees with its byte length or SHA256.");
            contents[index] = new(new(file.RelativePath, file.ByteLength, file.Sha256), bytes);
        }
        token.ThrowIfCancellationRequested(); return new(contents, metadata);
    }

    private static bool Digest(string? digest, bool lowercase = true) => digest is { Length: 64 } &&
        digest.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f' || !lowercase && value is >= 'A' and <= 'F');
    private static string DependencyLocalPath(int index) => "f" + index.ToString("D5", CultureInfo.InvariantCulture) + ".bin";
    private static ReferenceData? ToData(D3D9EffectDependencyReference? value) => value is null ? null : new(value.CopyBytes(), value.CodePage);
    private static D3D9EffectDependencyReference? FromData(ReferenceData? value) => value is null ? null : new(value.Bytes, value.CodePage);
    private static EdgeData ToData(D3D9EffectDependencyEdge edge) => new(edge.RootIndex, edge.TopFxOriginAbsolutePath,
        edge.ParentOriginAbsolutePath, edge.ProfileId, edge.Kind, ToData(edge.Reference)!, edge.TargetResourceId, edge.Symbol,
        edge.Parameter, edge.Annotation, edge.RowOrdinal, edge.RowKey, ToData(edge.RawRow), edge.FunctionEntryPoint, edge.FunctionTarget);
    private static DiagnosticData ToData(D3D9EffectDependencyDiagnostic value) => new(value.Status, value.Code, value.Message,
        value.RootIndex, value.TopFxOriginAbsolutePath, value.ParentOriginAbsolutePath, value.ProfileId, ToData(value.Reference),
        value.CandidateOriginAbsolutePath, value.Parameter, value.Annotation, ToData(value.RawMessage), value.HResult);
    private static D3D9EffectResourceTreeDependencyGraph ToPublicGraph(MetadataV3 value) => new(
        value.Roots.Select(root => new D3D9EffectDependencyRoot(root.OriginAbsolutePath, root.Provenance with { })).ToArray(),
        value.Nodes.Select(node => new D3D9EffectResourceTreeDependencyNode(node.ResourceId, node.OriginAbsolutePath, node.RelativePath, node.Category)).ToArray(),
        value.Edges.Select(edge => new D3D9EffectDependencyEdge(edge.RootIndex, edge.TopFxOriginAbsolutePath, edge.ParentOriginAbsolutePath,
            edge.ProfileId, edge.Kind, FromData(edge.Reference)!, edge.TargetResourceId, edge.Symbol, edge.Parameter, edge.Annotation,
            edge.RowOrdinal, edge.RowKey, FromData(edge.RawRow), edge.FunctionEntryPoint, edge.FunctionTarget)).ToArray(),
        value.CompilerIdentity with { }, value.Profiles.Select(profile => new D3D9EffectDependencyProfile(profile.Id, profile.Flags,
            profile.Macros.Select(macro => macro with { }), profile.EntryPoint, profile.Target)).ToArray(),
        value.Diagnostics.Select(diagnostic => new D3D9EffectDependencyDiagnostic(diagnostic.Status, diagnostic.Code, diagnostic.Message,
            diagnostic.RootIndex, diagnostic.TopFxOriginAbsolutePath, diagnostic.ParentOriginAbsolutePath, diagnostic.ProfileId,
            FromData(diagnostic.Reference), diagnostic.CandidateOriginAbsolutePath, diagnostic.Parameter, diagnostic.Annotation,
            FromData(diagnostic.RawMessage), diagnostic.HResult)).ToArray());

    private sealed record MetadataV3(int SchemaVersion, D3D9EffectDependencyStatus Status, string[] Directories,
        string[] RequiredRelativeFiles, FileData[] Files, NodeData[] Nodes, RootData[] Roots,
        D3D9EffectDependencyCompilerIdentity CompilerIdentity, ProfileData[] Profiles, EdgeData[] Edges, DiagnosticData[] Diagnostics);
    private sealed record RootData(string OriginAbsolutePath, D3D9EffectDependencyProvenance Provenance);
    private sealed record NodeData(string ResourceId, string OriginAbsolutePath, string RelativePath, D3D9EffectDependencyCategory Category);
    private sealed record ProfileData(string Id, uint Flags, D3D9EffectDependencyMacro[] Macros, string? EntryPoint, string? Target);
    private sealed record ReferenceData(byte[] Bytes, int CodePage);
    private sealed record EdgeData(int RootIndex, string TopFxOriginAbsolutePath, string ParentOriginAbsolutePath,
        string ProfileId, D3D9EffectDependencyEdgeKind Kind, ReferenceData Reference, string? TargetResourceId,
        D3D9EffectDependencySymbol? Symbol, string? Parameter, string? Annotation, int? RowOrdinal, string? RowKey,
        ReferenceData? RawRow, string? FunctionEntryPoint, string? FunctionTarget);
    private sealed record DiagnosticData(D3D9EffectDependencyStatus Status, string Code, string Message, int? RootIndex,
        string? TopFxOriginAbsolutePath, string? ParentOriginAbsolutePath, string? ProfileId, ReferenceData? Reference,
        string? CandidateOriginAbsolutePath, string? Parameter, string? Annotation, ReferenceData? RawMessage, int? HResult);
}
