using System.Numerics;

namespace Mmd.Modern.Tests;

// Independent CPU equations for synthetic fixtures. No product calls, source graph or assets.
internal static class EndfieldPaletteOracle
{
    internal readonly record struct Result(Vector3 Partial, Vector3 Lit, Vector3 Rimmed,
        Vector3 Waved, Vector3 Emission, Vector3 AddedDelta);

    internal static Result Evaluate(Vector3 pre, Vector3 mat, Vector3 rimColor,
        float baseline, float rim, float radial, float blue, float emissionStrength,
        float influence, Vector3 receivedLight, float density, Vector2 uv,
        bool hasHair = true, bool hasRim = true)
    {
        double r = hasRim ? rim : 0, p = hasHair ? radial : 0;
        double wave = SavedWave(uv.X, density), factor = Math.Clamp(influence, 0, 1);
        double[] partial = new double[3], lit = new double[3], rimmed = new double[3];
        for (int c = 0; c < 3; c++)
        {
            partial[c] = mat[c] + pre[c] * (2d * blue) * emissionStrength;
            lit[c] = partial[c] + factor * receivedLight[c];
            rimmed[c] = Overlay(lit[c], rimColor[c], r);
        }
        double[] hsv = SavedHsv(rimmed);
        double[] waved = new double[3], emission = new double[3], delta = new double[3];
        for (int c = 0; c < 3; c++)
        {
            waved[c] = rimmed[c] * (1 - wave) + hsv[c] * wave;
            emission[c] = waved[c] * (baseline + r + p);
            double oldHair = pre[c] * p;
            double oldRim = r == 0 ? 0 : Overlay(pre[c], rimColor[c], r) * (baseline + r) - pre[c] * baseline;
            delta[c] = emission[c] - pre[c] * baseline - oldHair - oldRim;
        }
        return new(V(partial), V(lit), V(rimmed), V(waved), V(emission), V(delta));
    }

    internal static double SavedWave(double u, double density)
        => Math.Clamp(.5 + .5 * Math.Sin(20 * ((u * (100 * density) + .000001) * .999999) - Math.PI / 2), 0, 1);

    internal static double Overlay(double color, double rimColor, double rim)
    {
        double f = Math.Clamp(rim, 0, 1);
        return color < .5 ? color * (1 - f + 2 * f * rimColor)
            : 1 - (1 - f + 2 * f * (1 - rimColor)) * (1 - color);
    }

    internal static Vector3 SavedHsv(Vector3 rgb) => V(SavedHsv([rgb.X, rgb.Y, rgb.Z]));

    private static double[] SavedHsv(double[] rgb)
    {
        double maximum = rgb.Max(), minimum = rgb.Min();
        double saturation = maximum == 0 ? 0 : (maximum - minimum) / maximum;
        double hue = 0;
        if (saturation != 0)
        {
            int largest = Array.IndexOf(rgb, maximum);
            hue = largest switch
            {
                0 => (rgb[1] - rgb[2]) / (maximum - minimum),
                1 => 2 + (rgb[2] - rgb[0]) / (maximum - minimum),
                _ => 4 + (rgb[0] - rgb[1]) / (maximum - minimum)
            };
            hue /= 6;
            if (hue < 0) hue += 1;
        }
        hue = hue + 1 - Math.Floor(hue + 1);
        saturation = Math.Clamp(saturation, 0, 1);
        double value = maximum * .7999999523162842;
        if (saturation == 0) return [value, value, value];
        double sector = hue * 6, fraction = sector - Math.Floor(sector);
        double low = value * (1 - saturation), down = value * (1 - saturation * fraction);
        double up = value * (1 - saturation * (1 - fraction));
        return (int)Math.Floor(sector) switch
        {
            0 => [value, up, low], 1 => [down, value, low], 2 => [low, value, up],
            3 => [low, down, value], 4 => [up, low, value], _ => [value, low, down]
        };
    }

    private static Vector3 V(double[] v) => new((float)v[0], (float)v[1], (float)v[2]);
}
