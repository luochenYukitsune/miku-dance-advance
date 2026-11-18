using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>
/// The single distance model of native lights. Received brightness falls smoothly from the light to zero at
/// Range: window(d) = (1 - (d/R)^2)^2, clamped to [0, 1]. Intensity is normalized at the aim distance, so the aim
/// target receives exactly Intensity (1 matches a fully lit main-light surface), and moving a light while it keeps
/// its aim distance keeps the target brightness. Directional lights do not attenuate.
/// Renderers use <see cref="RenderIntensity"/> with the unnormalized window; helpers use the same Range.
/// </summary>
public static class SceneLightFalloff
{
    /// <summary>The reference distance stays inside the range, where the window is still positive.</summary>
    public const float MaxReferenceFraction = .9f;

    /// <summary>Lights without an explicit aim use one sixth of their range, the placement used before aims existed.</summary>
    public static float DefaultAimDistance(float range) => range / 6;

    public static float Window(float distance, float range)
    {
        if (!(range > 0) || !float.IsFinite(distance)) return 0;
        float ratio = distance / range;
        float t = Math.Clamp(1 - ratio * ratio, 0, 1);
        return t * t;
    }

    /// <summary>Distance at which Intensity is received, clamped inside the range.</summary>
    public static float ReferenceDistance(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        return MathF.Min(light.EffectiveAimDistance, light.Range * MaxReferenceFraction);
    }

    /// <summary>Multiplier from authored Intensity to the renderer's unnormalized window units.</summary>
    public static float RenderScale(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        return light.Kind == SceneLightKind.Directional ? 1 : 1 / Window(ReferenceDistance(light), light.Range);
    }

    public static float RenderIntensity(NativeSceneLight light)
    {
        float result = light.Intensity * RenderScale(light);
        if (!float.IsFinite(result))
            throw new ArgumentException("A light intensity exceeds the supported range after aim normalization.", nameof(light));
        return result;
    }

    /// <summary>Brightness received at a distance, before cone, incidence and shadow terms.</summary>
    public static float Received(NativeSceneLight light, float distance) => light.Kind == SceneLightKind.Directional
        ? light.Intensity : RenderIntensity(light) * Window(distance, light.Range);

    /// <summary>Converts a light saved before aims existed (I / d^2 inside a quartic range window). Brightness is
    /// matched at the default aim distance, which is where the old placement put the subject; nearer points get
    /// less, farther points more light than before. Directional lights are unchanged.</summary>
    public static NativeSceneLight FromLegacy(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        if (light.Kind == SceneLightKind.Directional) return light;
        double aim = light.EffectiveAimDistance, ratio = aim / light.Range;
        double legacy = Math.Max(1 - ratio * ratio * ratio * ratio, 0) / (aim * aim);
        var result = light with { Intensity = (float)(light.Intensity * legacy) };
        SceneLightingSnapshot.ValidateLight(result);
        return result;
    }
}

/// <summary>Aims lights at world targets. Inputs and results are world-space; parented callers convert with
/// <see cref="SceneLightTransform"/>, which keeps AimDistance in scene units like Range.</summary>
public static class SceneLightAim
{
    /// <summary>Turns a light toward a target along the shortest arc (keeping its roll) and stores the target
    /// distance. Range grows when the target would leave the lit range; Intensity, and with it the target
    /// brightness, is unchanged. Point lights keep their rotation and only take the reference distance.</summary>
    public static NativeSceneLight AimAt(NativeSceneLight light, Vector3 target)
    {
        ArgumentNullException.ThrowIfNull(light);
        var offset = target - light.Position;
        float distance = offset.Length();
        if (!float.IsFinite(distance) || distance < 1e-4f)
            throw new ArgumentException("A light target must be finite and away from the light.", nameof(target));
        var rotation = light.Kind == SceneLightKind.Point ? light.Rotation
            : Quaternion.Normalize(Arc(light.Direction, offset / distance) * light.Rotation);
        float range = distance > light.Range * SceneLightFalloff.MaxReferenceFraction
            ? distance / SceneLightFalloff.MaxReferenceFraction : light.Range;
        var result = light with { Rotation = rotation, AimDistance = distance, Range = range };
        SceneLightingSnapshot.ValidateLight(result);
        return result;
    }

    /// <summary>Rotation taking unit-ish <paramref name="from"/> to unit <paramref name="to"/> along the shortest arc.</summary>
    public static Quaternion Arc(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float dot = Vector3.Dot(from, to);
        if (dot > 1 - 1e-7f) return Quaternion.Identity;
        if (dot < -1 + 1e-6f)
        {
            var axis = Vector3.Cross(from, Vector3.UnitY);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(from, Vector3.UnitX);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + dot));
    }
}
