// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// PMXModel.cpp BeginMorphMaterial/MorphMaterial/EndMorphMaterial and MaterialFactor::Mul/Add.
// Full license: third_party/saba/LICENCE.txt. Local adaptation includes edge colour/width,
// immutable offset copies and the caller's PMM-scaled baseline materials.
using System.Collections.ObjectModel;
using System.Numerics;
using System.Threading;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Evaluates PMX material colour and edge offsets independently of geometry and renderer state.</summary>
public sealed class PmxMaterialMorphAdapter
{
    private const int MaxCachedContributions = 65_536;
    private sealed record Definition(int Index, PmxMorph.MaterialOffset[] Offsets);
    private sealed record TextureEvaluationCacheEntry(
        PmxMorphWeights.Contribution[] Contributions,
        IReadOnlyList<PmxMaterialTextureFactors> Factors);
    private sealed record ApplyCacheEntry(
        PmxMorphWeights.Contribution[] Contributions,
        OriginalStyleScene InputSurface,
        OriginalOutlineData InputOutline,
        OriginalStyleScene ResultSurface,
        OriginalOutlineData ResultOutline,
        ReadOnlyCollection<SceneMaterial> EvaluatedMaterials,
        ReadOnlyCollection<OriginalOutlineMaterial> EvaluatedEdges);

    private readonly Definition?[] definitions;
    private readonly PmxMorphWeights groupWeights;
    private readonly bool[] supported;
    private readonly int materialCount;
    private readonly IReadOnlyList<PmxMaterialTextureFactors> identityTextureFactors;
    private TextureEvaluationCacheEntry? lastTextureEvaluation;
    private ApplyCacheEntry? lastApplyEvaluation;

