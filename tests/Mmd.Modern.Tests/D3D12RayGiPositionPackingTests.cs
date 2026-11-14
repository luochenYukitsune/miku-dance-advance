using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12RayGiPositionPackingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(32771)]
    [InlineData(65539)]
    public void PackingPreservesLegacyPositionBytesAcrossBlockAndParallelThresholds(int triangleCount)
    {
        var scene = Scene(triangleCount);
        var expected = Legacy(scene);
        foreach (int workers in new[] { 1, 2, 8, 16, 32 })
        {
            var actual = Pack(scene, workers);
            Assert.Equal(checked(triangleCount * 9), actual.Length);
            Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())),
                $"Position bytes differ for {triangleCount} triangles with requested degree {workers}.");
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void NonpositiveRequestedDegreeUsesTheSerialFallback(int workers)
    {
        var scene = Scene(32771);
        var expected = Legacy(scene);
        var actual = Pack(scene, workers);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
    }

    [Fact]
    public void PositionPackingCopiesOnlyXyzAndPreservesFiniteFloatBits()
    {
        var scene = Scene(17);
        var actual = Pack(scene, 16);
        var expected = Legacy(scene);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(actual[0]));
        Assert.Equal(0x00000001, BitConverter.SingleToInt32Bits(actual[1]));
        Assert.Equal(unchecked((int)0x80000001), BitConverter.SingleToInt32Bits(actual[2]));
        Assert.DoesNotContain(actual, value => value == 1234567f || value == -1234567f || value == 7654321f);
    }

    [Fact]
    public void ConcurrentPackingKeepsIndependentOutputAndDoesNotMutateTheScene()
    {
        var scene = Scene(32771);
        var original = MemoryMarshal.AsBytes(scene.Triangles).ToArray();
        var expected = Legacy(scene);
        var results = new float[4][];
        Parallel.For(0, results.Length, i => results[i] = Pack(scene, 2));
        for (int i = 0; i < results.Length; i++)
        {
            Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(results[i].AsSpan())));
            for (int j = 0; j < i; j++) Assert.NotSame(results[j], results[i]);
        }
        results[0][0] = 42;
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(results[1].AsSpan())));
        Assert.True(original.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(scene.Triangles)));
    }

    private static float[] Pack(RayGiScene scene, int workers)
    {
        var method = typeof(D3D12HardwareRayGi).GetMethod("PackPositions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<RayGiScene, int, float[]>>()(scene, workers);
    }

    private static RayGiScene Scene(int triangleCount)
    {
        int[] edgeBits = [
            unchecked((int)0x80000000), 0x00000001, unchecked((int)0x80000001),
            0x007fffff, unchecked((int)0x807fffff), 0x00800000,
            unchecked((int)0x80800000), 0x7f7fffff, unchecked((int)0xff7fffff),
            0x00000000, 0x3f800000, unchecked((int)0xbf800000), 0x41234567];
        float Edge(int index) => BitConverter.Int32BitsToSingle(edgeBits[index % edgeBits.Length]);
        var triangles = new RayGiTriangleGpu[triangleCount];
        for (int i = 0; i < triangles.Length; i++)
        {
            // Unique finite coordinate bits reveal reordered or duplicated triangles;
            // distinct W values reveal accidental 16-byte row copies.
            float unique = BitConverter.Int32BitsToSingle(0x3f000000 | i);
            triangles[i] = new(
                new(Edge(i * 3), Edge(i * 3 + 1), Edge(i * 3 + 2), 1234567f),
                new(unique, Edge(i * 3 + 3), Edge(i * 3 + 4), -1234567f),
                new(Edge(i * 3 + 5), -unique, Edge(i * 3 + 6), 7654321f),
                default, default, default, default, default, default, new(0, (uint)i, 1, 0));
        }
        RayGiMaterialGpu material = new(Vector4.One, Vector4.Zero,
            new(0, 1, 1, 0), new(0, 1, 1, 0), Vector4.Zero, default);
        return new(triangles, [material], [0xffffffff]);
    }

    // The accepted Build implementation before extraction and block scheduling.
    private static float[] Legacy(RayGiScene scene)
    {
        var positions = new float[checked(scene.Triangles.Length * 9)];
        for (int i = 0, p = 0; i < scene.Triangles.Length; i++)
        {
            var triangle = scene.Triangles[i];
            positions[p++] = triangle.P0.X; positions[p++] = triangle.P0.Y; positions[p++] = triangle.P0.Z;
            positions[p++] = triangle.P1.X; positions[p++] = triangle.P1.Y; positions[p++] = triangle.P1.Z;
            positions[p++] = triangle.P2.X; positions[p++] = triangle.P2.Y; positions[p++] = triangle.P2.Z;
        }
        return positions;
    }
}
