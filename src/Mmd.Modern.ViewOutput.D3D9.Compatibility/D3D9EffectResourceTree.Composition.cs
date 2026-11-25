using System.Text.Json;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

public sealed partial class D3D9EffectResourceTree
{
    /// <summary>Projects completed root graphs onto a new ordered root table. This metadata/byte-only
    /// operation never opens originals or reruns discovery. Different bytes for a retained origin,
    /// compiler identities or same-named profiles are rejected before a new tree is exposed.</summary>
    internal static D3D9EffectResourceTree ComposeDependencies(
        IReadOnlyList<D3D9EffectDependencyRoot> roots,
        IReadOnlyList<(D3D9EffectResourceTree Tree, int RootIndex)> sources,
        D3D9EffectResourceTreeLimits? limits, CancellationToken token)
    {
        limits ??= new(); limits.Validate(); token.ThrowIfCancellationRequested();
        if (roots.Count == 0 || roots.Count != sources.Count || roots.Count > MaximumGraphRecords)
            throw new InvalidDataException("A complete source graph is required for every composed effect root.");
        var edges = new List<EdgeData>();
        var diagnostics = new List<DiagnosticData>();
        var nodes = new Dictionary<string, (NodeData Node, D3D9EffectResourceContent Content)>(StringComparer.Ordinal);
        var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new Dictionary<string, ProfileData>(StringComparer.Ordinal);
        D3D9EffectDependencyCompilerIdentity? identity = null;
        for (int index = 0; index < sources.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var (tree, sourceIndex) = sources[index];
            var graph = tree.dependencyMetadata
                ?? throw new InvalidDataException("Effect composition requires completed schema-three source graphs.");
            if ((uint)sourceIndex >= graph.Roots.Length ||
                graph.Roots[sourceIndex].OriginAbsolutePath != roots[index].OriginAbsolutePath)
                throw new InvalidDataException("A composed root disagrees with its captured original identity.");
            identity ??= graph.CompilerIdentity;
            if (identity != graph.CompilerIdentity)
                throw new InvalidDataException("Effect composition cannot combine different compiler identities.");
            foreach (var profile in graph.Profiles)
            {
                if (profiles.TryGetValue(profile.Id, out var previous))
                {
                    if (previous.Flags != profile.Flags || previous.EntryPoint != profile.EntryPoint ||
                        previous.Target != profile.Target || !previous.Macros.SequenceEqual(profile.Macros))
                        throw new InvalidDataException("Effect composition found conflicting compiler profiles.");
                }
                else profiles.Add(profile.Id, profile);
            }
            var selected = graph.Edges.Where(edge => edge.RootIndex == sourceIndex).ToArray();
            var used = selected.SelectMany(edge => new[] { edge.TopFxOriginAbsolutePath, edge.ParentOriginAbsolutePath })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = selected.Where(edge => edge.TargetResourceId is not null)
                .Select(edge => edge.TargetResourceId!).ToHashSet(StringComparer.Ordinal);
            foreach (var node in graph.Nodes.Where(node => used.Contains(node.OriginAbsolutePath) || targets.Contains(node.ResourceId)))
            {
                token.ThrowIfCancellationRequested(); var content = tree.byPath[node.RelativePath];
                if (origins.TryGetValue(node.OriginAbsolutePath, out var originId) && originId != node.ResourceId)
                    throw new InvalidDataException("Effect composition found conflicting original resource identities.");
                origins[node.OriginAbsolutePath] = node.ResourceId;
                if (nodes.TryGetValue(node.ResourceId, out var previous))
                {
                    if (previous.Node.OriginAbsolutePath != node.OriginAbsolutePath ||
                        previous.Content.Description.ByteLength != content.Description.ByteLength ||
                        previous.Content.Description.Sha256 != content.Description.Sha256 ||
                        !previous.Content.Bytes.AsSpan().SequenceEqual(content.Bytes))
                        throw new InvalidDataException("A changed effect dependency conflicts with retained original bytes.");
                }
                else nodes.Add(node.ResourceId, (node, content));
            }
            if (selected.Length > MaximumGraphRecords - edges.Count)
                throw new InvalidDataException("Composed dependency edges exceed their record budget.");
            edges.AddRange(selected.Select(edge => edge with { RootIndex = index }));
            foreach (var diagnostic in graph.Diagnostics.Where(value => value.RootIndex == sourceIndex))
            {
                if (diagnostics.Count == MaximumGraphRecords)
                    throw new InvalidDataException("Composed dependency diagnostics exceed their record budget.");
                diagnostics.Add(diagnostic with { RootIndex = index });
            }
        }
        // Categories describe only retained graph uses; dropping a root can remove one of an origin's
        // former uses without removing another root's bytes. Do not retain stale category flags.
        var categories = nodes.Keys.ToDictionary(key => key, _ => (D3D9EffectDependencyCategory)0, StringComparer.Ordinal);
        foreach (var root in roots) Use(origins[root.OriginAbsolutePath], D3D9EffectDependencyCategory.Effect);
        foreach (var edge in edges)
        {
            Use(origins[edge.TopFxOriginAbsolutePath], D3D9EffectDependencyCategory.Effect);
            if (edge.Kind is D3D9EffectDependencyEdgeKind.IncludeLocal or D3D9EffectDependencyEdgeKind.IncludeSystem)
                Use(edge.TargetResourceId!, D3D9EffectDependencyCategory.Include);
            else if (edge.Kind == D3D9EffectDependencyEdgeKind.ResourceName)
                Use(edge.TargetResourceId!, D3D9EffectDependencyCategory.Resource);
            else if (edge.Kind == D3D9EffectDependencyEdgeKind.DefaultEffect)
                Use(edge.TargetResourceId!, D3D9EffectDependencyCategory.Effect);
        }
        var ordered = nodes.Values.OrderBy(value => value.Node.OriginAbsolutePath, StringComparer.Ordinal).ToArray();
        var contents = new D3D9EffectResourceContent[ordered.Length];
        var descriptions = new FileData[ordered.Length];
        var composedNodes = new NodeData[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var value = ordered[index]; string local = DependencyLocalPath(index);
            descriptions[index] = new(local, ResourcePath(index), value.Content.Description.ByteLength, value.Content.Description.Sha256);
            // All source contents are private immutable arrays. Sharing them preserves frozen bytes and
            // avoids copying the whole old graph during an unrelated model-set edit.
            contents[index] = new(new(local, descriptions[index].ByteLength, descriptions[index].Sha256), value.Content.Bytes);
            composedNodes[index] = value.Node with { RelativePath = local, Category = categories[value.Node.ResourceId] };
        }
        var localByOrigin = composedNodes.ToDictionary(node => node.OriginAbsolutePath, node => node.RelativePath, StringComparer.OrdinalIgnoreCase);
        var required = roots.Select(root => localByOrigin[root.OriginAbsolutePath]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var metadata = new MetadataV3(3, D3D9EffectDependencyStatus.Complete, [], required, descriptions, composedNodes,
            roots.Select(root => new RootData(root.OriginAbsolutePath, root.Provenance with { })).ToArray(), identity!,
            profiles.Values.ToArray(), edges.ToArray(), diagnostics.ToArray());
        new DependencyValidator(metadata, limits, token).Validate();
        if (JsonSerializer.SerializeToUtf8Bytes(metadata, Json).Length > limits.MaxMetadataBytes)
            throw new InvalidDataException("Composed effect dependency metadata exceeds its byte budget.");
        token.ThrowIfCancellationRequested(); return new(contents, metadata);

        void Use(string resourceId, D3D9EffectDependencyCategory category) => categories[resourceId] |= category;
    }
}
