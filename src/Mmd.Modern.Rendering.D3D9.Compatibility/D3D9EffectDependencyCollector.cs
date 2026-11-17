using System.Security.Cryptography;
using System.Text;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// Bounded discovery from caller-frozen whole-project FX roots. Compiler-driven includes and shared host
/// semantic events are the only discovery inputs. This produces a source graph, not a PMC package or a
/// portability claim: frozen-map recompilation and production binding integration are separate work.
/// </summary>
public sealed class D3D9EffectDependencyCollector
{
    private readonly ID3D9EffectDependencySource source;
    private readonly ID3D9EffectDependencyInspector inspector;
    private readonly D3D9EffectDependencyLimits limits;

    public D3D9EffectDependencyCollector(ID3D9EffectDependencySource source, ID3D9EffectDependencyInspector inspector,
        D3D9EffectDependencyLimits? limits = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        this.limits = limits ?? new();
        if (this.limits.MaximumFiles <= 0 || this.limits.MaximumEdges <= 0 || this.limits.MaximumFxInspections <= 0 ||
            this.limits.MaximumIncludeDepth <= 0 || this.limits.MaximumEffectDepth <= 0 || this.limits.MaximumFileBytes <= 0 ||
            this.limits.MaximumTotalBytes <= 0 || this.limits.MaximumReferenceBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "All discovery limits must be positive.");
    }

    public D3D9EffectDependencyResult Collect(IEnumerable<D3D9EffectDependencyRoot> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return new Collection(source, inspector, limits, cancellationToken).Run(roots);
    }

