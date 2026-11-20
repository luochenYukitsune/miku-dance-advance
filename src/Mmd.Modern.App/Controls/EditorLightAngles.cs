using System.Numerics;

namespace Mmd.Modern.App.Controls;

/// <summary>
/// Converts the main light's travel direction to and from the angles of its source, as seen
/// from the default MMD camera (which looks along +Z):
/// <list type="bullet">
/// <item>azimuth 0° = front (camera side), 90° = right, 180° = behind, 270° = left;</item>
/// <item>elevation 90° = straight above, 0° = level, negative = from below.</item>
/// </list>
/// The stored vector keeps its original length so only the direction changes.
/// </summary>
internal static class EditorLightAngles
{
    internal static Vector3 Direction(double azimuthDegrees, double elevationDegrees, float length)
    {
        double azimuth = azimuthDegrees * Math.PI / 180, elevation = Math.Clamp(elevationDegrees, -90, 90) * Math.PI / 180;
        var source = new Vector3((float)(Math.Cos(elevation) * Math.Sin(azimuth)), (float)Math.Sin(elevation),
            (float)(-Math.Cos(elevation) * Math.Cos(azimuth)));
        return -source * (float.IsFinite(length) && length > 1e-6f ? length : 1);
    }

    /// <summary>Straight above or below has no azimuth; <paramref name="priorAzimuth"/> is kept then.</summary>
    internal static (double Azimuth, double Elevation) Angles(Vector3 direction, double priorAzimuth)
    {
        var source = -Vector3.Normalize(direction);
        double elevation = Math.Asin(Math.Clamp(source.Y, -1, 1)) * 180 / Math.PI;
        double horizontal = Math.Sqrt(source.X * source.X + source.Z * source.Z);
        double azimuth = horizontal < 1e-4 ? priorAzimuth : Math.Atan2(source.X, -source.Z) * 180 / Math.PI;
        if (azimuth < 0) azimuth += 360;
        if (azimuth >= 360) azimuth -= 360;
        return (azimuth, elevation);
    }

    internal static Vector3 FlipHemisphereDirection(Vector3 direction, bool currentlyBacklit)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) || direction.LengthSquared() < 1e-12f)
            return currentlyBacklit ? new Vector3(0, 0, 1) : new Vector3(0, 0, -1);
        float length = direction.Length();
        if (MathF.Abs(direction.Z) > length * 1e-4f)
            return new Vector3(direction.X, direction.Y, -direction.Z);
        float targetTravelZSign = currentlyBacklit ? 1f : -1f;
        var tilted = new Vector3(direction.X, direction.Y, targetTravelZSign * length * 0.1f);
        return Vector3.Normalize(tilted) * length;
    }
}
