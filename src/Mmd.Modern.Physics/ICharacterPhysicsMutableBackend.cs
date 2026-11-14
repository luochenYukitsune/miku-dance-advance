using System.Numerics;

namespace Mmd.Modern.Physics;

/// <summary>Incremental membership in one initialized solver. Slots are stable and never reused until Initialize.
/// Removing or resetting one slot must preserve every unrelated body's pose, velocity and solver state.</summary>
public interface ICharacterPhysicsMutableBackend : ICharacterPhysicsBackend, ICharacterImpulseBackend, ICharacterGravityBackend
{
    /// <summary>Adds an authored body with a world-space initial pose. Returns a stable backend slot.</summary>
    int AddBody(PhysicsBodyDefinition body, Matrix4x4 worldTransform);
    /// <summary>BodyA/BodyB are stable backend slots; the two frames are exact body-local rigid matrices.
    /// Returns a stable constraint slot. Unsupported joint types throw; no silent conversion.</summary>
    int AddJoint(PhysicsJointDefinition joint, Matrix4x4 localFrameA, Matrix4x4 localFrameB);
    void RemoveJoint(int jointIndex);
    /// <summary>Caller removes referencing joints first. A removed slot is invalid thereafter.</summary>
    void RemoveBody(int bodyIndex);
    /// <summary>Teleports only this body, clearing its momentum and contact pairs.</summary>
    void ResetBody(int bodyIndex, Matrix4x4 worldTransform);
    /// <summary>Temporarily treats an authored dynamic body as kinematic. True/false transitions clear its
    /// momentum but retain authored mass, inertia and collision filters. Authored kinematic bodies remain so.
    /// Reapplying an unchanged state must not clear velocity or alter the pose.</summary>
    void SetBodyKinematic(int bodyIndex, bool kinematic);
}
