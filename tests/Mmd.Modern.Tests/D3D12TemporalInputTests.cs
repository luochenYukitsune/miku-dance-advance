using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12TemporalInputTests
{
    [Fact]
    public void StaticGeometryHasZeroMotionDespiteDifferentProjectionJitter()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var scene = Scene();
        var result = Capture(runtime, pass,
            scene.WithCamera(scene.Camera with { ProjectionJitterPixels = new(.4f, -.3f) }),
            scene.WithCamera(scene.Camera with { ProjectionJitterPixels = new(-.2f, .5f) }), 65, 49);
        Assert.InRange(result.Depth[24 * 65 + 32], .39999f, .40001f);
        Assert.All(result.Motion, v => Assert.True(v.Length() < .0001f, $"Unexpected motion {v}."));
        Assert.Equal(1f, result.Depth[0]);
        Assert.Equal(0, runtime.Readbacks); // Only the test helper explicitly copies data to the CPU.
    }

    [Fact]
    public void CurrentJitterMovesRasterCoverageWhileMotionRemainsUnjittered()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var scene = Scene();
        var baseline = Capture(runtime, pass, scene, scene, 65, 49);
        var shifted = Capture(runtime, pass, scene.WithCamera(scene.Camera with { ProjectionJitterPixels = new(1, 0) }), scene, 65, 49);
        int changed = 0;
        for (int y = 0; y < 49; y++) for (int x = 1; x < 65; x++)
        {
            int pixel = y * 65 + x;
            Assert.Equal(baseline.Depth[pixel - 1] < 1, shifted.Depth[pixel] < 1);
            if ((baseline.Depth[pixel] < 1) != (shifted.Depth[pixel] < 1)) changed++;
        }
        Assert.True(changed > 20);
        Assert.All(shifted.Motion, v => Assert.True(v.Length() < .0001f));
    }

    [Theory]
    [InlineData(true, 8f)]
    [InlineData(false, -8f)]
    public void CameraAndDeformedVertexMotionUseBackwardPixelSignAndScale(bool camera, float expected)
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var previous = Scene();
        var current = camera
            ? previous.WithCamera(previous.Camera with { Eye = previous.Camera.Eye + new Vector3(.25f, 0, 0), Target = new(.25f, 0, 0) })
            : previous.WithDeformedVertices(previous.Vertices.ToArray().Select(v => v with { Position = v.Position + new Vector3(.25f, 0, 0) }).ToArray());
        var result = Capture(runtime, pass, current, previous, 64, 64);
        Vector2 motion = result.Motion[32 * 64 + 32];
        Assert.InRange(motion.X, expected - .0001f, expected + .0001f);
        Assert.InRange(motion.Y, -.0001f, .0001f);
    }

    [Fact]
    public void WorldTransformAndVerticalDeformationBothContributeToMotion()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var previous = Scene();
        var current = Scene(world: Matrix4x4.CreateTranslation(.25f, 0, 0));
        current = current.WithDeformedVertices(current.Vertices.ToArray().Select(v => v with { Position = v.Position + new Vector3(0, .125f, 0) }).ToArray());
        var result = Capture(runtime, pass, current, previous, 64, 64);
        Vector2 motion = result.Motion[32 * 64 + 32];
        Assert.InRange(motion.X, -8.0001f, -7.9999f);
        Assert.InRange(motion.Y, 3.9999f, 4.0001f);
    }

    [Fact]
    public void NearestCurrentSurfaceWinsAndDepthIsDeviceSpaceForPerspective()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var orthographic = Scene(overlap: true);
        var result = Capture(runtime, pass, orthographic, orthographic, 65, 49);
        Assert.InRange(result.Depth[24 * 65 + 32], .29999f, .30001f);
        var perspective = Scene().WithCamera(Scene().Camera with { VerticalFovDegrees = 45 });
        result = Capture(runtime, pass, perspective, perspective, 65, 49);
        // Left handed near=1, far=11, view z=5 => 11/10 - 11/(10*5).
        Assert.InRange(result.Depth[24 * 65 + 32], .87999f, .88001f);
    }

    [Fact]
    public void OddSizeResizeAndFrameRetirementKeepOnePipelineAndNoOrdinaryReadbacks()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var scene = Scene();
        foreach (var (width, height) in new[] { (65, 49), (97, 73), (1, 1), (65, 49), (99, 51), (65, 49) })
        {
            var context = runtime.Begin();
            var inputs = pass.Record(context, scene, scene, width, height);
            Assert.Equal(width, inputs.Width); Assert.Equal(height, inputs.Height);
            Assert.Equal((ulong)width, inputs.Depth.Description.Width);
            Assert.Equal(height, inputs.MotionVectors.Description.Height);
            runtime.Submit();
            Assert.InRange(runtime.LiveFrameResources, 1, 24);
        }
        Assert.Equal(1, runtime.PipelineCreations);
        Assert.Equal(2, runtime.ShaderCompilations);
        Assert.Equal(0, runtime.Readbacks);
        runtime.WaitForIdle();
        Assert.Equal(3, runtime.LiveFrameResources); // One reusable constant upload page per warmed frame slot.
    }

    [Fact]
    public void TopologyAndUnsupportedCoverageRejectBeforeAllocatingThenRecover()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var scene = Scene();
        var reordered = new SceneSnapshot(scene.Vertices, new uint[] { 0, 2, 1 }, scene.Materials, camera: scene.Camera);
        var context = runtime.Begin();
        Assert.Throws<NotSupportedException>(() => pass.Record(context, scene, reordered, 65, 49));
        var translucent = scene.WithMaterials(scene.Materials.Select(m => m with { Diffuse = new(1, 1, 1, .5f) }));
        Assert.Throws<NotSupportedException>(() => pass.Record(context, translucent, scene, 65, 49));
        var texture = new SceneTexture("alpha", 1, 1, new byte[] { 255, 255, 255, 0 });
        var cutout = new SceneSnapshot(scene.Vertices, scene.Indices, [scene.Materials[0] with { DiffuseTexture = 0 }], [texture], camera: scene.Camera);
        Assert.Throws<NotSupportedException>(() => pass.Record(context, cutout, cutout, 65, 49));
        Assert.Equal(0, runtime.FrameOwnedAllocations);
        pass.Record(context, scene, scene, 65, 49);
        runtime.Wait(runtime.Submit());
    }

    private static SceneSnapshot Scene(Matrix4x4? world = null, bool overlap = false)
    {
        SceneVertex[] vertices = [new(new(-.8f, -.8f, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(.8f, -.8f, 0), -Vector3.UnitZ, Vector2.Zero), new(new(0, .8f, 0), -Vector3.UnitZ, Vector2.Zero)];
        uint[] indices = [0, 1, 2];
        if (overlap)
        {
            // Draw the nearer triangle first so an absent depth test would overwrite it with the far one.
            vertices = vertices.Select(v => v with { Position = v.Position - Vector3.UnitZ }).Concat(vertices).ToArray();
            indices = [0, 1, 2, 3, 4, 5];
        }
        return new(vertices, indices, [new("opaque", 0, indices.Length, Vector4.One, Vector3.Zero, Vector3.One, 1)],
            world: world, camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2, 1, 11, LeftHanded: true));
    }

    private static (float[] Depth, Vector2[] Motion) Capture(D3D12GpuRuntime runtime, D3D12TemporalInputPass pass,
        SceneSnapshot current, SceneSnapshot previous, int width, int height)
    {
        var context = runtime.Begin();
        try
        {
            var inputs = pass.Record(context, current, previous, width, height);
            var depth = Copy(inputs.Depth); var motion = Copy(inputs.MotionVectors);
            runtime.Wait(runtime.Submit());
            return (Read<float>(depth, 4), Read<Vector2>(motion, 8));

            (ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Bytes) Copy(ID3D12Resource source)
            {
                var layout = new PlacedSubresourceFootPrint[1];
                runtime.Device.GetCopyableFootprints(source.Description, 0, 1, 0, layout, new int[1], new ulong[1], out ulong size);
                var buffer = context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
                context.Commands.ResourceBarrierTransition(source, D3D12TemporalInputs.ShaderReadState, ResourceStates.CopySource);
                context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, layout[0]), 0, 0, 0, new TextureCopyLocation(source, 0));
                return (buffer, layout[0], checked((int)size));
            }
            T[] Read<T>((ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Bytes) source, int stride) where T : unmanaged
            {
                var output = new T[width * height];
                var data = source.Resource.Map<byte>(0, source.Bytes);
                try
                {
                    for (int y = 0; y < height; y++)
                        MemoryMarshal.Cast<byte, T>(data.Slice(y * source.Footprint.Footprint.RowPitch, width * stride)).CopyTo(output.AsSpan(y * width, width));
                }
                finally { source.Resource.Unmap(0); }
                return output;
            }
        }
        finally { runtime.Abort(); }
    }
}
