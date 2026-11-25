using System.Security.Cryptography;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

public sealed partial class D3D9EffectResourceTree
{
    // Validation owns these indices only until the complete metadata has been admitted.
    private sealed class DependencyValidator(MetadataV3 metadata, D3D9EffectResourceTreeLimits limits, CancellationToken token)
    {
        private readonly Dictionary<string, NodeData> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NodeData> byOrigin = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, D3D9EffectDependencyCategory> categories = new(StringComparer.Ordinal);
        private readonly HashSet<string> profileIds = new(StringComparer.Ordinal);
        private readonly HashSet<(string Entry, string Target)> functionProfiles = [];
        private readonly Dictionary<(int Root, string TopId), List<(int Root, string TopId)>> defaultEdges = [];
        private readonly Dictionary<(int Root, string TopId), Dictionary<string, List<string>>> includeGraphs = [];
        private long pathBytes, referenceBytes;

        internal void Validate()
        {
            ValidateHeader();
            ValidateCompilerIdentity();
            IndexNodes();
            ValidateRoots();
            IndexProfiles();
            ValidateEdges();
            ValidateReachability();
            foreach (var node in metadata.Nodes)
                if (categories[node.ResourceId] != node.Category)
                    throw new InvalidDataException("A dependency node's categories disagree with its graph references.");
            ValidateDiagnostics();
        }

        private void ValidateHeader()
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
        }

        private void ValidateCompilerIdentity()
        {
            var identity = metadata.CompilerIdentity;
            if (string.IsNullOrWhiteSpace(identity.HostSemanticsVersion) || !Digest(identity.CompilerSha256, lowercase: false) ||
                identity.ProcessBitness != 64 || identity.AnsiCodePage <= 0 ||
                identity.ShaderCompilerSha256 is { } shaderDigest && !Digest(shaderDigest, lowercase: false))
                throw new InvalidDataException("Invalid effect dependency compiler identity.");
        }

        private void IndexNodes()
        {
            long totalBytes = 0;
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
        }

        private void ValidateRoots()
        {
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
        }

