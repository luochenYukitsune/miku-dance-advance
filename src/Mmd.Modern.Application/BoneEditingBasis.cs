using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public enum BoneEditingSpace { Local, Global, View }

/// <summary>
/// Immutable gizmo basis and parent mapping for editing raw, pre-constraint VMD local deltas.
/// Matrices use System.Numerics row vectors. This does not invert or bake IK, morphs, append or physics.
/// </summary>
public sealed class BoneEditingBasis
{
    private static readonly byte[] LinearInterpolation = PmmMotionAdapter.ConvertBoneInterpolation(
        [20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107]);
    private readonly string boneName;
    private readonly Matrix4x4 parentRotation;
    private readonly Matrix4x4 inverseParentRotation;
    private readonly Matrix4x4 basisRotation;
    public Vector3 Origin { get; }
    public Vector3 XAxis => new(basisRotation.M11, basisRotation.M12, basisRotation.M13);
    public Vector3 YAxis => new(basisRotation.M21, basisRotation.M22, basisRotation.M23);
    public Vector3 ZAxis => new(basisRotation.M31, basisRotation.M32, basisRotation.M33);

    private BoneEditingBasis(string name, Matrix4x4 parent, Matrix4x4 basis, Vector3 origin)
    {
        boneName = name; parentRotation = parent; inverseParentRotation = Matrix4x4.Transpose(parent);
        basisRotation = basis; Origin = origin;
    }

