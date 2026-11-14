using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Optional secondary-hit lights; independent of the accepted material/triangle ABI.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRayGiLight(Vector4 PositionKind, Vector4 DirectionRange,
    Vector4 ColorIntensity, Vector4 ConeShadow);

internal sealed class NativeRayGiLights
{
    private static readonly ConditionalWeakTable<SceneLightingSnapshot, NativeRayGiLights> Cache = new();
    private static readonly NativeRayGiLights Empty = new(SceneLightingSnapshot.Empty);
    internal NativeRayGiLight[] Values { get; }
    internal static NativeRayGiLights From(OriginalFrameScene frame)
        => frame.Lighting is { } lighting ? Cache.GetValue(lighting, static value => new(value)) : Empty;

    private NativeRayGiLights(SceneLightingSnapshot lighting)
    {
        Values = lighting.Lights.Where(l => l.Enabled && l.Intensity > 0 && l.Color != Vector3.Zero).Select(l =>
            new NativeRayGiLight(new(l.Position, (float)l.Kind), new(NativeLightShadowProjection.Direction(l), l.Range),
                new(l.Color, l.Intensity), new(MathF.Cos(l.InnerConeRadians), MathF.Cos(l.OuterConeRadians), l.CastShadows ? 1 : 0, 0))).ToArray();
    }

    /// <summary>Conservative unscaled bound matching the shader's minimum squared distance, in artistic units.
    /// The nonnegative sum omits cosine/pi (at most 1), bounding both each colour-first
    /// product and the total incident radiance before any DirectScale multiplication.</summary>
    internal static double IrradianceBound(OriginalFrameScene frame)
    {
        double bound = 0;
        foreach (var light in frame.Lighting?.Lights ?? SceneLightingSnapshot.Empty.Lights)
        {
            if (!light.Enabled || light.Intensity == 0 || light.Color == Vector3.Zero) continue;
            double maximum = Math.Max(light.Color.X, Math.Max(light.Color.Y, light.Color.Z));
            bound += maximum * light.Intensity * (light.Kind == SceneLightKind.Directional ? 1 : 10000);
        }
        return bound;
    }
}
