using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12PerfOptimizationTests
{
    [Theory]
    [InlineData(512, 512)]
    [InlineData(256, 256)]
    [InlineData(256, 254)]
    [InlineData(513, 511)]
    [InlineData(512, 511)]
    [InlineData(513, 512)]
    [InlineData(321, 257)]
    [InlineData(4096, 2)]
    [InlineData(2, 4096)]
    [InlineData(3, 1)]
    [InlineData(1, 3)]
    [InlineData(1, 1)]
    public void DownsampleBox2x2MatchesScalarReferenceByteForByte(int srcWidth, int srcHeight)
    {
        byte[] src = CreateMixedPatternRgba(srcWidth, srcHeight, seed: srcWidth * 131 + srcHeight * 17);
        int dstWidth = Math.Max(1, srcWidth >> 1);
        int dstHeight = Math.Max(1, srcHeight >> 1);

        byte[] expected = ReferenceDownsampleBox2x2(srcWidth, srcHeight, src, dstWidth, dstHeight);
        byte[] actual = new byte[dstWidth * dstHeight * 4];
        D3D12TextureMipmaps.DownsampleBox2x2(srcWidth, srcHeight, src, dstWidth, dstHeight, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DownsampleBox2x2ZeroAlphaAndCutoutLargeTextureMatchScalarReference()
    {
        const int srcWidth = 515, srcHeight = 387;
        int dstWidth = srcWidth >> 1;
        int dstHeight = srcHeight >> 1;
        byte[] src = new byte[srcWidth * srcHeight * 4];

        // Fill top half with all-transparent pixels (sumA == 0 branch), bottom half with cutout fringes.
        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
            {
                int p = (y * srcWidth + x) * 4;
                if (y < srcHeight / 2)
                {
                    src[p] = (byte)((x * 7 + y * 13) & 255);
                    src[p + 1] = (byte)((x * 11 + y * 5) & 255);
                    src[p + 2] = (byte)((x * 3 + y * 19) & 255);
                    src[p + 3] = 0;
                }
                else
                {
                    bool opaque = ((x + y) & 1) == 0;
                    src[p] = opaque ? (byte)245 : (byte)0;
                    src[p + 1] = opaque ? (byte)190 : (byte)0;
                    src[p + 2] = opaque ? (byte)110 : (byte)0;
                    src[p + 3] = opaque ? (byte)255 : (byte)0;
                }
            }
        }

        byte[] expected = ReferenceDownsampleBox2x2(srcWidth, srcHeight, src, dstWidth, dstHeight);
        byte[] actual = new byte[dstWidth * dstHeight * 4];
        D3D12TextureMipmaps.DownsampleBox2x2(srcWidth, srcHeight, src, dstWidth, dstHeight, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildMipChainFullChainMatchesScalarReferenceAtEveryLevel()
    {
        const int width = 513, height = 385;
        byte[] root = CreateMixedPatternRgba(width, height, seed: 4242);
        D3D12TextureMipLevel[] chain = D3D12TextureMipmaps.BuildMipChain(width, height, root);

        Assert.Equal(D3D12TextureMipmaps.MipLevelCount(width, height), chain.Length);
        Assert.Equal(root, chain[0].Pixels);

        int prevWidth = width, prevHeight = height;
        byte[] prevPixels = root;
        for (int level = 1; level < chain.Length; level++)
        {
            int dstWidth = Math.Max(1, prevWidth >> 1);
            int dstHeight = Math.Max(1, prevHeight >> 1);
            Assert.Equal((dstWidth, dstHeight), (chain[level].Width, chain[level].Height));
            byte[] expected = ReferenceDownsampleBox2x2(prevWidth, prevHeight, prevPixels, dstWidth, dstHeight);
            Assert.Equal(expected, chain[level].Pixels);
            prevWidth = dstWidth;
            prevHeight = dstHeight;
            prevPixels = expected;
        }
        Assert.Equal((1, 1), (chain[^1].Width, chain[^1].Height));
    }

    [Fact]
    public void DownsampleBox2x2RejectsInvalidDimensionsAndBufferLengths()
    {
        byte[] valid4x4 = new byte[4 * 4 * 4];
        byte[] valid2x2 = new byte[2 * 2 * 4];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12TextureMipmaps.DownsampleBox2x2(0, 4, valid4x4, 1, 2, valid2x2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12TextureMipmaps.DownsampleBox2x2(4, 0, valid4x4, 2, 1, valid2x2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12TextureMipmaps.DownsampleBox2x2(-1, 4, valid4x4, 1, 2, valid2x2));

        Assert.Throws<ArgumentException>(() =>
            D3D12TextureMipmaps.DownsampleBox2x2(4, 4, valid4x4, 3, 2, new byte[3 * 2 * 4]));
        Assert.Throws<ArgumentException>(() =>
            D3D12TextureMipmaps.DownsampleBox2x2(4, 4, valid4x4, 2, 1, new byte[2 * 1 * 4]));
        Assert.Throws<ArgumentException>(() =>
            D3D12TextureMipmaps.DownsampleBox2x2(4, 4, new byte[4 * 4 * 4 - 4], 2, 2, valid2x2));
        Assert.Throws<ArgumentException>(() =>
            D3D12TextureMipmaps.DownsampleBox2x2(4, 4, valid4x4, 2, 2, new byte[2 * 2 * 4 - 4]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(65)]
    public void FindDeformationRangeIndexMatchesLinearScanAcrossBoundariesAndGaps(int rangeCount)
    {
        var inputs = new List<OriginalFrameScene>();
        static OriginalFrameScene MakeStaticGap(int vertexCount)
        {
            var verts = new SceneVertex[vertexCount];
            for (int v = 0; v < vertexCount; v++)
                verts[v] = new(new Vector3(v, 0, 0), Vector3.UnitZ, Vector2.Zero);
            var snap = new SceneSnapshot(verts, [0u, 1u, 2u],
                [new SceneMaterial("gap", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], []);
            var outline = new OriginalOutlineData(
                verts.Select(v => new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1f, 0)),
                [new(1f, Vector4.One, false)]);
            return new OriginalFrameScene(new OriginalStyleScene(snap, [new(Vector3.One)]), outline,
                new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f));
        }

        inputs.Add(MakeStaticGap(3));
        for (int i = 0; i < rangeCount; i++)
        {
            int vertexCount = 3 + (i % 4);
            var verts = new SceneVertex[vertexCount];
            var bindings = new SceneSkinBinding[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                verts[v] = new(new Vector3(v, i, 0), Vector3.UnitZ, Vector2.Zero);
                bindings[v] = new(SceneSkinKind.Bdef1, 0, -1, -1, -1, Vector4.UnitX);
            }
            var model = new SceneSkinningModel(verts, bindings, [], boneCount: 1);
            var deformation = new SceneDeformation(model, [Matrix4x4.Identity], [], [], [], null, () => verts);
            var snap = new SceneSnapshot(verts, [0u, 1u, 2u],
                [new SceneMaterial($"def-{i}", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], [])
                .WithDeferredDeformation(deformation);
            var outline = new OriginalOutlineData(
                verts.Select(v => new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1f, 0)),
                [new(1f, Vector4.One, false)]);
            inputs.Add(new OriginalFrameScene(new OriginalStyleScene(snap, [new(Vector3.One)]), outline,
                new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f)));
            if ((i % 2) == 0)
                inputs.Add(MakeStaticGap(3 + (i % 3)));
        }

        var combined = ProjectSceneAssembler.Combine(inputs, Vector3.One, -Vector3.UnitZ);
        var ranges = combined.Surface.Snapshot.DeformationRanges;
        Assert.Equal(rangeCount, ranges.Count);

        int maxVertex = combined.Surface.Snapshot.VertexCount + 4;
        for (int vertexIndex = -2; vertexIndex <= maxVertex; vertexIndex++)
        {
            int expected = -1;
            for (int r = 0; r < ranges.Count; r++)
            {
                if (vertexIndex >= ranges[r].Start && vertexIndex < ranges[r].Start + ranges[r].Count)
                {
                    expected = r;
                    break;
                }
            }
            int actual = D3D12HardwareRayGi.FindDeformationRangeIndex(ranges, vertexIndex);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TextureSha256CacheReusesExactStringPerSceneTextureAndSeparatesPayloads()
    {
        var table = new ConditionalWeakTable<SceneTexture, string>();
        byte[] pixelsA = CreateMixedPatternRgba(32, 32, seed: 11);
        byte[] pixelsB = CreateMixedPatternRgba(32, 32, seed: 22);
        var texA = new SceneTexture("texA", 32, 32, pixelsA);
        var texB = new SceneTexture("texB", 32, 32, pixelsB);
        var texACopy = new SceneTexture("texACopy", 32, 32, pixelsA);

        string expectedA = Convert.ToHexString(SHA256.HashData(pixelsA));
        string expectedB = Convert.ToHexString(SHA256.HashData(pixelsB));

        string firstA = D3D12GpuCache.GetOrComputeTextureSha256(table, texA);
        string secondA = D3D12GpuCache.GetOrComputeTextureSha256(table, texA);
        string hashB = D3D12GpuCache.GetOrComputeTextureSha256(table, texB);
        string hashACopy = D3D12GpuCache.GetOrComputeTextureSha256(table, texACopy);

        Assert.Equal(expectedA, firstA);
        Assert.Same(firstA, secondA);
        Assert.Equal(expectedB, hashB);
        Assert.NotEqual(firstA, hashB);
        Assert.Equal(firstA, hashACopy);
    }

    [Fact]
    public void HardwareRayGiPositionPackingChecksBoundsAndMatchesSingleAndParallelDegrees()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12HardwareRayGi.PositionByteCount(-1));
        Assert.Equal(0, D3D12HardwareRayGi.PositionByteCount(0));
        Assert.Equal(36, D3D12HardwareRayGi.PositionByteCount(1));
        Assert.Throws<ArgumentNullException>(() => D3D12HardwareRayGi.WritePositions(null!, 1, Span<byte>.Empty));

        const int trianglesPerGrid = 40_000;
        var vertices = new SceneVertex[trianglesPerGrid * 3];
        var indices = new uint[trianglesPerGrid * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new(new Vector3(i * 0.1f, (i % 7) * 0.25f, -(i % 13)), Vector3.UnitY, Vector2.Zero);
            indices[i] = (uint)i;
        }
        var snapshot = new SceneSnapshot(vertices, indices,
            [new SceneMaterial("mat", 0, indices.Length, Vector4.One, Vector3.Zero, Vector3.Zero, 1)],
            []);
        var frame = new OriginalFrameScene(new OriginalStyleScene(snapshot, [new(Vector3.One)]));
        var giScene = RayGiScene.FromOriginal(frame);

        Assert.Throws<ArgumentException>(() => D3D12HardwareRayGi.WritePositions(giScene, 4, new byte[4]));

        int triangleCount = giScene.Triangles.Length;
        byte[] single = new byte[D3D12HardwareRayGi.PositionByteCount(triangleCount)];
        byte[] parallel = new byte[single.Length];
        D3D12HardwareRayGi.WritePositions(giScene, 1, single);
        D3D12HardwareRayGi.WritePositions(giScene, 4, parallel);
        Assert.Equal(single, parallel);
        Assert.Equal(MemoryMarshal.AsBytes(D3D12HardwareRayGi.PackPositions(giScene, 4).AsSpan()).ToArray(), parallel);
    }

    private static byte[] CreateMixedPatternRgba(int width, int height, int seed)
    {
        byte[] data = new byte[checked(width * height * 4)];
        uint state = unchecked((uint)seed * 747796405u + 2891336453u);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                state = unchecked(state * 1664525u + 1013904223u);
                int p = (y * width + x) * 4;
                // Mix zero-alpha blocks, full-opaque blocks, and semi-transparent pixels.
                int blockMode = ((x >> 2) + (y >> 2)) % 4;
                byte a = blockMode switch
                {
                    0 => 0,
                    1 => 255,
                    2 => (byte)((state >> 24) & 255),
                    _ => (byte)((x + y) % 2 == 0 ? 255 : 0),
                };
                data[p] = (byte)(state & 255);
                data[p + 1] = (byte)((state >> 8) & 255);
                data[p + 2] = (byte)((state >> 16) & 255);
                data[p + 3] = a;
            }
        }
        return data;
    }

    private static byte[] ReferenceDownsampleBox2x2(
        int srcWidth, int srcHeight, ReadOnlySpan<byte> srcRgba, int dstWidth, int dstHeight)
    {
        byte[] dst = new byte[checked(dstWidth * dstHeight * 4)];
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
                    dst[dstOffset] = (byte)Math.Clamp((sumR + halfWeight) / sumA, 0, 255);
                    dst[dstOffset + 1] = (byte)Math.Clamp((sumG + halfWeight) / sumA, 0, 255);
                    dst[dstOffset + 2] = (byte)Math.Clamp((sumB + halfWeight) / sumA, 0, 255);
                }
                else
                {
                    dst[dstOffset] = (byte)((rawR + halfSamples) / sampleCount);
                    dst[dstOffset + 1] = (byte)((rawG + halfSamples) / sampleCount);
                    dst[dstOffset + 2] = (byte)((rawB + halfSamples) / sampleCount);
                }
                dst[dstOffset + 3] = (byte)Math.Clamp(outA, 0, 255);
            }
        }
        return dst;
    }
}
