using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12CameraHandednessTests
{
    private const int Width = 128, Height = 96;
    private static readonly Vector4 Red = new(1, 0, 0, 1);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void SingleSidedLayersFollowProjectedWindingAcrossCameraAndMirroredWorldTransforms(bool perspective, bool mirrorWorld, bool reverseTriangles)
    {
        var right = Layers(false, perspective, mirrorWorld, reverseTriangles);
        var left = Layers(true, perspective, mirrorWorld, reverseTriangles);
        bool front = ProjectedFront(right.Surface.Snapshot, right.Surface.Snapshot.Camera);
        Assert.Equal(mirrorWorld == reverseTriangles, front);
        Assert.Equal(front, ProjectedFront(left.Surface.Snapshot, left.Surface.Snapshot.Camera));
        using var renderer = new D3D12OriginalStyleRenderer();
        // Exercise both the surface-only recorder and the complete-frame recorder.
        var rightSurface = renderer.Render(right.Surface, Width, Height);
        var leftSurface = renderer.Render(left.Surface, Width, Height);
        AssertLayerCoverage(leftSurface, front);
        AssertHorizontalMirror(rightSurface, leftSurface);
        Assert.Equal(rightSurface.Rgba, renderer.Render(right.Surface, Width, Height).Rgba);

        // Keep the snapshot RH, then override it with the actual authored LH camera.
        var overridden = new OriginalFrameScene(right.Surface,
            options: right.Options! with { CameraOverride = left.Surface.Snapshot.Camera });
        var rightFrame = renderer.Render(right, Width, Height);
        var leftFrame = renderer.Render(overridden, Width, Height);
        AssertLayerCoverage(leftFrame, front);
        AssertHorizontalMirror(rightFrame, leftFrame);
        Assert.Equal(rightFrame.Rgba, renderer.Render(right, Width, Height).Rgba);
        Assert.Equal(leftSurface.Rgba, leftFrame.Rgba);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OriginalAndModernOutlineDrawOnlyTheOuterHullAndReuseTheCorrectCameraPipeline(bool modern, bool simple)
    {
        var right = OutlinedBox(false, modern, simple);
        var left = OutlinedBox(true, modern, simple);
        using var renderer = new D3D12OriginalStyleRenderer();
        var rightFrame = renderer.Render(right, Width, Height);
        var leftFrame = renderer.Render(left, Width, Height);
        AssertPixel(rightFrame, Width / 2, Height / 2, 255, 255, 255);
        AssertPixel(leftFrame, Width / 2, Height / 2, 255, 255, 255);
        Assert.True(CountRed(leftFrame) > 20, "The test must draw a visible expanded outer contour.");
        AssertHorizontalMirror(rightFrame, leftFrame);
        Assert.Equal(rightFrame.Rgba, renderer.Render(right, Width, Height).Rgba);
        Assert.Equal(leftFrame.Rgba, renderer.Render(left, Width, Height).Rgba);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporalCoverageMatchesTheVisibleFrontAndExpandedContourInBothCameraConventions(bool modern)
    {
        var right = OutlinedBox(false, modern, false);
        var left = OutlinedBox(true, modern, false);
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var rightDepth = CaptureDepth(runtime, pass, right);
        var leftDepth = CaptureDepth(runtime, pass, left);
        using var renderer = new D3D12OriginalStyleRenderer();
        var leftColor = renderer.Render(left, Width, Height);
        int visible = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x, reflected = y * Width + Width - 1 - x;
            Assert.Equal(rightDepth[reflected] < 1, leftDepth[i] < 1);
            Assert.InRange(MathF.Abs(rightDepth[reflected] - leftDepth[i]), 0, 2e-6f);
            if (leftColor.Rgba[4 * i] == 255)
            {
                visible++;
                Assert.True(leftDepth[i] < 1, "Visible surface and outline fragments must supply temporal depth.");
            }
        }
        Assert.True(visible > 500);
        var camera = left.Surface.Snapshot.Camera;
        var front = Vector4.Transform(new Vector4(0, 0, 0, 1), camera.ViewProjection(Width, Height));
        Assert.InRange(MathF.Abs(leftDepth[Height / 2 * Width + Width / 2] - front.Z / front.W), 0, 2e-6f);
        Assert.Equal(rightDepth, CaptureDepth(runtime, pass, right));
        Assert.Equal(0, runtime.Readbacks);
    }

    private static OriginalFrameScene Layers(bool leftHanded, bool perspective, bool mirrorWorld, bool reverseTriangles)
    {
        var vertices = new List<SceneVertex>();
        var indices = new List<uint>();
        var materials = new List<SceneMaterial>();
        void Quad(string name, float minX, float maxX, float minY, float maxY, float z, Vector4 color)
        {
            uint first = (uint)vertices.Count;
            foreach (var point in new Vector3[] { new(minX, minY, z), new(maxX, minY, z), new(maxX, maxY, z), new(minX, maxY, z) })
                vertices.Add(new(point, -Vector3.UnitZ, Vector2.Zero));
            materials.Add(new(name, indices.Count, 6, color, Vector3.Zero, Vector3.Zero, 1));
            // PMX's front face has a geometric -Z cross product, matching its normal.
            if (reverseTriangles)
                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            else
                indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }
        Quad("near red", -1, .7f, -.8f, .8f, 0, Red);
        Quad("far blue", -1.15f, 1.05f, -1, 1, .5f, new(0, 0, 1, 1));
        Quad("asymmetric green marker", 1.25f, 1.65f, -.3f, .4f, 0, new(0, 1, 0, 1));
        var camera = new SceneCamera(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 20,
            perspective ? 45 : null, leftHanded);
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials,
            world: mirrorWorld ? Matrix4x4.CreateScale(-1, 1, 1) : Matrix4x4.Identity, camera: camera,
            lightDirection: -Vector3.UnitZ);
        var surface = new OriginalStyleScene(snapshot, materials.Select(_ => new OriginalStyleMaterialBinding(Vector3.One)),
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp));
        return new(surface, options: D3D12OriginalFrameTests.Options());
    }

    private static OriginalFrameScene OutlinedBox(bool leftHanded, bool modern, bool simple)
    {
        var template = D3D12OriginalOutlineTests.Box();
        var camera = template.Surface.Snapshot.Camera with { LeftHanded = leftHanded };
        var snapshot = template.Surface.Snapshot.WithCamera(camera);
        var surface = new OriginalStyleScene(snapshot, template.Surface.Materials, template.Surface.Profile);
        var outline = new OriginalOutlineData(template.Outline!.Vertices, [new(1, Red)]);
        var flags = template.Materials.Select(flag => flag with
        {
            CharacterOutlineStyle = modern ? new CharacterOutlineStyle(1, Red) : null
        });
        return new(surface, outline, template.OutlineOptions, simple ? null : D3D12OriginalFrameTests.Options(), flags);
    }

    private static void AssertHorizontalMirror(RenderedFrame right, RenderedFrame left)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            Assert.Equal(right.Rgba.AsSpan((y * Width + Width - 1 - x) * 4, 4).ToArray(),
                left.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray());
    }

    private static bool ProjectedFront(SceneSnapshot scene, SceneCamera camera)
    {
        var transform = scene.World * camera.ViewProjection(Width, Height);
        Vector2 Project(uint index)
        {
            var point = Vector4.Transform(new Vector4(scene.Vertices[(int)index].Position, 1), transform);
            return new(point.X / point.W, -point.Y / point.W);
        }
        var a = Project(scene.Indices[0]);
        var b = Project(scene.Indices[1]);
        var c = Project(scene.Indices[2]);
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        // RH's established screen-space front convention is negative area. LH mirrors
        // the screen axis; a mirrored world still reverses the physical front face.
        return camera.LeftHanded ? area > 0 : area < 0;
    }

    private static void AssertLayerCoverage(RenderedFrame frame, bool front)
    {
        if (front)
        {
            AssertPixel(frame, Width / 2, Height / 2, 255, 0, 0);
            Assert.True(CountRed(frame) > 500, "Visible front faces must cover a nontrivial region.");
        }
        else
        {
            // Reflecting world geometry preserves historical back-face culling. It
            // becomes visible only when the authored triangle order is also reversed.
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                AssertPixel(frame, x, y, 20, 24, 32);
        }
    }

    private static int CountRed(RenderedFrame frame)
    {
        int count = 0;
        for (int i = 0; i < frame.Rgba.Length; i += 4)
            if (frame.Rgba[i] == 255 && frame.Rgba[i + 1] == 0 && frame.Rgba[i + 2] == 0) count++;
        return count;
    }

    private static void AssertPixel(RenderedFrame frame, int x, int y, byte red, byte green, byte blue)
        => Assert.Equal(new byte[] { red, green, blue, 255 }, frame.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray());

    private static float[] CaptureDepth(D3D12GpuRuntime runtime, D3D12TemporalInputPass pass, OriginalFrameScene frame)
    {
        var context = runtime.Begin();
        try
        {
            var inputs = pass.Record(context, frame, frame, Width, Height);
            var layout = new PlacedSubresourceFootPrint[1];
            runtime.Device.GetCopyableFootprints(inputs.Depth.Description, 0, 1, 0, layout, new int[1], new ulong[1], out ulong size);
            var buffer = context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
            context.Commands.ResourceBarrierTransition(inputs.Depth, D3D12TemporalInputs.ShaderReadState, ResourceStates.CopySource);
            context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, layout[0]), 0, 0, 0, new TextureCopyLocation(inputs.Depth, 0));
            runtime.Wait(runtime.Submit());
            var depth = new float[Width * Height];
            var bytes = buffer.Map<byte>(0, checked((int)size));
            try
            {
                for (int y = 0; y < Height; y++)
                    MemoryMarshal.Cast<byte, float>(bytes.Slice(y * layout[0].Footprint.RowPitch, Width * 4))
                        .CopyTo(depth.AsSpan(y * Width, Width));
            }
            finally { buffer.Unmap(0); }
            return depth;
        }
        finally { runtime.Abort(); }
    }
}
