using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12HardwareRayGiBvhInputsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly RayGiOptions Gi = new(LightingMode.HardwareRayTracing, Strength: .5f,
        SamplesPerPixel: 16, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f);

    [Fact]
    public void AnimatedTransportRetainsTexelBufferAndMatchesFreshUncachedRendering()
    {
        using var session = new D3D12FrameSession();
        RequireHardware(session);
        var fixture = D3D12RayGiTests.Bounce();
        var source = fixture.Surface.Snapshot;
        var texture = new SceneTexture("bounce", 2, 1, [255,80,30,255, 40,120,255,255]);
        var mesh = new SceneSnapshot(source.Vertices, source.Indices,
            source.Materials.Select(m => m with { DiffuseTexture = 0 }), [texture],
            source.World, source.Camera, source.LightDirection);
        var gi = Gi with { Denoiser = RayGiDenoiser.Off };
        ID3D12Resource? retained = null;
        object? identity = null;
        for (int step = 0; step < 3; step++)
        {
            var vertices = mesh.Vertices.ToArray();
            for (int i = 4; i < vertices.Length; i++)
                vertices[i] = vertices[i] with { Position = vertices[i].Position + new Vector3(step * .2f, 0, -step * .1f) };
            var changed = mesh.WithDeformedVertices(vertices);
            OriginalFrameScene Frame(SceneSnapshot snapshot) => new(
                new OriginalStyleScene(snapshot, fixture.Surface.Materials, fixture.Surface.Profile),
                options: fixture.Options, materials: fixture.Materials);
            var output = session.Capture(Frame(changed), 47, 35, rayGiOptions: gi);
            var pixels = CachedBuffer(session, "gi:texels");
            if (retained is null) { retained = pixels.Resource; identity = pixels.Identity; }
            else { Assert.Same(retained, pixels.Resource); Assert.Equal(identity, pixels.Identity); }
            // New immutable texture list forces fresh CPU packing and GPU upload in the oracle.
            var fresh = new SceneSnapshot(changed.Vertices, changed.Indices, changed.Materials,
                changed.Textures, changed.World, changed.Camera, changed.LightDirection);
            using var oracle = new D3D12FrameSession();
            Assert.Equal(output.Rgba, oracle.Capture(Frame(fresh), 47, 35, rayGiOptions: gi).Rgba);
        }
    }

    [Fact]
    public void DynamicPreviewMatchesCaptureAcrossPoseChangesAndRetainsTheSamePoseUpload()
    {
        using var preview = new D3D12FrameSession();
        using var oracle = new D3D12FrameSession();
        RequireHardware(preview);
        var gi = Gi with { Denoiser = RayGiDenoiser.Off };
        var first = Pose(0);
        var moved = Pose(3, moveReceiver: true);

        byte[] initial = ReadPreview(preview, first, gi);
        var firstTriangles = CachedBuffer(preview, "gi:triangles");
        Assert.Equal(initial, preview.Capture(first, 47, 35, rayGiOptions: gi).Rgba);
        Assert.Same(firstTriangles.Resource, CachedBuffer(preview, "gi:triangles").Resource);

        byte[] changed = ReadPreview(preview, moved, gi);
        Assert.NotEqual(firstTriangles.Identity, CachedBuffer(preview, "gi:triangles").Identity);
        Assert.False(initial.AsSpan().SequenceEqual(changed));
        Assert.Equal(oracle.Capture(moved, 47, 35, rayGiOptions: gi).Rgba, changed);
    }

    [Fact]
    public void HardwareDeformationsKeepSoftwareBvhLazyAndReuseFixedDummyBuffers()
    {
        using var session = new D3D12FrameSession();
        RequireHardware(session);
        ID3D12Resource? firstNodes = null, firstOrder = null;
        object? firstIdentity = null;
        for (int step = 0; step < 4; step++)
        {
            var frame = Pose(step);
            var scene = RayGiScene.FromOriginal(frame);
            Assert.False(Bvh(scene).IsValueCreated);
            using var output = session.Render(frame, 47, 35, rayGiOptions: Gi);
            Assert.False(Bvh(scene).IsValueCreated);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            var nodes = CachedBuffer(session, "gi:nodes");
            var order = CachedBuffer(session, "gi:order");
            Assert.Equal((ulong)Marshal.SizeOf<RayGiBvhNodeGpu>(), nodes.Resource.Description.Width);
            Assert.Equal(sizeof(uint), checked((int)order.Resource.Description.Width));
            Assert.NotEqual((object)scene.GeometryIdentity, nodes.Identity);
            Assert.Same(nodes.Identity, order.Identity);
            if (step == 0)
            {
                firstNodes = nodes.Resource; firstOrder = order.Resource; firstIdentity = nodes.Identity;
            }
            else
            {
                Assert.Same(firstNodes, nodes.Resource);
                Assert.Same(firstOrder, order.Resource);
                Assert.Same(firstIdentity, nodes.Identity);
            }
        }
    }

    [Fact]
    public void SoftwareSwitchBuildsRealBvhAndReturningToHardwareRestoresDummyBindings()
    {
        using var session = new D3D12FrameSession();
        RequireHardware(session);
        var frame = Pose(0);
        var scene = RayGiScene.FromOriginal(frame);
        var hardware = session.Capture(frame, 47, 35, rayGiOptions: Gi);
        Assert.False(Bvh(scene).IsValueCreated);
        object dummyIdentity = CachedBuffer(session, "gi:nodes").Identity;

        session.Capture(frame, 47, 35, rayGiOptions: Gi with { Mode = LightingMode.SoftwareRayTracing });
        Assert.True(Bvh(scene).IsValueCreated);
        var nodes = CachedBuffer(session, "gi:nodes");
        var order = CachedBuffer(session, "gi:order");
        Assert.Equal((ulong)(scene.Bvh.Nodes.Length * Marshal.SizeOf<RayGiBvhNodeGpu>()), nodes.Resource.Description.Width);
        Assert.Equal((ulong)(scene.Bvh.TriangleOrder.Length * sizeof(uint)), order.Resource.Description.Width);
        Assert.Equal((object)scene.GeometryIdentity, nodes.Identity);
        Assert.Equal((object)scene.GeometryIdentity, order.Identity);

        Assert.Equal(hardware.Rgba, session.Capture(frame, 47, 35, rayGiOptions: Gi).Rgba);
        nodes = CachedBuffer(session, "gi:nodes"); order = CachedBuffer(session, "gi:order");
        Assert.Same(dummyIdentity, nodes.Identity);
        Assert.Same(dummyIdentity, order.Identity);
        Assert.Equal((ulong)Marshal.SizeOf<RayGiBvhNodeGpu>(), nodes.Resource.Description.Width);
        Assert.Equal(sizeof(uint), checked((int)order.Resource.Description.Width));
    }

    private static Lazy<RayGiBvh> Bvh(RayGiScene scene)
        => (Lazy<RayGiBvh>)typeof(RayGiScene).GetField("bvh", Private)!.GetValue(scene)!;

    private static byte[] ReadPreview(D3D12FrameSession session, OriginalFrameScene frame, RayGiOptions gi)
    {
        const int width = 47, height = 35, rowBytes = width * 4;
        var footprints = new PlacedSubresourceFootPrint[1];
        ID3D12Resource? readback = null;
        ulong size = 0;
        try
        {
            using var output = session.RenderForPresent(frame, width, height, gi, (context, target) =>
            {
                context.Device.GetCopyableFootprints(target.Resource.Description, 0, 1, 0, footprints, new int[1], new ulong[1], out size);
                readback = context.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest);
                context.Commands.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0, new TextureCopyLocation(target.Resource, 0));
            });
            session.WaitForIdle();
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.NotNull(readback);
            var mapped = readback.Map<byte>(0, checked((int)size));
            var rgba = new byte[height * rowBytes];
            try
            {
                for (int y = 0; y < height; y++)
                    mapped.Slice(y * footprints[0].Footprint.RowPitch, rowBytes).CopyTo(rgba.AsSpan(y * rowBytes, rowBytes));
            }
            finally { readback.Unmap(0); }
            return rgba;
        }
        finally { readback?.Dispose(); }
    }

    private static (ID3D12Resource Resource, object Identity) CachedBuffer(D3D12FrameSession session, string name)
    {
        var renderer = (D3D12FrameRenderer)typeof(D3D12FrameSession).GetField("renderer", Private)!.GetValue(session)!;
        var cache = (D3D12GpuCache)typeof(D3D12FrameRenderer).GetField("cache", Private)!.GetValue(renderer)!;
        var entries = (Dictionary<string, D3D12RetainedResource>)typeof(D3D12GpuCache).GetField("replaceableObjects", Private)!.GetValue(cache)!;
        var entry = entries[name];
        return ((ID3D12Resource)entry.Value, entry.Identity);
    }

    private static void RequireHardware(D3D12FrameSession session)
    {
        var support = session.QueryRayGiCapabilities();
        Assert.True(support.HardwareAvailable, support.UnavailableReason ?? $"{session.AdapterName}: DXR unavailable");
    }

    private static OriginalFrameScene Pose(int step, bool moveReceiver = false)
    {
        var frame = D3D12RayGiTests.Bounce();
        var mesh = frame.Surface.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for (int i = moveReceiver ? 0 : 4; i < vertices.Length; i++)
            vertices[i] = vertices[i] with { Position = vertices[i].Position + new Vector3(step * .15f, 0, -step * .1f) };
        var snapshot = mesh.WithDeformedVertices(vertices);
        return new(new OriginalStyleScene(snapshot, frame.Surface.Materials, frame.Surface.Profile),
            options: frame.Options, materials: frame.Materials);
    }
}
