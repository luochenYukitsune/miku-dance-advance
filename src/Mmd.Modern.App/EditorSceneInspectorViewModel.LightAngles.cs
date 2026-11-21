using System.Numerics;
using Mmd.Modern.App.Controls;

namespace Mmd.Modern.App;

/// <summary>Main-light direction as source angles (azimuth/elevation), one-click aims and
/// aim-from-camera. Every path goes through <see cref="LightDirectionVector"/>, so the draft,
/// validation and one-transaction registration stay exactly as for the X/Y/Z fields.</summary>
public sealed partial class EditorSceneInspectorViewModel
{
    private double lastAzimuth;
    private bool mainLightBacklit;

    /// <summary>The MMD default main-light direction (−0.5, −1, 0.5).</summary>
    internal static Vector3 DefaultLightDirection => DefaultLight.Direction;

    public decimal? LightAzimuth
    {
        get => Angle(true);
        set { if (value is { } azimuth) SetAngles((double)azimuth, null); }
    }

    public decimal? LightElevation
    {
        get => Angle(false);
        set { if (value is { } elevation) SetAngles(null, (double)elevation); }
    }

    private decimal? Angle(bool azimuth)
    {
        var direction = lightPose.Direction;
        if (!Finite(direction) || direction.LengthSquared() < 1e-12f) return null;
        var angles = EditorLightAngles.Angles(direction, lastAzimuth);
        lastAzimuth = angles.Azimuth;
        return Math.Round((decimal)(azimuth ? angles.Azimuth : angles.Elevation), 1);
    }

    private void SetAngles(double? azimuth, double? elevation)
    {
        var direction = lightPose.Direction;
        bool valid = Finite(direction) && direction.LengthSquared() >= 1e-12f;
        var current = valid ? EditorLightAngles.Angles(direction, lastAzimuth) : (lastAzimuth, 45d);
        double nextAzimuth = azimuth ?? current.Item1;
        lastAzimuth = ((nextAzimuth % 360) + 360) % 360;
        LightDirectionVector = EditorLightAngles.Direction(nextAzimuth, elevation ?? current.Item2, valid ? direction.Length() : 1);
    }

    /// <summary>One-click aims, in source angles. "default" restores MMD's own direction.</summary>
    public void AimLight(string preset)
    {
        if (preset == "default") { LightDirectionVector = DefaultLight.Direction; return; }
        (double azimuth, double elevation) = preset switch
        {
            "top" => (lastAzimuth, 90d), "front" => (0d, 30d), "frontLeft" => (315d, 45d), "frontRight" => (45d, 45d),
            "left" => (270d, 20d), "right" => (90d, 20d), "back" => (180d, 35d), _ => (double.NaN, double.NaN)
        };
        if (double.IsNaN(azimuth)) return;
        lastAzimuth = azimuth;
        var direction = lightPose.Direction;
        float length = Finite(direction) && direction.LengthSquared() >= 1e-12f ? direction.Length() : 1;
        LightDirectionVector = EditorLightAngles.Direction(azimuth, elevation, length);
    }

    /// <summary>Light travels the way the current camera looks, so the visible side is lit.</summary>
    public void AimLightFromCamera()
    {
        SceneCamera_Forward(out var forward);
        if (forward.LengthSquared() < 1e-12f) return;
        var direction = lightPose.Direction;
        float length = Finite(direction) && direction.LengthSquared() >= 1e-12f ? direction.Length() : 1;
        LightDirectionVector = Vector3.Normalize(forward) * length;
    }

    private void SceneCamera_Forward(out Vector3 forward)
    {
        try { var camera = cameraPose.CreateCamera(); forward = camera.Target - camera.Eye; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { forward = Vector3.Zero; }
        if (!Finite(forward)) forward = Vector3.Zero;
    }

    public bool IsMainLightBacklit
    {
        get
        {
            var direction = lightPose.Direction;
            if (Finite(direction) && direction.LengthSquared() >= 1e-12f)
            {
                var normalized = Vector3.Normalize(direction);
                if (MathF.Abs(normalized.Z) > 1e-5f) return normalized.Z < 0;
            }
            return mainLightBacklit;
        }
    }

    public string MainLightHemisphereLabel => EditorLocalization.Text(IsMainLightBacklit
        ? "Mda.Text.LightAim.FlipToFront"
        : "Mda.Text.LightAim.FlipToBack");

    public void FlipMainLightHemisphere()
    {
        bool backlit = IsMainLightBacklit;
        var next = EditorLightAngles.FlipHemisphereDirection(lightPose.Direction, backlit);
        mainLightBacklit = !backlit;
        LightDirectionVector = next;
    }

    private void SyncMainLightHemisphere()
    {
        var direction = lightPose.Direction;
        if (!Finite(direction) || direction.LengthSquared() < 1e-12f) return;
        var normalized = Vector3.Normalize(direction);
        if (MathF.Abs(normalized.Z) > 1e-5f) mainLightBacklit = normalized.Z < 0;
    }
}