        private void IndexProfiles()
        {
            foreach (var profile in metadata.Profiles)
            {
                token.ThrowIfCancellationRequested();
                if (profile is null || string.IsNullOrEmpty(profile.Id) || profile.Macros is null || !profileIds.Add(profile.Id) ||
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
                else
                    functionProfiles.Add((profile.EntryPoint, profile.Target));
            }
            if (!profileIds.Contains("effect-mip-off") || !profileIds.Contains("effect-mip-on"))
                throw new InvalidDataException("Both fixed main compiler profiles are required.");
        }

        private void ValidateEdges()
        {
            var rootEdges = new bool[metadata.Roots.Length];
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
                    ValidateRootEdge(edge, top, parent, target, rootEdges);
                    continue;
                }

                ValidateTarget(edge, target);
                var key = (edge.RootIndex, top.ResourceId);
                if (!includeGraphs.TryGetValue(key, out var includes))
                    includeGraphs.Add(key, includes = new(StringComparer.Ordinal));
                if (edge.Kind is D3D9EffectDependencyEdgeKind.IncludeLocal or D3D9EffectDependencyEdgeKind.IncludeSystem)
                {
                    ValidateInclude(edge, target!);
                    AddConnection(includes, parent.ResourceId, target!.ResourceId);
                }
                else
                    ValidateSemanticEdge(edge, top, parent, target);
            }
            if (rootEdges.Any(present => !present)) throw new InvalidDataException("A dependency root edge is missing.");
        }

        private void ValidateRootEdge(EdgeData edge, NodeData top, NodeData parent, NodeData? target, bool[] rootEdges)
        {
            if (rootEdges[edge.RootIndex] || edge.ProfileId != "" || top != parent || target != top ||
                !StringComparer.OrdinalIgnoreCase.Equals(top.OriginAbsolutePath, metadata.Roots[edge.RootIndex].OriginAbsolutePath) ||
                edge.Symbol is not null || HasSemanticFields(edge) ||
                edge.Reference.CodePage != 65001 || !edge.Reference.Bytes.AsSpan().SequenceEqual(EffectTreeFiles.StrictUtf8.GetBytes(metadata.Roots[edge.RootIndex].OriginAbsolutePath)))
                throw new InvalidDataException("Invalid or duplicate dependency root edge.");
            rootEdges[edge.RootIndex] = true;
        }

        private void ValidateTarget(EdgeData edge, NodeData? target)
        {
            if (edge.ProfileId is null || !profileIds.Contains(edge.ProfileId))
                throw new InvalidDataException("A dependency edge references an unknown compiler profile.");
            if (edge.Kind == D3D9EffectDependencyEdgeKind.Symbol)
            {
                if (target is not null || edge.Symbol is null || !Enum.IsDefined(edge.Symbol.Value))
                    throw new InvalidDataException("A symbolic dependency edge has an invalid target.");
            }
            else if (target is null || edge.Symbol is not null)
                throw new InvalidDataException("A file dependency edge must identify one captured resource.");
        }

        private void ValidateInclude(EdgeData edge, NodeData target)
        {
            Use(target, D3D9EffectDependencyCategory.Include);
            if (HasSemanticFields(edge))
                throw new InvalidDataException("An include edge contains non-include semantic fields.");
        }

        private void ValidateSemanticEdge(EdgeData edge, NodeData top, NodeData parent, NodeData? target)
        {
            if (parent != top) throw new InvalidDataException("A semantic dependency reference must be based on its top FX.");
            if (edge.Kind == D3D9EffectDependencyEdgeKind.ResourceName) Use(target!, D3D9EffectDependencyCategory.Resource);
            if (edge.Kind == D3D9EffectDependencyEdgeKind.DefaultEffect)
            {
                Use(target!, D3D9EffectDependencyCategory.Effect);
                AddConnection(defaultEdges, (edge.RootIndex, top.ResourceId), (edge.RootIndex, target!.ResourceId));
            }
            if (edge.Kind == D3D9EffectDependencyEdgeKind.TextureFunction && (target != top || string.IsNullOrEmpty(edge.FunctionEntryPoint) ||
                string.IsNullOrEmpty(edge.FunctionTarget) || !functionProfiles.Contains((edge.FunctionEntryPoint, edge.FunctionTarget))))
                throw new InvalidDataException("A generated texture dependency lacks its exact empty-macro compiler invocation.");
        }

        private static bool HasSemanticFields(EdgeData edge) =>
            edge.Parameter is not null || edge.Annotation is not null || edge.RowOrdinal is not null || edge.RowKey is not null ||
            edge.RawRow is not null || edge.FunctionEntryPoint is not null || edge.FunctionTarget is not null;

        private static void AddConnection<T>(Dictionary<T, List<T>> edges, T from, T to) where T : notnull
        {
            if (!edges.TryGetValue(from, out var targets)) edges.Add(from, targets = []);
            targets.Add(to);
        }

        private void ValidateReachability()
        {
            var roots = metadata.Roots.Select((root, index) => (index, NodeAt(root.OriginAbsolutePath).ResourceId));
            var reachableFx = Reachable(roots, defaultEdges);
            foreach (var (top, includes) in includeGraphs)
            {
                token.ThrowIfCancellationRequested();
                if (!reachableFx.Contains(top))
                    throw new InvalidDataException("A dependency top FX is not reachable from its declared root.");
                if (includes.Count == 0) continue;
                var reachableIncludes = Reachable([top.TopId], includes);
                if (includes.Keys.Any(parent => !reachableIncludes.Contains(parent)))
                    throw new InvalidDataException("A dependency include parent is not connected to its top FX.");
            }
        }

        private HashSet<T> Reachable<T>(IEnumerable<T> roots, Dictionary<T, List<T>> edges) where T : notnull
        {
            var reached = new HashSet<T>(edges.Comparer);
            var queue = new Queue<T>(roots);
            while (queue.TryDequeue(out var item))
            {
                token.ThrowIfCancellationRequested();
                if (!reached.Add(item) || !edges.TryGetValue(item, out var next)) continue;
                foreach (var target in next) queue.Enqueue(target);
            }
            return reached;
        }

        private void ValidateDiagnostics()
        {
            foreach (var diagnostic in metadata.Diagnostics)
            {
                token.ThrowIfCancellationRequested();
                if (diagnostic is null || diagnostic.Status != D3D9EffectDependencyStatus.Complete || string.IsNullOrEmpty(diagnostic.Code) ||
                    diagnostic.Message is null || diagnostic.RootIndex is < 0 || diagnostic.RootIndex >= metadata.Roots.Length || diagnostic.HResult is < 0 ||
                    diagnostic.ProfileId is not null && !profileIds.Contains(diagnostic.ProfileId))
                    throw new InvalidDataException("Invalid informational dependency diagnostic.");
                if (diagnostic.TopFxOriginAbsolutePath is not null) _ = NodeAt(diagnostic.TopFxOriginAbsolutePath);
                if (diagnostic.ParentOriginAbsolutePath is not null) _ = NodeAt(diagnostic.ParentOriginAbsolutePath);
                if (diagnostic.CandidateOriginAbsolutePath is not null) Origin(diagnostic.CandidateOriginAbsolutePath);
                Reference(diagnostic.Reference, optional: true); Reference(diagnostic.RawMessage, optional: true);
            }
        }

        private void Origin(string origin)
        {
            if (D3D9EffectDependencySnapshot.CanonicalizeOrigin(origin) != origin) throw new InvalidDataException("A dependency origin is not canonical.");
            pathBytes += EffectTreeFiles.StrictUtf8.GetByteCount(origin);
            if (pathBytes > limits.MaxPathBytes) throw new InvalidDataException("Dependency origin paths exceed their byte budget.");
        }
        private NodeData NodeAt(string origin)
        {
            // Validate references lexically, but do not charge the same already-retained origin repeatedly.
            if (D3D9EffectDependencySnapshot.CanonicalizeOrigin(origin) != origin || !byOrigin.TryGetValue(origin, out var node))
                throw new InvalidDataException("A dependency graph references an unknown or noncanonical origin.");
            return node;
        }
        private void Use(NodeData node, D3D9EffectDependencyCategory category) => categories[node.ResourceId] |= category;
        private void Reference(ReferenceData? reference, bool optional = false)
        {
            if (reference is null) { if (optional) return; throw new InvalidDataException("A dependency reference is missing."); }
            if (reference.Bytes is null || reference.CodePage <= 0 || reference.Bytes.LongLength > MaximumReferenceBytes - referenceBytes)
                throw new InvalidDataException("A dependency reference has invalid bytes, encoding or budget.");
            referenceBytes += reference.Bytes.LongLength;
        }
    }
}
