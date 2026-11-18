using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Core;

/// <summary>PMX vertex deformation families, in file order.</summary>
public enum SceneSkinKind { Bdef1, Bdef2, Bdef4, Sdef, Qdef }

/// <summary>One vertex's normalized influences. Zero-weight slots may hold the placeholder bone -1.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SceneSkinBinding(SceneSkinKind Kind, int Bone0, int Bone1, int Bone2, int Bone3, Vector4 Weights);

/// <summary>Raw SDEF C and the two rest-derived anchors used by the reference skinning.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SceneSdefBinding(Vector3 C, Vector3 Anchor0, Vector3 Anchor1);

/// <summary>Immutable bind pose and weights of one rig. Engines may upload it once and share it across
/// every frame; it contains no animation, morph or engine state.</summary>
public sealed class SceneSkinningModel
{
    private readonly SceneVertex[] restVertices;
    private readonly SceneSkinBinding[] bindings;
    private readonly SceneSdefBinding[] sdef;
    public int VertexCount => restVertices.Length;
    public int BoneCount { get; }
    public bool HasSdef { get; }
    public bool HasQdef { get; }
    public ReadOnlySpan<SceneVertex> RestVertices => restVertices;
    public ReadOnlySpan<SceneSkinBinding> Bindings => bindings;
    /// <summary>One row per vertex when any vertex is SDEF, otherwise empty.</summary>
    public ReadOnlySpan<SceneSdefBinding> Sdef => sdef;

    public SceneSkinningModel(ReadOnlySpan<SceneVertex> restVertices, ReadOnlySpan<SceneSkinBinding> bindings,
        ReadOnlySpan<SceneSdefBinding> sdef, int boneCount)
    {
        if (restVertices.IsEmpty || bindings.Length != restVertices.Length || boneCount <= 0)
            throw new ArgumentException("A skinning model needs one binding per rest vertex and at least one bone.");
        this.restVertices = restVertices.ToArray(); this.bindings = bindings.ToArray(); BoneCount = boneCount;
        foreach (var binding in this.bindings)
        {
            if (!Enum.IsDefined(binding.Kind)) throw new ArgumentException("Unknown skinning kind.", nameof(bindings));
            for (int slot = 0; slot < 4; slot++)
            {
                float weight = slot switch { 0 => binding.Weights.X, 1 => binding.Weights.Y, 2 => binding.Weights.Z, _ => binding.Weights.W };
                int bone = slot switch { 0 => binding.Bone0, 1 => binding.Bone1, 2 => binding.Bone2, _ => binding.Bone3 };
                if (!float.IsFinite(weight) || weight < 0 || bone < -1 || bone >= boneCount || (weight != 0 && bone < 0))
                    throw new ArgumentException("Skin weights must be finite, nonnegative and reference model bones.", nameof(bindings));
            }
            HasSdef |= binding.Kind == SceneSkinKind.Sdef; HasQdef |= binding.Kind == SceneSkinKind.Qdef;
        }
        if (HasSdef ? sdef.Length != restVertices.Length : !sdef.IsEmpty)
            throw new ArgumentException("SDEF rows are required for every vertex exactly when an SDEF vertex exists.", nameof(sdef));
        this.sdef = sdef.ToArray();
    }
}

/// <summary>Sparse model-space vertex and base-UV morph deltas, applied before skinning.
/// One immutable instance is shared by every frame with the same morph weights.</summary>
public sealed class SceneMorphDeltas
{
    private readonly int[] indices;
    private readonly Vector3[] positions;
    private readonly Vector2[] uvs;
    public int VertexCount { get; }
    public ReadOnlySpan<int> Indices => indices;
    public ReadOnlySpan<Vector3> Positions => positions;
    public ReadOnlySpan<Vector2> Uvs => uvs;

    public SceneMorphDeltas(int vertexCount, ReadOnlySpan<int> indices, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs)
    {
        if (vertexCount <= 0 || indices.Length != positions.Length || indices.Length != uvs.Length)
            throw new ArgumentException("Morph deltas need one position and UV delta per listed vertex.");
        int previous = -1;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] <= previous || indices[i] >= vertexCount || !float.IsFinite(positions[i].X) || !float.IsFinite(positions[i].Y)
                || !float.IsFinite(positions[i].Z) || !float.IsFinite(uvs[i].X) || !float.IsFinite(uvs[i].Y))
                throw new ArgumentException("Morph delta indices must ascend within the model and deltas must be finite.", nameof(indices));
            previous = indices[i];
        }
        VertexCount = vertexCount; this.indices = indices.ToArray(); this.positions = positions.ToArray(); this.uvs = uvs.ToArray();
    }
}

