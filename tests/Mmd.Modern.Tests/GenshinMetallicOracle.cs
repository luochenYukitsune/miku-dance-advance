using System.Numerics;

namespace Mmd.Modern.Tests;

// Independent scalar/16-tap reference for synthetic fixtures. No product shader or binding calls.
internal static class GenshinMetallicOracle
{
    internal static float Clamp(float value) => Math.Clamp(value, 0, 1);
    internal static float RawGate(float red, float enable) => red > .9f ? enable : 0;
    internal static float Gamma(float value) => value > 0 ? MathF.Pow(value, 2.2f) : value;
    internal static Vector3 Gamma(Vector3 value) => new(Gamma(value.X), Gamma(value.Y), Gamma(value.Z));
    internal static float Decode(float value) => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);

    internal static Vector2 Coordinates(Vector3 normal, Vector3 position, Vector3 eye,
        Vector3 cameraUp, Vector3 cameraBackward, bool orthographic, float tile)
    {
        var incoming = orthographic ? cameraBackward : Vector3.Normalize(eye - position);
        var tangent = Vector3.Cross(incoming, -Vector3.Normalize(cameraUp));
        var bitangent = Vector3.Cross(incoming, tangent);
        return new(.5f + .5f * tile * Vector3.Dot(normal, tangent), .5f + .5f * Vector3.Dot(normal, bitangent));
    }

    internal static Vector3 LocalTint(Vector3 image, float brightness, Vector3 dark,
        Vector3 light, Vector3 shadow, float lit)
    {
        float luminance = Vector3.Dot(image * brightness, new(.2126f, .7152f, .0722f));
        return Vector3.Lerp(Gamma(dark), Gamma(light), Clamp(luminance))
            * Vector3.Lerp(Vector3.One, Gamma(shadow), Clamp(lit - .8f));
    }

    internal static Vector3 Palette(Vector3 nonmetal, Vector3 tint, float raw)
        => Vector3.Lerp(Vector3.One, nonmetal, Clamp(1 - raw))
            * Vector3.Lerp(Vector3.One, tint, Clamp(raw));

    internal static Vector3 CubicRepeat(ReadOnlySpan<byte> rgba, int width, int height, Vector2 sourceUv, bool srgb)
    {
        // Source lower-row-first coordinates are converted exactly once for D3D's top-row-first bytes.
        double x = (double)sourceUv.X * width - .5, y = (1d - sourceUv.Y) * height - .5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        var wx = Weights(x - ix); var wy = Weights(y - iy);
        var result = Vector3.Zero;
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++)
        {
            int column = ((ix + i - 1) % width + width) % width;
            int row = ((iy + j - 1) % height + height) % height;
            int at = 4 * (row * width + column);
            var color = new Vector3(rgba[at], rgba[at + 1], rgba[at + 2]) / 255;
            if (srgb) color = new(Decode(color.X), Decode(color.Y), Decode(color.Z));
            result += color * (float)(wx[i] * wy[j]);
        }
        return result;
    }

    private static double[] Weights(double t) =>
        [Math.Pow(1 - t, 3) / 6, (3 * t * t * t - 6 * t * t + 4) / 6,
            (-3 * t * t * t + 3 * t * t + 3 * t + 1) / 6, t * t * t / 6];
}
