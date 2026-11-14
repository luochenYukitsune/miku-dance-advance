using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12TemporalGiBoundedBudgetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(32)]
    public void RetirementBoundsTwoWholeHistoriesAndOneWorkspaceWithoutReducingAnySample(int samples)
    {
        const ulong committed = 65536, workspace = 192837, triangles = 76543, otherReservation = 98765;
        var full = D3D12TemporalGiMemoryBudget.WithAdditionalReservation(
            D3D12TemporalGiMemoryBudget.FromResourceSizes(committed, workspace, triangles, samples), otherReservation);
        var bounded = D3D12TemporalGiMemoryBudget.ForBoundedFlight(full);
        ulong history = committed * 4 * (ulong)samples;
        Assert.Equal(full.HistoryBytes, bounded.HistoryBytes);
        Assert.Equal(history, bounded.HistoryBytes);
        Assert.Equal(full.PerFrameBytes, bounded.PerFrameBytes);
        Assert.Equal(2 * (history + triangles), bounded.HistoryWindowBytes);
        Assert.Equal(2 * (history + triangles) + workspace + otherReservation, bounded.InFlightBytes);
        Assert.Equal(1, bounded.InFlightFrames);
        Assert.Equal(2, bounded.HistoryGenerations);
        Assert.Equal(3, full.InFlightFrames);
        Assert.Equal(4, full.HistoryGenerations);
    }

    [Fact]
    public void ReportedNativeResolutionAndSixteenBanksFitOnlyAfterActualFlightWindowReduction()
    {
        const ulong mib = 1024 * 1024;
        ulong committed = (1400UL * 621 * 16 + 65535) / 65536 * 65536;
        // Representative driver-aligned history and workspace from the reported request.
        // Admission uses fresh driver sizes, not these fixture numbers, in the renderer.
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(committed, 994 * mib, 256 * 1024, 16);
        var bounded = D3D12TemporalGiMemoryBudget.ForBoundedFlight(full);
        var budget = new D3D12SsaaGiMemoryBudget.Budget(7219 * mib, 0, true);
        Assert.InRange(full.HistoryBytes / (double)mib, 851, 853);
        Assert.True(full.InFlightBytes > budget.AvailableBytes);
        Assert.True(bounded.InFlightBytes < budget.AvailableBytes);
        Assert.Equal(full.HistoryBytes, bounded.HistoryBytes);
        Assert.Equal(full.PerFrameBytes, bounded.PerFrameBytes);
    }

    [Fact]
    public void FullWindowFastPathDoesNotWaitOrQueryAgain()
    {
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 100);
        var events = new List<string>();
        var admitted = D3D12TemporalGiMemoryBudget.Admit(full,
            () => { events.Add("query"); return new(full.InFlightBytes * 2, 0, true); },
            () => events.Add("retire"), 17, 13, allowBoundedFlight: true);
        Assert.Equal(new[] { "query" }, events);
        Assert.Equal(full, admitted.Reservation);
        Assert.False(admitted.BoundedFlight);
        Assert.Equal(0, admitted.RetirementMilliseconds);
    }

    [Fact]
    public void PreviewPolicyCanRejectWithoutRetiringOrReducingTheReservation()
    {
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 100);
        var bounded = D3D12TemporalGiMemoryBudget.ForBoundedFlight(full);
        var events = new List<string>();
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => D3D12TemporalGiMemoryBudget.Admit(full,
            () => { events.Add("query"); return new(bounded.InFlightBytes * 2, 0, true); },
            () => events.Add("retire"), 17, 13));
        Assert.Equal(new[] { "query" }, events);
        Assert.Contains("4 history generations and 3 in-flight frames", error.Message);
        Assert.Contains("Quality was not changed", error.Message);
    }

    [Fact]
    public void EveryBoundedAdmissionRetiresBeforeRequeryAndUsesTheFreshHeadroom()
    {
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 500000, 12345, 16);
        var bounded = D3D12TemporalGiMemoryBudget.ForBoundedFlight(full);
        var events = new List<string>();
        int queries = 0;
        for (int frame = 0; frame < 3; frame++)
        {
            bool retired = false;
            var admitted = D3D12TemporalGiMemoryBudget.Admit(full,
                () =>
                {
                    queries++;
                    events.Add(retired ? "query-after-retire" : "query-before-retire");
                    ulong budget = bounded.InFlightBytes * 2;
                    // Only the completed-slot retirement makes the requested reservation fit.
                    return new(budget, retired ? 0 : budget - bounded.InFlightBytes + 1, true);
                },
                () => { events.Add("retire"); retired = true; }, 1400, 621, 16, allowBoundedFlight: true);
            Assert.True(admitted.BoundedFlight);
            Assert.Equal(bounded, admitted.Reservation);
            Assert.Equal(bounded.InFlightBytes, admitted.Budget.AvailableBytes);
            Assert.True(admitted.RetirementMilliseconds >= 0);
        }
        Assert.Equal(6, queries);
        Assert.Equal(Enumerable.Repeat(new[] { "query-before-retire", "retire", "query-after-retire" }, 3).SelectMany(value => value), events);
    }

    [Fact]
    public void BoundedFailureStillRejectsBeforeRecordingAndDoesNotCreditTheSurvivingOwner()
    {
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 100, spatialSamples: 16);
        var bounded = D3D12TemporalGiMemoryBudget.ForBoundedFlight(full);
        int queries = 0, retirements = 0;
        var budget = new D3D12SsaaGiMemoryBudget.Budget(bounded.InFlightBytes * 2,
            bounded.InFlightBytes + 1, true);
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => D3D12TemporalGiMemoryBudget.Admit(full,
            () => { queries++; return budget; }, () => retirements++, 1400, 621, 16, allowBoundedFlight: true));
        Assert.Equal(2, queries);
        Assert.Equal(1, retirements);
        Assert.Equal(bounded.InFlightBytes - 1, budget.AvailableBytes);
        Assert.Contains("2 history generations and 1 in-flight frames", error.Message);
        Assert.Contains("no GPU frame was begun", error.Message);
        Assert.True(error.IsPreSubmit);
        Assert.Equal(16, error.SpatialSamples);
        Assert.Equal(bounded.InFlightBytes, error.RequiredBytes);
        Assert.Equal(budget.AvailableBytes, error.AvailableBytes);
    }

    [Fact]
    public void MissingDxgiBudgetCannotAdmitAnUnboundedWorkspaceAfterRetirement()
    {
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536,
            D3D12SsaaGiMemoryBudget.FallbackReservationBytes, spatialSamples: 16);
        int retirements = 0;
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => D3D12TemporalGiMemoryBudget.Admit(full,
            () => new(0, 0, false), () => retirements++, 1400, 621, 16, allowBoundedFlight: true));
        Assert.Equal(1, retirements);
        Assert.Contains("DXGI unavailable", error.Message);
        Assert.Throws<ArgumentException>(() => D3D12TemporalGiMemoryBudget.ForBoundedFlight(
            D3D12TemporalGiMemoryBudget.ForBoundedFlight(full)));
    }

    [Fact]
    public void FenceFailureRemainsTheOriginalFailureAndCannotBecomeARetryableBudgetRejection()
    {
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 100);
        var failure = new InvalidOperationException("Device removed while waiting for a submitted fence.");
        int queries = 0;
        var error = Assert.Throws<InvalidOperationException>(() => D3D12TemporalGiMemoryBudget.Admit(full,
            () => { queries++; return new(1, 0, true); }, () => throw failure,
            1400, 621, 16, allowBoundedFlight: true));
        Assert.Same(failure, error);
        Assert.Equal(1, queries);
    }

    [Fact]
    public void RetiringEveryConsumerLeasePreservesCurrentHistoryAndOnlyTwoGenerationsDuringRecording()
    {
        var resources = Enumerable.Range(0, 4).Select(_ => new TrackedResource()).ToArray();
        var owners = resources.Select(value => new D3D12RetainedResource(new object(), value)).ToArray();
        var slots = new[]
        {
            new[] { owners[0].Lease(), owners[1].Lease() },
            new[] { owners[1].Lease(), owners[2].Lease() },
            new[] { owners[2].Lease(), owners[3].Lease() }
        };
        foreach (var owner in owners.Take(3)) owner.Dispose();
        Assert.All(resources, value => Assert.False(value.Disposed));
        foreach (var lease in slots.SelectMany(value => value)) lease.Dispose();
        Assert.All(resources.Take(3), value => Assert.True(value.Disposed));
        Assert.False(resources[3].Disposed); // Submitted history owner survives WaitForIdle.
        var pendingResource = new TrackedResource();
        var pending = new D3D12RetainedResource(new object(), pendingResource);
        using (var oldConsumer = owners[3].Lease())
        using (var newConsumer = pending.Lease())
        {
            owners[3].Dispose(); // Commit publishes pending then releases the old owner.
            Assert.False(resources[3].Disposed);
            Assert.False(pendingResource.Disposed);
        }
        Assert.True(resources[3].Disposed);
        Assert.False(pendingResource.Disposed);
        pending.Dispose();
        Assert.True(pendingResource.Disposed);
    }

    [Fact, Trait("Category", "Gpu")]
    public void RealFenceRetirementReleasesEverySlotLeaseWithoutBeginningAnotherFrame()
    {
        using var runtime = new D3D12GpuRuntime();
        var retainedResource = new TrackedResource();
        using var current = new D3D12RetainedResource(new object(), retainedResource);
        var transients = new List<TrackedResource>();
        for (int i = 0; i < 3; i++)
        {
            var context = runtime.Begin();
            context.Own(current.Lease());
            var transient = context.Own(new TrackedResource());
            transients.Add(transient);
            runtime.Submit();
        }
        long frameId = runtime.FrameId, submissions = runtime.Submissions;
        var full = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 100, spatialSamples: 16);
        var bounded = D3D12TemporalGiMemoryBudget.ForBoundedFlight(full);
        int query = 0;
        var admitted = D3D12TemporalGiMemoryBudget.Admit(full,
            () =>
            {
                if (++query == 2)
                {
                    Assert.All(transients, value => Assert.True(value.Disposed));
                    Assert.False(retainedResource.Disposed);
                    Assert.Equal(0, runtime.LiveFrameResources);
                    Assert.Equal(runtime.SubmittedFence, runtime.CompletedFence);
                }
                return new(bounded.InFlightBytes * 2, 0, true);
            }, runtime.WaitForIdle, 1400, 621, 16, allowBoundedFlight: true);
        Assert.True(admitted.BoundedFlight);
        Assert.Equal(frameId, runtime.FrameId);
        Assert.Equal(submissions, runtime.Submissions);
        Assert.False(runtime.IsRecording);
        Assert.False(retainedResource.Disposed);
    }

    private sealed class TrackedResource : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
