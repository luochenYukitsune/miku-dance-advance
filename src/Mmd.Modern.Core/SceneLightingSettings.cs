namespace Mmd.Modern.Core;

/// <summary>Project-wide modern lighting controls. EnvironmentStrength adds white uniform diffuse
/// fill in linear light; ExposureEv applies overall exposure in stops. Neutral defaults preserve existing output.</summary>
public sealed record SceneLightingSettings
{
    public static SceneLightingSettings Default { get; } = new();
    public float EnvironmentStrength { get; }
    public float ExposureEv { get; }

    public SceneLightingSettings(float EnvironmentStrength = 0, float ExposureEv = 0)
    {
        this.EnvironmentStrength = EnvironmentStrength; this.ExposureEv = ExposureEv;
        Validate(this);
    }

    public static void Validate(SceneLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!float.IsFinite(settings.EnvironmentStrength) || settings.EnvironmentStrength is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(settings.EnvironmentStrength), "Environment strength must be finite and between 0 and 4.");
        if (!float.IsFinite(settings.ExposureEv) || settings.ExposureEv is < -8 or > 8)
            throw new ArgumentOutOfRangeException(nameof(settings.ExposureEv), "Exposure must be finite and between -8 and 8 EV.");
    }
}
