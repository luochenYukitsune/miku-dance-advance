using System.Numerics;

namespace Mmd.Modern.Animation;

/// <summary>Immutable model-space vertex/UV deltas and local bone base transforms, before animation/IK/physics.</summary>
public sealed class MorphPose
{
    private Vector3[] vertexDeltas;
    private readonly IReadOnlyList<Vector3>? denseView;
    private readonly Lazy<IReadOnlyList<Vector3>>? expandedView;
    internal int FirstVertex { get; }
    internal int VertexCount { get; }
    private readonly int storedCount;
    private readonly Vector2[] vertexUvDeltas;
    private readonly ImmutableZeroUvChannel? zeroUvChannel;
    public IReadOnlyList<Vector3> VertexDeltas => denseView ?? expandedView!.Value;
    public IReadOnlyList<Vector2> VertexUvDeltas { get; }
    public IReadOnlyList<Vector3> BoneTranslations { get; }
    public IReadOnlyList<Quaternion> BoneRotations { get; }
    internal bool HasVertexEffects { get; }
    internal bool HasUvEffects { get; }
    internal bool HasBoneEffects { get; }
    internal int ActiveVertexStart { get; }
    internal int ActiveVertexEnd { get; }
    internal int ActiveUvStart { get; }
    internal int ActiveUvEnd { get; }
    // Borrow the pose-owned immutable arrays inside the animation assembly;
    // public callers retain read-only collection views, never the backing arrays.
    internal ReadOnlySpan<Vector3> VertexDeltaSpan
    {
        get { if (expandedView is not null) _ = expandedView.Value; return Volatile.Read(ref vertexDeltas); }
    }
    internal ReadOnlySpan<Vector3> PositionRange
    {
        get
        {
            var data = Volatile.Read(ref vertexDeltas);
            return data.Length == storedCount ? data : data.AsSpan(FirstVertex, storedCount);
        }
    }
    internal ReadOnlySpan<Vector2> VertexUvDeltaSpan => zeroUvChannel is null ? vertexUvDeltas : zeroUvChannel.Span;

    // This owner can only construct zero-filled storage and exposes no writable view.
    // Unlike transferred pose arrays, it is therefore safe to share between immutable poses.
    internal sealed class ImmutableZeroUvChannel
    {
        private readonly Vector2[] values;
        internal int Count => values.Length;
        internal IReadOnlyList<Vector2> View { get; }
        internal ReadOnlySpan<Vector2> Span => values;

        internal ImmutableZeroUvChannel(int count)
        {
            values = new Vector2[count];
            View = Array.AsReadOnly(values);
        }
    }

    public MorphPose(IEnumerable<Vector3> vertexDeltas, IEnumerable<Vector3> boneTranslations, IEnumerable<Quaternion> boneRotations,
        IEnumerable<Vector2>? vertexUvDeltas = null)
        : this(CopyInputs(vertexDeltas, boneTranslations, boneRotations, vertexUvDeltas))
    {
    }

    // The caller transfers fresh arrays and must not use them after this call.
    internal static MorphPose FromOwnedArrays(Vector3[] vertices, Vector3[] translations, Quaternion[] rotations, Vector2[] uvs)
        => new((vertices, translations, rotations, uvs));

    // Only the zero owner is shared. The remaining fresh arrays still transfer to this pose.
    internal static MorphPose FromOwnedArraysWithZeroUvs(Vector3[] vertices, Vector3[] translations, Quaternion[] rotations,
        ImmutableZeroUvChannel zeroUvs)
    {
        ArgumentNullException.ThrowIfNull(zeroUvs);
        return new((vertices, translations, rotations, []), zeroUvs);
    }

