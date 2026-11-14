using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12RayGiRebuildPolicyTests
{
    private static Vector3[] Block()
        => [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.Zero, Vector3.UnitX,
            Vector3.UnitY, Vector3.Zero, Vector3.UnitX, Vector3.UnitY];

    [Fact]
    public void SmallDeformationsCompareWithTheLastFullBuildRatherThanTheLastUpdate()
    {
        var initial = Block();
        var small = initial.Select(p => p + new Vector3(.1f, 0, 0)).ToArray();
        var accumulated = initial.Select(p => p + new Vector3(.4f, 0, 0)).ToArray();
        Assert.False(D3D12HardwareRayGi.RequiresRebuild(initial, small, 1));
        Assert.True(D3D12HardwareRayGi.RequiresRebuild(initial, accumulated, 2));
        Assert.False(D3D12HardwareRayGi.RequiresRebuild(small, accumulated, 2));
    }

    [Fact]
    public void AnyMovedBlockCanInvalidateAnOtherwiseStaticLargeScene()
    {
        var initial = Block().Concat(Block().Select(p => p * 1000)).ToArray();
        var current = initial.ToArray();
        current[0] += Vector3.UnitX;
        Assert.True(D3D12HardwareRayGi.RequiresRebuild(initial, current, 0));
        Assert.Equal(Vector3.Zero, initial[0]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(31, false)]
    [InlineData(32, true)]
    [InlineData(100, true)]
    public void ARefitChainHasABoundedAgeEvenWithUnchangedSamples(int updates, bool rebuild)
        => Assert.Equal(rebuild, D3D12HardwareRayGi.RequiresRebuild(Block(), Block(), updates));

    [Fact]
    public void DegenerateAndVeryLargeFiniteBoundsDoNotHideMotionThroughOverflow()
    {
        var tiny = new Vector3[9];
        Assert.False(D3D12HardwareRayGi.RequiresRebuild(tiny, tiny, 0));
        var moved = tiny.ToArray(); moved[8] = new(.001f, 0, 0);
        Assert.True(D3D12HardwareRayGi.RequiresRebuild(tiny, moved, 0));
        var large = Enumerable.Repeat(new Vector3(-float.MaxValue, 0, 0), 9).ToArray();
        large[1] = new(float.MaxValue, 0, 0);
        var changed = large.ToArray(); changed[0] = new(float.MaxValue, 0, 0);
        Assert.True(D3D12HardwareRayGi.RequiresRebuild(large, changed, 0));
        Assert.False(D3D12HardwareRayGi.RequiresRebuild(large, large, 0));
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12RayGiAdaptiveRebuildGpuTests
{
    private static readonly RayGiOptions Gi = new(LightingMode.HardwareRayTracing,
        Strength: .5f, SamplesPerPixel: 1, Seed: 17, MaxRayDistance: 100, RayEpsilon: .001f);

    [Fact]
    public void ADiscontinuousPoseRebuildsOnceWhileHeldAndResizedFramesReuseIt()
    {
        using var session = new D3D12FrameSession();
        Assert.True(session.QueryRayGiCapabilities().HardwareAvailable);
        using (session.Render(Pose(0), 47, 35, rayGiOptions: Gi)) { }
        var jump = Pose(12);
        var actual = session.Capture(jump, 47, 35, rayGiOptions: Gi);
        Assert.Equal(1, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
        using var fresh = new D3D12FrameSession();
        Assert.Equal(fresh.Capture(jump, 47, 35, rayGiOptions: Gi).Rgba, actual.Rgba);
        using (var held = session.Render(jump, 61, 43, rayGiOptions: Gi))
        {
            Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
            Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
            Assert.Equal(0, held.Statistics.ReadbackCount);
            Assert.Equal(1, held.Statistics.SubmissionCount);
        }
    }

    [Fact]
    public void CompatibleSmallDeformationsRetainRefitsButPeriodicallyRebuild()
    {
        using var session = new D3D12FrameSession();
        using (session.Render(Pose(0), 31, 23, rayGiOptions: Gi)) { }
        for (int i = 1; i <= 33; i++)
        {
            using var frame = session.Render(Pose(i * .001f), 31, 23, rayGiOptions: Gi);
            Assert.Equal(i == 33 ? 1 : 0, session.RayGiStatistics!.AccelerationBuilds);
            Assert.Equal(i == 33 ? 0 : 1, session.RayGiStatistics.AccelerationUpdates);
            Assert.Equal(0, frame.Statistics.ReadbackCount);
            Assert.InRange(frame.Statistics.PendingFrames, 0, 3);
        }
        session.WaitForIdle();
    }

    private static OriginalFrameScene Pose(float amount)
    {
        var frame = D3D12RayGiTests.Bounce();
        var mesh = frame.Surface.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for (int i = 4; i < vertices.Length; i++)
            vertices[i] = vertices[i] with { Position = vertices[i].Position + new Vector3(amount, 0, -amount * .5f) };
        return new(new(mesh.WithDeformedVertices(vertices), frame.Surface.Materials, frame.Surface.Profile),
            options: frame.Options, materials: frame.Materials);
    }
}
