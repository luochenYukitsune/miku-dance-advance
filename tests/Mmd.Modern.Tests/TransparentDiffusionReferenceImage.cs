namespace Mmd.Modern.Tests;

// Test-only double precision oracle. Input is a captured straight RGBA8 witness;
// its quantization is independent of the production FP16 source (up to 2 RGB bytes).
internal static class TransparentDiffusionReferenceImage
{
    internal static byte[] Apply(byte[] rgba, int width, int height, double strength, double extent)
    {
        var input = new double[rgba.Length];
        for (int p = 0; p < rgba.Length; p += 4)
        {
            double a = rgba[p + 3] / 255d;
            for (int c = 0; c < 3; c++) input[p + c] = rgba[p + c] / 255d * a;
            input[p + 3] = a;
        }
        var horizontal = new double[input.Length];
        double[] weights = Enumerable.Range(-7, 15).Select(i => Math.Exp(-.5 * Math.Pow(i / 3.5, 2))).ToArray();
        double total = weights.Sum(), step = extent * height;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 4;
            for (int tap = -7; tap <= 7; tap++)
            {
                double a = Sample(input, width, height, x + tap * step, y, 3), w = weights[tap + 7];
                for (int c = 0; c < 3; c++)
                {
                    double straight = a > 0 ? Math.Clamp(Sample(input, width, height, x + tap * step, y, c) / a, 0, 1) : 0;
                    horizontal[p + c] += w * a * straight * straight / total;
                }
                horizontal[p + 3] += w * a / total;
            }
            for (int c = 0; c < 4; c++) horizontal[p + c] = (double)(Half)horizontal[p + c];
        }
        var output = new byte[rgba.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 4;
            double a = input[p + 3];
            if (a == 0) continue;
            var sums = new double[4];
            for (int tap = -7; tap <= 7; tap++) for (int c = 0; c < 4; c++)
                sums[c] += weights[tap + 7] * Sample(horizontal, width, height, x, y + tap * step, c);
            var squared = new double[3]; var screen = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double original = rgba[p + c] / 255d, blur = sums[3] > 0 ? sums[c] / sums[3] : 0;
                squared[c] = original * original;
                screen[c] = blur + squared[c] - blur * squared[c];
            }
            bool replace = Luma(squared) > Luma(screen);
            for (int c = 0; c < 3; c++)
            {
                double original = rgba[p + c] / 255d;
                output[p + c] = Quantize(original + strength * ((replace ? squared[c] : screen[c]) - original));
            }
            output[p + 3] = rgba[p + 3];
        }
        return output;
    }
    private static double Luma(double[] c) => .29 * c[0] + .59 * c[1] + .12 * c[2];
    private static byte Quantize(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.ToEven);
    private static double Sample(double[] image, int width, int height, double x, double y, int channel)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double P(int xx, int yy) => image[(Math.Clamp(yy, 0, height - 1) * width + Math.Clamp(xx, 0, width - 1)) * 4 + channel];
        return (P(x0, y0) * (1 - fx) + P(x0 + 1, y0) * fx) * (1 - fy)
            + (P(x0, y0 + 1) * (1 - fx) + P(x0 + 1, y0 + 1) * fx) * fy;
    }
}
