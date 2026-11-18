namespace Mmd.Modern.App;

/// <summary>Convenience slider coordinates for the stored values: brightness received at the aim target
/// (1 matches the fully lit main light; the thumb spans 0 to about 10) and scene-unit distances.
/// Numeric precision is independent of these thumb bounds.</summary>
internal static class EditorLightParameterScale
{
    internal static double BrightnessPosition(decimal? value) => value is >= 0 ? Math.Log10(1 + 100 * (double)value.Value) : 0;
    internal static decimal BrightnessValue(double position) => (decimal)((Math.Pow(10, position) - 1) / 100);
    internal static double DistancePosition(decimal? value) => value is > 0 ? Math.Log10((double)value.Value) : -2;
    internal static decimal DistanceValue(double position) => (decimal)Math.Pow(10, position);
}
