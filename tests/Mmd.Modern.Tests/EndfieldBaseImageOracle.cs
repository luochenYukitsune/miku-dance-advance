using System.Numerics;

namespace Mmd.Modern.Tests;

// Independent binary32 equations for the bounded source-default ColorBands and Cpre.
internal static class EndfieldBaseImageOracle
{
    internal static Vector3 Evaluate(EndfieldBaseImageSpec binding, bool front,
        Vector3? sampledBase = null, float? sampledDetailBlue = null, float? sampledShadowFactor = null)
    {
        Vector3 x = sampledBase ?? binding.BaseImageLinearRgb;
        if (!front) x *= x;
        x = x * (1 - binding.Contrast) + x * x * binding.Contrast;
        x = Hsv(x, binding.Saturation) * binding.ColorMultiplier;
        float detail = sampledDetailBlue ?? binding.DetailBlue;
        x = x * .5f + x * detail * .5f;
        Vector3 shadow = Shadow(binding.ModelColor, binding.ShadowColor,
            sampledShadowFactor ?? binding.ShadowFactor, binding.WarmTint);
        float strength = Math.Clamp(binding.ShadowStrength, 0, 1);
        return x * (1 - strength) + x * shadow * strength;
    }

    internal static Vector3 Shadow(Vector3 model, Vector3 tint, float q, float warm)
    {
        float k = Math.Clamp(warm, 0, 1), fq = Math.Clamp(q, 0, 1);
        Vector3 a = SampleLut(0, q) * (1 - k) + SampleLut(1, q) * k;
        Vector3 b = default;
        for (int i = 0; i < 3; i++)
            b[i] = a[i] < .5f ? 2 * a[i] * tint[i] : 1 - 2 * (1 - tint[i]) * (1 - a[i]);
        Vector3 c = model * (1 - fq) + Vector3.One * fq;
        Vector3 d = c * (1 - k) + c * SampleLut(2, q) * k;
        float luminance = tint.X * .2126f + tint.Y * .7152f + tint.Z * .0722f;
        return luminance < 1 ? b : d;
    }

    internal static Vector3 SampleLut(int row, float q)
    {
        float coordinate = Math.Clamp(q, 0, 1) * 257 - .5f;
        int left = (int)MathF.Floor(coordinate);
        float fraction = coordinate - left;
        return Texel(row, Math.Clamp(left, 0, 256)) * (1 - fraction)
            + Texel(row, Math.Clamp(left + 1, 0, 256)) * fraction;
    }

    private static Vector3 Texel(int row, int index)
    {
        Vector3 left = row == 2 ? Vector3.One : new(.5f);
        Vector3 middle = row switch { 0 => new(.800000011920929f),
            1 => new(1,.8531801104545593f,.6814245581626892f),
            2 => new(1,.8999999761581421f,.75f), _ => throw new ArgumentOutOfRangeException(nameof(row)) };
        Vector3 right = Vector3.One;
        float x = index / 256f;
        float f = x <= .5f ? (x - .5f) / -.5f : (x - 1) / -.5f;
        Vector3 value = x <= .5f ? middle * (1 - f) + left * f : right * (1 - f) + middle * f;
        return new((float)(Half)value.X,(float)(Half)value.Y,(float)(Half)value.Z);
    }

    internal static Vector3 Hsv(Vector3 rgb, float multiplier)
    {
        float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        float min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        float chroma = max - min, saturation = max == 0 ? 0 : chroma / max;
        float hue = 0;
        if (saturation != 0)
        {
            hue = rgb.X == max ? (rgb.Y - rgb.Z) / chroma
                : rgb.Y == max ? 2 + (rgb.Z - rgb.X) / chroma : 4 + (rgb.X - rgb.Y) / chroma;
            hue /= 6;
            if (hue < 0) hue += 1;
        }
        hue = hue + 1 - MathF.Floor(hue + 1);
        saturation = Math.Clamp(saturation * multiplier, 0, 1);
        if (saturation == 0) return new(max);
        float sector = hue * 6, floor = MathF.Floor(sector), f = sector - floor;
        float p = max * (1 - saturation), q = max * (1 - saturation * f), t = max * (1 - saturation * (1 - f));
        return (int)floor switch { 0 => new(max,t,p),1 => new(q,max,p),2 => new(p,max,t),
            3 => new(p,q,max),4 => new(t,p,max),_ => new(max,p,q) };
    }
}