/// <summary>Neutral per-frame deformation input of one rig: skin palette (inverse bind * bone world), the SDEF
/// world rotations, the QDEF rigid dual-quaternion palette and sparse morph deltas. The CPU reference result is
/// produced only on demand, so a consumer that reads vertices still observes the exact CPU deformation.</summary>
public sealed class SceneDeformation
{
    private static long cpuMaterializations;
    private readonly Matrix4x4[] skin;
    private readonly Quaternion[] sdefRotations, qdefReal, qdefDual;
    private readonly Lazy<SceneVertex[]> reference;
    public SceneSkinningModel Model { get; }
    public ReadOnlySpan<Matrix4x4> SkinMatrices => skin;
    /// <summary>Normalized world rotations per bone when the model has SDEF vertices, otherwise empty.</summary>
    public ReadOnlySpan<Quaternion> SdefRotations => sdefRotations;
    /// <summary>Real and dual parts per bone when the model has QDEF vertices; unreferenced bones are zero.</summary>
    public ReadOnlySpan<Quaternion> QdefReal => qdefReal;
    public ReadOnlySpan<Quaternion> QdefDual => qdefDual;
    public SceneMorphDeltas? Morphs { get; }
    /// <summary>Process-wide count of CPU reference evaluations, for diagnostics of consumers that still read vertices.</summary>
    public static long CpuMaterializations => Interlocked.Read(ref cpuMaterializations);
    internal bool IsMaterialized => reference.IsValueCreated;

    /// <summary>The reference delegate must return a fresh private array; it is called at most once.</summary>
    public SceneDeformation(SceneSkinningModel model, ReadOnlySpan<Matrix4x4> skinMatrices, ReadOnlySpan<Quaternion> sdefRotations,
        ReadOnlySpan<Quaternion> qdefReal, ReadOnlySpan<Quaternion> qdefDual, SceneMorphDeltas? morphs, Func<SceneVertex[]> cpuReference)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(cpuReference);
        if (skinMatrices.Length != model.BoneCount || sdefRotations.Length != (model.HasSdef ? model.BoneCount : 0)
            || qdefReal.Length != (model.HasQdef ? model.BoneCount : 0) || qdefDual.Length != qdefReal.Length
            || morphs is not null && morphs.VertexCount != model.VertexCount)
            throw new ArgumentException("Deformation palettes must match the skinning model.");
        Model = model; Morphs = morphs;
        skin = skinMatrices.ToArray(); this.sdefRotations = sdefRotations.ToArray();
        this.qdefReal = qdefReal.ToArray(); this.qdefDual = qdefDual.ToArray();
        reference = new(() =>
        {
            Interlocked.Increment(ref cpuMaterializations);
            var vertices = cpuReference();
            if (vertices is null || vertices.Length != model.VertexCount)
                throw new InvalidOperationException("The CPU reference deformation must preserve the model vertex count.");
            foreach (var v in vertices)
                if (!float.IsFinite(v.Position.X) || !float.IsFinite(v.Position.Y) || !float.IsFinite(v.Position.Z)
                    || !float.IsFinite(v.Normal.X) || !float.IsFinite(v.Normal.Y) || !float.IsFinite(v.Normal.Z)
                    || !float.IsFinite(v.Normal.LengthSquared()) || !float.IsFinite(v.Uv.X) || !float.IsFinite(v.Uv.Y))
                    throw new ArgumentException("Deformed vertices must be finite.");
            return vertices;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True when both inputs deform the same model with bitwise-equal palettes and the same morph instance.</summary>
    public bool HasSameInputs(SceneDeformation other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(this, other) || ReferenceEquals(Model, other.Model) && ReferenceEquals(Morphs, other.Morphs)
            && MemoryMarshal.AsBytes(skin.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other.skin.AsSpan()))
            && MemoryMarshal.AsBytes(sdefRotations.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other.sdefRotations.AsSpan()))
            && MemoryMarshal.AsBytes(qdefReal.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other.qdefReal.AsSpan()))
            && MemoryMarshal.AsBytes(qdefDual.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other.qdefDual.AsSpan()));
    }

    internal SceneVertex[] Materialize() => reference.Value;
    /// <summary>A fresh caller-owned copy of the exact CPU reference deformation.</summary>
    public SceneVertex[] CopyReferenceVertices() => (SceneVertex[])Materialize().Clone();
}

/// <summary>A contiguous vertex range whose deformed values are produced from Deformation, then placed by World
/// exactly as project composition places CPU-deformed vertices (identity World copies them unchanged).</summary>
public sealed class SceneDeformationRange
{
    public int Start { get; }
    public int Count { get; }
    public SceneDeformation Deformation { get; }
    public Matrix4x4 World { get; }
    internal SceneDeformationRange(int start, SceneDeformation deformation, Matrix4x4 world)
    { Start = start; Count = deformation.Model.VertexCount; Deformation = deformation; World = world; }
}

/// <summary>Read-only list whose immutable chunks are produced on first element access; Count never evaluates them.</summary>
internal sealed class DeferredVertexList<T>(int count, Func<OwnedVertexChunks<T>> chunks) : IList<T> where T : unmanaged
{
    public int Count => count;
    public bool IsReadOnly => true;
    public T this[int index] { get => chunks()[index]; set => throw new NotSupportedException("Owned vertex chunks are immutable."); }
    public int IndexOf(T item) => chunks().IndexOf(item);
    public bool Contains(T item) => chunks().Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => chunks().CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => chunks().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(T item) => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public void Clear() => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public void Insert(int index, T item) => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public bool Remove(T item) => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public void RemoveAt(int index) => throw new NotSupportedException("Owned vertex chunks are immutable.");
}
