using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Optional secondary-hit lights; independent of the accepted material/triangle ABI.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRayGiLight(Vector4 PositionKind, Vector4 DirectionRange,
    Vector4 ColorIntensity, Vector4 ConeShadow, Vector4 AreaRightTile);

internal sealed class NativeRayGiLights
{
    private static readonly ConditionalWeakTable<SceneLightingSnapshot, NativeRayGiLights> Cache = new();
    private static readonly ConditionalWeakTable<OriginalFrameScene, NativeRayGiLights> ReceiverCache = new();
    private static readonly NativeRayGiLights Empty = new(SceneLightingSnapshot.Empty);
    internal NativeRayGiLight[] Values { get; }
    internal NativeRayGiLight[] UploadRows { get; }
    internal bool HasReceiverMasks { get; }
    internal float EnvironmentStrength { get; }
    internal bool HasContribution => Values.Length > 0 || EnvironmentStrength > 0;
    internal static NativeRayGiLights From(OriginalFrameScene frame)
        => frame.Lighting is not { } lighting ? Empty
            : lighting.EffectiveLights.Any(light => light.Receivers is not null)
                ? ReceiverCache.GetValue(frame, static value => new(value.Lighting!, value))
                : Cache.GetValue(lighting, static value => new(value));

    private NativeRayGiLights(SceneLightingSnapshot lighting, OriginalFrameScene? receivers = null)
    {
        EnvironmentStrength = lighting.Settings.EnvironmentStrength;
        foreach (var light in lighting.EffectiveLights.Where(NativeLightShadowAtlasLayout.Contributes)) NativeAreaLight.Validate(light);
        Values = lighting.EffectiveLights.Where(l => l.Enabled && l.Intensity > 0 && l.Color != Vector3.Zero).Select(l =>
            new NativeRayGiLight(new(l.Position, (float)l.Kind), new(NativeLightShadowProjection.Direction(l), l.Range),
                new(l.Color, SceneLightFalloff.RenderIntensity(l)), NativeAreaLight.Cone(l, l.CastShadows ? 1 : 0),
                new(l.Kind == SceneLightKind.Area ? NativeAreaLight.Right(l) : Vector3.Zero, 0))).ToArray();
        HasReceiverMasks = receivers is not null && Values.Length > 0;
        // Optional material masks share the existing native-light SRV, following its light rows.
        // The optional 80-byte light rows do not change the accepted GI geometry/material ABIs.
        UploadRows = !HasReceiverMasks ? Values : [..Values, ..receivers!.Materials.Select(flags =>
            new NativeRayGiLight(new(BitConverter.UInt32BitsToSingle(lighting.ReceiverMask(flags.LightReceiverObjectId)),0,0,0), default, default, default, default))];
        if (UploadRows.Length == 0 && EnvironmentStrength > 0) UploadRows = [default];
    }

    /// <summary>Conservative unscaled bound: the shared falloff window never exceeds one, so each light contributes
    /// at most its aim-normalized render intensity. The nonnegative sum omits cosine/pi (at most 1), bounding both each colour-first
    /// product and the total incident radiance before any DirectScale multiplication.</summary>
    internal static double IrradianceBound(OriginalFrameScene frame)
    {
        double bound = frame.Lighting?.Settings.EnvironmentStrength ?? 0;
        foreach (var light in frame.Lighting?.EffectiveLights ?? SceneLightingSnapshot.Empty.EffectiveLights)
        {
            if (!light.Enabled || light.Intensity == 0 || light.Color == Vector3.Zero) continue;
            if (light.Receivers is { } receivers && !frame.Materials.Any(flags => receivers.Affects(flags.LightReceiverObjectId))) continue;
            double maximum = Math.Max(light.Color.X, Math.Max(light.Color.Y, light.Color.Z));
            bound += maximum * light.Intensity * SceneLightFalloff.RenderScale(light);
        }
        return bound;
    }
}