    /// <summary>
    /// parentWorld maps the bone's parent-local coordinates to scene world (include object placement for roots).
    /// boneWorld is the current solved world, used only to orient/position the gizmo. Local uses PMX X/Z axes,
    /// orthogonalized with X primary, followed by current bone rotation. View requires camera-to-world, not a
    /// projection or world-to-camera matrix. All supplied worlds must be proper rigid affine transforms.
    /// Callers editing constrained/external-parented bones must supply their effective parent mapping or an
    /// explicit constraint editing policy: solved transforms alone cannot recover editable animation keys.
    /// FixedAxis does not add a new animation clamp here; existing runtime constraint policy remains authoritative.
    /// </summary>
    public static BoneEditingBasis Create(PmxBone bone, Matrix4x4 parentWorld, Matrix4x4 boneWorld,
        BoneEditingSpace space, Matrix4x4? viewToWorld = null)
    {
        ArgumentNullException.ThrowIfNull(bone);
        if (!Enum.IsDefined(space)) throw new ArgumentOutOfRangeException(nameof(space));
        if (string.IsNullOrWhiteSpace(bone.Name) || bone.Name.Any(char.IsControl))
            throw new ArgumentException("Editing requires a nonempty, unambiguous bone name.", nameof(bone));
        var parent = Rotation(parentWorld, nameof(parentWorld));
        var current = Rotation(boneWorld, nameof(boneWorld));
        Matrix4x4 basis;
        switch (space)
        {
            case BoneEditingSpace.Local:
                basis = LocalAxes(bone) * current;
                break;
            case BoneEditingSpace.Global:
                basis = Matrix4x4.Identity;
                break;
            case BoneEditingSpace.View:
                if (viewToWorld is not { } camera)
                    throw new ArgumentException("View editing requires an explicit camera-to-world matrix.", nameof(viewToWorld));
                basis = Rotation(camera, nameof(viewToWorld));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(space));
        }
        return new(bone.Name, parent, basis, boneWorld.Translation);
    }

    /// <summary>Converts signed gizmo-axis distances to a world-space direction; never applies an origin.</summary>
    public Vector3 TranslationToWorld(Vector3 basisDelta)
    {
        RequireFinite(basisDelta, nameof(basisDelta));
        var world = Vector3.TransformNormal(basisDelta, basisRotation);
        RequireFinite(world, nameof(basisDelta)); return world;
    }

    public VpdLocalBoneTransform TranslateInBasis(VpdLocalBoneTransform current, Vector3 basisDelta)
        => TranslateInWorld(current, TranslationToWorld(basisDelta));

    /// <summary>Adds a world drag to the raw animation translation, excluding the local rest position.</summary>
    public VpdLocalBoneTransform TranslateInWorld(VpdLocalBoneTransform current, Vector3 worldDelta)
    {
        Validate(current); RequireFinite(worldDelta, nameof(worldDelta));
        var local = current.Translation + Vector3.TransformNormal(worldDelta, inverseParentRotation);
        RequireFinite(local, nameof(worldDelta));
        return current with { Translation = local };
    }

    /// <summary>Rotates about the bone origin using the captured gizmo basis; translation is unchanged.</summary>
    public VpdLocalBoneTransform RotateInBasis(VpdLocalBoneTransform current, Quaternion basisDelta)
    {
        var delta = Matrix4x4.CreateFromQuaternion(Normalize(basisDelta, nameof(basisDelta)));
        var world = Matrix4x4.Transpose(basisRotation) * delta * basisRotation;
        return RotateInWorld(current, Quaternion.CreateFromRotationMatrix(world));
    }

    /// <summary>Postmultiplies the animation's world orientation by worldDelta about its own origin.</summary>
    public VpdLocalBoneTransform RotateInWorld(VpdLocalBoneTransform current, Quaternion worldDelta)
    {
        Validate(current);
        var local = Matrix4x4.CreateFromQuaternion(Normalize(current.Rotation, nameof(current)))
            * parentRotation * Matrix4x4.CreateFromQuaternion(Normalize(worldDelta, nameof(worldDelta)))
            * inverseParentRotation;
        return current with { Rotation = Normalize(Quaternion.CreateFromRotationMatrix(local), nameof(worldDelta)) };
    }

    /// <summary>Creates an ordinary VMD bone key. An optional same-bone template retains raw names and all curve bytes.</summary>
    public VmdBoneKeyframe CreateKey(uint frame, VpdLocalBoneTransform localAnimation, VmdBoneKeyframe? template = null)
    {
        Validate(localAnimation);
        if (template is not null && template.BoneName != boneName)
            throw new ArgumentException("The interpolation template must belong to the edited bone.", nameof(template));
        return new()
        {
            BoneName = boneName, OriginalName = template?.OriginalName, Frame = frame,
            Translation = localAnimation.Translation,
            Rotation = Normalize(localAnimation.Rotation, nameof(localAnimation)),
            Interpolation = template?.Interpolation ?? LinearInterpolation
        };
    }

    /// <summary>Uses MotionEditor's existing overwrite preflight, explicit confirmation and exact undo/redo transaction.</summary>
    public MotionEditPlan PrepareRegister(MotionEditor editor, uint frame, VpdLocalBoneTransform localAnimation,
        VmdBoneKeyframe? template = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return editor.PrepareRegister(new() { BoneKeyframes = [CreateKey(frame, localAnimation, template)] });
    }

    private static Matrix4x4 LocalAxes(PmxBone bone)
    {
        // Saba MIT PMXFile.cpp preserves these PMX model-space rest axes separately from animation rotation.
        if (!bone.Flags.HasFlag(PmxBoneFlags.LocalAxis)) return Matrix4x4.Identity;
        var x = Unit(bone.LocalXAxis, nameof(bone));
        var z = Unit(bone.LocalZAxis, nameof(bone));
        var y = Vector3.Cross(z, x);
        if (y.LengthSquared() < 1e-12f)
            throw new ArgumentException("PMX local X and Z axes must not be parallel or degenerate.", nameof(bone));
        y = Unit(y, nameof(bone)); z = Unit(Vector3.Cross(x, y), nameof(bone));
        return new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    }

    private static Matrix4x4 Rotation(Matrix4x4 m, string field)
    {
        var x = new Vector3(m.M11, m.M12, m.M13); var y = new Vector3(m.M21, m.M22, m.M23); var z = new Vector3(m.M31, m.M32, m.M33);
        const float tolerance = 2e-4f;
        RequireFinite(x, field); RequireFinite(y, field); RequireFinite(z, field); RequireFinite(m.Translation, field);
        if (!float.IsFinite(m.M14) || !float.IsFinite(m.M24) || !float.IsFinite(m.M34) || !float.IsFinite(m.M44)
            || MathF.Abs(m.M14) > tolerance || MathF.Abs(m.M24) > tolerance || MathF.Abs(m.M34) > tolerance || MathF.Abs(m.M44 - 1) > tolerance
            || MathF.Abs(x.LengthSquared() - 1) > tolerance || MathF.Abs(y.LengthSquared() - 1) > tolerance || MathF.Abs(z.LengthSquared() - 1) > tolerance
            || MathF.Abs(Vector3.Dot(x, y)) > tolerance || MathF.Abs(Vector3.Dot(x, z)) > tolerance || MathF.Abs(Vector3.Dot(y, z)) > tolerance
            || MathF.Abs(Vector3.Dot(Vector3.Cross(x, y), z) - 1) > tolerance)
            throw new ArgumentException("Bone editing matrices must be proper rigid affine transforms; scale, shear, reflection and projection are unsupported.", field);
        // Canonicalize small accumulated float error before using transpose as the rotational inverse.
        return Matrix4x4.CreateFromQuaternion(Normalize(Quaternion.CreateFromRotationMatrix(m), field));
    }

    private static Vector3 Unit(Vector3 value, string field)
    {
        double length = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
        if (!double.IsFinite(length) || length == 0) throw new ArgumentException("Editing axes must be finite and nonzero.", field);
        return new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length));
    }

    private static Quaternion Normalize(Quaternion value, string field)
    {
        double length = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z + (double)value.W * value.W);
        if (!double.IsFinite(length) || length == 0) throw new ArgumentException("Editing rotations must be finite and nonzero.", field);
        return new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length), (float)(value.W / length));
    }
    private static void Validate(VpdLocalBoneTransform pose) { RequireFinite(pose.Translation, nameof(pose)); _ = Normalize(pose.Rotation, nameof(pose)); }
    private static void RequireFinite(Vector3 value, string field)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentException("Editing vectors must remain finite.", field);
    }
}