    // Transfers caller-owned compact position deltas starting at first vertex alongside an immutable zero UV channel.
    internal static MorphPose FromOwnedPositionRange(Vector3[] vertices, int first, Vector3[] translations,
        Quaternion[] rotations, ImmutableZeroUvChannel zeroUvs)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(zeroUvs);
        return new((vertices, translations, rotations, []), zeroUvs, first);
    }

    private static (Vector3[] vertexDeltas, Vector3[] boneTranslations, Quaternion[] boneRotations, Vector2[] vertexUvDeltas) CopyInputs(
        IEnumerable<Vector3> vertexDeltas, IEnumerable<Vector3> boneTranslations, IEnumerable<Quaternion> boneRotations,
        IEnumerable<Vector2>? vertexUvDeltas)
    {
        ArgumentNullException.ThrowIfNull(vertexDeltas); ArgumentNullException.ThrowIfNull(boneTranslations);
        ArgumentNullException.ThrowIfNull(boneRotations);
        var vertices = vertexDeltas.ToArray(); var translations = boneTranslations.ToArray(); var rotations = boneRotations.ToArray();
        var uvs = vertexUvDeltas?.ToArray() ?? new Vector2[vertices.Length];
        return (vertices, translations, rotations, uvs);
    }

    private MorphPose((Vector3[] vertexDeltas, Vector3[] boneTranslations, Quaternion[] boneRotations, Vector2[] vertexUvDeltas) owned,
        ImmutableZeroUvChannel? zeroUvs = null, int? first = null)
    {
        var (vertices, translations, rotations, uvs) = owned;
        VertexCount = zeroUvs?.Count ?? uvs.Length;
        FirstVertex = first ?? 0;
        storedCount = vertices.Length;
        if (FirstVertex < 0 || FirstVertex > VertexCount - storedCount || (first is null && storedCount != VertexCount))
            throw new ArgumentException("Morph UV and position delta counts differ.", nameof(owned.vertexUvDeltas));
        if (translations.Length != rotations.Length) throw new ArgumentException("Morph bone translation and rotation counts differ.");
        // Detect effects during each channel's full validation pass. An earlier effect
        // must never skip later finite checks or move rotation normalization earlier.
        bool hasVertexEffects = false, hasUvEffects = false, hasBoneEffects = false;
        int activeVertexStart = VertexCount, activeVertexEnd = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            if (!AnimationValidation.Finite(v)) throw new ArgumentException("Morph vertex deltas must be finite.", nameof(owned.vertexDeltas));
            if (v != Vector3.Zero)
            {
                hasVertexEffects = true;
                int vertexIndex = FirstVertex + i;
                if (vertexIndex < activeVertexStart) activeVertexStart = vertexIndex;
                activeVertexEnd = vertexIndex + 1;
            }
        }
        int activeUvStart = VertexCount, activeUvEnd = 0;
        if (zeroUvs is null)
        {
            for (int i = 0; i < uvs.Length; i++)
            {
                var v = uvs[i];
                if (!AnimationValidation.Finite(v)) throw new ArgumentException("Morph UV deltas must be finite.", nameof(owned.vertexUvDeltas));
                if (v != Vector2.Zero)
                {
                    hasUvEffects = true;
                    if (i < activeUvStart) activeUvStart = i;
                    activeUvEnd = i + 1;
                }
            }
        }
        foreach (var v in translations)
        {
            if (!AnimationValidation.Finite(v)) throw new ArgumentException("Morph bone translations must be finite.", nameof(owned.boneTranslations));
            hasBoneEffects = hasBoneEffects || v != Vector3.Zero;
        }
        for (int i = 0; i < rotations.Length; i++)
        {
            var q = rotations[i];
            double lengthSquared = (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
            if (!double.IsFinite(lengthSquared) || lengthSquared == 0) throw new ArgumentException("Morph bone rotations must be finite and nonzero.", nameof(owned.boneRotations));
            double length = Math.Sqrt(lengthSquared);
            rotations[i] = new((float)(q.X / length), (float)(q.Y / length), (float)(q.Z / length), (float)(q.W / length));
            hasBoneEffects = hasBoneEffects || (rotations[i] != Quaternion.Identity && rotations[i] != -Quaternion.Identity);
        }
        HasVertexEffects = hasVertexEffects;
        HasUvEffects = hasUvEffects;
        HasBoneEffects = hasBoneEffects;
        ActiveVertexStart = hasVertexEffects ? activeVertexStart : 0;
        ActiveVertexEnd = hasVertexEffects ? activeVertexEnd : 0;
        ActiveUvStart = hasUvEffects ? activeUvStart : 0;
        ActiveUvEnd = hasUvEffects ? activeUvEnd : 0;
        this.vertexDeltas = vertices; this.vertexUvDeltas = uvs; zeroUvChannel = zeroUvs;
        if (storedCount == VertexCount) denseView = Array.AsReadOnly(vertices);
        else expandedView = new(ExpandPositions);
        BoneTranslations = Array.AsReadOnly(translations); BoneRotations = Array.AsReadOnly(rotations);
        VertexUvDeltas = zeroUvs?.View ?? Array.AsReadOnly(uvs);
    }

    // A caller requesting the full indexed view gets it once. Release the
    // compact backing afterwards; concurrent skinning can read either array.
    private IReadOnlyList<Vector3> ExpandPositions()
    {
        var full = new Vector3[VertexCount];
        vertexDeltas.CopyTo(full, FirstVertex);
        Volatile.Write(ref vertexDeltas, full);
        return Array.AsReadOnly(full);
    }

    internal void ValidateRig(CharacterRig rig)
    {
        if (VertexCount != rig.Vertices.Count || VertexUvDeltas.Count != rig.Vertices.Count || BoneTranslations.Count != rig.Bones.Count)
            throw new ArgumentException("Morph pose vertex and bone counts must match the character rig.", "options");
    }

    internal Quaternion ApplyBoneRotation(int index, Quaternion animationRotation)
    {
        var rotation = BoneRotations[index];
        // Original D3DXQuaternionMultiply(animation, animation, morph) returns Hamilton morph * animation.
        // Identity (including its antipodal representation) keeps the pre-morph arithmetic exact.
        return rotation == Quaternion.Identity || rotation == -Quaternion.Identity ? animationRotation : rotation * animationRotation;
    }

    internal Vector3 ApplyBoneTranslation(int index, Vector3 animationTranslation)
        => BoneTranslations[index] == Vector3.Zero ? animationTranslation : animationTranslation + BoneTranslations[index];
}
