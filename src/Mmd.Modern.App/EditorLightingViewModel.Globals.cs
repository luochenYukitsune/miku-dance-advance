using System.Collections.Immutable;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public sealed partial class EditorLightingViewModel
{
    private SceneLightingSettings originalSettings = SceneLightingSettings.Default;
    private decimal? environmentStrength, exposureEv;
    public decimal? EnvironmentStrength
    {
        get => environmentStrength;
        set { if (environmentStrength == value) return; environmentStrength = value; Changed(); }
    }
    public decimal? ExposureEv
    {
        get => exposureEv;
        set { if (exposureEv == value) return; exposureEv = value; Changed(); }
    }
    private void InitializeGlobalSettings(SceneLightingSettings settings)
    {
        originalSettings = settings;
        environmentStrength = (decimal)settings.EnvironmentStrength;
        exposureEv = (decimal)settings.ExposureEv;
    }
    internal SceneLightingSettings BuildGlobalSettings()
    {
        if (EnvironmentStrength is not (>= 0 and <= 4) || ExposureEv is not (>= -8 and <= 8))
            throw new ArgumentException(Text("Mda.Text.Context.Lighting.Global.Invalid"));
        // Merely showing a float in a decimal editor must preserve its original bits.
        var result = new SceneLightingSettings(EnvironmentStrength == (decimal)originalSettings.EnvironmentStrength
            ? originalSettings.EnvironmentStrength : (float)EnvironmentStrength.Value,
            ExposureEv == (decimal)originalSettings.ExposureEv ? originalSettings.ExposureEv : (float)ExposureEv.Value);
        SceneLightingSettings.Validate(result);
        return result;
    }
    internal EditorRecoveryGlobalLightingDraft CaptureGlobalDraft() => new()
    {
        Original = originalSettings, EnvironmentStrength = EnvironmentStrength, ExposureEv = ExposureEv
    };
    internal void RestoreGlobalDraft(EditorRecoveryGlobalLightingDraft draft)
    {
        originalSettings = draft.Original;
        EnvironmentStrength = draft.EnvironmentStrength; ExposureEv = draft.ExposureEv;
    }
    internal void RestorePendingGlobals(EditorRecoveryGlobalLightingDraft pending, SceneLightingSettings committed)
    {
        if (pending.EnvironmentStrength != (decimal)committed.EnvironmentStrength) EnvironmentStrength = pending.EnvironmentStrength;
        if (pending.ExposureEv != (decimal)committed.ExposureEv) ExposureEv = pending.ExposureEv;
    }
}

/// <summary>Committed settings are validated; nullable numbers and raw text can still be unfinished input.</summary>
public sealed record EditorRecoveryGlobalLightingDraft
{
    public required SceneLightingSettings Original { get; init; }
    public required decimal? EnvironmentStrength { get; init; }
    public required decimal? ExposureEv { get; init; }
    public ImmutableSortedDictionary<string, string> NumericText { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
    internal static readonly IReadOnlySet<string> NumericNames = new HashSet<string>(StringComparer.Ordinal)
        { "GlobalEnvironmentInput", "GlobalExposureInput" };
    internal void Validate()
    {
        try { SceneLightingSettings.Validate(Original); }
        catch (ArgumentException error) { throw EditorRecoverySessionState.Invalid("Recovery global lighting baseline is invalid.", error); }
        if (NumericText is null || NumericText.Count > NumericNames.Count)
            throw EditorRecoverySessionState.Invalid("Recovery global lighting input is invalid.");
        foreach (var input in NumericText)
        {
            if (!NumericNames.Contains(input.Key)) throw EditorRecoverySessionState.Invalid("Recovery global lighting field is unsupported.");
            EditorRecoverySessionState.ValidateText(input.Value);
        }
    }
}
