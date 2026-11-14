using System.Numerics;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.Tests;

// Independent numerical contract and direct four-texel sampler for synthetic fixtures.
// Product code and reference graph/pixels are deliberately not evaluated here.
internal static class GenshinMetallicSpecularOracle
{
    internal static float Gate(float red, float enable) => red > .9f ? enable : 0;
    internal static float Gamma(float channel) => channel > 0 ? MathF.Pow(channel, 2.2f) : channel;
    internal static Vector3 Gamma(Vector3 color) => new(Gamma(color.X), Gamma(color.Y), Gamma(color.Z));
    internal static float P(float nh, float shininess, float scale, float globalScale = 1)
    {
        float shifted = nh + (globalScale - 1);
        return Math.Clamp(scale * MathF.Pow(Math.Max(shifted, .001f), shininess), 0, 1);
    }

    internal static Vector3 Local(float nh, float blue, Vector3 color, Vector3 sharpColor,
        float shininess = 16, float scale = 1, float attenuation = .5f, float sharpOffset = 1,
        float lit = 1, float rampMix = 0, Func<float, Vector3>? ramp = null,
        float globalScale = 1, float intensity = 1)
    {
        float p = P(nh, shininess, scale, globalScale);
        Vector3 authored = Gamma(color) * intensity;
        Vector3 ordinary = authored * p;
        float mix = Math.Clamp(rampMix, 0, 1);
        if (mix > 0)
        {
            if (ramp is null) throw new ArgumentException("An active ramp needs an explicit numerical image.", nameof(ramp));
            ordinary = Vector3.Lerp(ordinary, authored * ramp(p), mix);
        }
        Vector3 selected = sharpOffset < p ? Gamma(sharpColor) : ordinary * blue;
        return selected * (1 + (attenuation - 1) * Math.Clamp(1 - lit, 0, 1));
    }

    internal static Vector3 Local(float nh, float blue, D3D12GenshinMetallicSpecularBinding binding,
        float lit = 1, Func<float, Vector3>? ramp = null)
        => Local(nh, blue, binding.Color, binding.SharpColor, binding.Shininess, binding.Scale,
            binding.Attenuation, binding.SharpOffset, lit, binding.RampMix, ramp);

    internal static Vector3 Compose(Vector3 nonmetal, Vector3 metal, float raw)
        => raw <= 0 ? nonmetal : nonmetal * Math.Clamp(1 - raw, 0, 1) + metal * Math.Clamp(raw, 0, 1);

    internal static Vector3 SafeNormalize(Vector3 vector)
        => vector.LengthSquared() > 0 ? Vector3.Normalize(vector) : Vector3.Zero;

    internal static float NdotH(Vector3 normal, Vector3 position, Vector3 eye, Vector3 light)
    {
        // Existing primary adapter uses Eye-world for both projection modes.
        var incoming = SafeNormalize(eye - position);
        return Vector3.Dot(SafeNormalize(normal), SafeNormalize(incoming + SafeNormalize(light)));
    }

    internal static Vector3 LinearRepeat(ReadOnlySpan<byte> rgba, int width, int height, float p)
    {
        // v=.5 is unchanged by the source-to-D3D row convention. Use actual two-dimensional
        // dimensions; an even-height image interpolates its two middle rows.
        double x = (double)p * width - .5, y = .5 * height - .5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        Vector3 result = Vector3.Zero;
        for (int row = 0; row < 2; row++) for (int column = 0; column < 2; column++)
        {
            int wrappedX = ((ix + column) % width + width) % width;
            int wrappedY = ((iy + row) % height + height) % height;
            int at = (wrappedY * width + wrappedX) * 4;
            float weight = (float)((column == 0 ? 1 - fx : fx) * (row == 0 ? 1 - fy : fy));
            result += new Vector3(rgba[at], rgba[at + 1], rgba[at + 2]) * (weight / 255);
        }
        return result;
    }
}
