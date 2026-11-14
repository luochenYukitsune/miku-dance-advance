using System.Numerics;

namespace Mmd.Modern.Physics;

/// <summary>Optional capability of an explicitly selected numerical owner. Slots
/// map actor-local rigid indices into the owner's stable body table.</summary>
public interface ICharacterSoftBodyProvider
{
    ICharacterSoftBodyBinding CreateSoftBodyBinding(IReadOnlyList<int> rigidBodySlots);
}

/// <summary>Owns attached soft content, borrowing one rigid-soft numerical world.
/// Only the owner advances time. Positions are in numerical-world coordinates.
/// Owner reinitialization/disposal invalidates all outstanding bindings.</summary>
public interface ICharacterSoftBodyBinding : IDisposable
{
    IReadOnlyList<string> Diagnostics { get; }
    /// <summary>Build a complete candidate before replacing existing content.</summary>
    void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, ReadOnlySpan<Vector3> worldSkinnedPositions);
    void SetTargets(ReadOnlySpan<Vector3> worldSkinnedPositions);
    IReadOnlyList<SoftBodySample> Sample();
    /// <summary>Retain collider presence and saved solver history while suspending this actor.</summary>
    void SetFrozen(bool frozen);
    /// <summary>Whether arbitrary frozen collision geometry can be updated without changing saved solver history.</summary>
    bool SupportsFrozenCollisionSurface => false;
    /// <summary>Validate a complete proposed collision surface without changing nodes, frozen state or history.
    /// Valid before the binding is frozen, so a shared owner can preflight every actor atomically.</summary>
    void ValidateFrozenCollisionSurface(ReadOnlySpan<Vector3> worldPositions)
        => throw new NotSupportedException("This soft binding cannot preflight a frozen collision surface.");
    /// <summary>Atomically replace the live collision surface of a frozen binding in global vertex order.
    /// Saved solver history remains unchanged; reset/seek is required to resume at the deformed pose.</summary>
    void SetFrozenCollisionSurface(ReadOnlySpan<Vector3> worldPositions)
        => throw new NotSupportedException("This soft binding cannot update a nonuniform frozen collision surface.");
    /// <summary>Rigid affine change of world frame, preserving node and velocity history.
    /// The caller rebases associated rigid bodies by the same delta.</summary>
    void Rebase(Matrix4x4 worldDelta);
}
