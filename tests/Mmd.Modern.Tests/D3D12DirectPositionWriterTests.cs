using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12DirectPositionWriterTests
{
    private delegate void PositionWriter(RayGiScene scene, int maxDegree, Span<byte> destination);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4095, 8)]
    [InlineData(4096, 16)]
    [InlineData(4097, 32)]
    [InlineData(32767, 16)]
    [InlineData(32768, 8)]
    [InlineData(32771, 32)]
    [InlineData(32771, -1)]
    [InlineData(32771, 0)]
    [InlineData(65539, 16)]
    public void DirectWriterMatchesIndependentLegacyBytesAndPreservesGuards(int triangleCount, int workers)
    {
        var write = GetWriter();
        var byteCount = GetByteCount();
        var scene = ExistingScene(triangleCount);
        var expected = ExistingLegacy(scene);
        int length = checked(expected.Length * sizeof(float));
        Assert.Equal(length, byteCount(triangleCount));
        var output = Enumerable.Repeat((byte)0xa5, length + 8).ToArray();

        write(scene, workers, output.AsSpan(4, length));

        Assert.True(output.AsSpan(0, 4).SequenceEqual(new byte[] { 0xa5, 0xa5, 0xa5, 0xa5 }));
        Assert.True(output.AsSpan(4 + length, 4).SequenceEqual(new byte[] { 0xa5, 0xa5, 0xa5, 0xa5 }));
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(output.AsSpan(4, length)),
            $"Packed position bytes differ for {triangleCount} triangles with requested degree {workers}.");
        // ExistingScene and ExistingLegacy include negative zero, positive and negative subnormals,
        // finite extremes, unique positions and distinctive W components.
    }

    [Fact]
    public void ByteCountRejectsNegativeAndOverflowWithoutAllocatingAGiantScene()
    {
        var count = GetByteCount();
        Assert.Equal(0, count(0));
        Assert.Equal(36, count(1));
        Assert.Equal((int.MaxValue / 36) * 36, count(int.MaxValue / 36));
        Assert.ThrowsAny<ArgumentException>(() => count(-1));
        Assert.Throws<OverflowException>(() => count(int.MaxValue / 36 + 1));
    }

    [Fact]
    public void DestinationMustHaveExactLengthAndInvalidLengthsDoNotOverwriteGuards()
    {
        var write = GetWriter();
        var scene = ExistingScene(1);
        var tooShort = Enumerable.Repeat((byte)0x6d, 35).ToArray();
        var tooLong = Enumerable.Repeat((byte)0x6d, 37).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => write(scene, 1, tooShort));
        Assert.ThrowsAny<ArgumentException>(() => write(scene, 1, tooLong));
        Assert.All(tooShort, value => Assert.Equal((byte)0x6d, value));
        Assert.All(tooLong, value => Assert.Equal((byte)0x6d, value));
    }

    [Fact]
    public void ConcurrentCallsKeepIndependentDestinationsAndLeaveSceneUnchanged()
    {
        var write = GetWriter();
        var scene = ExistingScene(32771);
        var original = MemoryMarshal.AsBytes(scene.Triangles).ToArray();
        var expected = MemoryMarshal.AsBytes(ExistingLegacy(scene).AsSpan()).ToArray();
        var outputs = new byte[3][];
        Parallel.For(0, outputs.Length, index =>
        {
            var destination = new byte[expected.Length];
            write(scene, 1, destination);
            outputs[index] = destination;
        });
        foreach (var output in outputs) Assert.Equal(expected, output);
        Assert.NotSame(outputs[0], outputs[1]);
        outputs[0][0] ^= 0xff;
        Assert.Equal(expected, outputs[1]);
        Assert.True(original.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(scene.Triangles)));
    }

    private static PositionWriter GetWriter()
    {
        var method = typeof(D3D12HardwareRayGi).GetMethod("WritePositions",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
            [typeof(RayGiScene), typeof(int), typeof(Span<byte>)], null);
        Assert.NotNull(method);
        return method.CreateDelegate<PositionWriter>();
    }

    private static Func<int, int> GetByteCount()
    {
        var method = typeof(D3D12HardwareRayGi).GetMethod("PositionByteCount",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(int)], null);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<int, int>>();
    }

    // Reuse the accepted pre-existing oracle and its edge-case scene without changing either.
    private static RayGiScene ExistingScene(int triangleCount)
    {
        var method = typeof(D3D12RayGiPositionPackingTests).GetMethod("Scene", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<int, RayGiScene>>()(triangleCount);
    }

    private static float[] ExistingLegacy(RayGiScene scene)
    {
        var method = typeof(D3D12RayGiPositionPackingTests).GetMethod("Legacy", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<RayGiScene, float[]>>()(scene);
    }
}
