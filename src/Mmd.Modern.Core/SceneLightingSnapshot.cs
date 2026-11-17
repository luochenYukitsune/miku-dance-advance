using System.Collections.ObjectModel;
using System.Numerics;

namespace Mmd.Modern.Core;

public enum SceneLightKind { Directional, Point, Spot, Area }

/// <summary>A stable scene object and, optionally, a model bone used as a light's transform parent.</summary>
public sealed record SceneLightParent(Guid ObjectId, int? BoneIndex = null);

/// <summary>
/// Format-neutral native light. Position and range use scene units, color is linear RGB,
/// and intensity is an artistic multiplier rather than an MMD or photometric unit.
/// Rotation orients local -Z in the direction that light travels; cone values are half angles.
/// Position and rotation are local to Parent when present; range and AreaSize never inherit parent scaling.
/// AreaSize is the full width/height of an area's local XY emitting rectangle, facing local -Z.
/// A SceneLightingSnapshot validates records before they cross the rendering boundary.
/// </summary>
public sealed record NativeSceneLight(Guid Id, string Name, SceneLightKind Kind, Vector3 Position,
    Quaternion Rotation, Vector3 Color, float Intensity = 1, float Range = 10,
    float InnerConeRadians = .35f, float OuterConeRadians = .7f, bool Enabled = true, bool CastShadows = true,
    Guid? GroupId = null, SceneLightParent? Parent = null, SceneLightReceivers? Receivers = null,
    Vector2? AreaSize = null)
{
    public Vector3 Direction => Vector3.Transform(-Vector3.UnitZ, Rotation);
}

/// <summary>A single-level authoring group. Its enable switch and brightness multiplier never overwrite member values.</summary>
public sealed record SceneLightGroup(Guid Id, string Name, bool Enabled = true, float Intensity = 1);

/// <summary>Immutable authored light inputs, independent of asset formats and GPU types.</summary>
public sealed partial class SceneLightingSnapshot
{
    /// <summary>Authoring budget, including disabled lights; excess inputs are rejected.</summary>
    public const int MaxLights = 128;
    /// <summary>Effective enable-switch budget after group switches. Zero-intensity and black lights still count.</summary>
    public const int MaxActiveLights = 32;
    public static SceneLightingSnapshot Empty { get; } = new([]);
    /// <summary>Original authored values, including each member's own enable switch and intensity.</summary>
    public ReadOnlyCollection<NativeSceneLight> Lights { get; }
    public ReadOnlyCollection<SceneLightGroup> Groups { get; }
    public SceneLightingSettings Settings { get; }
    /// <summary>All lights in authored order, with group enable switches and intensity multipliers resolved.
    /// Disabled lights remain present; this is a render input, not a replacement for authored Lights.</summary>
    public ReadOnlyCollection<NativeSceneLight> EffectiveLights { get; }

    public SceneLightingSnapshot(IEnumerable<NativeSceneLight> lights, IEnumerable<SceneLightGroup>? groups = null,
        SceneLightingSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(lights);
        Settings = settings ?? SceneLightingSettings.Default;
        SceneLightingSettings.Validate(Settings);
        var copy = lights.Take(MaxLights + 1).ToArray();
        if (copy.Length > MaxLights)
            throw new ArgumentException($"At most {MaxLights} native scene lights are supported.", nameof(lights));
        var groupCopy = (groups ?? []).ToArray();
        var groupById = new Dictionary<Guid, SceneLightGroup>();
        foreach (var group in groupCopy)
        {
            if (group is null || group.Id == Guid.Empty || string.IsNullOrWhiteSpace(group.Name)
                || !float.IsFinite(group.Intensity) || group.Intensity < 0 || !groupById.TryAdd(group.Id, group))
                throw new ArgumentException("Light groups require distinct nonempty IDs, names and finite nonnegative intensity multipliers.", nameof(groups));
        }
        var effective = new NativeSceneLight[copy.Length];
        var ids = new HashSet<Guid>();
        for (int i = 0; i < copy.Length; i++)
        {
            var light = copy[i];
            if (light is null) throw new ArgumentException("A light entry cannot be null.", nameof(lights));
            ValidateLight(light);
            if (!ids.Add(light.Id))
                throw new ArgumentException("Lights require distinct IDs.", nameof(lights));
            effective[i] = light;
            if (light.GroupId is not { } groupId) continue;
            if (!groupById.TryGetValue(groupId, out var group))
                throw new ArgumentException("A light's group must exist in the same lighting snapshot.", nameof(lights));
            float intensity = light.Intensity * group.Intensity;
            // Validate disabled members too: toggling a group must not expose a latent overflow.
            if (!float.IsFinite(intensity))
                throw new ArgumentException("A light intensity multiplied by its group intensity must remain finite.", nameof(lights));
            effective[i] = light with { Enabled = light.Enabled && group.Enabled, Intensity = intensity };
        }
        if (effective.Count(light => light.Enabled) > MaxActiveLights)
            throw new ArgumentException($"At most {MaxActiveLights} native scene lights may be enabled after group switches are applied.", nameof(lights));
        Lights = Array.AsReadOnly(copy);
        Groups = Array.AsReadOnly(groupCopy);
        EffectiveLights = Array.AsReadOnly(effective);
    }

