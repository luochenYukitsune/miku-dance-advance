using System.Numerics;

namespace Mmd.Modern.Tests;

// Independent CPU reference for synthetic fixtures, with no product shader/binding calls.
// This is the bounded MDA palette adapter, not a complete HA/HB material oracle.
internal static class MatcapOracle
{
    internal static float Saturate(float value) => Math.Clamp(value, 0, 1);
    internal static float Srgb(float value) => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);

    internal static Vector4 BilinearRepeat(ReadOnlySpan<byte> rgba, int width, int height, Vector2 uv, bool srgb)
    {
        double x = (double)uv.X * width - .5, y = (double)uv.Y * height - .5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        var result = Vector4.Zero;
        for (int j = 0; j < 2; j++) for (int i = 0; i < 2; i++)
        {
            int column = ((ix + i) % width + width) % width;
            int row = ((iy + j) % height + height) % height;
            int at = 4 * (row * width + column);
            var sample = new Vector4(rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) / 255;
            if (srgb) sample = new(Srgb(sample.X), Srgb(sample.Y), Srgb(sample.Z), sample.W);
            result += sample * (float)((i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy));
        }
        return result;
    }

    internal static Vector3 PackedNormal(Vector2 rg, Vector3 sourceNi, Vector4 tangent,
        float determinantSign, bool front, float strength)
    {
        Vector3 ni = sourceNi * (front ? 1 : -1);
        Vector3 geometric = Vector3.Normalize(ni);
        Vector3 perturbed;
        if (tangent == new Vector4(0, 0, 0, 1)) perturbed = ni;
        else
        {
            tangent *= front ? 1 : -1;
            var t = new Vector3(tangent.X, tangent.Y, tangent.Z);
            var b = tangent.W * determinantSign * Vector3.Cross(ni, t);
            double nx = 2d * rg.X - 1, ny = 2d * rg.Y - 1;
            float nz = (float)Math.Sqrt(Math.Max(1 - nx * nx - ny * ny, 0));
            perturbed = Vector3.Normalize(t * (float)nx + b * (float)ny + ni * nz);
        }
        return Vector3.Normalize(geometric + (perturbed - geometric) * Math.Max(strength, 0));
    }

    internal static Vector2 Coordinates(Vector3 mapped, Vector3 cameraRight, Vector3 cameraUp,
        Vector3 cameraForward, float rotation)
    {
        double x = Vector3.Dot(mapped, cameraRight), y = Vector3.Dot(mapped, cameraUp), z = -Vector3.Dot(mapped, cameraForward);
        return new((float)(.5 + .5 * (Math.Cos(rotation) * x + Math.Sin(rotation) * z)), (float)(.5 - .5 * y));
    }

    internal static (float Range, float Gate) SourceGate(Vector3 mapped, Vector3 lightControl,
        float hardness, float pRed, float pAlpha, float coefficient)
    {
        float a = 1 - (1 - hardness), fromMin = a * hardness, fromMax = a;
        float value = .5f * Vector3.Dot(mapped, Vector3.Normalize(lightControl)) + .5f;
        float range = fromMin == fromMax ? 0 : 1.5f * Saturate((value - fromMin) / (fromMax - fromMin));
        return (range, Saturate(range * Saturate(coefficient + Saturate(pRed + pAlpha))));
    }

    internal static Vector3 MaterialColor(Vector3 initial, Vector3 highlight, Vector4 image, float gate)
    {
        Vector3 m = new Vector3(image.X, image.Y, image.Z) * image.W;
        Vector3 screen = Vector3.One - (Vector3.One - m) * (Vector3.One - highlight);
        float f = Saturate(gate);
        return initial * ((1 - f) * Vector3.One + f * screen);
    }

    internal static Vector3 Overlay(Vector3 initial, Vector3 tint, float factor)
    {
        float f = Saturate(factor);
        Vector3 low = initial * ((1 - f) * Vector3.One + 2 * f * tint);
        Vector3 high = Vector3.One - ((1 - f) * Vector3.One + 2 * f * (Vector3.One - tint)) * (Vector3.One - initial);
        return new(initial.X < .5f ? low.X : high.X, initial.Y < .5f ? low.Y : high.Y, initial.Z < .5f ? low.Z : high.Z);
    }

    internal static Vector3 Delta(Vector3 initial, Vector3 changed, float baseline,
        float rim = 0, float radial = 0, Vector3? rimColor = null)
    {
        var oldPalette = rimColor is { } oldTint ? Overlay(initial, oldTint, rim) : initial;
        var newPalette = rimColor is { } newTint ? Overlay(changed, newTint, rim) : changed;
        return (newPalette - oldPalette) * (baseline + rim + radial);
    }
}
