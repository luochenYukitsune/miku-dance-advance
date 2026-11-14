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
        ValidateDependencyMetadata(metadata, limits, cancellationToken);
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
        ValidateDependencyMetadata(metadata, limits, token);
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

    private static void ValidateDependencyMetadata(MetadataV3 metadata, D3D9EffectResourceTreeLimits limits, CancellationToken token)
    {
        if (metadata.SchemaVersion != 3 || metadata.Status != D3D9EffectDependencyStatus.Complete ||
            metadata.Directories is null || metadata.Directories.Length != 0 || metadata.RequiredRelativeFiles is null ||
            metadata.Files is null || metadata.Files.Length is < 1 || metadata.Files.Length > limits.MaxFiles ||
            metadata.Nodes is null || metadata.Nodes.Length != metadata.Files.Length ||
            metadata.Roots is null || metadata.Roots.Length is < 1 or > MaximumGraphRecords ||
            metadata.Profiles is null || metadata.Profiles.Length is < 2 or > MaximumGraphRecords ||
            metadata.Edges is null || metadata.Edges.Length is < 1 or > MaximumGraphRecords ||
            metadata.Diagnostics is null || metadata.Diagnostics.Length > MaximumGraphRecords || metadata.CompilerIdentity is null)
            throw new InvalidDataException("Unsupported or incomplete effect dependency graph.");
        var identity = metadata.CompilerIdentity;
        if (string.IsNullOrWhiteSpace(identity.HostSemanticsVersion) || !Digest(identity.CompilerSha256, lowercase: false) ||
            identity.ProcessBitness != 64 || identity.AnsiCodePage <= 0 ||
            identity.ShaderCompilerSha256 is { } shaderDigest && !Digest(shaderDigest, lowercase: false))
            throw new InvalidDataException("Invalid effect dependency compiler identity.");
        var byId = new Dictionary<string, NodeData>(StringComparer.Ordinal);
        var byOrigin = new Dictionary<string, NodeData>(StringComparer.OrdinalIgnoreCase);
        var categories = new Dictionary<string, D3D9EffectDependencyCategory>(StringComparer.Ordinal);
        long pathBytes = 0, totalBytes = 0, referenceBytes = 0;
        string? previousOrigin = null;
        for (int index = 0; index < metadata.Nodes.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var node = metadata.Nodes[index] ?? throw new InvalidDataException("Missing dependency node.");
            var file = metadata.Files[index] ?? throw new InvalidDataException("Missing dependency file descriptor.");
            Origin(node.OriginAbsolutePath);
            if (previousOrigin is not null && StringComparer.Ordinal.Compare(previousOrigin, node.OriginAbsolutePath) >= 0)
                throw new InvalidDataException("Dependency origins are not in canonical order.");
            previousOrigin = node.OriginAbsolutePath;
            string expectedId = "origin-" + Convert.ToHexStringLower(SHA256.HashData(EffectTreeFiles.StrictUtf8.GetBytes(node.OriginAbsolutePath.ToUpperInvariant())));
            if (node.ResourceId != expectedId || !byId.TryAdd(node.ResourceId, node) || !byOrigin.TryAdd(node.OriginAbsolutePath, node) ||
                node.Category == 0 || (node.Category & ~(D3D9EffectDependencyCategory.Effect | D3D9EffectDependencyCategory.Include | D3D9EffectDependencyCategory.Resource)) != 0 ||
                node.RelativePath != DependencyLocalPath(index) || file.RelativePath != node.RelativePath || file.ResourcePath != ResourcePath(index) ||
                file.ByteLength < 0 || file.ByteLength > limits.MaxFileBytes || file.ByteLength > limits.MaxTotalBytes - totalBytes || !Digest(file.Sha256))
                throw new InvalidDataException("Invalid dependency node, local path or blob descriptor.");
            pathBytes += EffectTreeFiles.RelativePath(node.RelativePath);
            if (pathBytes > limits.MaxPathBytes) throw new InvalidDataException("Dependency paths exceed their byte budget.");
            totalBytes += file.ByteLength; categories.Add(node.ResourceId, 0);
        }
        string[] required = Required(metadata.RequiredRelativeFiles, limits);
        if (!metadata.RequiredRelativeFiles.SequenceEqual(required, StringComparer.Ordinal))
            throw new InvalidDataException("Dependency required files are not in canonical order.");
        var expectedRequired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in metadata.Roots)
        {
            token.ThrowIfCancellationRequested();
            if (root is null || root.Provenance is null) throw new InvalidDataException("Missing dependency root provenance.");
            var node = NodeAt(root.OriginAbsolutePath); expectedRequired.Add(node.RelativePath);
            Use(node, D3D9EffectDependencyCategory.Effect);
        }
        if (!expectedRequired.SetEquals(required)) throw new InvalidDataException("Every dependency root must be required, with no extra required files.");
        var profiles = new Dictionary<string, ProfileData>(StringComparer.Ordinal);
        foreach (var profile in metadata.Profiles)
        {
            token.ThrowIfCancellationRequested();
            if (profile is null || string.IsNullOrEmpty(profile.Id) || profile.Macros is null || !profiles.TryAdd(profile.Id, profile) ||
                profile.Macros.Length > MaximumGraphRecords || profile.Macros.Any(macro => macro is null || string.IsNullOrEmpty(macro.Name) || macro.Value is null) ||
                profile.Macros.Select(macro => macro.Name).Distinct(StringComparer.Ordinal).Count() != profile.Macros.Length)
                throw new InvalidDataException("Invalid or duplicate dependency compiler profile.");
            if (profile.Id is "effect-mip-off" or "effect-mip-on")
            {
                bool mip = profile.Id == "effect-mip-on";
                if (profile.Flags is not (0x1000 or 0x10C0) || profile.EntryPoint is not null || profile.Target is not null ||
                    profile.Macros.Length != (mip ? 2 : 1) || !profile.Macros.Any(macro => macro.Name == "_INDEX" && macro.Value == "PSIZE15") ||
                    mip && !profile.Macros.Any(macro => macro.Name == "MME_MIPMAP" && macro.Value == ""))
                    throw new InvalidDataException("A main dependency compiler profile disagrees with fixed host semantics.");
            }
            else if (profile.Flags != 0 || profile.Macros.Length != 0 || string.IsNullOrEmpty(profile.EntryPoint) || string.IsNullOrEmpty(profile.Target))
                throw new InvalidDataException("A generated texture compiler profile must retain its zero flags, empty defines, Function and Target.");
        }
        if (!profiles.ContainsKey("effect-mip-off") || !profiles.ContainsKey("effect-mip-on"))
            throw new InvalidDataException("Both fixed main compiler profiles are required.");
        var rootEdges = new bool[metadata.Roots.Length];
        var groups = new Dictionary<(int Root, string TopId), List<EdgeData>>();
        var defaultEdges = new Dictionary<(int Root, string TopId), List<string>>();
        foreach (var edge in metadata.Edges)
        {
            token.ThrowIfCancellationRequested();
            if (edge is null || (uint)edge.RootIndex >= metadata.Roots.Length || !Enum.IsDefined(edge.Kind) || edge.RowOrdinal is < 0)
                throw new InvalidDataException("Invalid dependency edge kind, root or row ordinal.");
            Reference(edge.Reference); Reference(edge.RawRow, optional: true);
            var top = NodeAt(edge.TopFxOriginAbsolutePath); var parent = NodeAt(edge.ParentOriginAbsolutePath);
            NodeData? target = edge.TargetResourceId is null ? null : byId.GetValueOrDefault(edge.TargetResourceId)
                ?? throw new InvalidDataException("A dependency edge targets an unknown resource.");
            Use(top, D3D9EffectDependencyCategory.Effect);
            if (edge.Kind == D3D9EffectDependencyEdgeKind.Root)
            {
                if (rootEdges[edge.RootIndex] || edge.ProfileId != "" || top != parent || target != top ||
                    !StringComparer.OrdinalIgnoreCase.Equals(top.OriginAbsolutePath, metadata.Roots[edge.RootIndex].OriginAbsolutePath) ||
                    edge.Symbol is not null || edge.Parameter is not null || edge.Annotation is not null || edge.RowOrdinal is not null ||
                    edge.RowKey is not null || edge.RawRow is not null || edge.FunctionEntryPoint is not null || edge.FunctionTarget is not null ||
                    edge.Reference.CodePage != 65001 || !edge.Reference.Bytes.AsSpan().SequenceEqual(EffectTreeFiles.StrictUtf8.GetBytes(metadata.Roots[edge.RootIndex].OriginAbsolutePath)))
                    throw new InvalidDataException("Invalid or duplicate dependency root edge.");
                rootEdges[edge.RootIndex] = true; continue;
            }
            if (edge.ProfileId is null || !profiles.ContainsKey(edge.ProfileId))
                throw new InvalidDataException("A dependency edge references an unknown compiler profile.");
            if (edge.Kind == D3D9EffectDependencyEdgeKind.Symbol)
            {
                if (target is not null || edge.Symbol is null || !Enum.IsDefined(edge.Symbol.Value))
                    throw new InvalidDataException("A symbolic dependency edge has an invalid target.");
            }
            else if (target is null || edge.Symbol is not null)
                throw new InvalidDataException("A file dependency edge must identify one captured resource.");
            if (edge.Kind is D3D9EffectDependencyEdgeKind.IncludeLocal or D3D9EffectDependencyEdgeKind.IncludeSystem)
            {
                Use(target!, D3D9EffectDependencyCategory.Include);
                if (edge.Parameter is not null || edge.Annotation is not null || edge.RowOrdinal is not null || edge.RowKey is not null ||
                    edge.RawRow is not null || edge.FunctionEntryPoint is not null || edge.FunctionTarget is not null)
                    throw new InvalidDataException("An include edge contains non-include semantic fields.");
            }
            else
            {
                if (parent != top) throw new InvalidDataException("A semantic dependency reference must be based on its top FX.");
                if (edge.Kind == D3D9EffectDependencyEdgeKind.ResourceName) Use(target!, D3D9EffectDependencyCategory.Resource);
                if (edge.Kind == D3D9EffectDependencyEdgeKind.DefaultEffect)
                {
                    Use(target!, D3D9EffectDependencyCategory.Effect);
                    if (!defaultEdges.TryGetValue((edge.RootIndex, top.ResourceId), out var next)) defaultEdges.Add((edge.RootIndex, top.ResourceId), next = []);
                    next.Add(target!.ResourceId);
                }
                if (edge.Kind == D3D9EffectDependencyEdgeKind.TextureFunction && (target != top || string.IsNullOrEmpty(edge.FunctionEntryPoint) ||
                    string.IsNullOrEmpty(edge.FunctionTarget) || !profiles.Values.Any(profile => profile.EntryPoint == edge.FunctionEntryPoint && profile.Target == edge.FunctionTarget)))
                    throw new InvalidDataException("A generated texture dependency lacks its exact empty-macro compiler invocation.");
            }
            if (!groups.TryGetValue((edge.RootIndex, top.ResourceId), out var group)) groups.Add((edge.RootIndex, top.ResourceId), group = []);
            group.Add(edge);
        }
        if (rootEdges.Any(present => !present)) throw new InvalidDataException("A dependency root edge is missing.");
        // Each top FX must belong to the declared root's DefaultEffect graph. Includes must be connected
        // to that top FX, rather than merely naming an unrelated captured file as their base.
        var reachableFx = new HashSet<(int Root, string TopId)>();
        var fxQueue = new Queue<(int Root, string TopId)>();
        for (int index = 0; index < metadata.Roots.Length; index++) fxQueue.Enqueue((index, NodeAt(metadata.Roots[index].OriginAbsolutePath).ResourceId));
        while (fxQueue.TryDequeue(out var work))
        {
            token.ThrowIfCancellationRequested(); if (!reachableFx.Add(work)) continue;
            if (defaultEdges.TryGetValue(work, out var next)) foreach (string id in next) fxQueue.Enqueue((work.Root, id));
        }
        foreach (var pair in groups)
        {
            token.ThrowIfCancellationRequested();
            if (!reachableFx.Contains(pair.Key)) throw new InvalidDataException("A dependency top FX is not reachable from its declared root.");
            var includes = pair.Value.Where(edge => edge.Kind is D3D9EffectDependencyEdgeKind.IncludeLocal or D3D9EffectDependencyEdgeKind.IncludeSystem)
                .GroupBy(edge => NodeAt(edge.ParentOriginAbsolutePath).ResourceId).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var reached = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<string>(); queue.Enqueue(pair.Key.TopId);
            while (queue.TryDequeue(out var parentId))
            {
                token.ThrowIfCancellationRequested(); if (!reached.Add(parentId)) continue;
                if (includes.TryGetValue(parentId, out var next)) foreach (var edge in next) queue.Enqueue(edge.TargetResourceId!);
            }
            if (includes.Keys.Any(parentId => !reached.Contains(parentId))) throw new InvalidDataException("A dependency include parent is not connected to its top FX.");
        }
        foreach (var node in metadata.Nodes)
            if (categories[node.ResourceId] != node.Category) throw new InvalidDataException("A dependency node's categories disagree with its graph references.");
        foreach (var diagnostic in metadata.Diagnostics)
        {
            token.ThrowIfCancellationRequested();
            if (diagnostic is null || diagnostic.Status != D3D9EffectDependencyStatus.Complete || string.IsNullOrEmpty(diagnostic.Code) ||
                diagnostic.Message is null || diagnostic.RootIndex is < 0 || diagnostic.RootIndex >= metadata.Roots.Length || diagnostic.HResult is < 0 ||
                diagnostic.ProfileId is not null && !profiles.ContainsKey(diagnostic.ProfileId))
                throw new InvalidDataException("Invalid informational dependency diagnostic.");
            if (diagnostic.TopFxOriginAbsolutePath is not null) _ = NodeAt(diagnostic.TopFxOriginAbsolutePath);
            if (diagnostic.ParentOriginAbsolutePath is not null) _ = NodeAt(diagnostic.ParentOriginAbsolutePath);
            if (diagnostic.CandidateOriginAbsolutePath is not null) Origin(diagnostic.CandidateOriginAbsolutePath);
            Reference(diagnostic.Reference, optional: true); Reference(diagnostic.RawMessage, optional: true);
        }
        return;

        void Origin(string origin)
        {
            if (D3D9EffectDependencySnapshot.CanonicalizeOrigin(origin) != origin) throw new InvalidDataException("A dependency origin is not canonical.");
            pathBytes += EffectTreeFiles.StrictUtf8.GetByteCount(origin);
            if (pathBytes > limits.MaxPathBytes) throw new InvalidDataException("Dependency origin paths exceed their byte budget.");
        }
        NodeData NodeAt(string origin)
        {
            // Validate references lexically, but do not charge the same already-retained origin repeatedly.
            if (D3D9EffectDependencySnapshot.CanonicalizeOrigin(origin) != origin || !byOrigin.TryGetValue(origin, out var node))
                throw new InvalidDataException("A dependency graph references an unknown or noncanonical origin.");
            return node;
        }
        void Use(NodeData node, D3D9EffectDependencyCategory category) => categories[node.ResourceId] |= category;
        void Reference(ReferenceData? reference, bool optional = false)
        {
            if (reference is null) { if (optional) return; throw new InvalidDataException("A dependency reference is missing."); }
            if (reference.Bytes is null || reference.CodePage <= 0 || reference.Bytes.LongLength > MaximumReferenceBytes - referenceBytes)
                throw new InvalidDataException("A dependency reference has invalid bytes, encoding or budget.");
            referenceBytes += reference.Bytes.LongLength;
        }
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
