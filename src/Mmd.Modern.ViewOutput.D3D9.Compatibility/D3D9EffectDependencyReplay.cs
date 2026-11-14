using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

public enum D3D9EffectDependencyReplayStatus { Accepted, NonPortable, Cancelled }

/// <summary>Acceptance of this bounded CPU replay, not of GPU execution, media decoding or arbitrary FX.</summary>
public sealed class D3D9EffectDependencyReplayResult
{
    internal D3D9EffectDependencyReplayResult(D3D9EffectDependencyReplayStatus status, int inspectedFxCount,
        IEnumerable<D3D9EffectDependencyDiagnostic> diagnostics)
    {
        Status = status; InspectedFxCount = inspectedFxCount;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        Warnings = Array.AsReadOnly(Diagnostics.Where(item => item.Status == D3D9EffectDependencyStatus.Complete).ToArray());
    }
    public D3D9EffectDependencyReplayStatus Status { get; }
    public bool Accepted => Status == D3D9EffectDependencyReplayStatus.Accepted;
    public int InspectedFxCount { get; }
    public IReadOnlyList<D3D9EffectDependencyDiagnostic> Diagnostics { get; }
    public IReadOnlyList<D3D9EffectDependencyDiagnostic> Warnings { get; }
}

/// <summary>
/// Recompiles the frozen graph with the same official inspector/shared host semantics, using private
/// materialized names for compiler source identity. All bytes come from the immutable tree; neither
/// original nor materialized source files are opened. The materialization/asset lease remains the caller's
/// responsibility. Acceptance compares the requests observed now; it cannot prove future nondeterministic
/// macro expansion, GPU behavior, external scripting or decoder support.
/// </summary>
public static class D3D9EffectDependencyReplay
{
    public static D3D9EffectDependencyReplayResult Validate(D3D9EffectResourceTree tree,
        D3D9EffectResourceMap resourceMap, ID3D9EffectDependencyInspector inspector,
        D3D9EffectDependencyLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree); ArgumentNullException.ThrowIfNull(resourceMap);
        ArgumentNullException.ThrowIfNull(inspector);
        var graph = tree.DependencyGraph;
        if (graph is null) return Failure("replay-graph-missing", "CPU replay requires a saved automatic compiler dependency graph.");
        ReplayInspector? replayInspector = null;
        FrozenSource? source = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            source = new(tree, graph, resourceMap);
            int maximumRecords = limits?.MaximumEdges ?? 262144;
            replayInspector = new(graph, source, inspector, maximumRecords);
            var roots = graph.Roots.Select(root => new D3D9EffectDependencyRoot(source.LocalForOrigin(root.OriginAbsolutePath), root.Provenance));
            var result = new D3D9EffectDependencyCollector(source, replayInspector, limits).Collect(roots, cancellationToken);
            var diagnostics = new List<D3D9EffectDependencyDiagnostic>();
            foreach (var item in result.Diagnostics.SelectMany(item => RestoreDiagnostic(item, source)))
            {
                if (diagnostics.Count == maximumRecords)
                {
                    diagnostics.Add(new(D3D9EffectDependencyStatus.Unsupported, "replay-diagnostic-budget",
                        "Attributing compiler diagnostics to saved owners exceeds the configured record budget."));
                    return new(D3D9EffectDependencyReplayStatus.NonPortable, replayInspector.Count, diagnostics);
                }
                diagnostics.Add(item);
            }
            if (result.Status != D3D9EffectDependencyStatus.Complete || result.Closure is null)
                return new(result.Status == D3D9EffectDependencyStatus.Cancelled ? D3D9EffectDependencyReplayStatus.Cancelled :
                    D3D9EffectDependencyReplayStatus.NonPortable, replayInspector.Count, diagnostics);

