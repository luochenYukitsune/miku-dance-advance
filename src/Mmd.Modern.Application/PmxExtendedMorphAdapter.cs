// PMX channel layout reference: Saba PMXFile.h @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332,
// MIT, Copyright (c) 2016-2017 benikabocha. Full license: third_party/saba/LICENCE.txt.
// Additional-channel accumulation is a managed extension; Saba's renderer only consumes base UV.
using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Four additional PMX UV vectors. Undeclared channels are zero, not aliases of base UV.</summary>
public readonly record struct PmxAdditionalUvVertex(Vector4 Uv1, Vector4 Uv2, Vector4 Uv3, Vector4 Uv4)
{
    public Vector4 this[int channel] => channel switch
    {
        0 => Uv1, 1 => Uv2, 2 => Uv3, 3 => Uv4,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    internal PmxAdditionalUvVertex Add(int channel, Vector4 value) => channel switch
    {
        0 => this with { Uv1 = Uv1 + value }, 1 => this with { Uv2 = Uv2 + value },
        2 => this with { Uv3 = Uv3 + value }, 3 => this with { Uv4 = Uv4 + value },
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
}

/// <summary>Immutable neutral UV1..4 output, including rest baselines. Does not change the GPU vertex contract.</summary>
public sealed class PmxExtendedMorphAdapter
{
    private sealed record Definition(int Channel, PmxMorph.UvOffset[] Offsets);
    private readonly PmxAdditionalUvVertex[] baseline;
    private readonly Definition?[] definitions;
    private readonly PmxMorphWeights groupWeights;
    private readonly bool[] supported;
    private IReadOnlyList<PmxMorphWeights.Contribution>? reusableContributions;
    private IReadOnlyList<PmxAdditionalUvVertex>? reusableResult;
    public int AdditionalUvCount { get; }
    public IReadOnlyDictionary<string, int> SupportedMorphIndices { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public PmxExtendedMorphAdapter(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Header is null || model.Vertices is null || model.Morphs is null || model.Morphs.Any(m => m is null))
            throw new ArgumentException("Additional UV evaluation requires header, vertices and morph definitions.", nameof(model));
        AdditionalUvCount = model.Header.AdditionalUvCount;
        if (AdditionalUvCount is < 0 or > 4)
            throw new ArgumentException("PMX declares zero to four additional UV channels.", nameof(model));
        baseline = new PmxAdditionalUvVertex[model.Vertices.Count];
        for (int i = 0; i < baseline.Length; i++)
        {
            var vertex = model.Vertices[i];
            if (vertex?.AdditionalUvs is null || vertex.AdditionalUvs.Count != AdditionalUvCount || vertex.AdditionalUvs.Any(v => !Finite(v)))
                throw new ArgumentException($"Vertex {i} has an invalid additional UV baseline.", nameof(model));
            for (int channel = 0; channel < AdditionalUvCount; channel++)
                baseline[i] = baseline[i].Add(channel, vertex.AdditionalUvs[channel]);
        }
        groupWeights = new(model);
        supported = groupWeights.Supported(IsAdditionalUv);
        definitions = new Definition?[model.Morphs.Count];
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        for (int i = 0; i < definitions.Length; i++)
        {
            var morph = model.Morphs[i];
            bool first = seen.Add(morph.Name);
            if (supported[i] && first) names.Add(morph.Name, i);
            if (supported[i] && !first)
                diagnostics.Add($"Duplicate PMX morph name '{morph.Name}': only the first PMX record can bind by name.");
            if (!IsAdditionalUv(morph.Type)) continue;
            int channel = (int)morph.Type - (int)PmxMorphType.AdditionalUv1;
            if (channel >= AdditionalUvCount)
                throw new ArgumentException($"Morph '{morph.Name}' references undeclared additional UV channel {channel + 1}.", nameof(model));
            if (morph.UvOffsets is null) throw new ArgumentException("Additional UV offsets cannot be null.", nameof(model));
            var offsets = morph.UvOffsets.ToArray();
            foreach (var offset in offsets)
                if ((uint)offset.VertexIndex >= (uint)baseline.Length || !Finite(offset.Uv))
                    throw new ArgumentException($"Morph '{morph.Name}' has an invalid additional UV offset.", nameof(model));
            definitions[i] = new(channel, offsets);
        }
        foreach (var type in model.Morphs.Select(m => m.Type).Where(t => t is PmxMorphType.Impulse).Distinct())
            diagnostics.Add($"PMX {type} morph evaluation is not implemented by the additional UV adapter; it is not expanded as a Group.");
        SupportedMorphIndices = new ReadOnlyDictionary<string, int>(names);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>Each call owns a new immutable snapshot. Finite weights remain unclamped, including negative weights.</summary>
    public IReadOnlyList<PmxAdditionalUvVertex> Evaluate(IReadOnlyDictionary<int, float> weights)
        => EvaluateContributions(groupWeights.Expand(weights, supported),weights);

    // The controller owns these immutable results. Other morph channels can change
    // while this channel's ordered effective inputs stay identical. Still expand
    // and validate every incoming map before reusing the one most recent result.
    internal IReadOnlyList<PmxAdditionalUvVertex> EvaluateReusable(IReadOnlyDictionary<int,float> weights)
    {
        var contributions = groupWeights.Expand(weights, supported);
        if(reusableResult is {} previous&&SameContributions(reusableContributions,contributions))return previous;
        var result=EvaluateContributions(contributions,weights);
        reusableContributions=contributions;reusableResult=result;
        return result;
    }

    private static bool SameContributions(IReadOnlyList<PmxMorphWeights.Contribution>? previous,
        IReadOnlyList<PmxMorphWeights.Contribution> current)
    {
        if(previous is null||previous.Count!=current.Count)return false;
        for(int i=0;i<current.Count;i++)
            if(previous[i].Index!=current[i].Index
                ||BitConverter.SingleToInt32Bits(previous[i].Weight)!=BitConverter.SingleToInt32Bits(current[i].Weight))return false;
        return true;
    }

    private IReadOnlyList<PmxAdditionalUvVertex> EvaluateContributions(
        IReadOnlyList<PmxMorphWeights.Contribution> contributions,IReadOnlyDictionary<int,float> weights)
    {
        var result = (PmxAdditionalUvVertex[])baseline.Clone();
        foreach (var contribution in contributions)
        {
            var definition = definitions[contribution.Index]!;
            foreach (var offset in definition.Offsets)
                result[offset.VertexIndex] = result[offset.VertexIndex].Add(definition.Channel, offset.Uv * contribution.Weight);
        }
        for (int i = 0; i < result.Length; i++)
            for (int channel = 0; channel < AdditionalUvCount; channel++)
                if (!Finite(result[i][channel]))
                    throw new ArgumentException($"Additional UV result for vertex {i}, channel {channel + 1} is not finite.", nameof(weights));
        return Array.AsReadOnly(result);
    }

    private static bool IsAdditionalUv(PmxMorphType type) => type is PmxMorphType.AdditionalUv1 or PmxMorphType.AdditionalUv2
        or PmxMorphType.AdditionalUv3 or PmxMorphType.AdditionalUv4;
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>Sampler result is transformed by sampled * Multiply + Add, with independent accumulators.</summary>
public readonly record struct PmxTextureFactor(Vector4 Multiply, Vector4 Add)
{
    public Vector4 Apply(Vector4 sampled) => sampled * Multiply + Add;
    public static PmxTextureFactor Identity => new(Vector4.One, Vector4.Zero);
}

/// <summary>PMX diffuse, sphere and toon sampler factors; no renderer handles or mutable arrays.</summary>
public readonly record struct PmxMaterialTextureFactors(PmxTextureFactor Diffuse, PmxTextureFactor Sphere, PmxTextureFactor Toon)
{
    public static PmxMaterialTextureFactors Identity => new(PmxTextureFactor.Identity, PmxTextureFactor.Identity, PmxTextureFactor.Identity);
}
