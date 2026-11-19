using System.Numerics;

namespace Mmd.Modern.Physics;

/// <summary>Replaceable numerical solver. Owns its simulation and is owned/disposed by the session.</summary>
public interface ICharacterPhysicsBackend : IDisposable
{
    string Name { get; }
    IReadOnlyList<string> Diagnostics { get; }
    bool SupportsConcurrentIndependentWorlds => false;
    void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms);
    void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds);
    Matrix4x4 GetBodyWorldTransform(int bodyIndex);
    void Step(float stepSeconds);
}

/// <summary>Optional capability. Vectors are world-space momentum impulses, never velocities or forces.
/// Static/kinematic bodies ignore a valid impulse; invalid indices/nonfinite values must fail before mutation.</summary>
public interface ICharacterImpulseBackend
{
    void ApplyImpulse(int bodyIndex, Vector3 linearImpulse, Vector3 angularImpulse);
}

/// <summary>Optional world-space acceleration control. A valid update preserves poses, velocities,
/// contact history and the fixed-step clock; it also applies after subsequent initialization.</summary>
public interface ICharacterGravityBackend
{
    void SetGravity(Vector3 gravity);
}

/// <summary>Optional MMD pull synchronization. Called in authored joint order before bone reflection.
/// When separation exceeds maximumDistance by at least two, pull B toward A and clear only B's velocities.
/// Backends retaining Saba reflection need not implement this capability.</summary>
public interface ICharacterPhysicsJointPullBackend
{
    /// <summary>Validates native support before a shared actor candidate changes world membership.</summary>
    void ValidateJointPullSupport();
    void CorrectJointOverextension(int bodyA, int bodyB, float maximumDistance);
}

/// <summary>A sampled impulse reapplied once per fixed step. Local vectors use the body's current orientation.</summary>
public readonly record struct CharacterBodyImpulse(int BodyIndex, bool Local, Vector3 Linear, Vector3 Angular);

public readonly record struct PhysicsKinematicTargetBatchItem(int BodyIndex, bool Kinematic, Matrix4x4 WorldTransform);
public readonly record struct PhysicsJointPullBatchItem(int BodyA, int BodyB, float MaximumDistance);

/// <summary>Optional batched FFI capability that reduces per-body and per-joint cross-boundary calls.</summary>
public interface ICharacterPhysicsBatchedBackend : ICharacterPhysicsMutableBackend
{
    bool SupportsBatchedExecution => true;
    void ApplyKinematicTargetsBatch(ReadOnlySpan<PhysicsKinematicTargetBatchItem> targets, float stepSeconds);
    void CorrectJointsAndGetBodyTransforms(ReadOnlySpan<PhysicsJointPullBatchItem> joints, ReadOnlySpan<int> bodyIndices, Span<Matrix4x4> worldTransforms);
}