            CompareGraph(graph, result.Closure, source, diagnostics, cancellationToken);
            return new(diagnostics.Any(item => item.Status != D3D9EffectDependencyStatus.Complete) ?
                D3D9EffectDependencyReplayStatus.NonPortable : D3D9EffectDependencyReplayStatus.Accepted, replayInspector.Count, diagnostics);
        }
        catch (OperationCanceledException)
        {
            return new(D3D9EffectDependencyReplayStatus.Cancelled, replayInspector?.Count ?? 0,
                [new(D3D9EffectDependencyStatus.Cancelled, "cancelled", "Frozen effect dependency replay was cancelled.")]);
        }
        catch (D3D9EffectDependencyException error)
        {
            var node = graph.Nodes.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(
                item.OriginAbsolutePath, error.CandidateOriginAbsolutePath));
            D3D9EffectDependencyEdge[] edges = node is null ? [] : graph.Edges.Where(edge => edge.TargetResourceId == node.ResourceId ||
                StringComparer.OrdinalIgnoreCase.Equals(edge.TopFxOriginAbsolutePath, node.OriginAbsolutePath)).ToArray();
            return new(D3D9EffectDependencyReplayStatus.NonPortable, replayInspector?.Count ?? 0,
                edges.Length == 0 ? [new(error.Status, error.Code, error.Message, CandidateOriginAbsolutePath: error.CandidateOriginAbsolutePath)] :
                edges.Select(edge => new D3D9EffectDependencyDiagnostic(error.Status, error.Code, error.Message, edge.RootIndex,
                    edge.TopFxOriginAbsolutePath, edge.ParentOriginAbsolutePath, edge.ProfileId, edge.Reference,
                    error.CandidateOriginAbsolutePath, edge.Parameter, edge.Annotation)));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return Failure("replay-contract", "Cannot replay the frozen compiler graph: " + error.Message, replayInspector?.Count ?? 0);
        }
    }

    private static D3D9EffectDependencyReplayResult Failure(string code, string message, int count = 0) =>
        new(D3D9EffectDependencyReplayStatus.NonPortable, count, [new(D3D9EffectDependencyStatus.Unsupported, code, message)]);

    private static IEnumerable<D3D9EffectDependencyDiagnostic> RestoreDiagnostic(D3D9EffectDependencyDiagnostic item,
        FrozenSource source)
    {
        var restored = item with
        {
            TopFxOriginAbsolutePath = source.OriginForLocalOrUnchanged(item.TopFxOriginAbsolutePath),
            ParentOriginAbsolutePath = source.OriginForLocalOrUnchanged(item.ParentOriginAbsolutePath),
            CandidateOriginAbsolutePath = source.OriginForLocalOrUnchanged(item.CandidateOriginAbsolutePath)
        };
        // The collector compiles shared FX only once. A failed compile still belongs to every owner whose
        // saved DefaultEffect graph reaches it, including owners not yet visited when compilation failed.
        if (restored.Status != D3D9EffectDependencyStatus.Complete && restored.TopFxOriginAbsolutePath is { } top)
        {
            var owners = source.RootIndicesForOrigin(top);
            if (owners.Length > 0)
            {
                foreach (int index in owners) yield return restored with { RootIndex = index };
                yield break;
            }
        }
        yield return restored;
    }

    private static void CompareGraph(D3D9EffectResourceTreeDependencyGraph expected, D3D9EffectDependencyClosure actual,
        FrozenSource source, List<D3D9EffectDependencyDiagnostic> diagnostics, CancellationToken token)
    {
        var profiles = expected.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        if (actual.Profiles.Count != profiles.Count || actual.Profiles.Any(profile =>
            !profiles.TryGetValue(profile.Id, out var saved) || !SameProfile(profile, saved)))
            diagnostics.Add(new(D3D9EffectDependencyStatus.Unsupported, "replay-profile-drift",
                "The materialized compiler did not reproduce all saved compiler profiles."));
        var byActualId = new Dictionary<string, D3D9EffectResourceTreeDependencyNode>(StringComparer.Ordinal);
        foreach (var node in actual.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var saved = source.NodeForLocal(node.Snapshot.OriginAbsolutePath);
            byActualId.Add(node.ResourceId, saved);
            if (node.Category != saved.Category)
                diagnostics.Add(new(D3D9EffectDependencyStatus.Unsupported, "replay-request-drift",
                    "The materialized compiler used a saved resource with different dependency categories.",
                    CandidateOriginAbsolutePath: saved.OriginAbsolutePath));
        }
        if (actual.Nodes.Count != expected.Nodes.Count)
            diagnostics.Add(new(D3D9EffectDependencyStatus.Unsupported, "replay-request-drift",
                "The materialized compiler did not reproduce the complete saved resource set."));

        var remaining = new Dictionary<D3D9EffectDependencyEdge, int>(EdgeComparer.Instance);
        foreach (var edge in expected.Edges) remaining[edge] = remaining.GetValueOrDefault(edge) + 1;
        foreach (var edge in actual.Edges)
        {
            token.ThrowIfCancellationRequested();
            var restored = edge with
            {
                TopFxOriginAbsolutePath = source.NodeForLocal(edge.TopFxOriginAbsolutePath).OriginAbsolutePath,
                ParentOriginAbsolutePath = source.NodeForLocal(edge.ParentOriginAbsolutePath).OriginAbsolutePath,
                TargetResourceId = edge.TargetResourceId is null ? null : byActualId[edge.TargetResourceId].ResourceId,
                // Root reference bytes are generated by the collector from its compiler source identity;
                // all actual FX/include/annotation bytes must compare unchanged.
                Reference = edge.Kind == D3D9EffectDependencyEdgeKind.Root ? new(Encoding.UTF8.GetBytes(
                    expected.Roots[edge.RootIndex].OriginAbsolutePath), 65001) : edge.Reference
            };
            if (remaining.TryGetValue(restored, out int count) && count > 0) remaining[restored] = count - 1;
            else diagnostics.Add(Drift(restored, "The materialized compiler emitted a request absent from the saved graph."));
        }
        foreach (var pair in remaining)
        {
            token.ThrowIfCancellationRequested();
            if (pair.Value > 0) diagnostics.Add(Drift(pair.Key, "The materialized compiler no longer emitted a saved request."));
        }
    }

    private static D3D9EffectDependencyDiagnostic Drift(D3D9EffectDependencyEdge edge, string message) =>
        new(D3D9EffectDependencyStatus.Unsupported, "replay-request-drift", message, edge.RootIndex,
            edge.TopFxOriginAbsolutePath, edge.ParentOriginAbsolutePath, edge.ProfileId, edge.Reference,
            Parameter: edge.Parameter, Annotation: edge.Annotation);

    private static bool SameProfile(D3D9EffectDependencyProfile a, D3D9EffectDependencyProfile b) =>
        a.Flags == b.Flags && a.EntryPoint == b.EntryPoint && a.Target == b.Target && a.Macros.SequenceEqual(b.Macros);

    private sealed class FrozenSource : ID3D9EffectDependencySource
    {
        private readonly D3D9EffectResourceTree tree;
        private readonly D3D9EffectResourceMap map;
        private readonly Dictionary<string, string> locals = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, D3D9EffectResourceTreeDependencyNode> nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, D3D9EffectDependencySnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int[]> rootIndices;

        internal FrozenSource(D3D9EffectResourceTree tree, D3D9EffectResourceTreeDependencyGraph graph, D3D9EffectResourceMap map)
        {
            this.tree = tree; this.map = map;
            var byId = graph.Nodes.ToDictionary(node => node.ResourceId, StringComparer.Ordinal);
            var owners = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in graph.Edges)
            {
                AddOwner(edge.TopFxOriginAbsolutePath, edge.RootIndex);
                if (edge.Kind is D3D9EffectDependencyEdgeKind.DefaultEffect or D3D9EffectDependencyEdgeKind.Root)
                    AddOwner(byId[edge.TargetResourceId!].OriginAbsolutePath, edge.RootIndex);
            }
            rootIndices = owners.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray(), StringComparer.OrdinalIgnoreCase);
            var bindings = map.Bindings.ToDictionary(binding => binding.OriginAbsolutePath, StringComparer.OrdinalIgnoreCase);
            foreach (var node in graph.Nodes)
            {
                if (!bindings.TryGetValue(node.OriginAbsolutePath, out var binding))
                    throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported, "replay-map-miss",
                        "A saved dependency has no private materialized resource mapping.", node.OriginAbsolutePath);
                locals.Add(node.OriginAbsolutePath, binding.MaterializedAbsolutePath);
                nodes.Add(binding.MaterializedAbsolutePath, node);
            }
            void AddOwner(string origin, int index)
            {
                if (!owners.TryGetValue(origin, out var indices)) owners.Add(origin, indices = []);
                indices.Add(index);
            }
        }
        internal string LocalForOrigin(string origin) => locals[origin];
        internal D3D9EffectResourceTreeDependencyNode NodeForLocal(string local) => nodes[local];
        internal string? OriginForLocalOrUnchanged(string? path) => path is not null && nodes.TryGetValue(path, out var node) ? node.OriginAbsolutePath : path;
        internal int[] RootIndicesForOrigin(string origin) => rootIndices.GetValueOrDefault(origin) ?? [];

        public D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Encoding encoding = request.Reference.CodePage == 65001 ? new UTF8Encoding(false, true) :
                    CodePagesEncodingProvider.Instance.GetEncoding(request.Reference.CodePage, EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback) ?? throw new NotSupportedException("The request code page is unavailable.");
                byte[] bytes = request.Reference.CopyBytes();
                string reference = encoding.GetString(bytes);
                if (!encoding.GetBytes(reference).AsSpan().SequenceEqual(bytes))
                    throw new DecoderFallbackException("The request cannot round-trip through its declared code page.");
                string local = request.Kind == D3D9EffectDependencyRequestKind.Root ?
                    D3D9EffectDependencySnapshot.CanonicalizeOrigin(reference) : map.Resolve(request.BaseOriginAbsolutePath!, reference);
                if (!nodes.TryGetValue(local, out var node))
                    throw new ArgumentException("The request does not identify a file in this immutable saved graph.");
                if (!snapshots.TryGetValue(local, out var snapshot))
                {
                    snapshot = new(local, tree.CopyFileBytes(node.RelativePath));
                    snapshots.Add(local, snapshot);
                }
                return snapshot;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported, "replay-map-miss",
                    "Frozen-map replay cannot satisfy the compiler request: " + error.Message, innerException: error);
            }
        }
    }

    private sealed class ReplayInspector(D3D9EffectResourceTreeDependencyGraph graph, FrozenSource source,
        ID3D9EffectDependencyInspector inspector, int maximumRecords) : ID3D9EffectDependencyInspector
    {
        internal int Count { get; private set; }
        private readonly Dictionary<string, Dictionary<string, D3D9EffectDependencyProfile>> expectedProfiles = BuildExpectedProfiles(graph);
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken cancellationToken)
        {
            Count++;
            var inspection = inspector.Inspect(topFx, captureInclude, cancellationToken);
            if (inspection.Profiles.Count > maximumRecords || inspection.References.Count > maximumRecords || inspection.Diagnostics.Count > maximumRecords)
                throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported, "replay-record-budget",
                    "The compiler replay report exceeds the configured record budget.");
            if (inspection.Status != D3D9EffectDependencyStatus.Complete) return inspection;
            if (inspection.Identity != graph.CompilerIdentity)
                throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported, "replay-compiler-drift",
                    "Compiler runtime, ACP or shared host semantic identity differs from the saved graph.");
            string origin = source.NodeForLocal(topFx.OriginAbsolutePath).OriginAbsolutePath;
            var expected = expectedProfiles[origin];
            if (inspection.Profiles.Count != expected.Count || inspection.Profiles.Any(profile =>
                !expected.TryGetValue(profile.Id, out var saved) || !SameProfile(profile, saved)))
                throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported, "replay-profile-drift",
                    "The materialized compiler invocations differ from this FX's saved profiles.");
            return inspection;
        }

        private static Dictionary<string, Dictionary<string, D3D9EffectDependencyProfile>> BuildExpectedProfiles(D3D9EffectResourceTreeDependencyGraph graph)
        {
            var main = graph.Profiles.Where(profile => profile.Id is "effect-mip-off" or "effect-mip-on").ToDictionary(profile => profile.Id, StringComparer.Ordinal);
            var functions = graph.Profiles.Where(profile => profile.EntryPoint is not null)
                .GroupBy(profile => (profile.EntryPoint, profile.Target)).ToDictionary(group => group.Key, group => group.ToArray());
            var result = graph.Nodes.Where(node => (node.Category & D3D9EffectDependencyCategory.Effect) != 0)
                .ToDictionary(node => node.OriginAbsolutePath, _ => new Dictionary<string, D3D9EffectDependencyProfile>(main, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
            foreach (var edge in graph.Edges)
                if (edge.Kind == D3D9EffectDependencyEdgeKind.TextureFunction)
                    foreach (var profile in functions[(edge.FunctionEntryPoint, edge.FunctionTarget)])
                        result[edge.TopFxOriginAbsolutePath].TryAdd(profile.Id, profile);
            return result;
        }
    }

    private sealed class EdgeComparer : IEqualityComparer<D3D9EffectDependencyEdge>
    {
        internal static readonly EdgeComparer Instance = new();
        public bool Equals(D3D9EffectDependencyEdge? a, D3D9EffectDependencyEdge? b) =>
            ReferenceEquals(a, b) || a is not null && b is not null && (a with { Reference = b.Reference, RawRow = b.RawRow }) == b &&
                SameReference(a.Reference, b.Reference) && SameReference(a.RawRow, b.RawRow);
        private static bool SameReference(D3D9EffectDependencyReference? a, D3D9EffectDependencyReference? b) =>
            a is null ? b is null : b is not null && a.CodePage == b.CodePage && a.CopyBytes().AsSpan().SequenceEqual(b.CopyBytes());
        public int GetHashCode(D3D9EffectDependencyEdge edge)
        {
            var hash = new HashCode();
            hash.Add(edge.RootIndex); hash.Add(edge.TopFxOriginAbsolutePath); hash.Add(edge.ParentOriginAbsolutePath);
            hash.Add(edge.ProfileId); hash.Add(edge.Kind); hash.Add(edge.TargetResourceId); hash.Add(edge.Symbol);
            hash.Add(edge.Parameter); hash.Add(edge.Annotation); hash.Add(edge.RowOrdinal); hash.Add(edge.RowKey);
            hash.Add(edge.FunctionEntryPoint); hash.Add(edge.FunctionTarget);
            hash.Add(edge.Reference.CodePage); hash.AddBytes(edge.Reference.CopyBytes());
            if (edge.RawRow is { } row) { hash.Add(row.CodePage); hash.AddBytes(row.CopyBytes()); }
            return hash.ToHashCode();
        }
    }
}