    private sealed class Collection(ID3D9EffectDependencySource source, ID3D9EffectDependencyInspector inspector,
        D3D9EffectDependencyLimits limits, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, Node> nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Discovery> discoveries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, D3D9EffectDependencyProfile> profiles = new(StringComparer.Ordinal);
        private readonly List<D3D9EffectDependencyRoot> roots = [];
        private readonly List<D3D9EffectDependencyEdge> edges = [];
        private readonly List<D3D9EffectDependencyDiagnostic> diagnostics = [];
        private readonly Dictionary<string, IReadOnlyList<D3D9EffectOffscreenDeclaration>> offscreen = new(StringComparer.OrdinalIgnoreCase);
        private D3D9EffectDependencyCompilerIdentity? identity;
        private long totalBytes, referenceBytes, retainedReferenceBytes, captureRequests;
        private Context context = new();

        internal D3D9EffectDependencyResult Run(IEnumerable<D3D9EffectDependencyRoot> inputRoots)
        {
            try
            {
                foreach (var root in inputRoots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(root);
                    if (roots.Count >= limits.MaximumEdges) Fail("root-budget", "The FX root count exceeds the edge budget.");
                    roots.Add(root);
                }
                var queue = new Queue<(int Root, Node Fx, int Depth)>();
                for (var i = 0; i < roots.Count; i++)
                {
                    var reference = new D3D9EffectDependencyReference(Encoding.UTF8.GetBytes(roots[i].OriginAbsolutePath), 65001);
                    context = new(i, roots[i].OriginAbsolutePath, roots[i].OriginAbsolutePath, Reference: reference);
                    var node = Capture(null, reference, D3D9EffectDependencyRequestKind.Root, D3D9EffectDependencyCategory.Effect);
                    AddEdge(new(i, node.Snapshot.OriginAbsolutePath, node.Snapshot.OriginAbsolutePath, "",
                        D3D9EffectDependencyEdgeKind.Root, reference, node.Id));
                    queue.Enqueue((i, node, 1));
                }
                // A cycle still contributes its real edge; only expansion is suppressed. Root provenance is
                // part of visited so shared FX roots are never silently attributed to the first owner only.
                var visited = new HashSet<(int Root, string Origin)>();
                while (queue.TryDequeue(out var work))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var top = work.Fx.Snapshot.OriginAbsolutePath;
                    if (!visited.Add((work.Root, top.ToUpperInvariant()))) continue;
                    context = new(work.Root, top, top);
                    if (work.Depth > limits.MaximumEffectDepth) Fail("effect-depth-budget", "The DefaultEffect graph exceeds the configured depth.");
                    if (!discoveries.TryGetValue(top, out var discovery))
                    {
                        if (discoveries.Count >= limits.MaximumFxInspections) Fail("inspection-budget", "The FX inspection count exceeds the configured budget.");
                        discovery = Inspect(work.Fx);
                        discoveries.Add(top, discovery);
                        offscreen[top] = discovery.Inspection.OffscreenTargets;
                    }
                    foreach (var include in discovery.Includes)
                    {
                        context = new(work.Root, top, include.Parent, include.Profile, include.Reference);
                        AddEdge(new(work.Root, top, include.Parent, include.Profile,
                            include.IsSystem ? D3D9EffectDependencyEdgeKind.IncludeSystem : D3D9EffectDependencyEdgeKind.IncludeLocal,
                            include.Reference, include.Target.Id));
                    }
                    foreach (var reference in discovery.Inspection.References)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        context = new(work.Root, top, top, reference.ProfileId, reference.Reference, reference.Parameter, reference.Annotation);
                        Node? target = null;
                        var kind = reference.Kind switch
                        {
                            D3D9EffectDependencyReferenceKind.Resource => D3D9EffectDependencyEdgeKind.ResourceName,
                            D3D9EffectDependencyReferenceKind.Effect => D3D9EffectDependencyEdgeKind.DefaultEffect,
                            D3D9EffectDependencyReferenceKind.TextureFunction => D3D9EffectDependencyEdgeKind.TextureFunction,
                            D3D9EffectDependencyReferenceKind.Symbol => D3D9EffectDependencyEdgeKind.Symbol,
                            _ => throw Unsupported("inspector-contract", "The inspector returned an unknown reference kind.")
                        };
                        if (reference.Kind is D3D9EffectDependencyReferenceKind.Resource or D3D9EffectDependencyReferenceKind.Effect)
                        {
                            target = Capture(top, reference.Reference,
                                reference.Kind == D3D9EffectDependencyReferenceKind.Resource ? D3D9EffectDependencyRequestKind.ResourceName : D3D9EffectDependencyRequestKind.DefaultEffect,
                                reference.Kind == D3D9EffectDependencyReferenceKind.Resource ? D3D9EffectDependencyCategory.Resource : D3D9EffectDependencyCategory.Effect);
                        }
                        else if (reference.Kind == D3D9EffectDependencyReferenceKind.TextureFunction) target = work.Fx;
                        AddEdge(new(work.Root, top, top, reference.ProfileId, kind, reference.Reference, target?.Id,
                            reference.Symbol, reference.Parameter, reference.Annotation, reference.RowOrdinal, reference.RowKey,
                            reference.RawRow, reference.FunctionEntryPoint, reference.FunctionTarget));
                        if (reference.Kind == D3D9EffectDependencyReferenceKind.Effect) queue.Enqueue((work.Root, target!, work.Depth + 1));
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new(D3D9EffectDependencyStatus.Complete,
                    new(roots, nodes.Values.Select(n => new D3D9EffectDependencyNode(n.Id, n.Snapshot, n.Category)), edges,
                        identity, profiles.Values, offscreen), diagnostics);
            }
            catch (OperationCanceledException)
            {
                diagnostics.Add(Diagnostic(D3D9EffectDependencyStatus.Cancelled, "cancelled", "FX dependency discovery was cancelled."));
                return new(D3D9EffectDependencyStatus.Cancelled, null, diagnostics);
            }
            catch (InspectionFailureException error)
            {
                return new(error.Status, null, diagnostics);
            }
            catch (D3D9EffectDependencyException error)
            {
                diagnostics.Add(Diagnostic(error.Status, error.Code, error.Message, error.CandidateOriginAbsolutePath));
                return new(error.Status, null, diagnostics);
            }
            catch (IOException error)
            {
                diagnostics.Add(Diagnostic(D3D9EffectDependencyStatus.Incomplete, "source-read", error.Message));
                return new(D3D9EffectDependencyStatus.Incomplete, null, diagnostics);
            }
            catch (UnauthorizedAccessException error)
            {
                diagnostics.Add(Diagnostic(D3D9EffectDependencyStatus.Incomplete, "source-access", error.Message));
                return new(D3D9EffectDependencyStatus.Incomplete, null, diagnostics);
            }
            catch (NotSupportedException error)
            {
                diagnostics.Add(Diagnostic(D3D9EffectDependencyStatus.Unsupported, "unsupported-source-or-compiler", error.Message));
                return new(D3D9EffectDependencyStatus.Unsupported, null, diagnostics);
            }
            catch (ArgumentException error)
            {
                diagnostics.Add(Diagnostic(D3D9EffectDependencyStatus.Unsupported, "source-or-inspector-contract", error.Message));
                return new(D3D9EffectDependencyStatus.Unsupported, null, diagnostics);
            }
        }

        private Discovery Inspect(Node top)
        {
            var includes = new List<Include>();
            var knownParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { top.Snapshot.OriginAbsolutePath };
            Exception? includeFailure = null;
            Context? includeFailureContext = null;
            var inspection = inspector.Inspect(top.Snapshot, request =>
            {
                // Preserve a callback failure even if a compiler adapter converts the exception into its
                // own failed HRESULT. A failed include may never be hidden behind a Complete report.
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parent = request.ParentOriginAbsolutePath == null ? top.Snapshot.OriginAbsolutePath :
                        D3D9EffectDependencySnapshot.CanonicalizeOrigin(request.ParentOriginAbsolutePath);
                    context = context with { Parent = parent, Profile = request.ProfileId, Reference = request.Reference, Parameter = null, Annotation = null };
                    if (request.Depth < 1 || request.Depth > limits.MaximumIncludeDepth) Fail("include-depth-budget", "The compiler include chain exceeds the configured depth.");
                    if (string.IsNullOrEmpty(request.ProfileId) || !knownParents.Contains(parent))
                        throw Unsupported("inspector-contract", "An include must identify its profile and an already captured parent within the same FX.");
                    if (includes.Count >= limits.MaximumEdges) Fail("include-request-budget", "The compiler include request count exceeds the configured budget.");
                    var target = Capture(request.IsSystem ? top.Snapshot.OriginAbsolutePath : parent, request.Reference,
                        request.IsSystem ? D3D9EffectDependencyRequestKind.IncludeSystem : D3D9EffectDependencyRequestKind.IncludeLocal,
                        D3D9EffectDependencyCategory.Include);
                    RetainReference(request.Reference);
                    knownParents.Add(target.Snapshot.OriginAbsolutePath);
                    includes.Add(new(parent, request.ProfileId, request.IsSystem, request.Reference, target));
                    return target.Snapshot;
                }
                catch (Exception error)
                {
                    includeFailure ??= error; includeFailureContext ??= context;
                    throw;
                }
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (includeFailure != null)
            {
                context = includeFailureContext!;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(includeFailure).Throw();
            }
            context = context with { Parent = top.Snapshot.OriginAbsolutePath, Profile = null, Reference = null, Parameter = null, Annotation = null };
            if (inspection.Status != D3D9EffectDependencyStatus.Complete)
            {
                var failure = inspection.Diagnostics.FirstOrDefault();
                if (failure != null) context = context with { Profile = failure.ProfileId, Parameter = failure.Parameter, Annotation = failure.Annotation };
                if (inspection.Status == D3D9EffectDependencyStatus.Cancelled) throw new OperationCanceledException();
                var status = inspection.Status == D3D9EffectDependencyStatus.Unsupported ? D3D9EffectDependencyStatus.Unsupported : D3D9EffectDependencyStatus.Incomplete;
                if (failure != null)
                {
                    foreach (var diagnostic in inspection.Diagnostics)
                    {
                        RetainReference(diagnostic.RawMessage);
                        diagnostics.Add(Diagnostic(status, diagnostic.Code, diagnostic.Message) with
                        { ProfileId = diagnostic.ProfileId, Parameter = diagnostic.Parameter, Annotation = diagnostic.Annotation,
                            RawMessage = diagnostic.RawMessage, HResult = diagnostic.HResult });
                    }
                    throw new InspectionFailureException(status);
                }
                throw new D3D9EffectDependencyException(
                    inspection.Status == D3D9EffectDependencyStatus.Unsupported ? D3D9EffectDependencyStatus.Unsupported : D3D9EffectDependencyStatus.Incomplete,
                    failure?.Code ?? "compile", failure?.Message ?? "The official compiler did not complete every required profile.");
            }
            ValidateInspection(inspection, includes);
            // Complete reports may still contain informational diagnostics; they remain attached to the
            // completed graph and never stand in for missing profiles or failed required requests.
            foreach (var diagnostic in inspection.Diagnostics)
            {
                RetainReference(diagnostic.RawMessage);
                diagnostics.Add(Diagnostic(D3D9EffectDependencyStatus.Complete, diagnostic.Code, diagnostic.Message) with
                { ProfileId = diagnostic.ProfileId, Parameter = diagnostic.Parameter, Annotation = diagnostic.Annotation,
                    RawMessage = diagnostic.RawMessage, HResult = diagnostic.HResult });
            }
            return new(inspection, includes);
        }

        private void ValidateInspection(D3D9EffectDependencyInspection inspection, IReadOnlyList<Include> includes)
        {
            var currentIdentity = inspection.Identity;
            if (string.IsNullOrWhiteSpace(currentIdentity.HostSemanticsVersion) || currentIdentity.ProcessBitness != 64 || currentIdentity.AnsiCodePage <= 0 ||
                string.IsNullOrEmpty(currentIdentity.CompilerSha256) || currentIdentity.CompilerSha256.Length != 64 || currentIdentity.CompilerSha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw Unsupported("compiler-identity", "The inspection requires the fixed host version, compiler digest, x64 bitness and actual ACP.");
            if (identity != null && identity != currentIdentity) throw Unsupported("compiler-identity-changed", "Compiler/host identity changed during the source graph collection.");
            identity ??= currentIdentity;
            if (inspection.Profiles.Count > limits.MaximumEdges || inspection.References.Count > limits.MaximumEdges || inspection.Diagnostics.Count > limits.MaximumEdges)
                Fail("inspection-record-budget", "The compiler report exceeds the configured record budget.");
            var local = new Dictionary<string, D3D9EffectDependencyProfile>(StringComparer.Ordinal);
            if (inspection.Diagnostics.Any(d => d.HResult is < 0))
                throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Incomplete, "compile", "A compiler failure HRESULT cannot produce a complete graph.");
            foreach (var profile in inspection.Profiles)
            {
                if (profile == null || !local.TryAdd(profile.Id, profile)) throw Unsupported("compiler-profile", "Compiler profile IDs must be present and unique per FX.");
                if (profile.Id is "effect-mip-off" or "effect-mip-on")
                {
                    var mip = profile.Id == "effect-mip-on";
                    if (profile.Flags is not (0x1000 or 0x10C0) || profile.EntryPoint != null || profile.Target != null ||
                        profile.Macros.Count != (mip ? 2 : 1) ||
                        !profile.Macros.Any(m => m.Name == "_INDEX" && m.Value == "PSIZE15") ||
                        mip && !profile.Macros.Any(m => m.Name == "MME_MIPMAP" && m.Value == ""))
                        throw Unsupported("compiler-profile", "A main FX profile does not match the fixed host flags and macro definitions.");
                }
                else if (profile.Flags != 0 || profile.Macros.Count != 0 || string.IsNullOrEmpty(profile.EntryPoint) || string.IsNullOrEmpty(profile.Target))
                    throw Unsupported("compiler-profile", "Generated texture profiles require empty defines, zero flags and the actual Function/Target.");
                if (profiles.TryGetValue(profile.Id, out var prior) && !SameProfile(profile, prior))
                    throw Unsupported("compiler-profile-changed", "A compiler profile ID was reused with different invocation settings.");
                profiles[profile.Id] = profile;
            }
            if (!local.ContainsKey("effect-mip-off") || !local.ContainsKey("effect-mip-on"))
                throw Unsupported("compiler-profile-missing", "Both fixed mip-off and mip-on FX profiles must compile successfully.");
            foreach (var include in includes)
                if (!local.ContainsKey(include.Profile)) throw Unsupported("compiler-profile-missing", "An include request references an unreported compiler profile.");
            foreach (var reference in inspection.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RetainReference(reference.Reference);
                RetainReference(reference.RawRow);
                context = context with { Profile = reference.ProfileId, Reference = reference.Reference, Parameter = reference.Parameter, Annotation = reference.Annotation };
                if (!local.ContainsKey(reference.ProfileId)) throw Unsupported("compiler-profile-missing", "A semantic event references an unreported compiler profile.");
                if (reference.Kind == D3D9EffectDependencyReferenceKind.Symbol && reference.Symbol == null ||
                    reference.Kind != D3D9EffectDependencyReferenceKind.Symbol && reference.Symbol != null)
                    throw Unsupported("inspector-contract", "Only a classified symbolic DefaultEffect row may carry a symbolic target.");
                if (reference.Symbol is { } symbol && !Enum.IsDefined(symbol))
                    throw Unsupported("inspector-contract", "The inspector returned an unknown symbolic target.");
                if (reference.Kind == D3D9EffectDependencyReferenceKind.TextureFunction &&
                    !local.Values.Any(p => p.EntryPoint == reference.FunctionEntryPoint && p.Target == reference.FunctionTarget && p.EntryPoint != null))
                    throw Unsupported("function-profile-missing", "The generated texture Function/Target was not compiled with empty defines.");
            }
        }

        private Node Capture(string? baseOrigin, D3D9EffectDependencyReference reference,
            D3D9EffectDependencyRequestKind kind, D3D9EffectDependencyCategory category)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++captureRequests > limits.MaximumEdges) Fail("source-request-budget", "Source requests exceed the configured budget.");
            if (reference.ByteLength > limits.MaximumReferenceBytes) Fail("reference-byte-budget", "A resource request exceeds the copied-reference budget.");
            var snapshot = source.Capture(new(baseOrigin, reference, kind), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot == null) throw Unsupported("source-contract", "Source capture must return a stable immutable snapshot.");
            if (snapshot.ByteLength > limits.MaximumFileBytes) Fail("file-byte-budget", "An original resource exceeds the per-file byte budget.");
            if (nodes.TryGetValue(snapshot.OriginAbsolutePath, out var existing))
            {
                if (existing.Snapshot.ByteLength != snapshot.ByteLength || existing.Snapshot.Sha256 != snapshot.Sha256)
                    throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Incomplete, "file-changed",
                        "The same original identity returned different bytes during discovery.", snapshot.OriginAbsolutePath);
                existing.Category |= category;
                return existing;
            }
            if (nodes.Count >= limits.MaximumFiles) Fail("file-count-budget", "The resource graph exceeds the file-count budget.");
            if (snapshot.ByteLength > limits.MaximumTotalBytes - totalBytes) Fail("total-byte-budget", "The resource graph exceeds the total frozen-byte budget.");
            totalBytes += snapshot.ByteLength;
            var id = "origin-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.OriginAbsolutePath.ToUpperInvariant()))).ToLowerInvariant();
            var node = new Node(id, snapshot, category);
            nodes.Add(snapshot.OriginAbsolutePath, node);
            return node;
        }

        private void AddEdge(D3D9EffectDependencyEdge edge)
        {
            if (edges.Count >= limits.MaximumEdges) Fail("edge-budget", "The dependency graph exceeds the edge budget.");
            var bytes = (long)edge.Reference.ByteLength + (edge.RawRow?.ByteLength ?? 0);
            if (bytes > limits.MaximumReferenceBytes - referenceBytes) Fail("reference-byte-budget", "The dependency graph exceeds the copied-reference budget.");
            referenceBytes += bytes;
            edges.Add(edge);
        }

        private void RetainReference(D3D9EffectDependencyReference? reference)
        {
            if (reference == null) return;
            if (reference.ByteLength > limits.MaximumReferenceBytes - retainedReferenceBytes)
                Fail("reference-byte-budget", "Compiler references and diagnostics exceed the retained-byte budget.");
            retainedReferenceBytes += reference.ByteLength;
        }

        private D3D9EffectDependencyDiagnostic Diagnostic(D3D9EffectDependencyStatus status, string code, string message, string? candidate = null) =>
            new(status, code, message, context.Root, context.Top, context.Parent, context.Profile, context.Reference, candidate, context.Parameter, context.Annotation);

        private static bool SameProfile(D3D9EffectDependencyProfile a, D3D9EffectDependencyProfile b) =>
            a.Flags == b.Flags && a.EntryPoint == b.EntryPoint && a.Target == b.Target && a.Macros.SequenceEqual(b.Macros);
        private static D3D9EffectDependencyException Unsupported(string code, string message) => new(D3D9EffectDependencyStatus.Unsupported, code, message);
        private static void Fail(string code, string message) => throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Incomplete, code, message);

        private sealed class Node(string id, D3D9EffectDependencySnapshot snapshot, D3D9EffectDependencyCategory category)
        {
            internal string Id { get; } = id;
            internal D3D9EffectDependencySnapshot Snapshot { get; } = snapshot;
            internal D3D9EffectDependencyCategory Category { get; set; } = category;
        }
        private sealed record Include(string Parent, string Profile, bool IsSystem, D3D9EffectDependencyReference Reference, Node Target);
        private sealed record Discovery(D3D9EffectDependencyInspection Inspection, IReadOnlyList<Include> Includes);
        private sealed class InspectionFailureException(D3D9EffectDependencyStatus status) : Exception
        { internal D3D9EffectDependencyStatus Status { get; } = status; }
        private sealed record Context(int? Root = null, string? Top = null, string? Parent = null,
            string? Profile = null, D3D9EffectDependencyReference? Reference = null, string? Parameter = null, string? Annotation = null);
    }
}
