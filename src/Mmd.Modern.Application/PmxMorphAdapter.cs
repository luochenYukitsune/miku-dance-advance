// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// Position and UV accumulation follow Saba PMXModel.cpp's MorphPosition / MorphUV / UpdatePositions.
// Copyright (c) 2016-2017 benikabocha. Full license: third_party/saba/LICENCE.txt.
// Bone rotation accumulation follows original MMD BD770's weighted quaternion multiplication.
using System.Collections.ObjectModel;
using System.Numerics;
using System.Threading;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Copies supported PMX offsets once. Samples contain only format-neutral deltas, before skinning.</summary>
public sealed class PmxMorphAdapter
{
    private sealed record Definition(int Index, PmxMorph.PositionOffset[] Positions, PmxMorph.BoneOffset[] Bones, PmxMorph.UvOffset[] Uvs,
        int FirstVertex, int EndVertex);
    private sealed record EvaluationCacheEntry(PmxMorphWeights.Contribution[] Contributions, MorphPose Pose);
    private const long MaxCachedBytes = 4L * 1024 * 1024;
    private const int MaxCachedContributions = 65_536;
    private readonly Definition?[] definitions;
    private readonly PmxMorphWeights groupWeights;
    private readonly bool[] supported;
    private readonly int vertexCount, boneCount;
    private readonly Lazy<MorphPose.ImmutableZeroUvChannel> zeroUvChannel;
    private EvaluationCacheEntry? lastEvaluation;
    public IReadOnlyDictionary<string, int> SupportedMorphIndices { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    internal IReadOnlyList<int> SupportedIndices { get; }
    public int SupportedMorphCount => SupportedIndices.Count;

    public PmxMorphAdapter(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        vertexCount = model.Vertices.Count; boneCount = model.Bones.Count;
        zeroUvChannel = new(() => new(vertexCount));
        groupWeights = new(model);
        supported = groupWeights.Supported(type => type is PmxMorphType.Position or PmxMorphType.Bone or PmxMorphType.Uv);
        SupportedIndices = Array.AsReadOnly(Enumerable.Range(0, supported.Length).Where(i => supported[i]).ToArray());
        definitions = new Definition?[model.Morphs.Count];
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal); var diagnostics = new List<string>();
        for (int index = 0; index < model.Morphs.Count; index++)
        {
            var morph = model.Morphs[index];
            bool first = seen.TryAdd(morph.Name, index);
            if (!first) diagnostics.Add($"Duplicate PMX morph name '{morph.Name}': VMD binds only the first PMX morph (index {seen[morph.Name]}).");
            if (first && supported[index]) names.Add(morph.Name, index);
            if (morph.Type is not (PmxMorphType.Position or PmxMorphType.Bone or PmxMorphType.Uv)) continue;
            var positions = morph.Type == PmxMorphType.Position ? morph.PositionOffsets.ToArray() : [];
            var bones = morph.Type == PmxMorphType.Bone ? morph.BoneOffsets.ToArray() : [];
            var uvs = morph.Type == PmxMorphType.Uv ? morph.UvOffsets.ToArray() : [];
            foreach (var offset in uvs)
                if (offset.VertexIndex < 0 || offset.VertexIndex >= vertexCount || !Finite(offset.Uv))
                    throw new ArgumentException($"PMX morph '{morph.Name}' has an invalid UV offset.", nameof(model));
            int firstVertex = positions.Length == 0 ? int.MaxValue : vertexCount, endVertex = 0;
            foreach (var offset in positions)
            {
                if (offset.VertexIndex < 0 || offset.VertexIndex >= vertexCount || !Finite(offset.Position))
                    throw new ArgumentException($"PMX morph '{morph.Name}' has an invalid vertex offset.", nameof(model));
                if (offset.VertexIndex < firstVertex) firstVertex = offset.VertexIndex;
                if (offset.VertexIndex + 1 > endVertex) endVertex = offset.VertexIndex + 1;
            }
            for (int i = 0; i < bones.Length; i++)
            {
                var offset = bones[i];
                if (offset.BoneIndex < 0 || offset.BoneIndex >= boneCount || !Finite(offset.Position) || !Finite(offset.Rotation))
                    throw new ArgumentException($"PMX morph '{morph.Name}' has an invalid bone offset.", nameof(model));
                var q = offset.Rotation;
                double lengthSquared = (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
                if (Math.Abs(lengthSquared - 1) > 1e-5)
                    diagnostics.Add($"PMX bone morph '{morph.Name}' contains a non-unit rotation; its raw finite components are retained for original MMD weighting.");
            }
            definitions[index] = new(index, positions, bones, uvs, firstVertex, endVertex);
        }
        foreach (var unsupported in model.Morphs.Where(m => m.Type is not (PmxMorphType.Group or PmxMorphType.Flip or PmxMorphType.Position or PmxMorphType.Bone or PmxMorphType.Uv)).GroupBy(m => m.Type))
            diagnostics.Add($"PMX morph type '{unsupported.Key}' ({unsupported.Count()} morphs) is not evaluated by the vertex/bone morph adapter; it supports Group expansion into Position, Bone and base Uv morphs. AdditionalUv1..4 require additional shader UV channels.");
        if (model.Morphs.Any(m => m.Type == PmxMorphType.Bone))
            diagnostics.Add("Bone morph rotations use original MMD's weighted quaternion accumulation; an original-runtime morph pose comparison remains unverified.");
        SupportedMorphIndices = new ReadOnlyDictionary<string, int>(names);
        Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Weights stay finite and unclamped. A zero pose returns null to preserve the unchanged animation path.
    /// Identical expanded contributions may reuse the last completely validated immutable pose.</summary>
    public MorphPose? Evaluate(IReadOnlyDictionary<int, float> weights)
    {
        // Always validate every input and derived weight, including unsupported and unrelated keys,
        // before considering reuse. Caller dictionaries never become part of the cache.
        var contributions = groupWeights.Expand(weights, supported);
        if (contributions.Count == 0) return null;
        var cached = Volatile.Read(ref lastEvaluation);
        if (cached is not null && SameContributions(cached.Contributions, contributions)) return cached.Pose;
        // Expand validates all input and derived weights first. Only an absence of active
        // UV offsets proves this channel is immutable zero; zero-valued UV math still runs normally.
        bool hasUvOffsets = false;
        int first = vertexCount, end = 0;
        foreach (var contribution in contributions)
        {
            var definition = definitions[contribution.Index]!;
            if (definition.Uvs.Length != 0) { hasUvOffsets = true; first = 0; end = vertexCount; break; }
            if (definition.Positions.Length != 0)
            {
                if (definition.FirstVertex < first) first = definition.FirstVertex;
                if (definition.EndVertex > end) end = definition.EndVertex;
            }
        }
        if (end == 0) first = 0;
        var vertices = new Vector3[end - first]; var translations = new Vector3[boneCount];
        var uvs = hasUvOffsets ? new Vector2[vertexCount] : null;
        var rotations = new Quaternion[boneCount]; Array.Fill(rotations, Quaternion.Identity);
        foreach (var contribution in contributions)
        {
            var definition = definitions[contribution.Index]!;
            float weight = contribution.Weight;
            foreach (var offset in definition.Positions) vertices[offset.VertexIndex - first] += offset.Position * weight;
            // Saba accumulates the Vector4 offset but rendering consumes only its X/Y components.
            foreach (var offset in definition.Uvs) uvs![offset.VertexIndex] += new Vector2(offset.Uv.X, offset.Uv.Y) * weight;
            foreach (var offset in definition.Bones)
            {
                translations[offset.BoneIndex] += offset.Position * weight;
                var weightedRotation = WeightRotation(offset.Rotation, weight);
                // D3DXQuaternionMultiply(accum, accum, weighted) returns weighted * accum.
                rotations[offset.BoneIndex] = weightedRotation * rotations[offset.BoneIndex];
            }
        }
        var pose = uvs is null
            ? MorphPose.FromOwnedPositionRange(vertices, first, translations, rotations, zeroUvChannel.Value)
            : MorphPose.FromOwnedArrays(vertices, translations, rotations, uvs);
        // Publish only after full finite validation and rotation normalization succeed. A precise
        // private fingerprint avoids retaining the expansion List's potentially larger capacity.
        // There is one entry, no parent pose or reference to the caller's mutable model/weights.
        Volatile.Write(ref lastEvaluation, CanCache(contributions.Count)
            ? new EvaluationCacheEntry(contributions.ToArray(), pose) : null);
        return pose;
    }

    private bool CanCache(int contributionCount)
    {
        // Conservatively include active UV storage even for the shared zero channel, all pose
        // arrays, the exact eight-byte contribution fingerprint, and collection/object overhead.
        return contributionCount <= MaxCachedContributions
            && 20L * vertexCount + 28L * boneCount + 8L * contributionCount + 512 <= MaxCachedBytes;
    }

    private static bool SameContributions(PmxMorphWeights.Contribution[] cached, IReadOnlyList<PmxMorphWeights.Contribution> current)
    {
        if (cached.Length != current.Count) return false;
        for (int i = 0; i < cached.Length; i++)
        {
            var previous = cached[i]; var next = current[i];
            if (previous.Index != next.Index
                || BitConverter.SingleToInt32Bits(previous.Weight) != BitConverter.SingleToInt32Bits(next.Weight)) return false;
        }
        return true;
    }

    private static Quaternion WeightRotation(Quaternion raw, float weight)
    {
        // Original BD770: RVA 0x132B90 is exactly 3.14f (0x4048F5C3), not pi.
        // Preserve raw W before acos; pre-normalization or shortest-path slerp changes this behavior.
        float half = MathF.Acos(Math.Clamp(raw.W, -1, 1));
        if (raw.W < 0) half -= 3.14f;
        float axisLength = MathF.Sqrt(raw.X * raw.X + raw.Y * raw.Y + raw.Z * raw.Z);
        if (half == 0 || axisLength < 1.1920929e-7f) return Quaternion.Identity;
        float scale = MathF.Sin(half * weight) / axisLength;
        return new(raw.X * scale, raw.Y * scale, raw.Z * scale, MathF.Cos(half * weight));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool Finite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
