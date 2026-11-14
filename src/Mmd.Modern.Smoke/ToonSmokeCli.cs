using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;

namespace Mmd.Modern.Smoke;

/// <summary>
/// <c>--toon-ramps &lt;out.json&gt;</c>: dumps the procedural toon ramp table fingerprints.
/// </summary>
/// <remarks>
/// The ramps are generated, not loaded, so "which ramp is the build actually producing" needs an
/// artifact. This also gives the golden test a single place to read the expected hashes from when
/// a generation change is intentional.
/// </remarks>
public static class ToonSmokeCli
{
    public static int Run(string outputPath)
    {
        var presets = new List<object>();

        for (int index = 0; index < ToonRamps.Presets.Count; index++)
        {
            var preset = ToonRamps.Presets[index];
            var ramp = ToonRamps.Get(index);
            var luminance = new List<int>();
            for (int i = 0; i < ToonRamp.Entries; i++)
            {
                luminance.Add(ramp.Rgba.Span[i * 4 + 1]);
            }

            presets.Add(new
            {
                index,
                name = preset.Name,
                steps = preset.Steps,
                dark_level = preset.DarkLevel,
                light_level = preset.LightLevel,
                softness = preset.Softness,
                contrast = preset.Contrast,
                tint = new[] { preset.Tint.X, preset.Tint.Y, preset.Tint.Z },
                distinct_values = luminance.Distinct().Count(),
                first = luminance[0],
                last = luminance[^1],
                sha256 = Convert.ToHexString(SHA256.HashData(ramp.Rgba.Span)),
            });
        }

        var report = new
        {
            source = "procedural (no MMD assets redistributed)",
            entries = ToonRamp.Entries,
            bytes = ToonRamp.Entries * 4,
            presets,
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        string full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
        Console.WriteLine(json);
        return 0;
    }
}
