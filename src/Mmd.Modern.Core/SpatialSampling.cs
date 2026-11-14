using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>Deterministic spatial samples for one output pixel, without temporal history.</summary>
public static class SpatialSampling
{
    /// <summary>Returns a centered Hammersley sample in pixels; positive X is right and positive Y is down.</summary>
    public static Vector2 JitterPixels(int samples, int sampleIndex)
    {
        if (samples is not (1 or 2 or 4 or 8 or 16 or 32))
            throw new ArgumentOutOfRangeException(nameof(samples), "Spatial sample count must be 1, 2, 4, 8, 16 or 32.");
        if ((uint)sampleIndex >= (uint)samples) throw new ArgumentOutOfRangeException(nameof(sampleIndex));
        if (samples == 1) return Vector2.Zero;

        int reversed = 0;
        for (int bits = samples, index = sampleIndex; bits > 1; bits >>= 1, index >>= 1)
            reversed = (reversed << 1) | (index & 1);

        // Both axes occupy stratum centers. Power-of-two fractions give an exact zero mean.
        return new((sampleIndex + .5f) / samples - .5f, (reversed + .5f) / samples - .5f);
    }
}
