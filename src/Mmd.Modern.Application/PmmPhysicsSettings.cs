using System.Numerics;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public readonly record struct PmmEffectivePhysicsSettings(bool Enabled, Vector3 Gravity, bool GroundEnabled);

/// <summary>Converts PMM gravity controls to the original physics world's units.</summary>
public static class PmmPhysicsSettings
{
    /// <summary>V1 does not serialize these fields. Apply explicit Modern defaults without mutating
    /// its neutral raw metadata or claiming the defaults are original MMD behavior.</summary>
    public static PmmEffectivePhysicsSettings Resolve(PmmDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Version == 1 ? new(true, new(0, -98, 0), true)
            : new(document.Settings.PhysicsSimulationMode != 0,
                ToGravity(document.Gravity.Acceleration, document.Gravity.Direction), document.Settings.PhysicsGroundEnabled);
    }

    public static Vector3 ToGravity(float acceleration, Vector3 direction)
    {
        if (!float.IsFinite(acceleration))
            throw new ArgumentOutOfRangeException(nameof(acceleration), "PMM gravity acceleration must be finite.");
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z))
            throw new ArgumentOutOfRangeException(nameof(direction), "PMM gravity direction must be finite.");
        // Preserve a zero direction from imported metadata as zero gravity. MMD's edit control
        // substitutes Y=0.1 when the user clears every axis; that UI mutation is not reproduced here.
        if (acceleration == 0 || direction == Vector3.Zero) return Vector3.Zero;

        // Double precision keeps both the largest and smallest finite float directions normalizable.
        double x = direction.X, y = direction.Y, z = direction.Z;
        double length = Math.Sqrt(x * x + y * y + z * z);
        var normalized = new Vector3((float)(x / length), (float)(y / length), (float)(z / length));
        // Original VA 0x140095b30 applies normalized direction * acceleration * 10;
        // float constant at VA 0x140132980 is 0x41200000 (gravity-constant.json records the source SHA).
        var gravity = normalized * acceleration * 10f;
        if (!float.IsFinite(gravity.X) || !float.IsFinite(gravity.Y) || !float.IsFinite(gravity.Z))
            throw new ArgumentOutOfRangeException(nameof(acceleration), "PMM gravity exceeds the physics world's finite range.");
        return gravity;
    }
}
