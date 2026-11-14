// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// MMDPhysics.cpp PMX body/joint frames; adapted to System.Numerics row vectors and raw PMX coordinates.
using System.Numerics;

namespace Mmd.Modern.Physics;

public enum PhysicsBodyMode { Kinematic, Dynamic, DynamicAligned }
public enum PhysicsShapeKind { Sphere, Box, Capsule }
public sealed record PhysicsBoneDefinition(int ParentIndex, Matrix4x4 RestWorld);
/// <summary>Raw PMX dimensions: sphere X radius; box XYZ half extents; capsule X radius and Y cylinder height.</summary>
public sealed record PhysicsBodyDefinition(string Name, int BoneIndex, PhysicsShapeKind Shape, Vector3 Size,
    Vector3 Position, Vector3 Rotation, float Mass, float LinearDamping, float AngularDamping, float Restitution,
    float Friction, int CollisionGroup, ushort CollisionMask, PhysicsBodyMode Mode);
/// <summary>Raw PMX joint data, including type; lower > upper means a free axis, as in Bullet.</summary>
public sealed record PhysicsJointDefinition(string Name, int BodyA, int BodyB, byte Type, Vector3 Position, Vector3 Rotation,
    Vector3 LinearLower, Vector3 LinearUpper, Vector3 AngularLower, Vector3 AngularUpper, Vector3 LinearSpring, Vector3 AngularSpring);

/// <summary>Immutable, engine/format neutral physics inputs. PMX and rendering assemblies are not dependencies.</summary>
public sealed class CharacterPhysicsDefinition
{
    public IReadOnlyList<PhysicsBoneDefinition> Bones { get; }
    public IReadOnlyList<PhysicsBodyDefinition> Bodies { get; }
    public IReadOnlyList<PhysicsJointDefinition> Joints { get; }
    public IReadOnlyList<Matrix4x4> BodyRestWorldTransforms { get; }
    public IReadOnlyList<Matrix4x4> BodyBoneOffsets { get; }
    public IReadOnlyList<Matrix4x4> JointLocalFramesA { get; }
    public IReadOnlyList<Matrix4x4> JointLocalFramesB { get; }
    /// <summary>MMD loader's pull safeguard distance: both body-local joint offsets plus the linear limit vector length.</summary>
    public IReadOnlyList<float> JointMaximumDistances { get; }
    internal int[] BoneOrder { get; }
    internal Matrix4x4[] InverseBodyBoneOffsets { get; }

    public CharacterPhysicsDefinition(IEnumerable<PhysicsBoneDefinition> bones, IEnumerable<PhysicsBodyDefinition> bodies,
        IEnumerable<PhysicsJointDefinition> joints)
    {
        ArgumentNullException.ThrowIfNull(bones); ArgumentNullException.ThrowIfNull(bodies); ArgumentNullException.ThrowIfNull(joints);
        var boneArray = bones.ToArray(); var bodyArray = bodies.ToArray(); var jointArray = joints.ToArray();
        for (int i = 0; i < boneArray.Length; i++)
        {
            var bone = boneArray[i];
            if (bone is null || bone.ParentIndex < -1 || bone.ParentIndex >= boneArray.Length || bone.ParentIndex == i)
                throw new ArgumentException($"Invalid parent on physics bone {i}.", nameof(bones));
            PhysicsMath.ValidateRigid(bone.RestWorld, nameof(bones));
        }
        BoneOrder = BuildOrder(boneArray);
        var rest = new Matrix4x4[bodyArray.Length]; var offsets = new Matrix4x4[bodyArray.Length];
        InverseBodyBoneOffsets = new Matrix4x4[bodyArray.Length];
        for (int i = 0; i < bodyArray.Length; i++)
        {
            var body = bodyArray[i];
            if (body is null || body.Name is null || !Enum.IsDefined(body.Mode) || !Enum.IsDefined(body.Shape)
                || body.BoneIndex < -1 || body.BoneIndex >= boneArray.Length || body.CollisionGroup is < 0 or > 15
                || !PhysicsMath.Finite(body.Size) || !PhysicsMath.Finite(body.Position) || !PhysicsMath.Finite(body.Rotation)
                || !float.IsFinite(body.Mass) || body.Mass < 0 || !float.IsFinite(body.LinearDamping) || !float.IsFinite(body.AngularDamping)
                || !float.IsFinite(body.Restitution) || !float.IsFinite(body.Friction)
                || body.Size.X <= 0 || (body.Shape == PhysicsShapeKind.Box && (body.Size.Y <= 0 || body.Size.Z <= 0))
                || (body.Shape == PhysicsShapeKind.Capsule && body.Size.Y < 0))
                throw new ArgumentException($"Invalid physics body {i}.", nameof(bodies));
            rest[i] = PhysicsMath.PmxBodyTransform(body.Position, body.Rotation);
            // Unbound kinematic bodies follow root bone 0, matching Saba; unbound dynamic bodies do not drive it.
            int anchor = body.BoneIndex >= 0 ? body.BoneIndex : boneArray.Length == 0 ? -1 : 0;
            var boneRest = anchor < 0 ? Matrix4x4.Identity : boneArray[anchor].RestWorld;
            Matrix4x4.Invert(boneRest, out var inverseBone);
            offsets[i] = rest[i] * inverseBone;
            Matrix4x4.Invert(offsets[i], out var inverseOffset); InverseBodyBoneOffsets[i] = inverseOffset;
        }
        var frameA = new Matrix4x4[jointArray.Length]; var frameB = new Matrix4x4[jointArray.Length];
        var maximumDistances = new float[jointArray.Length];
        for (int i = 0; i < jointArray.Length; i++)
        {
            var joint = jointArray[i];
            if (joint is null || joint.Name is null || joint.BodyA < 0 || joint.BodyA >= bodyArray.Length
                || joint.BodyB < 0 || joint.BodyB >= bodyArray.Length || joint.BodyA == joint.BodyB
                || !PhysicsMath.Finite(joint.Position) || !PhysicsMath.Finite(joint.Rotation)
                || !PhysicsMath.Finite(joint.LinearLower) || !PhysicsMath.Finite(joint.LinearUpper)
                || !PhysicsMath.Finite(joint.AngularLower) || !PhysicsMath.Finite(joint.AngularUpper)
                || !PhysicsMath.Finite(joint.LinearSpring) || !PhysicsMath.Finite(joint.AngularSpring))
                throw new ArgumentException($"Invalid physics joint {i}.", nameof(joints));
            // Bullet setEulerZYX(x,y,z) in the pinned Saba call implements Rz * Ry * Rx (column vectors).
            var world = Matrix4x4.CreateRotationX(joint.Rotation.X) * Matrix4x4.CreateRotationY(joint.Rotation.Y)
                * Matrix4x4.CreateRotationZ(joint.Rotation.Z) * Matrix4x4.CreateTranslation(joint.Position);
            Matrix4x4.Invert(rest[joint.BodyA], out var inverseA); Matrix4x4.Invert(rest[joint.BodyB], out var inverseB);
            frameA[i] = world * inverseA; frameB[i] = world * inverseB;
            // Both original PMX and PMD loaders store this at constraint +0x94.
            // It is unrelated to shape size or angular limits. Free linear axes retain their authored values.
            var limit = Vector3.Max(Vector3.Abs(joint.LinearLower), Vector3.Abs(joint.LinearUpper));
            maximumDistances[i] = Vector3.Transform(joint.Position, inverseA).Length()
                + Vector3.Transform(joint.Position, inverseB).Length() + limit.Length();
            if (!float.IsFinite(maximumDistances[i]))
                throw new ArgumentException($"Physics joint {i} pull maximum distance is not finite.", nameof(joints));
        }
        Bones = Array.AsReadOnly(boneArray); Bodies = Array.AsReadOnly(bodyArray); Joints = Array.AsReadOnly(jointArray);
        BodyRestWorldTransforms = Array.AsReadOnly(rest); BodyBoneOffsets = Array.AsReadOnly(offsets);
        JointLocalFramesA = Array.AsReadOnly(frameA); JointLocalFramesB = Array.AsReadOnly(frameB);
        JointMaximumDistances = Array.AsReadOnly(maximumDistances);
    }

