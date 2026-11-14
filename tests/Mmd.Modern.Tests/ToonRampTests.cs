using System.Security.Cryptography;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>
/// The procedural toon ramps that replace MMD's shared <c>toon01..10.bmp</c> assets.
/// </summary>
/// <remarks>
/// These tests pin the properties that make a ramp usable — endpoints, monotonicity, band count,
/// determinism — rather than the exact bytes of a formula, plus one golden hash so an accidental
/// change to the generator is visible in review.
/// </remarks>
public sealed class ToonRampTests
{
    private static int[] Luminance(byte[] rgba)
    {
        var values = new int[ToonRamp.Entries];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = i * 4;
            // Ramps are grayscale multipliers except for tinted presets; use the green channel,
            // which every tint leaves closest to neutral.
            values[i] = rgba[offset + 1];
        }

        return values;
    }

    [Fact]
    public void Ramp_is_a_256_entry_rgba8_table()
    {
        var ramp = ToonRamps.Default;

        Assert.Equal(ToonRamp.Entries * 4, ramp.Rgba.Length);
        for (int i = 0; i < ToonRamp.Entries; i++)
        {
            Assert.Equal(255, ramp.Rgba.Span[i * 4 + 3]);
        }
    }

    [Fact]
    public void Hard_cel_ramp_has_exactly_its_band_count()
    {
        var ramp = ToonRamps.Build(ToonRamps.Presets[0]);

        Assert.Equal(0, ToonRamps.Presets[0].Softness);
        Assert.Equal(2, ramp.Preset.Steps);
        Assert.Equal(2, Luminance(ramp.Rgba.ToArray()).Distinct().Count());
    }

    [Fact]
    public void Gradient_ramp_is_continuous()
    {
        var ramp = ToonRamps.Build(ToonRamps.Presets[2]);

        Assert.True(ramp.Preset.Steps > 32);
        // 256 entries spread over a 0.45..1.0 range collapse to roughly 140 distinct bytes after
        // 8-bit rounding; "continuous" means far more levels than any banded preset, not 256.
        Assert.True(Luminance(ramp.Rgba.ToArray()).Distinct().Count() > 100);
    }

    [Fact]
    public void Every_ramp_is_monotonic_and_reaches_both_endpoints()
    {
        foreach (var preset in ToonRamps.Presets)
        {
            var values = Luminance(ToonRamps.Build(preset).Rgba.ToArray());

            for (int i = 1; i < values.Length; i++)
            {
                Assert.True(values[i] >= values[i - 1],
                    $"{preset.Name} is not monotonic at entry {i}: {values[i - 1]} -> {values[i]}");
            }

            int expectedLow = (int)MathF.Round(preset.DarkLevel * preset.Tint.Y * 255f);
            int expectedHigh = (int)MathF.Round(preset.LightLevel * preset.Tint.Y * 255f);
            Assert.Equal(expectedLow, values[0]);
            Assert.Equal(expectedHigh, values[^1]);
        }
    }

    [Fact]
    public void Ten_distinct_presets_are_available_and_distinct()
    {
        Assert.Equal(10, ToonRamps.Presets.Count);

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in ToonRamps.Presets)
        {
            string hash = Convert.ToHexString(
                SHA256.HashData(ToonRamps.Build(preset).Rgba.Span));
            Assert.True(fingerprints.Add(hash), $"preset '{preset.Name}' duplicates another ramp");
        }
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var first = ToonRamps.Get(3).Rgba.ToArray();
        var second = ToonRamps.Get(3).Rgba.ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Out_of_range_toon_index_falls_back_to_the_default()
    {
        var fallback = ToonRamps.Get(-5).Rgba.ToArray();
        var high = ToonRamps.Get(99).Rgba.ToArray();
        var expected = ToonRamps.Get(1).Rgba.ToArray();

        Assert.Equal(expected, fallback);
        Assert.Equal(expected, high);
    }

    [Fact]
    public void Sample_matches_the_table_it_exposes()
    {
        var ramp = ToonRamps.Get(0);

        Assert.Equal(ramp.Rgba.Span[0] / 255f, ramp.Sample(0f).X, 5);
        Assert.Equal(ramp.Rgba.Span[(ToonRamp.Entries - 1) * 4] / 255f, ramp.Sample(1f).X, 5);
        Assert.Equal(ramp.Sample(2f), ramp.Sample(1f));
        Assert.Equal(ramp.Sample(-1f), ramp.Sample(0f));
    }

    [Fact]
    public void Hard_cel_golden_hash_is_stable()
    {
        // Golden: if the generator changes, this hash changes and the diff is reviewed on purpose.
        var hash = Convert.ToHexString(SHA256.HashData(ToonRamps.Get(0).Rgba.Span));

        Assert.Equal("7B437EA0923B35B455C38E549D17DDC0F91EF2AD6D37AB9C787214CAFACA02D6", hash);
    }
}
