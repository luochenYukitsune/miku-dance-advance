using System.Numerics;

namespace Mmd.Modern.Tests;

// Independent scalar oracle. Do not make product shader tests call the same
// helper on both sides. No dependency on product binding or generated HLSL.
internal static class GenshinHairAtlasOracle
{
    internal static float SafeDivide(float a, float b) => b == 0 ? 0 : a / b;
    internal static float Saturate(float a) => Math.Clamp(a, 0, 1);
    internal static float Smooth(float a, float b, float x)
    {
        float t = Saturate(SafeDivide(x - a, b - a));
        return t * t * (3 - 2 * t);
    }

    internal static (float Q, float NightY, float DayY, float Gate) Coordinates(
        float ndotl, float lightmapGreen, float row, float shadowPosition, float rampWidth,
        float vertexWidth, float vertexAo, float useLightmapAo, float useVertexAo)
    {
        float ao = (.5f + Saturate(useLightmapAo) * (lightmapGreen - .5f))
            * (1 + Saturate(useVertexAo) * (vertexAo - 1));
        float light = Smooth(.01f, .4f, ao) * (.5f * ndotl + .5f);
        float width = (vertexWidth * 2) * rampWidth;
        float q = 1 - SafeDivide(SafeDivide(shadowPosition - light, shadowPosition), width);
        float night = ((6 - row) - 1) * .1f + .05f;
        float gate = Saturate((light < shadowPosition ? 1 : 0) - (lightmapGreen > .95f ? 1 : 0));
        return (q, night, night + .5f, gate);
    }

    internal static Vector3 ClosestExtend(byte[] rgba, int width, int height, float x, float y, bool srgb)
    {
        // Clamp before float-to-int to keep unbounded-but-finite q safe.
        int ix = Math.Clamp((int)MathF.Floor(Saturate(x) * width), 0, width - 1);
        int iy = Math.Clamp((int)MathF.Floor(Saturate(y) * height), 0, height - 1);
        int at = 4 * (iy * width + ix);
        var value = new Vector3(rgba[at], rgba[at + 1], rgba[at + 2]) / 255;
        return srgb ? new(Decode(value.X), Decode(value.Y), Decode(value.Z)) : value;
    }

    internal static float Decode(float x) => x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f);

    internal static Vector3 Palette(Vector3 day, Vector3 night, float dayNight, float gate)
        => Vector3.Lerp(Vector3.One, Vector3.Lerp(day, night, Saturate(dayNight)), gate);

    internal static Vector3 MdaVisibility(Vector3 atlasPalette, Vector3 authoredDark, float visibility)
        => Vector3.Lerp(authoredDark, atlasPalette, visibility);

}
