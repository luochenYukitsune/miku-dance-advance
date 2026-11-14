using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>
/// One of the ten shared toon ramp families MMD exposes by index.
/// </summary>
/// <remarks>
/// These are <b>authored here</b>, not copied from MMD's <c>toon01..10.bmp</c>. MMD's toon look is a
/// technique — quantise <c>N·L</c> into a few bands — and that technique is not protectable, while
/// the specific bitmaps are. Generating them procedurally gives zero-install distribution with no
/// asset files, no hashes and no redistribution, at the cost of being visually close rather than
/// pixel identical (our compatibility promise is L2; see docs/lighting-modes.md).
/// </remarks>
public sealed record ToonRampPreset(
    string Name,
    int Steps,
    float DarkLevel,
    float LightLevel,
    float Softness,
    float Contrast,
    Vector3 Tint);

/// <summary>A generated 256-entry toon ramp: RGB holds the multiplier, alpha is unused.</summary>
public sealed class ToonRamp
{
    public const int Entries = 256;

    private readonly byte[] _rgba;

    internal ToonRamp(ToonRampPreset preset, byte[] rgba)
    {
        Preset = preset;
        _rgba = rgba;
    }

    public ToonRampPreset Preset { get; }

    /// <summary>RGBA8 table, 256 * 4 bytes, ready for a 1D texture upload or CPU sampling.</summary>
    public ReadOnlyMemory<byte> Rgba => _rgba;

    /// <summary>Samples the ramp the way the shader would: <paramref name="ndotl"/> in [0,1].</summary>
    public Vector3 Sample(float ndotl)
    {
        int index = (int)(Math.Clamp(ndotl, 0f, 1f) * (Entries - 1) + 0.5f);
        int offset = index * 4;
        return new Vector3(_rgba[offset] / 255f, _rgba[offset + 1] / 255f, _rgba[offset + 2] / 255f);
    }
}

/// <summary>
/// The ten procedural toon ramp families used when the user has not supplied MMD's own textures.
/// </summary>
public static class ToonRamps
{
    /// <summary>MMD materials reference toon indices 0..9 (toon01.bmp..toon10.bmp).</summary>
    public static IReadOnlyList<ToonRampPreset> Presets { get; } =
    [
        // 0: the classic hard two-step cel ramp.
        new("HardCel2", Steps: 2, DarkLevel: 0.55f, LightLevel: 1.00f, Softness: 0.00f, Contrast: 1.0f, Tint: Vector3.One),
        // 1: softer three-step, the most common anime look.
        new("SoftCel3", Steps: 3, DarkLevel: 0.62f, LightLevel: 1.00f, Softness: 0.35f, Contrast: 1.0f, Tint: Vector3.One),
        // 2: continuous gradient, for models that expect smooth shading.
        new("Gradient", Steps: 64, DarkLevel: 0.45f, LightLevel: 1.00f, Softness: 1.00f, Contrast: 1.0f, Tint: Vector3.One),
        // 3: high contrast two-step with a lower break point.
        new("HardCel2Low", Steps: 2, DarkLevel: 0.45f, LightLevel: 1.00f, Softness: 0.00f, Contrast: 1.6f, Tint: Vector3.One),
        // 4: four-step ramp.
        new("SoftCel4", Steps: 4, DarkLevel: 0.60f, LightLevel: 1.00f, Softness: 0.20f, Contrast: 1.1f, Tint: Vector3.One),
        // 5: warm bounce light in the dark band.
        new("WarmCel3", Steps: 3, DarkLevel: 0.64f, LightLevel: 1.00f, Softness: 0.30f, Contrast: 1.0f, Tint: new Vector3(1.00f, 0.94f, 0.86f)),
        // 6: cool shadow, common in modern shader packs.
        new("CoolCel3", Steps: 3, DarkLevel: 0.60f, LightLevel: 1.00f, Softness: 0.30f, Contrast: 1.0f, Tint: new Vector3(0.88f, 0.94f, 1.00f)),
        // 7: five-step painterly ramp.
        new("SoftCel5", Steps: 5, DarkLevel: 0.58f, LightLevel: 1.00f, Softness: 0.15f, Contrast: 1.0f, Tint: Vector3.One),
        // 8: rim-biased ramp that brightens late.
        new("RimCel3", Steps: 3, DarkLevel: 0.50f, LightLevel: 1.00f, Softness: 0.25f, Contrast: 2.2f, Tint: Vector3.One),
        // 9: nearly flat, for effects that supply their own lighting.
        new("Flat", Steps: 2, DarkLevel: 0.92f, LightLevel: 1.00f, Softness: 0.50f, Contrast: 1.0f, Tint: Vector3.One),
    ];

    public static ToonRamp Default { get; } = Get(1);

    /// <summary>Builds the ramp for a toon index; out-of-range indices clamp to the default.</summary>
    public static ToonRamp Get(int toonIndex)
    {
        int index = toonIndex < 0 || toonIndex >= Presets.Count ? 1 : toonIndex;
        return Build(Presets[index]);
    }

    public static ToonRamp Build(ToonRampPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Steps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset.Steps, "A ramp needs at least one step.");
        }

        var rgba = new byte[ToonRamp.Entries * 4];
        for (int i = 0; i < ToonRamp.Entries; i++)
        {
            float t = i / (float)(ToonRamp.Entries - 1);
            float curved = MathF.Pow(t, MathF.Max(0.01f, preset.Contrast));

            float quantised = preset.Steps == 1
                ? 1f
                : MathF.Min(1f, MathF.Floor(curved * preset.Steps) / (preset.Steps - 1));

            float value = quantised + (curved - quantised) * Math.Clamp(preset.Softness, 0f, 1f);
            float level = preset.DarkLevel + (preset.LightLevel - preset.DarkLevel) * value;

            int offset = i * 4;
            rgba[offset] = ToByte(level * preset.Tint.X);
            rgba[offset + 1] = ToByte(level * preset.Tint.Y);
            rgba[offset + 2] = ToByte(level * preset.Tint.Z);
            rgba[offset + 3] = 255;
        }

        return new ToonRamp(preset, rgba);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
}
