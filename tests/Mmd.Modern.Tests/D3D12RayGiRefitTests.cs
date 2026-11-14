using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12RayGiRefitTests
{
    private static readonly RayGiOptions Gi = new(LightingMode.HardwareRayTracing, Strength: .5f,
        SamplesPerPixel: 16, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f);

    [Fact]
    public void TwelveQueuedDeformationsUpdateDisjointPairsAndMatchFreshBuildAndSoftware()
    {
        using var session = new D3D12FrameSession();
        Require(session);
        var initial = Pose(0);
        var first = session.Capture(initial, 79, 59, rayGiOptions: Gi);
        Assert.Equal(1, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
        int cached = session.Statistics!.CachedResources;
        OriginalFrameScene final = initial;
        for (int i = 1; i <= 12; i++)
        {
            final = Pose(i);
            using var output = session.Render(final, 79, 59, rayGiOptions: Gi);
            Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
            Assert.Equal(1, session.RayGiStatistics.AccelerationUpdates);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            Assert.InRange(output.Statistics.PendingFrames, 0, 3);
            Assert.InRange(output.Statistics.CachedResources, cached, cached + 2);
        }
        var actual = session.Capture(final, 79, 59, rayGiOptions: Gi);
        Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
        using var fresh = new D3D12FrameSession();
        var rebuilt = fresh.Capture(final, 79, 59, rayGiOptions: Gi);
        Assert.Equal(1, fresh.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, fresh.RayGiStatistics.AccelerationUpdates);
        Assert.Equal(rebuilt.Rgba, actual.Rgba);
        var software = fresh.Capture(final, 79, 59,
            rayGiOptions: Gi with { Mode = LightingMode.SoftwareRayTracing });
        for (int i = 0; i < actual.Rgba.Length; i++)
            Assert.InRange(Math.Abs(actual.Rgba[i] - software.Rgba[i]), 0, 1);
        Assert.NotEqual(first.Rgba, actual.Rgba);
        session.WaitForIdle();
        Assert.InRange(session.Runtime.LiveFrameResources, 0, 3);
    }

    [Fact]
    public void StaticThenDynamicAndChangedTriangleCountRebuildButCompatiblePosesUpdate()
    {
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        Assert.False(runtime.IsWarp);
        var source = D3D12RayGiTests.Bounce();
        var context = runtime.Begin();
        var staticPair = D3D12HardwareRayGi.GetAccelerationStructure(context, cache, RayGiScene.FromOriginal(source), false);
        Assert.False(staticPair.UpdateCapable);
        Assert.False(staticPair.WasUpdate);
        runtime.Submit();
        context = runtime.Begin();
        var first = D3D12HardwareRayGi.GetAccelerationStructure(context, cache, RayGiScene.FromOriginal(Pose(0)), true);
        Assert.True(first.UpdateCapable);
        Assert.False(first.WasUpdate); // A static source was not built with AllowUpdate.
        runtime.Submit();
        context = runtime.Begin();
        var scene = RayGiScene.FromOriginal(Pose(1));
        var second = D3D12HardwareRayGi.GetAccelerationStructure(context, cache, scene, true);
        Assert.True(second.WasUpdate);
        Assert.NotEqual(first.BottomLevel.GPUVirtualAddress, second.BottomLevel.GPUVirtualAddress);
        Assert.NotEqual(first.TopLevel.GPUVirtualAddress, second.TopLevel.GPUVirtualAddress);
        Assert.Same(second, D3D12HardwareRayGi.GetAccelerationStructure(context, cache, scene, true));
        runtime.Submit();
        context = runtime.Begin();
        var forced = D3D12HardwareRayGi.GetAccelerationStructure(context, cache, scene, true, forceRebuild: true);
        Assert.False(forced.WasUpdate); // Identical geometry must still rebuild when explicitly requested by this test.
        Assert.NotEqual(second.BottomLevel.GPUVirtualAddress, forced.BottomLevel.GPUVirtualAddress);
        runtime.Submit();
        context = runtime.Begin();
        var mesh = source.Surface.Snapshot;
        var materials = mesh.Materials.ToArray();
        materials[1] = materials[1] with { IndexCount = 3 };
        var smaller = new SceneSnapshot(mesh.Vertices, mesh.Indices[..9], materials,
            camera: mesh.Camera, lightDirection: mesh.LightDirection);
        smaller = smaller.WithDeformedVertices(smaller.Vertices.ToArray());
        var frame = WithSnapshot(source, smaller);
        var third = D3D12HardwareRayGi.GetAccelerationStructure(context, cache, RayGiScene.FromOriginal(frame), true);
        Assert.Equal(3, third.TriangleCount);
        Assert.False(third.WasUpdate);
        Assert.True(third.UpdateCapable);
        runtime.Submit();
        runtime.WaitForIdle();
        Assert.Equal(RaytracingAccelerationStructureBuildFlags.PreferFastTrace, D3D12HardwareRayGi.BuildFlags(false));
        Assert.Equal(RaytracingAccelerationStructureBuildFlags.PreferFastTrace | RaytracingAccelerationStructureBuildFlags.AllowUpdate,
            D3D12HardwareRayGi.BuildFlags(true));
    }

    [Fact]
    public void AbortedUpdateColdRebuildsAndResizeReusesSubmittedGeometry()
    {
        using var session = new D3D12FrameSession();
        Require(session);
        using (session.Render(Pose(0), 47, 35, rayGiOptions: Gi)) { }
        var changed = Pose(1);
        long submissions = session.Runtime.Submissions;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changed, 47, 35, Gi,
            (_, _) =>
            {
                Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
                Assert.Equal(1, session.RayGiStatistics.AccelerationUpdates);
                throw new OperationCanceledException("Cancel recorded AS update before submit");
            }));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        var recovered = session.Capture(changed, 61, 43, rayGiOptions: Gi);
        Assert.Equal(1, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
        using var fresh = new D3D12FrameSession();
        Assert.Equal(fresh.Capture(changed, 61, 43, rayGiOptions: Gi).Rgba, recovered.Rgba);
        session.Capture(changed, 47, 35, rayGiOptions: Gi);
        Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    public void SpatialSamplesShareExactlyOneUpdatePerGeometrySubmission(int samples)
    {
        using var session = new D3D12FrameSession();
        Require(session);
        using (session.Render(Pose(0), 17, 13, rayGiOptions: Gi)) { }
        var changed = Pose(1);
        changed = new(changed.Surface, options: changed.Options! with { SpatialSamples = samples }, materials: changed.Materials);
        var actual = session.Capture(changed, 17, 13, rayGiOptions: Gi with { SamplesPerPixel = 1 });
        Assert.Equal(samples, session.RayGiStatistics!.DispatchCount);
        Assert.Equal(0, session.RayGiStatistics.AccelerationBuilds);
        Assert.Equal(1, session.RayGiStatistics.AccelerationUpdates);
        Assert.Equal(1, session.Statistics!.SubmissionCount);
        using var fresh = new D3D12FrameSession();
        Assert.Equal(fresh.Capture(changed, 17, 13, rayGiOptions: Gi with { SamplesPerPixel = 1 }).Rgba, actual.Rgba);
        Assert.Equal(1, fresh.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, fresh.RayGiStatistics.AccelerationUpdates);
    }

    [Fact]
    public void DynamicPreflightIncludesUpdateCapableSourceDestinationScratchAndUploadsOncePerFrame()
    {
        using var runtime = new D3D12GpuRuntime();
        Assert.False(runtime.IsWarp);
        var frame = Pose(0);
        var hardware = D3D12SsaaGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, 2, Gi, false, false, false, frame: frame);
        var software = D3D12SsaaGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, 2,
            Gi with { Mode = LightingMode.SoftwareRayTracing }, false, false, false, frame: frame);
        var many = D3D12SsaaGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, 32, Gi, false, false, false, frame: frame);
        Assert.Equal(software.PerSampleBytes, hardware.PerSampleBytes);
        Assert.True(hardware.PerFrameBytes > software.PerFrameBytes);
        Assert.Equal(30UL * hardware.PerSampleBytes, many.PerFrameBytes - hardware.PerFrameBytes);
        Assert.Equal(3UL * hardware.PerFrameBytes, hardware.InFlightBytes);
        var sizes = D3D12HardwareRayGi.PrebuildResources(runtime.Device, 4, true);
        Assert.True(sizes.Bottom > 0 && sizes.Top > 0 && sizes.BottomScratch > 0 && sizes.TopScratch > 0);
        Assert.False(runtime.IsRecording);
        Assert.Equal(0, runtime.Submissions);
    }

    private static void Require(D3D12FrameSession session)
    {
        var support = session.QueryRayGiCapabilities();
        Assert.True(support.HardwareAvailable, support.UnavailableReason ?? $"{session.AdapterName}: DXR unavailable");
    }

    private static OriginalFrameScene Pose(int step)
    {
        var frame = D3D12RayGiTests.Bounce();
        var mesh = frame.Surface.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for (int i = 4; i < vertices.Length; i++)
            vertices[i] = vertices[i] with { Position = vertices[i].Position * new Vector3(.25f, .25f, 1)
                + new Vector3(step * .15f, 0, -step * .1f) };
        return WithSnapshot(frame, mesh.WithDeformedVertices(vertices));
    }

    private static OriginalFrameScene WithSnapshot(OriginalFrameScene frame, SceneSnapshot snapshot)
        => new(new OriginalStyleScene(snapshot, frame.Surface.Materials, frame.Surface.Profile),
            options: frame.Options, materials: frame.Materials);
}
