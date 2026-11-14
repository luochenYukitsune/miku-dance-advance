using System.Collections.ObjectModel;
using System.Numerics;

namespace Mmd.Modern.Core;

public enum SceneLightKind { Directional, Point, Spot }

/// <summary>
/// Format-neutral native light. Position and range use scene units, color is linear RGB,
/// and intensity is an artistic multiplier rather than an MMD or photometric unit.
/// Rotation orients local -Z in the direction that light travels; cone values are half angles.
/// A SceneLightingSnapshot validates records before they cross the rendering boundary.
/// </summary>
public sealed record NativeSceneLight(Guid Id, string Name, SceneLightKind Kind, Vector3 Position,
    Quaternion Rotation, Vector3 Color, float Intensity = 1, float Range = 10,
    float InnerConeRadians = .35f, float OuterConeRadians = .7f, bool Enabled = true, bool CastShadows = true)
{
    public Vector3 Direction => Vector3.Transform(-Vector3.UnitZ, Rotation);
}

/// <summary>Immutable world-space light inputs, independent of asset formats and GPU types.</summary>
public sealed class SceneLightingSnapshot
{
    /// <summary>Explicit first-version budget, including disabled lights; excess inputs are rejected.</summary>
    public const int MaxLights = 4;
    public static SceneLightingSnapshot Empty { get; } = new([]);
    public ReadOnlyCollection<NativeSceneLight> Lights { get; }

    public SceneLightingSnapshot(IEnumerable<NativeSceneLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);
        var copy = lights.Take(MaxLights + 1).ToArray();
        if (copy.Length > MaxLights)
            throw new ArgumentException($"At most {MaxLights} native scene lights are supported.", nameof(lights));
        var ids = new HashSet<Guid>();
        foreach (var light in copy)
        {
            if (light is null || light.Id == Guid.Empty || !ids.Add(light.Id)
                || string.IsNullOrWhiteSpace(light.Name) || !Enum.IsDefined(light.Kind))
                throw new ArgumentException("Lights require distinct nonempty IDs, names and supported kinds.", nameof(lights));
            float rotationLength = light.Rotation.LengthSquared();
            if (!Finite(light.Position) || !Finite(light.Rotation) || !float.IsFinite(rotationLength)
                || MathF.Abs(rotationLength - 1) > 1e-4f)
                throw new ArgumentException("Light transforms require finite positions and unit quaternions (squared-length tolerance 0.0001).", nameof(lights));
            if (!Finite(light.Color) || light.Color.X < 0 || light.Color.Y < 0 || light.Color.Z < 0
                || !float.IsFinite(light.Intensity) || light.Intensity < 0
                || !float.IsFinite(light.Range) || light.Range <= 0)
                throw new ArgumentException("Light colors and intensity must be finite and nonnegative; range must be finite and positive.", nameof(lights));
            if (!float.IsFinite(light.InnerConeRadians) || !float.IsFinite(light.OuterConeRadians)
                || light.InnerConeRadians < 0 || light.InnerConeRadians >= light.OuterConeRadians
                || light.OuterConeRadians > MathF.PI / 2)
                throw new ArgumentException("Light cone half angles require 0 <= inner < outer <= PI/2 radians.", nameof(lights));
        }
        Lights = Array.AsReadOnly(copy);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
