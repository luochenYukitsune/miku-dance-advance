// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// PMXModel.cpp UpdateMorphAnimation / Morph: PMX root order, then depth-first Group offset order.
// Full license: third_party/saba/LICENCE.txt. Local adaptation validates the graph, traverses
// iteratively and bounds expansion. Flip is a discrete selector, never a linear Group.
// Its mathematical selection rule was checked against nanoem's PMX 2.1 implementation;
// no nanoem host source is incorporated. See the stage's flip-impulse-design.md.
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Immutable Group/Flip graph shared by all morph adapters.</summary>
internal sealed class PmxMorphWeights
{
    internal readonly record struct Contribution(int Index, float Weight);
    private const int ExpansionBudget = 1_000_000;
    private readonly PmxMorphType[] types;
    private readonly PmxMorph.GroupOffset[][] groups;
    private readonly int[] groupOrder;

    internal PmxMorphWeights(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Morphs is null || model.Morphs.Any(m => m is null))
            throw new ArgumentException("Morph graph requires non-null morph definitions.", nameof(model));
        types = model.Morphs.Select(m => m.Type).ToArray();
        groups = new PmxMorph.GroupOffset[types.Length][];
        var incoming = new int[types.Length];
        int groupCount = 0;
        for (int i = 0; i < types.Length; i++)
        {
            groups[i] = types[i] switch
            {
                PmxMorphType.Group => (model.Morphs[i].GroupOffsets ?? throw new ArgumentException("Group offsets cannot be null.", nameof(model))).ToArray(),
                PmxMorphType.Flip => (model.Morphs[i].FlipOffsets ?? throw new ArgumentException("Flip offsets cannot be null.", nameof(model))).ToArray(),
                _ => []
            };
            if (!Branch(types[i])) continue;
            groupCount++;
            foreach (var offset in groups[i])
            {
                if (offset.MorphIndex < 0 || offset.MorphIndex >= types.Length || !float.IsFinite(offset.Weight))
                    throw new ArgumentException($"PMX {types[i]} morph '{model.Morphs[i].Name}' has an invalid morph reference or non-finite ratio.", nameof(model));
                if (Branch(types[offset.MorphIndex])) incoming[offset.MorphIndex]++;
            }
        }
        var ready = new Queue<int>();
        for (int i = 0; i < types.Length; i++)
            if (Branch(types[i]) && incoming[i] == 0) ready.Enqueue(i);
        var order = new List<int>(groupCount);
        while (ready.TryDequeue(out int index))
        {
            order.Add(index);
            foreach (var offset in groups[index])
                if (Branch(types[offset.MorphIndex]) && --incoming[offset.MorphIndex] == 0)
                    ready.Enqueue(offset.MorphIndex);
        }
        if (order.Count != groupCount)
            throw new ArgumentException("PMX Group/Flip morph graph contains a cycle, including self-references or zero-ratio edges.", nameof(model));
        groupOrder = order.ToArray();
    }

    /// <summary>Empty groups and groups with no supported descendants do not bind VMD tracks.</summary>
    internal bool[] Supported(Func<PmxMorphType, bool> supportsLeaf)
    {
        var supported = types.Select(type => !Branch(type) && supportsLeaf(type)).ToArray();
        for (int i = groupOrder.Length - 1; i >= 0; i--)
        {
            int index = groupOrder[i];
            supported[index] = groups[index].Any(offset => supported[offset.MorphIndex]);
        }
        return supported;
    }

    /// <summary>Preserves each path separately: merging leaf weights would change material
    /// multiplication and the order of noncommuting bone rotations.</summary>
    internal IReadOnlyList<Contribution> Expand(IReadOnlyDictionary<int, float> weights, bool[] supported)
    {
        ArgumentNullException.ThrowIfNull(weights);
        foreach (float weight in weights.Values)
            if (!float.IsFinite(weight)) throw new ArgumentException("Morph weights must be finite.", nameof(weights));
        var result = new List<Contribution>();
        var pending = new Stack<Contribution>();
        int scheduled = 0;
        void Schedule(int index, float weight)
        {
            // Charge before pushing, so a wide group also cannot grow the pending stack without bound.
            if (++scheduled > ExpansionBudget)
                throw new ArgumentException($"PMX Group/Flip morph expansion exceeds the {ExpansionBudget} node budget.", nameof(weights));
            pending.Push(new(index, weight));
        }
        for (int root = 0; root < types.Length; root++)
        {
            if (!supported[root] || !weights.TryGetValue(root, out float rootWeight) || rootWeight == 0) continue;
            Schedule(root, rootWeight);
            while (pending.TryPop(out var current))
            {
                if (!Branch(types[current.Index]))
                {
                    result.Add(current);
                    continue;
                }
                var offsets = groups[current.Index];
                if (types[current.Index] == PmxMorphType.Flip)
                {
                    if (current.Weight <= 0 || offsets.Length == 0) continue;
                    // Positive weights divide into N+1 bins; the first two select entry zero.
                    // Clamp before integer conversion so even a finite float.MaxValue is safe.
                    float bucket = (offsets.Length + 1f) * current.Weight;
                    int selected = bucket >= offsets.Length ? offsets.Length - 1 : Math.Max(0, (int)bucket - 1);
                    var offset = offsets[selected];
                    if (supported[offset.MorphIndex] && offset.Weight != 0) Schedule(offset.MorphIndex, offset.Weight);
                    continue;
                }
                for (int i = offsets.Length - 1; i >= 0; i--)
                {
                    var offset = offsets[i];
                    if (!supported[offset.MorphIndex] || offset.Weight == 0) continue;
                    float weight = current.Weight * offset.Weight;
                    if (!float.IsFinite(weight))
                        throw new ArgumentException("PMX Group morph expansion produced a non-finite weight.", nameof(weights));
                    if (weight != 0) Schedule(offset.MorphIndex, weight);
                }
            }
        }
        return result;
    }

    private static bool Branch(PmxMorphType type) => type is PmxMorphType.Group or PmxMorphType.Flip;
}
