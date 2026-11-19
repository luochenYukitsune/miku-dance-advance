// QDEF layout/hemisphere blend reference: Saba PMXModel.cpp:1057-1100,
// @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT, Copyright(c) 2016-2017 benikabocha.
// Full license: third_party/saba/LICENCE.txt. Independent System.Numerics row-vector implementation.
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Animation;

/// <summary>One sample's lazy rigid dual-quaternion palette, built from inverse-bind * bone-world matrices.</summary>
internal sealed class QdefSkinning
{
    private readonly IReadOnlyList<Matrix4x4> skin;
    private readonly Quaternion[] real, dual;
    private readonly bool[] ready;

    public QdefSkinning(IReadOnlyList<Matrix4x4> skin)
    {
        this.skin = skin;
        real = new Quaternion[skin.Count]; dual = new Quaternion[skin.Count]; ready = new bool[skin.Count];
    }

    public (Vector3 Position, Vector3 Normal) Transform(SceneVertex vertex, SkinBinding binding)
    {
        var (rotation, translation) = Blend(binding);
        Vector3 position = Vector3.Transform(vertex.Position, rotation) + translation;
        Vector3 normal = Vector3.Transform(vertex.Normal, rotation);
        if (!Finite(position) || !Finite(normal)) throw new InvalidOperationException("QDEF transformed coordinates overflowed.");
        return (position, normal);
    }

    // Shared with frozen-pose projection: this is the same normalized dual-quaternion blend,
    // including its hemisphere correction and dual-part orthogonalization.
    public (Quaternion Rotation, Vector3 Translation) Blend(SkinBinding binding)
    {
        Quaternion reference = default, blendedReal = default, blendedDual = default;
        bool hasReference = false;
        for (int slot = 0; slot < 4; slot++)
        {
            float weight = binding.Weight(slot);
            if (weight == 0) continue;
            int bone = binding.Bone(slot);
            Prepare(bone);
            if (!hasReference) { reference = real[bone]; hasReference = true; }
            // Negate both parts together: q and -q describe the same rigid transform.
            if (Quaternion.Dot(reference, real[bone]) < 0) weight = -weight;
            blendedReal += real[bone] * weight;
            blendedDual += dual[bone] * weight;
        }
        float normSquared = blendedReal.LengthSquared();
        if (!hasReference || !float.IsFinite(normSquared) || normSquared < 1e-12f)
            throw new InvalidOperationException("QDEF blend has no finite rotation.");
        float inverseNorm = 1 / MathF.Sqrt(normSquared);
        blendedReal *= inverseNorm; blendedDual *= inverseNorm;
        // A unit dual quaternion also has orthogonal real and dual parts. Remove roundoff / blend drift.
        blendedDual -= blendedReal * Quaternion.Dot(blendedReal, blendedDual);
        Quaternion translation = (blendedDual * Quaternion.Conjugate(blendedReal)) * 2;
        return (blendedReal, new Vector3(translation.X, translation.Y, translation.Z));
    }

    /// <summary>Prepares the listed bones in order, with the same rigid-transform checks as lazy blending.</summary>
    public void CopyPalette(ReadOnlySpan<int> bones, Quaternion[] realPalette, Quaternion[] dualPalette)
    {
        foreach (int bone in bones) { Prepare(bone); realPalette[bone] = real[bone]; dualPalette[bone] = dual[bone]; }
    }

    private void Prepare(int bone)
    {
        if (ready[bone]) return;
        Matrix4x4 matrix = skin[bone];
        var x = new Vector3(matrix.M11, matrix.M12, matrix.M13);
        var y = new Vector3(matrix.M21, matrix.M22, matrix.M23);
        var z = new Vector3(matrix.M31, matrix.M32, matrix.M33);
        // Dual quaternions encode proper rigid transforms. Silently stripping scale/shear would deform incorrectly.
        const float tolerance = 1e-4f;
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(matrix.Translation)
            || MathF.Abs(matrix.M14) > tolerance || MathF.Abs(matrix.M24) > tolerance || MathF.Abs(matrix.M34) > tolerance
            || !float.IsFinite(matrix.M44) || MathF.Abs(matrix.M44 - 1) > tolerance
            || MathF.Abs(x.LengthSquared() - 1) > tolerance || MathF.Abs(y.LengthSquared() - 1) > tolerance
            || MathF.Abs(z.LengthSquared() - 1) > tolerance || MathF.Abs(Vector3.Dot(x, y)) > tolerance
            || MathF.Abs(Vector3.Dot(x, z)) > tolerance || MathF.Abs(Vector3.Dot(y, z)) > tolerance
            || MathF.Abs(Vector3.Dot(Vector3.Cross(x, y), z) - 1) > tolerance)
            throw new NotSupportedException($"QDEF bone {bone} requires a finite rigid skin transform; scale, shear and reflections are not supported.");
        real[bone] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
        // Hamilton quaternion multiplication: dual = 0.5 * translationQuaternion * real.
        // System.Numerics matrix extraction already accounts for its row-vector representation.
        dual[bone] = (new Quaternion(matrix.Translation, 0) * real[bone]) * .5f;
        ready[bone] = true;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
