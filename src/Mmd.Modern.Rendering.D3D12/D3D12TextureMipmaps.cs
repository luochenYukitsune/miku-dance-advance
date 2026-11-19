using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

internal readonly record struct D3D12TextureMipLevel(int Width, int Height, byte[] Pixels);

/// <summary>CPU 2x2 box mip-chain generator for straight-alpha RGBA8 UNorm textures (<see cref="SceneTexture"/>).
/// RGB averages are weighted by source alpha when nonzero so transparent cutout fringes never darken.</summary>
internal static class D3D12TextureMipmaps
{
    internal static int MipLevelCount(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
            levels++;
        }
        return levels;
    }

    internal static D3D12TextureMipLevel[] BuildMipChain(SceneTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return BuildMipChain(texture.Width, texture.Height, texture.Rgba);
    }

    internal static D3D12TextureMipLevel[] BuildMipChain(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        long expected = (long)width * height * 4;
        if (rgba.Length != expected)
            throw new ArgumentException($"Expected {expected} RGBA8 bytes for {width}x{height}, got {rgba.Length}.", nameof(rgba));

        int count = MipLevelCount(width, height);
        var chain = new D3D12TextureMipLevel[count];
        chain[0] = new(width, height, rgba.ToArray());

        int srcWidth = width, srcHeight = height;
        for (int level = 1; level < count; level++)
        {
            int dstWidth = Math.Max(1, srcWidth >> 1);
            int dstHeight = Math.Max(1, srcHeight >> 1);
            var dst = new byte[checked(dstWidth * dstHeight * 4)];
            DownsampleBox2x2(srcWidth, srcHeight, chain[level - 1].Pixels, dstWidth, dstHeight, dst);
            chain[level] = new(dstWidth, dstHeight, dst);
            srcWidth = dstWidth;
            srcHeight = dstHeight;
        }
        return chain;
    }

    internal static void DownsampleBox2x2(
        int srcWidth, int srcHeight, ReadOnlySpan<byte> srcRgba,
        int dstWidth, int dstHeight, Span<byte> dstRgba)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(srcWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(srcHeight, 1);
        int expectedWidth = Math.Max(1, srcWidth >> 1);
        int expectedHeight = Math.Max(1, srcHeight >> 1);
        if (dstWidth != expectedWidth || dstHeight != expectedHeight)
            throw new ArgumentException($"Expected destination dimensions {expectedWidth}x{expectedHeight}, got {dstWidth}x{dstHeight}.");
        if (srcRgba.Length != (long)srcWidth * srcHeight * 4)
            throw new ArgumentException("Source buffer length does not match source texture dimensions.", nameof(srcRgba));
        if (dstRgba.Length != (long)dstWidth * dstHeight * 4)
            throw new ArgumentException("Destination buffer length does not match destination mip dimensions.", nameof(dstRgba));

        int sampleXCount = srcWidth > 1 ? 2 : 1;
        int sampleYCount = srcHeight > 1 ? 2 : 1;
        int sampleCount = sampleXCount * sampleYCount;
        int halfSamples = sampleCount >> 1;
        int srcStride = checked(srcWidth * 4);
        int dstStride = checked(dstWidth * 4);

        for (int dy = 0; dy < dstHeight; dy++)
        {
            int sy0 = dy * 2;
            int dstRow = dy * dstStride;
            for (int dx = 0; dx < dstWidth; dx++)
            {
                int sx0 = dx * 2;
                int sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int rawR = 0, rawG = 0, rawB = 0;
                for (int oy = 0; oy < sampleYCount; oy++)
                {
                    int rowBase = (sy0 + oy) * srcStride + sx0 * 4;
                    for (int ox = 0; ox < sampleXCount; ox++)
                    {
                        int p = rowBase + ox * 4;
                        int r = srcRgba[p];
                        int g = srcRgba[p + 1];
                        int b = srcRgba[p + 2];
                        int a = srcRgba[p + 3];
                        rawR += r;
                        rawG += g;
                        rawB += b;
                        sumR += r * a;
                        sumG += g * a;
                        sumB += b * a;
                        sumA += a;
                    }
                }

                int dstOffset = dstRow + dx * 4;
                int outA = (sumA + halfSamples) / sampleCount;
                if (sumA > 0)
                {
                    int halfWeight = sumA >> 1;
                    dstRgba[dstOffset] = (byte)Math.Clamp((sumR + halfWeight) / sumA, 0, 255);
                    dstRgba[dstOffset + 1] = (byte)Math.Clamp((sumG + halfWeight) / sumA, 0, 255);
                    dstRgba[dstOffset + 2] = (byte)Math.Clamp((sumB + halfWeight) / sumA, 0, 255);
                }
                else
                {
                    dstRgba[dstOffset] = (byte)((rawR + halfSamples) / sampleCount);
                    dstRgba[dstOffset + 1] = (byte)((rawG + halfSamples) / sampleCount);
                    dstRgba[dstOffset + 2] = (byte)((rawB + halfSamples) / sampleCount);
                }
                dstRgba[dstOffset + 3] = (byte)Math.Clamp(outA, 0, 255);
            }
        }
    }
}
