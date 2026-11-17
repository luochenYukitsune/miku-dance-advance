using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Bounded two-by-two Gauss quadrature of a one-sided rectangular emitter.
/// Intensity is total artistic power, so each of the four samples has weight one quarter.</summary>
internal static class NativeAreaLight
{
    internal const int Samples = 4;
    internal static void Validate(NativeSceneLight light)
    {
        if (light.Kind != SceneLightKind.Area) return;
        for (int sample = 0; sample < Samples; sample++) _ = SamplePosition(light, sample);
    }
    internal static Vector3 Right(NativeSceneLight light) => Vector3.Transform(Vector3.UnitX, light.Rotation);
    internal static Vector3 SamplePosition(NativeSceneLight light, int sample)
    {
        if ((uint)sample >= Samples) throw new ArgumentOutOfRangeException(nameof(sample));
        var size = light.AreaSize!.Value;
        float x = ((sample & 1) == 0 ? -1 : 1) * size.X * .2886751345948129f;
        float y = ((sample & 2) == 0 ? -1 : 1) * size.Y * .2886751345948129f;
        var result = light.Position + Vector3.Transform(new Vector3(x, y, 0), light.Rotation);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
            throw new ArgumentException("The area light's emitter exceeds the finite coordinate range.");
        return result;
    }
    internal static Vector4 Cone(NativeSceneLight light, float shadow, float bias = 0)
        => light.Kind == SceneLightKind.Area ? new(light.AreaSize!.Value * .5f, shadow, 0)
            : new(MathF.Cos(light.InnerConeRadians), MathF.Cos(light.OuterConeRadians), shadow, bias);
}
