using System.Numerics;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Animation deltas before append, IK, bone morphs, external parenting and physics; translation excludes the local rest offset.</summary>
public readonly record struct VpdLocalBoneTransform(Vector3 Translation, Quaternion Rotation);

/// <summary>Exports explicit editable local deltas or an explicitly baked final pose. Does not infer original keys from solved worlds.</summary>
public static class VpdPoseExporter
{
    /// <summary>Exports caller-supplied pre-constraint animation deltas, indexed by PMX bone, with normalized quaternions.</summary>
    public static VpdPose ConvertLocalPose(PmxModel model, IReadOnlyList<VpdLocalBoneTransform> localAnimation,
        IReadOnlyDictionary<int, float>? morphWeights = null)
    {
        ArgumentNullException.ThrowIfNull(localAnimation);
        ValidateModel(model);
        if (localAnimation.Count != model.Bones.Count) throw new ArgumentException("One local animation delta per PMX bone is required.", nameof(localAnimation));
        var bones = new VpdBonePose[localAnimation.Count];
        for (int i = 0; i < bones.Length; i++)
        {
            var local = localAnimation[i];
            if (!Finite(local.Translation)) throw new ArgumentException($"Bone {i} translation is not finite.", nameof(localAnimation));
            bones[i] = new() { Index = i, BoneName = model.Bones[i].Name, Translation = local.Translation,
                Rotation = Normalize(local.Rotation, nameof(localAnimation)) };
        }
        return new() { Bones = bones, Morphs = Morphs(model, morphWeights, baked: false) };
    }

    /// <summary>
    /// Bakes final model-space bone worlds into ordinary parent-local VPD deltas (row vectors, no Z flip).
    /// The worlds may contain solved IK, append, bone morphs, external parenting and physics. Replay must disable
    /// those constraints and physics: VPD has no metadata to disable them and cannot represent their original keys.
    /// Scene/object world transforms must be removed by the caller. Nonzero bone-affecting or impulse morphs are
    /// rejected because retaining them would apply already baked effects again. Scale/shear/reflection are unrepresentable.
    /// </summary>
    public static VpdPose ConvertBakedPose(PmxModel model, IReadOnlyList<Matrix4x4> boneWorldMatrices,
        IReadOnlyDictionary<int, float>? morphWeights = null)
    {
        ArgumentNullException.ThrowIfNull(boneWorldMatrices);
        ValidateModel(model);
        if (boneWorldMatrices.Count != model.Bones.Count) throw new ArgumentException("One model-space world per PMX bone is required.", nameof(boneWorldMatrices));
        var worlds = boneWorldMatrices.ToArray(); var inverses = new Matrix4x4[worlds.Length];
        for (int i = 0; i < worlds.Length; i++)
        {
            ValidateRigid(worlds[i], nameof(boneWorldMatrices));
            if (!Matrix4x4.Invert(worlds[i], out inverses[i])) throw new ArgumentException($"Bone {i} world cannot be inverted.", nameof(boneWorldMatrices));
        }
        var bones = new VpdBonePose[worlds.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            var bone = model.Bones[i]; int parent = bone.ParentIndex;
            var local = parent < 0 ? worlds[i] : worlds[i] * inverses[parent];
            ValidateRigid(local, nameof(boneWorldMatrices));
            var rest = bone.Position - (parent < 0 ? Vector3.Zero : model.Bones[parent].Position);
            var delta = local.Translation - rest;
            if (!Finite(delta)) throw new ArgumentException("Baked local translation overflowed.", nameof(boneWorldMatrices));
            bones[i] = new() { Index = i, BoneName = bone.Name, Translation = delta,
                Rotation = Normalize(Quaternion.CreateFromRotationMatrix(local), nameof(boneWorldMatrices)) };
        }
        return new() { Bones = bones, Morphs = Morphs(model, morphWeights, baked: true) };
    }

