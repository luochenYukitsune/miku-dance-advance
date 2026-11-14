using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Physics.Bepu;
using Mmd.Modern.Physics.Bullet;

namespace Mmd.Modern.Physics.Runtime;

/// <summary>Core-owned physics composition. Bullet is the maintained default; Bepu remains an
/// explicit experimental backend. Each call creates an independent owner without starting a solver.</summary>
public static class ProjectPhysicsRuntime
{
    /// <summary>Creates a provider whose backend calls each return a fresh owner. Pose initialization
    /// is host policy and does not advance the authored animation clock.</summary>
    public static IProjectPhysicsBackendProvider CreateProjectProvider(int poseInitializationSubsteps = 120)
        => new ProjectSettingsBackendProvider(
            (settings, _) => CreateRigid(settings), CreateRigid,
            (settings, _) => CreateSoftBody(settings), poseInitializationSubsteps);

    /// <summary>Creates a rigid owner from validated project settings, without initializing a world.</summary>
    public static ICharacterPhysicsMutableBackend CreateRigid(ProjectPhysicsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return CreateRigid(settings.Backend, settings.Gravity, settings.GroundEnabled ? 0 : null,
            settings.CoupledSoftBodies);
    }

    /// <summary>Creates the explicitly selected owner. HostDefault resolves to Bullet, and unsupported
    /// selections fail before any backend is constructed.</summary>
    public static ICharacterPhysicsMutableBackend CreateRigid(ProjectPhysicsBackend backend,
        Vector3? gravity = null, float? groundY = 0, bool coupledSoftBodies = false)
        => backend switch
        {
            ProjectPhysicsBackend.HostDefault or ProjectPhysicsBackend.Bullet
                => new BulletCharacterPhysicsBackend(gravity, groundY, coupledSoftBodies),
            ProjectPhysicsBackend.Bepu when !coupledSoftBodies
                => new BepuCharacterPhysicsBackend(gravity, groundY),
            ProjectPhysicsBackend.Bepu
                => throw new ProjectPhysicsCapabilityException(ProjectPhysicsBackend.Bepu, "coupledSoftBodies",
                    "Bepu does not support coupled soft bodies. Select Bullet."),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown physics backend.")
        };

    // Standalone soft worlds retain the existing Bullet strategy, including alongside Bepu rigid
    // worlds. Coupled soft content is supplied by the rigid owner's capability, under session control.
    private static ISoftBodyBackend CreateSoftBody(ProjectPhysicsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return new SoftBodyBulletBackend(settings.Gravity, settings.GroundEnabled ? 0 : null);
    }
}

/// <summary>A neutral failure identity lets hosts explain unsupported settings in their own language.</summary>
public sealed class ProjectPhysicsCapabilityException(ProjectPhysicsBackend backend, string capability, string message)
    : NotSupportedException(message)
{
    public ProjectPhysicsBackend Backend { get; } = backend;
    public string Capability { get; } = capability;
}