    public IReadOnlyDictionary<string, int> SupportedMorphIndices { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    internal IReadOnlyList<int> SupportedIndices { get; }
    internal IReadOnlyList<PmxMaterialTextureFactors> IdentityTextureFactors => identityTextureFactors;
    public int SupportedMorphCount => SupportedIndices.Count;

    public PmxMaterialMorphAdapter(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        materialCount = model.Materials.Count;
        var identityFactors = new PmxMaterialTextureFactors[materialCount];
        Array.Fill(identityFactors, PmxMaterialTextureFactors.Identity);
        identityTextureFactors = Array.AsReadOnly(identityFactors);
        groupWeights = new(model);
        supported = groupWeights.Supported(type => type == PmxMorphType.Material);
        SupportedIndices = Array.AsReadOnly(Enumerable.Range(0, supported.Length).Where(i => supported[i]).ToArray());
        definitions = new Definition?[model.Morphs.Count];
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal); var diagnostics = new List<string>();
        for (int index = 0; index < model.Morphs.Count; index++)
        {
            var morph = model.Morphs[index];
            bool first = seen.TryAdd(morph.Name, index);
            if (!first && supported[index])
                diagnostics.Add($"Duplicate PMX morph name '{morph.Name}': VMD binds only the first PMX morph (index {seen[morph.Name]}).");
            if (first && supported[index]) names.Add(morph.Name, index);
            if (morph.Type != PmxMorphType.Material) continue;
            var offsets = morph.MaterialOffsets.ToArray();
            foreach (var offset in offsets)
            {
                if (offset.MaterialIndex < -1 || offset.MaterialIndex >= materialCount || !Enum.IsDefined(offset.Operation)
                    || !Finite(offset.Diffuse) || !Finite(offset.Specular) || !float.IsFinite(offset.SpecularPower)
                    || !Finite(offset.Ambient) || !Finite(offset.EdgeColor) || !float.IsFinite(offset.EdgeSize)
                    || !Finite(offset.TextureFactor) || !Finite(offset.SphereTextureFactor) || !Finite(offset.ToonTextureFactor))
                    throw new ArgumentException($"PMX material morph '{morph.Name}' has an invalid material offset.", nameof(model));
                Vector4 identity = offset.Operation == PmxMaterialMorphOperation.Multiply ? Vector4.One : Vector4.Zero;
                var unsupported = new List<string>();
                if (offset.TextureFactor != identity) unsupported.Add("diffuse texture");
                if (offset.SphereTextureFactor != identity) unsupported.Add("sphere texture");
                if (offset.ToonTextureFactor != identity) unsupported.Add("toon texture");
                if (unsupported.Count != 0)
                    diagnostics.Add($"PMX material morph '{morph.Name}' contains nonidentity {string.Join(", ", unsupported)} factors; EvaluateTextureFactors preserves these raw accumulators, and GPU consumption depends on the selected renderer path.");
            }
            definitions[index] = new(index, offsets);
        }
        SupportedMorphIndices = new ReadOnlyDictionary<string, int>(names);
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Pass the current posed geometry with rest materials on every sample. No state is accumulated
    /// between calls. Caller material baselines preserve project edge scaling, texture identities and ranges.
    /// Finite weights and colour/alpha results remain unclamped; negative power/width exceed the scene contract.</summary>
    public (OriginalStyleScene Surface, OriginalOutlineData Outline) Apply(OriginalStyleScene surface,
        OriginalOutlineData outline, IReadOnlyDictionary<int, float> weights)
    {
        ArgumentNullException.ThrowIfNull(surface); ArgumentNullException.ThrowIfNull(outline); ArgumentNullException.ThrowIfNull(weights);
        if (surface.Snapshot.Materials.Count != materialCount || outline.Materials.Count != materialCount)
            throw new ArgumentException("Material morph baselines must preserve the PMX material count.");
        var contributions = groupWeights.Expand(weights, supported);
        if (contributions.Count == 0) return (surface, outline);

        var cached = Volatile.Read(ref lastApplyEvaluation);
        if (cached is not null && SameContributions(cached.Contributions, contributions))
        {
            if (ReferenceEquals(surface, cached.InputSurface) && ReferenceEquals(outline, cached.InputOutline))
                return (cached.ResultSurface, cached.ResultOutline);
            if (ReferenceEquals(surface.Snapshot.Materials, cached.InputSurface.Snapshot.Materials)
                && ReferenceEquals(outline.Materials, cached.InputOutline.Materials))
                return (new(surface.Snapshot.WithMaterials(cached.EvaluatedMaterials), surface.Materials, surface.Profile),
                    outline.WithMaterials(cached.EvaluatedEdges));
        }

        Span<Factor> multiply = materialCount <= 32 ? stackalloc Factor[materialCount] : new Factor[materialCount];
        Span<Factor> add = materialCount <= 32 ? stackalloc Factor[materialCount] : new Factor[materialCount];
        multiply.Fill(Factor.One);
        add.Clear();
        // PMX root and depth-first Group offset order; shared leaves remain separate applications.
        foreach (var contribution in contributions)
        {
            var definition = definitions[contribution.Index]!;
            float weight = contribution.Weight;
            foreach (var offset in definition.Offsets)
            {
                int start = offset.MaterialIndex == -1 ? 0 : offset.MaterialIndex;
                int end = offset.MaterialIndex == -1 ? materialCount : start + 1;
                var value = Factor.From(offset);
                for (int i = start; i < end; i++)
                {
                    if (offset.Operation == PmxMaterialMorphOperation.Multiply) multiply[i] = multiply[i].Multiply(value, weight);
                    else add[i] = add[i].Add(value, weight);
                }
            }
        }
        var materials = new SceneMaterial[materialCount]; var edges = new OriginalOutlineMaterial[materialCount];
        for (int i = 0; i < materialCount; i++)
        {
            var baseline = surface.Snapshot.Materials[i]; var edge = outline.Materials[i];
            var value = new Factor(baseline.Diffuse, baseline.Specular, baseline.Shininess, baseline.Ambient, edge.Color, edge.Width)
                .Multiply(multiply[i], 1).Add(add[i], 1);
            if (!value.IsFinite) throw new ArgumentException($"PMX material morph result for material {i} is not finite.", nameof(weights));
            if (value.Power < 0) throw new NotSupportedException($"PMX material morph result for material {i} has negative specular power; the current scene contract requires nonnegative power.");
            if (value.Width < 0) throw new NotSupportedException($"PMX material morph result for material {i} has negative edge width; the current outline contract requires nonnegative width.");
            materials[i] = baseline with { Diffuse = value.Diffuse, Specular = value.Specular, Shininess = value.Power, Ambient = value.Ambient };
            edges[i] = edge with { Color = value.Edge, Width = value.Width };
        }
        var nextSnapshot = surface.Snapshot.WithMaterials(materials);
        var nextSurface = new OriginalStyleScene(nextSnapshot, surface.Materials, surface.Profile);
        var nextOutline = outline.WithMaterials(edges);
        Volatile.Write(ref lastApplyEvaluation, contributions.Count <= MaxCachedContributions
            ? new ApplyCacheEntry(contributions.ToArray(), surface, outline, nextSurface, nextOutline,
                nextSnapshot.Materials, nextOutline.Materials)
            : null);
        return (nextSurface, nextOutline);
    }

    /// <summary>Evaluates all four components of diffuse/sphere/toon sample factors. This neutral output
    /// is transported to surface bindings by the animation controller; actual consumption depends on the renderer path.</summary>
    public IReadOnlyList<PmxMaterialTextureFactors> EvaluateTextureFactors(IReadOnlyDictionary<int, float> weights)
    {
        var contributions = groupWeights.Expand(weights, supported);
        if (contributions.Count == 0) return identityTextureFactors;
        var cached = Volatile.Read(ref lastTextureEvaluation);
        if (cached is not null && SameContributions(cached.Contributions, contributions)) return cached.Factors;

        var result = new PmxMaterialTextureFactors[materialCount];
        Array.Fill(result, PmxMaterialTextureFactors.Identity);
        static PmxTextureFactor Combine(PmxTextureFactor current, Vector4 value, float weight, PmxMaterialMorphOperation operation)
            => operation == PmxMaterialMorphOperation.Multiply
                ? current with { Multiply = current.Multiply * Vector4.Lerp(Vector4.One, value, weight) }
                : current with { Add = current.Add + value * weight };
        foreach (var contribution in contributions)
            foreach (var offset in definitions[contribution.Index]!.Offsets)
            {
                int first = offset.MaterialIndex < 0 ? 0 : offset.MaterialIndex;
                int last = offset.MaterialIndex < 0 ? materialCount : first + 1;
                for (int i = first; i < last; i++)
                {
                    var current = result[i];
                    result[i] = new(Combine(current.Diffuse, offset.TextureFactor, contribution.Weight, offset.Operation),
                        Combine(current.Sphere, offset.SphereTextureFactor, contribution.Weight, offset.Operation),
                        Combine(current.Toon, offset.ToonTextureFactor, contribution.Weight, offset.Operation));
                }
            }
        for (int i = 0; i < result.Length; i++)
        {
            var value = result[i];
            if (!Finite(value.Diffuse.Multiply) || !Finite(value.Diffuse.Add) || !Finite(value.Sphere.Multiply)
                || !Finite(value.Sphere.Add) || !Finite(value.Toon.Multiply) || !Finite(value.Toon.Add))
                throw new ArgumentException($"Texture morph factors for material {i} are not finite.", nameof(weights));
        }
        var readonlyResult = Array.AsReadOnly(result);
        Volatile.Write(ref lastTextureEvaluation, contributions.Count <= MaxCachedContributions
            ? new TextureEvaluationCacheEntry(contributions.ToArray(), readonlyResult)
            : null);
        return readonlyResult;
    }

    private static bool SameContributions(PmxMorphWeights.Contribution[] cached, IReadOnlyList<PmxMorphWeights.Contribution> current)
    {
        if (cached.Length != current.Count) return false;
        for (int i = 0; i < cached.Length; i++)
        {
            var previous = cached[i]; var next = current[i];
            if (previous.Index != next.Index
                || BitConverter.SingleToInt32Bits(previous.Weight) != BitConverter.SingleToInt32Bits(next.Weight))
                return false;
        }
        return true;
    }

    private readonly record struct Factor(Vector4 Diffuse, Vector3 Specular, float Power, Vector3 Ambient, Vector4 Edge, float Width)
    {
        public static Factor One { get; } = new(Vector4.One, Vector3.One, 1, Vector3.One, Vector4.One, 1);
        public static Factor From(PmxMorph.MaterialOffset offset) => new(offset.Diffuse, offset.Specular, offset.SpecularPower, offset.Ambient, offset.EdgeColor, offset.EdgeSize);
        public Factor Multiply(Factor value, float weight) => new(
            Diffuse * Vector4.Lerp(Vector4.One, value.Diffuse, weight), Specular * Vector3.Lerp(Vector3.One, value.Specular, weight),
            Power * ((1 - weight) + value.Power * weight), Ambient * Vector3.Lerp(Vector3.One, value.Ambient, weight),
            Edge * Vector4.Lerp(Vector4.One, value.Edge, weight), Width * ((1 - weight) + value.Width * weight));
        public Factor Add(Factor value, float weight) => new(Diffuse + value.Diffuse * weight, Specular + value.Specular * weight,
            Power + value.Power * weight, Ambient + value.Ambient * weight, Edge + value.Edge * weight, Width + value.Width * weight);
        public bool IsFinite => Finite(Diffuse) && Finite(Specular) && float.IsFinite(Power) && Finite(Ambient) && Finite(Edge) && float.IsFinite(Width);
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => Finite(new Vector3(value.X, value.Y, value.Z)) && float.IsFinite(value.W);
}
