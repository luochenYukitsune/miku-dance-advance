using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12TemporalSsaaBudgetTests
{
    private const ulong CommittedBufferBytes = 65536;

    [Fact]
    public void OmittedSampleCountKeepsTheEstablishedSingleSampleEstimate()
    {
        var legacy = D3D12TemporalGiMemoryBudget.FromResourceSizes(CommittedBufferBytes, 100, 200);
        var explicitOne = D3D12TemporalGiMemoryBudget.FromResourceSizes(CommittedBufferBytes, 100, 200, spatialSamples: 1);
        Assert.Equal(legacy, explicitOne);
        Assert.Equal(262144UL, legacy.HistoryBytes);
        Assert.Equal(1049376UL, legacy.HistoryWindowBytes);
        Assert.Equal(100UL, legacy.PerFrameBytes);
        Assert.Equal(1049676UL, legacy.InFlightBytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void EachSampleRetainsFourIndependentlyCommittedBuffersAcrossFourGenerations(int samples)
    {
        // These are individual driver-aligned allocations, not one aligned aggregate buffer.
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(CommittedBufferBytes, 0, spatialSamples: samples);
        ulong historySet = 4UL * (ulong)samples * CommittedBufferBytes;
        Assert.Equal(historySet, estimate.HistoryBytes);
        Assert.Equal(historySet * 4, estimate.HistoryWindowBytes);
        Assert.Equal(estimate.HistoryWindowBytes, estimate.InFlightBytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    public void SharedWorkspaceAndPreviousTriangleCacheAreNotDuplicatedForEverySample(int samples)
    {
        const ulong receiverBytes = 100, shadowAndAccelerationBytes = 700, triangleBytes = 200;
        var common = D3D12SsaaGiMemoryBudget.FromResourceSizes(receiverBytes, shadowAndAccelerationBytes, samples);
        var withoutTriangles = D3D12TemporalGiMemoryBudget.FromResourceSizes(CommittedBufferBytes,
            common.PerFrameBytes, spatialSamples: samples);
        var withTriangles = D3D12TemporalGiMemoryBudget.FromResourceSizes(CommittedBufferBytes,
            common.PerFrameBytes + triangleBytes, triangleBytes, samples);
        Assert.Equal(receiverBytes * (ulong)samples + shadowAndAccelerationBytes, withoutTriangles.PerFrameBytes);
        Assert.Equal(common.InFlightBytes + withoutTriangles.HistoryWindowBytes, withoutTriangles.InFlightBytes);
        Assert.Equal(triangleBytes * 4, withTriangles.HistoryWindowBytes - withoutTriangles.HistoryWindowBytes);
        // One staging upload for each in-flight frame, plus one shared cache generation per owner/slot.
        Assert.Equal(triangleBytes * (3UL + 4UL), withTriangles.InFlightBytes - withoutTriangles.InFlightBytes);
    }

    [Fact]
    public void ACombinedRequestMustFitAsAWholeEvenWhenWorkspaceAndHistoryFitSeparately()
    {
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(16, 100, spatialSamples: 2);
        var budget = new D3D12SsaaGiMemoryBudget.Budget(1400, 0, true);
        Assert.True(estimate.HistoryWindowBytes <= budget.AvailableBytes);
        Assert.True(estimate.PerFrameBytes * 3 <= budget.AvailableBytes);
        Assert.True(estimate.InFlightBytes > budget.AvailableBytes);
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() =>
            D3D12TemporalGiMemoryBudget.RequireFits(estimate, budget, 17, 13, spatialSamples: 2));
        Assert.Contains("Temporal GI at 17x13", error.Message);
        Assert.Contains("2 spatial samples", error.Message);
        Assert.Contains("Quality was not changed", error.Message);
        Assert.Contains("no GPU frame was begun", error.Message);
    }

    [Fact]
    public void WholeRequestAcceptsTheExactBudgetAndRejectsOneByteOfAdditionalUsage()
    {
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(CommittedBufferBytes, 37, 53, spatialSamples: 8);
        var budget = new D3D12SsaaGiMemoryBudget.Budget(estimate.InFlightBytes * 2, 0, true);
        D3D12TemporalGiMemoryBudget.RequireFits(estimate, budget, 71, 53, spatialSamples: 8);
        var exhausted = budget with { CurrentUsageBytes = budget.BudgetBytes - estimate.InFlightBytes + 1 };
        Assert.Throws<D3D12GiMemoryBudgetExceededException>(() =>
            D3D12TemporalGiMemoryBudget.RequireFits(estimate, exhausted, 71, 53, spatialSamples: 8));
    }

    [Fact]
    public void UnknownResidencyBudgetCannotAdmitAnUnboundedNumberOfHistories()
    {
        // One 1440p history buffer is already 56.25 MiB before driver padding.
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(2560UL * 1440 * 16, 0, spatialSamples: 2);
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() =>
            D3D12TemporalGiMemoryBudget.RequireFits(estimate, new(ulong.MaxValue, 0, false), 2560, 1440, spatialSamples: 2));
        Assert.Contains("DXGI unavailable", error.Message);
        Assert.Equal(1800UL * 1024 * 1024, estimate.HistoryWindowBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(int.MaxValue)]
    public void InvalidSampleCountIsRejectedBeforeDriverMeasurement(int samples)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12TemporalGiMemoryBudget.FromResourceSizes(CommittedBufferBytes, 0, spatialSamples: samples));
        // A null runtime makes accidental driver access observable without creating any GPU resource.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12TemporalGiMemoryBudget.Measure(null!, 17, 13, 17, 13, null!, false, spatialSamples: samples));
    }

    [Fact]
    public void EveryHistoryWorkspaceAndCombinedWindowOperationIsChecked()
    {
        // Overflow while multiplying by the bank count, while retaining generations,
        // while adding shared previous triangles, and while merging the workspace window.
        Assert.Throws<OverflowException>(() =>
            D3D12TemporalGiMemoryBudget.FromResourceSizes(ulong.MaxValue / 8, 0, spatialSamples: 32));
        Assert.Throws<OverflowException>(() =>
            D3D12TemporalGiMemoryBudget.FromResourceSizes(ulong.MaxValue / 32, 0, spatialSamples: 8));
        Assert.Throws<OverflowException>(() =>
            D3D12TemporalGiMemoryBudget.FromResourceSizes(1, 0, ulong.MaxValue, spatialSamples: 2));
        Assert.Throws<OverflowException>(() =>
            D3D12TemporalGiMemoryBudget.FromResourceSizes(1, ulong.MaxValue / 3, spatialSamples: 32));
        Assert.Throws<OverflowException>(() =>
            D3D12TemporalGiMemoryBudget.FromResourceSizes(1, ulong.MaxValue, spatialSamples: 2));
    }
}