    private static int[] BuildOrder(PhysicsBoneDefinition[] bones)
    {
        var order = new int[bones.Length]; var state = new byte[bones.Length]; var chain = new int[bones.Length]; int count = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            int current = i, depth = 0;
            while (current >= 0 && state[current] != 2)
            {
                if (state[current] == 1) throw new ArgumentException("Physics bone hierarchy contains a cycle.", nameof(bones));
                state[current] = 1; chain[depth++] = current; current = bones[current].ParentIndex;
            }
            while (depth > 0) { current = chain[--depth]; state[current] = 2; order[count++] = current; }
        }
        return order;
    }
}

public static class PhysicsMath
{
    public static Matrix4x4 PmxBodyTransform(Vector3 position, Vector3 radians) => Matrix4x4.CreateRotationZ(radians.Z)
        * Matrix4x4.CreateRotationX(radians.X) * Matrix4x4.CreateRotationY(radians.Y) * Matrix4x4.CreateTranslation(position);
    public static bool ShouldCollide(PhysicsBodyDefinition a, PhysicsBodyDefinition b) =>
        (a.CollisionMask & (1 << b.CollisionGroup)) != 0 && (b.CollisionMask & (1 << a.CollisionGroup)) != 0;
    internal static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public static void ValidateRigid(Matrix4x4 value, string name)
    {
        var x = new Vector3(value.M11, value.M12, value.M13); var y = new Vector3(value.M21, value.M22, value.M23); var z = new Vector3(value.M31, value.M32, value.M33);
        if (!Finite(x) || !Finite(y) || !Finite(z) || !float.IsFinite(value.M14) || !float.IsFinite(value.M24)
            || !float.IsFinite(value.M34) || !float.IsFinite(value.M44)
            || MathF.Abs(Vector3.Dot(x, y)) + MathF.Abs(Vector3.Dot(x, z)) + MathF.Abs(Vector3.Dot(y, z)) > 1e-4f
            || !Matrix4x4.Decompose(value, out var scale, out var orientation, out var position) || !Finite(scale) || !Finite(position)
            || !float.IsFinite(orientation.LengthSquared()) || Vector3.DistanceSquared(scale, Vector3.One) > 1e-8f
            || MathF.Abs(value.M14) + MathF.Abs(value.M24) + MathF.Abs(value.M34) > 1e-6f || MathF.Abs(value.M44 - 1) > 1e-6f)
            throw new ArgumentException("Physics poses must be finite rigid affine transforms.", name);
        var rebuilt = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(orientation));
        float rotationError = Vector3.DistanceSquared(x, new(rebuilt.M11, rebuilt.M12, rebuilt.M13))
            + Vector3.DistanceSquared(y, new(rebuilt.M21, rebuilt.M22, rebuilt.M23))
            + Vector3.DistanceSquared(z, new(rebuilt.M31, rebuilt.M32, rebuilt.M33));
        if (!float.IsFinite(rotationError) || rotationError > 1e-8f)
            throw new ArgumentException("Physics pose rotation is not orthonormal.", name);
    }
}