    private static void ValidateModel(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Bones is null || model.Morphs is null) throw new ArgumentException("PMX requires bone and morph collections.", nameof(model));
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < model.Bones.Count; i++)
        {
            var bone = model.Bones[i];
            if (bone is null || !Name(bone.Name) || !names.Add(bone.Name) || !Finite(bone.Position)
                || bone.ParentIndex < -1 || bone.ParentIndex >= model.Bones.Count || bone.ParentIndex == i)
                throw new ArgumentException($"Invalid PMX bone {i}: names, rest positions and hierarchy must be valid.", nameof(model));
        }
        var state = new byte[model.Bones.Count];
        for (int i = 0; i < state.Length; i++)
        {
            int current = i;
            while (current >= 0 && state[current] == 0) { state[current] = 1; current = model.Bones[current].ParentIndex; }
            if (current >= 0 && state[current] == 1) throw new ArgumentException("PMX bone hierarchy contains a cycle.", nameof(model));
            current = i;
            while (current >= 0 && state[current] == 1) { state[current] = 2; current = model.Bones[current].ParentIndex; }
        }
        names.Clear();
        for (int i = 0; i < model.Morphs.Count; i++)
            if (model.Morphs[i] is not { } morph || !Name(morph.Name) || !names.Add(morph.Name))
                throw new ArgumentException($"Invalid or ambiguous PMX morph name at {i}.", nameof(model));
    }

    private static VpdMorphPose[] Morphs(PmxModel model, IReadOnlyDictionary<int, float>? weights, bool baked)
    {
        var values = new float[model.Morphs.Count];
        if (weights is not null) foreach (var (index, weight) in weights)
        {
            if ((uint)index >= (uint)values.Length || !float.IsFinite(weight))
                throw new ArgumentException("Morph weights require valid PMX indices and finite values.", nameof(weights));
            values[index] = weight;
        }
        if (baked)
        {
            var graph = new PmxMorphWeights(model);
            var unsafeLeaves = graph.Supported(type => type is PmxMorphType.Bone or PmxMorphType.Impulse);
            // Use the same effective paths as playback: an unselected Flip branch has no effect.
            if (graph.Expand(values.Select((weight, index) => (weight, index))
                .ToDictionary(value => value.index, value => value.weight), unsafeLeaves).Count != 0)
                throw new ArgumentException("A baked VPD cannot retain nonzero bone-affecting or impulse morphs; use raw local animation export or omit already baked weights.", nameof(weights));
        }
        return values.Select((weight, index) => new VpdMorphPose { Index = index, MorphName = model.Morphs[index].Name, Weight = weight }).ToArray();
    }

    private static Quaternion Normalize(Quaternion value, string field)
    {
        // Double intermediates also handle finite, very large or subnormal raw quaternions without overflow/underflow.
        double norm = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z + (double)value.W * value.W);
        if (!double.IsFinite(norm) || norm == 0) throw new ArgumentException("Pose rotations must be finite and nonzero.", field);
        return new((float)(value.X / norm), (float)(value.Y / norm), (float)(value.Z / norm), (float)(value.W / norm));
    }

    private static void ValidateRigid(Matrix4x4 m, string field)
    {
        var x = new Vector3(m.M11, m.M12, m.M13); var y = new Vector3(m.M21, m.M22, m.M23); var z = new Vector3(m.M31, m.M32, m.M33);
        const float tolerance = 2e-4f;
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(m.Translation)
            || !float.IsFinite(m.M14) || !float.IsFinite(m.M24) || !float.IsFinite(m.M34) || !float.IsFinite(m.M44)
            || MathF.Abs(m.M14) > tolerance || MathF.Abs(m.M24) > tolerance || MathF.Abs(m.M34) > tolerance || MathF.Abs(m.M44 - 1) > tolerance
            || MathF.Abs(x.LengthSquared() - 1) > tolerance || MathF.Abs(y.LengthSquared() - 1) > tolerance || MathF.Abs(z.LengthSquared() - 1) > tolerance
            || MathF.Abs(Vector3.Dot(x, y)) > tolerance || MathF.Abs(Vector3.Dot(x, z)) > tolerance || MathF.Abs(Vector3.Dot(y, z)) > tolerance
            || MathF.Abs(Vector3.Dot(Vector3.Cross(x, y), z) - 1) > tolerance)
            throw new ArgumentException("VPD bone worlds must be finite proper rigid affine matrices; scale, shear and reflection cannot be encoded.", field);
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Name(string? name) => !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl)
        && name.IndexOfAny(['{', '}', ';']) < 0 && !name.Contains("//", StringComparison.Ordinal);
}
