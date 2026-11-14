using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ExpandedChunksEdgeCasesTests
{
    private const int Chunk = 4096;
    private const int Count = 3 * Chunk + 7;
    private static readonly float NegativeZero = BitConverter.Int32BitsToSingle(int.MinValue);

    [Fact]
    [Trait("Category", "Gpu")]
    public void RootChunkUploadAbortedBeforeSubmissionRetriesTheSameRevisionWithCorrectReadback()
    {
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        var scene = Scene(); var data = Data(scene); var options = OriginalOutlineTests.Options();
        var producer = new D3D12OutlinePositionCache();
        var root = producer.GetChunks(data, options);
        var expected = Bytes(OriginalOutlineMath.BuildPositions(data, options));
        Assert.False(root.Revision.SupportsIncrementalUpdates);
        Assert.False(root.IsMaterialized);
        ID3D12Resource? readback = null;
        try
        {
            runtime.Begin();
            var aborted = cache.ExpandedPositionBuffer("root-chunk-abort", root);
            runtime.Abort();
            Assert.Equal(0, runtime.Submissions);
            Assert.Equal(1, runtime.BufferUploads);
            Assert.Equal(0, cache.LiveResourceCount);
            Assert.False(root.IsMaterialized);

            // Reuse the exact same output and revision; the aborted copies/barrier never ran.
            runtime.Begin();
            var retry = cache.ExpandedPositionBuffer("root-chunk-abort", root);
            Assert.NotSame(aborted, retry);
            readback = Copy(runtime, retry, expected.Length);
            runtime.Wait(runtime.Submit());
            Assert.Equal(expected, Read(readback, expected.Length));
            Assert.Equal(2, runtime.BufferUploads);
            Assert.Equal(0, cache.ExpandedPositionBufferStatistics.BufferCreations);
            Assert.False(root.IsMaterialized);
            Assert.False(producer.IsOutputMaterialized);
        }
        finally
        {
            runtime.Abort();
            runtime.WaitForIdle();
            readback?.Dispose();
        }
    }

    [Fact]
    public void ChunkBoundsKeepSignedZeroAndNonfiniteFallbackEqualToIndependentFlatOracle()
    {
        var scene = Scene(); var data = Data(scene);
        // Large but finite base lets a later finite extreme normal overflow without changing
        // expansion parameters, so both updates exercise chunk-range comparison directly.
        var options = OriginalOutlineTests.Options(float.MaxValue);
        Assert.True(float.IsFinite(OriginalOutlineMath.ComputeModelBase(options)));
        var producer = new D3D12OutlinePositionCache();
        var cache = new D3D12VisibilityBoundsCache(1);
        var root = producer.GetChunks(data, options);
        var initial = cache.GetOutline(scene, root);
        EqualOracle(scene, data, options, root, initial);
        Assert.False(OriginalFrustumVisibility.IsPotentiallyVisible(initial[3], Matrix4x4.Identity));

        var negativeZero = Compose(data, Chunk, vertex => vertex with
        {
            Position = vertex.Position with { X = NegativeZero },
            Normal = new(NegativeZero, 0, 0)
        });
        var signed = producer.GetChunks(negativeZero, options);
        var signedBounds = cache.GetOutline(scene, signed);
        Assert.Equal(Chunk, cache.LastComparedVertices);
        EqualOracle(scene, negativeZero, options, signed, signedBounds);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(signed[Chunk].X));
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(signedBounds[2].Minimum.X));

        var extreme = Compose(negativeZero, 2 * Chunk, vertex => vertex with { Normal = new(float.MaxValue) });
        var nonfinite = producer.GetChunks(extreme, options);
        var nonfiniteBounds = cache.GetOutline(scene, nonfinite);
        Assert.Equal(Chunk, cache.LastComparedVertices);
        EqualOracle(scene, extreme, options, nonfinite, nonfiniteBounds);
        Assert.False(float.IsFinite(nonfinite[2 * Chunk].X));
        Assert.True(float.IsNaN(nonfiniteBounds[3].Minimum.X));
        // The original finite box was outside. Unknown expanded geometry must remain drawable.
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(nonfiniteBounds[3], Matrix4x4.Identity));
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(nonfiniteBounds[3], Matrix4x4.Identity, Matrix4x4.Identity));

        // Traverse backward to prove that a prior invalid bound cannot survive a restored chunk.
        var restored = cache.GetOutline(scene, signed);
        Assert.Equal(Chunk, cache.LastComparedVertices);
        EqualOracle(scene, negativeZero, options, signed, restored);
        Assert.False(OriginalFrustumVisibility.IsPotentiallyVisible(restored[3], Matrix4x4.Identity));
        Assert.False(root.IsMaterialized);
        Assert.False(signed.IsMaterialized);
        Assert.False(nonfinite.IsMaterialized);
        Assert.False(producer.IsOutputMaterialized);
    }

    private static void EqualOracle(SceneSnapshot scene, OriginalOutlineData source, OriginalOutlineOptions options,
        D3D12ExpandedPositions chunks, OriginalVisibilityBounds[] actual)
    {
        // Build the independent existing scalar oracle, never the compatibility Materialize path.
        var flat = OriginalOutlineMath.BuildPositions(source, options);
        var copied = new Vector4[chunks.Length]; chunks.CopyTo(copied);
        Assert.Equal(Bytes(flat), Bytes(copied));
        var expected = OriginalFrustumVisibility.PositionMaterialBounds(scene, flat);
        Assert.Equal(Bytes(expected), Bytes(actual));
        Assert.False(chunks.IsMaterialized);
    }

    // These short helpers follow the existing ExpandedPositionRevision/Buffer tests; their
    // private visibility is left unchanged. The public Options fixture is shared directly.
    private sealed class Writer(Func<OriginalOutlineVertex, OriginalOutlineVertex> edit)
    {
        public void Write(int _, Span<OriginalOutlineVertex> destination)
        { for (int index = 0; index < destination.Length; index++) destination[index] = edit(destination[index]); }
    }
    private static OriginalOutlineData Compose(OriginalOutlineData source, int index,
        Func<OriginalOutlineVertex, OriginalOutlineVertex> edit)
    {
        var method = typeof(OriginalOutlineData).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,
            new Writer(edit), typeof(Writer).GetMethod(nameof(Writer.Write))!);
        try { return (OriginalOutlineData)method.Invoke(source, [new[] { (index, 1) }, writer, source.Materials.ToArray()])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static SceneSnapshot Scene()
    {
        var vertices = Enumerable.Range(0, Count).Select(index => new SceneVertex(
            new(index == Chunk ? 0 : 100 + index % 3, index % 2 * .25f, .5f), Vector3.Zero, Vector2.Zero)).ToArray();
        return new(vertices,
            [0, 1, 2, Chunk, Chunk + 1, Chunk + 2, 2 * Chunk, 2 * Chunk + 1, 2 * Chunk + 2, 3 * Chunk, 3 * Chunk + 1, 3 * Chunk + 2],
            [Material("outside", 0, 3), Material("empty", 3, 0), Material("signed", 3, 3),
                Material("nonfinite", 6, 3), Material("tail", 9, 3)]);
    }
    private static OriginalOutlineData Data(SceneSnapshot scene) => new(scene.Vertices.ToArray().Select(vertex =>
        new OriginalOutlineVertex(vertex.Position, vertex.Normal, OriginalOutlineVertexMode.Expand, 1, 0)),
        scene.Materials.Select(_ => new OriginalOutlineMaterial(.2f, Vector4.One)));
    private static SceneMaterial Material(string name, int start, int count)
        => new(name, start, count, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
    private static byte[] Bytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static ID3D12Resource Copy(D3D12GpuRuntime runtime, ID3D12Resource source, int bytes)
    {
        var read = runtime.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer((ulong)bytes), ResourceStates.CopyDest);
        runtime.Commands.ResourceBarrierTransition(source, ResourceStates.VertexAndConstantBuffer, ResourceStates.CopySource);
        runtime.Commands.CopyBufferRegion(read, 0, source, 0, (ulong)bytes);
        runtime.Commands.ResourceBarrierTransition(source, ResourceStates.CopySource, ResourceStates.VertexAndConstantBuffer);
        return read;
    }
    private static byte[] Read(ID3D12Resource resource, int bytes)
    {
        var mapped = resource.Map<byte>(0, bytes);
        try { return mapped.ToArray(); }
        finally { resource.Unmap(0); }
    }
}
