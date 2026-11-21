using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ImmutableGeometryIdentityTests
{
    [Fact]
    public void CameraLightingAndMaterialsShareVertexStorageWhileNewPoseRequiresFreshVertexUpload()
    {
        var scene = Scene(); var vertexIdentity = new D3D12VertexStreamIdentity(scene); var topology = new D3D12IndexStreamIdentity(scene);
        var camera = scene.WithCamera(scene.Camera with { Eye = scene.Camera.Eye + Vector3.UnitX });
        var light = scene.WithLighting(Vector3.UnitY);
        var material = scene.WithMaterials(scene.Materials.Select(value => value with { Diffuse = new(.3f) }));
        foreach (var metadata in new[] { camera, light, material })
        {
            Assert.Equal(vertexIdentity, new D3D12VertexStreamIdentity(metadata));
            Assert.Equal(topology, new D3D12IndexStreamIdentity(metadata));
        }
        var values = scene.Vertices.ToArray(); values[0] = values[0] with { Position = new(9, 8, 7) };
        var pose = scene.WithDeformedVertices(values);
        Assert.NotEqual(vertexIdentity, new D3D12VertexStreamIdentity(pose));
        Assert.Equal(topology, new D3D12IndexStreamIdentity(pose));
        Assert.Equal(Vector3.Zero, scene.Vertices[0].Position);
    }

    [Fact]
    public void EqualVertexContentInSeparateSnapshotsDoesNotProveSharedStreamIdentity()
    {
        var first = Scene(); var second = Scene();
        Assert.Equal(first.Vertices.ToArray(), second.Vertices.ToArray());
        Assert.NotEqual(new D3D12VertexStreamIdentity(first), new D3D12VertexStreamIdentity(second));
        // Index resources are immutable content, so independently copied but identical triangles may reuse them.
        Assert.Equal(new D3D12IndexStreamIdentity(first), new D3D12IndexStreamIdentity(second));
        Assert.NotEqual(new D3D12IndexStreamIdentity(first), new D3D12IndexStreamIdentity(Scene(reverse: true)));
    }

    [Fact]
    public void TemporalPairRequiresBothCurrentAndPreviousStorageAndKeepsTheirOrder()
    {
        var previous = Scene(); var values = previous.Vertices.ToArray(); values[1] = values[1] with { Normal = Vector3.UnitZ, Uv = Vector2.One };
        var current = previous.WithDeformedVertices(values);
        var pair = new D3D12TemporalVertexIdentity(current, previous);
        Assert.Equal(pair, new D3D12TemporalVertexIdentity(current.WithLighting(Vector3.UnitX), previous.WithCamera(previous.Camera)));
        Assert.NotEqual(pair, new D3D12TemporalVertexIdentity(previous, current));
        Assert.NotEqual(pair, new D3D12TemporalVertexIdentity(current, current));
        Assert.NotEqual(pair, new D3D12TemporalVertexIdentity(current.WithDeformedVertices(current.Vertices.ToArray()), previous));
    }

    [Fact]
    public void SpanStorageIdentityRequiresExactStartAndLengthAndTreatsEmptyStreamsExplicitly()
    {
        uint[] values = [3, 4, 3, 4]; uint[] copy = [3, 4];
        Assert.True(D3D12ImmutableGeometry.SameStorage<uint>(values.AsSpan(0, 2), values.AsSpan(0, 2)));
        Assert.False(D3D12ImmutableGeometry.SameStorage<uint>(values.AsSpan(0, 2), values.AsSpan(2, 2)));
        Assert.False(D3D12ImmutableGeometry.SameStorage<uint>(values.AsSpan(0, 2), values.AsSpan(0, 3)));
        Assert.False(D3D12ImmutableGeometry.SameStorage<uint>(values.AsSpan(0, 2), copy));
        Assert.True(D3D12ImmutableGeometry.SameStorage<uint>(ReadOnlySpan<uint>.Empty, values.AsSpan(2, 0)));
        Assert.False(D3D12ImmutableGeometry.SameStorage<uint>(values.AsSpan(0, 1), ReadOnlySpan<uint>.Empty));
    }

    [Fact]
    public void StreamStorageIdentityDoesNotBakeWorldPlacementIntoVertexBytes()
    {
        var scene = Scene();
        // World is consumed through per-draw constants. Equal local vertex bytes in a different
        // independently constructed snapshot are not a reason to suppress a new geometry upload.
        var placed = new SceneSnapshot(scene.Vertices, scene.Indices, scene.Materials, scene.Textures,
            Matrix4x4.CreateTranslation(20, 30, 40));
        Assert.NotEqual(new D3D12VertexStreamIdentity(scene), new D3D12VertexStreamIdentity(placed));
        Assert.Equal(new D3D12IndexStreamIdentity(scene), new D3D12IndexStreamIdentity(placed));
        Assert.NotEqual(scene.World, placed.World);
        Assert.Equal(scene.Vertices.ToArray(), placed.Vertices.ToArray());
    }

    [Fact]
    public void RepeatedSharedTopologyChecksAllocateNoPayloadSizedBuffers()
    {
        var scene = Scene(); var pose = scene.WithDeformedVertices(scene.Vertices.ToArray());
        var first = new D3D12IndexStreamIdentity(scene); var second = new D3D12IndexStreamIdentity(pose);
        // Warm the measured loop and equality path before sampling this thread's allocations.
        Assert.True(CompareRepeatedly(first, second));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool equal = CompareRepeatedly(first, second);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(equal); Assert.InRange(allocated, 0, 128);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static bool CompareRepeatedly(D3D12IndexStreamIdentity first, D3D12IndexStreamIdentity second)
    {
        bool equal = true;
        for (int index = 0; index < 10_000; index++) equal &= first.Equals(second);
        return equal;
    }

    internal static SceneSnapshot Scene(bool reverse = false) => new(
        new SceneVertex[] { new(Vector3.Zero, Vector3.UnitY, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitY, Vector2.UnitX), new(Vector3.UnitZ, Vector3.UnitY, Vector2.UnitY) },
        reverse ? new uint[] { 0, 2, 1 } : new uint[] { 0, 1, 2 },
        new[] { new SceneMaterial("body", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0) });
}