    /// <summary>Replaces authored lights while retaining groups and project-wide lighting settings.</summary>
    public SceneLightingSnapshot WithLights(IEnumerable<NativeSceneLight> lights) => new(lights, Groups, Settings);
    /// <summary>Replaces groups while retaining authored lights and project-wide lighting settings.</summary>
    public SceneLightingSnapshot WithGroups(IEnumerable<SceneLightGroup> groups) => new(Lights, groups, Settings);
    public SceneLightingSnapshot WithSettings(SceneLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(Lights, Groups, settings);
    }

    /// <summary>Validates one authored light without requiring its group collection. Scene snapshots additionally
    /// validate distinct identities, membership and effective intensity. Suitable for an individual light inspector.</summary>
    public static void ValidateLight(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        if (light.Id == Guid.Empty || string.IsNullOrWhiteSpace(light.Name) || !Enum.IsDefined(light.Kind)
            || light.GroupId == Guid.Empty)
            throw new ArgumentException("Lights require nonempty IDs and names, a supported kind and a nonempty optional group ID.", nameof(light));
        if (light.Parent is { } parent && (parent.ObjectId == Guid.Empty || parent.BoneIndex < 0))
            throw new ArgumentException("A light parent requires a nonempty object ID and an optional nonnegative bone index.", nameof(light));
        if ((light.Kind == SceneLightKind.Area && light.AreaSize is null)
            || light.AreaSize is { } areaSize && (!float.IsFinite(areaSize.X) || !float.IsFinite(areaSize.Y) || areaSize.X <= 0 || areaSize.Y <= 0))
            throw new ArgumentException("Area lights require finite positive width and height; optional retained area dimensions must also be valid.", nameof(light));
        float rotationLength = light.Rotation.LengthSquared();
        if (!Finite(light.Position) || !Finite(light.Rotation) || !float.IsFinite(rotationLength)
            || MathF.Abs(rotationLength - 1) > 1e-4f)
            throw new ArgumentException("Light transforms require finite positions and unit quaternions (squared-length tolerance 0.0001).", nameof(light));
        if (!Finite(light.Color) || light.Color.X < 0 || light.Color.Y < 0 || light.Color.Z < 0
            || !float.IsFinite(light.Intensity) || light.Intensity < 0
            || !float.IsFinite(light.Range) || light.Range <= 0)
            throw new ArgumentException("Light colors and intensity must be finite and nonnegative; range must be finite and positive.", nameof(light));
        if (!float.IsFinite(light.InnerConeRadians) || !float.IsFinite(light.OuterConeRadians)
            || light.InnerConeRadians < 0 || light.InnerConeRadians >= light.OuterConeRadians
            || light.OuterConeRadians > MathF.PI / 2)
            throw new ArgumentException("Light cone half angles require 0 <= inner < outer <= PI/2 radians.", nameof(light));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
