using System.Numerics;
using System.Text.Json.Serialization;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public enum ProjectPhysicsBackend { HostDefault, Bullet, Bepu }

public sealed record ProjectPhysicsSettings
{
    public ProjectPhysicsBackend Backend { get; init; }
    public bool SharedWorld { get; init; }
    /// <summary>Attach PMX soft content to the actual rigid owner. The selected host backend must supply the coupled capability.</summary>
    public bool CoupledSoftBodies { get; init; }
    public float GravityX { get; init; }
    public float GravityY { get; init; } = -98;
    public float GravityZ { get; init; }
    public bool GroundEnabled { get; init; } = true;
    [JsonIgnore] public Vector3 Gravity => new(GravityX, GravityY, GravityZ);
    public void Validate()
    {
        if (!Enum.IsDefined(Backend) || !float.IsFinite(GravityX) || !float.IsFinite(GravityY) || !float.IsFinite(GravityZ)
            || Math.Abs(GravityX) > 1_000_000 || Math.Abs(GravityY) > 1_000_000 || Math.Abs(GravityZ) > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(GravityY), "Physics backend and bounded finite gravity are required.");
    }
}

/// <summary>Core settings. Output FPS never changes the fixed 30 FPS MMD animation clock.</summary>
public sealed record ProjectSettings
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FpsNumerator { get; init; } = 30;
    public int FpsDenominator { get; init; } = 1;
    public ProjectPhysicsSettings Physics { get; init; } = new();
    public bool SelfShadowEnabled { get; init; } = true;
    public bool GroundShadowEnabled { get; init; } = true;
    public bool OutlineEnabled { get; init; } = true;
    public float BackgroundRed { get; init; }
    public float BackgroundGreen { get; init; }
    public float BackgroundBlue { get; init; }
    public float BackgroundAlpha { get; init; } = 1;
    [JsonIgnore] public Vector4 Background => new(BackgroundRed, BackgroundGreen, BackgroundBlue, BackgroundAlpha);
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Physics); Physics.Validate();
        _ = CreateOutputPlan(0, 0);
        foreach (float value in new[] { BackgroundRed, BackgroundGreen, BackgroundBlue, BackgroundAlpha })
            if (!float.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(BackgroundRed));
    }
    public OfflineFramePlan CreateOutputPlan(uint startFrame, uint endFrame, uint preRoll = 0)
        => new(Width, Height, new(FpsNumerator, FpsDenominator), startFrame, endFrame, preRoll);
}

/// <summary>Host supplies concrete Bullet/Bepu constructors. It must honor all settings or reject them,
/// including a provider-capable owner when CoupledSoftBodies is selected; silently falling back to
/// another solver is not a valid implementation.</summary>
public interface IProjectPhysicsBackendProvider
{
    /// <summary>Host policy for fixed-pose solver initialization, without advancing the animation clock.
    /// Zero retains the ordinary reset-at-pose contract. This policy is not serialized in the project.</summary>
    int PoseInitializationSubsteps => 0;
    ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item);
    ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings);
    ISoftBodyBackend CreateSoftBody(ProjectPhysicsSettings settings, EditableSceneObject item)
        => throw new NotSupportedException("The host must provide an actual PMX soft-body backend.");
}

/// <summary>Executable adapter for explicit typed backend constructors, without adding native physics dependencies to Application.</summary>
public sealed class ProjectSettingsBackendProvider(
    Func<ProjectPhysicsSettings, EditableSceneObject, ICharacterPhysicsBackend> independent,
    Func<ProjectPhysicsSettings, ICharacterPhysicsMutableBackend> shared,
    Func<ProjectPhysicsSettings, EditableSceneObject, ISoftBodyBackend>? softBody = null,
    int poseInitializationSubsteps = 0) : IProjectPhysicsBackendProvider
{
    private readonly Func<ProjectPhysicsSettings, EditableSceneObject, ICharacterPhysicsBackend> independent = independent ?? throw new ArgumentNullException(nameof(independent));
    private readonly Func<ProjectPhysicsSettings, ICharacterPhysicsMutableBackend> shared = shared ?? throw new ArgumentNullException(nameof(shared));
    public int PoseInitializationSubsteps { get; } = poseInitializationSubsteps is >= 0 and <= 240
        ? poseInitializationSubsteps : throw new ArgumentOutOfRangeException(nameof(poseInitializationSubsteps));
    public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
    { settings.Validate(); return independent(settings, item) ?? throw new InvalidOperationException("Physics provider returned no backend."); }
    public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
    { settings.Validate(); return shared(settings) ?? throw new InvalidOperationException("Physics provider returned no shared backend."); }
    public ISoftBodyBackend CreateSoftBody(ProjectPhysicsSettings settings, EditableSceneObject item)
    { settings.Validate(); return softBody?.Invoke(settings, item) ?? throw new NotSupportedException("The host has not supplied a PMX soft-body backend."); }
}
