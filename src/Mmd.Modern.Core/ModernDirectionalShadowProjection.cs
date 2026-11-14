using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>A selected modern scene fit, separate from the original host's projective light warp.
/// Returns deformed model-position to light-clip (World included once), using all referenced
/// geometry including camera-invisible casters. Scene LightDirection points toward the light.</summary>
public static class ModernDirectionalShadowProjection
{
    public static Matrix4x4 Fit(SceneSnapshot scene, float paddingFraction = .03f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(paddingFraction) || paddingFraction is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(paddingFraction));

        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        foreach (uint index in scene.Indices)
        {
            var position = Vector3.Transform(scene.Vertices[(int)index].Position, scene.World);
            minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
        }
        Vector3 extent = maximum - minimum, center = minimum * .5f + maximum * .5f;
        float radius = extent.Length() * .5f;
        if (!Finite(center) || !float.IsFinite(radius))
            throw new ArgumentException("Scene bounds exceed finite shadow projection range.", nameof(scene));

        Vector3 towardLight = Vector3.Normalize(scene.LightDirection), z = -towardLight;
        Vector3 up = MathF.Abs(z.Z) < .99f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, z)), y = Vector3.Cross(z, x);
        Vector3 eye = center + towardLight * (radius + 1);
        var view = new Matrix4x4(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0,
            -Vector3.Dot(x, eye), -Vector3.Dot(y, eye), -Vector3.Dot(z, eye), 1);

        Vector3 lightMin = new(float.MaxValue), lightMax = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var position = new Vector3((corner & 1) == 0 ? minimum.X : maximum.X,
                (corner & 2) == 0 ? minimum.Y : maximum.Y, (corner & 4) == 0 ? minimum.Z : maximum.Z);
            var lightPosition = Vector3.Transform(position, view);
            lightMin = Vector3.Min(lightMin, lightPosition); lightMax = Vector3.Max(lightMax, lightPosition);
        }
        Vector3 padding = Vector3.Max((lightMax - lightMin) * paddingFraction, new Vector3(.01f));
        lightMin -= padding; lightMax += padding;
        var range = lightMax - lightMin;
        var projection = new Matrix4x4(2 / range.X, 0, 0, 0, 0, 2 / range.Y, 0, 0,
            0, 0, 1 / range.Z, 0, -(lightMax.X + lightMin.X) / range.X,
            -(lightMax.Y + lightMin.Y) / range.Y, -lightMin.Z / range.Z, 1);
        var result = scene.World * view * projection;
        if (!Finite(lightMin) || !Finite(lightMax) || !Finite(result) || !Matrix4x4.Invert(result, out _))
            throw new ArgumentException("Scene cannot produce an invertible finite shadow projection.", nameof(scene));
        return result;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
