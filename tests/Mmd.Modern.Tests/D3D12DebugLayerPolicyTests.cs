using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12DebugLayerPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FirstDeviceDecisionIsFrozenAcrossLaterRequests(bool requested, bool available)
    {
        var policy = new D3D12DebugLayerPolicy();
        int attempts = 0;
        bool Enable() { attempts++; return available; }
        bool expected = requested && available;
        Assert.Equal(expected, policy.BeforeDeviceCreation(requested, Enable));
        foreach (bool next in new[] { true, false, true, false })
            Assert.Equal(expected, policy.BeforeDeviceCreation(next, Enable));
        Assert.Equal(requested ? 1 : 0, attempts);
    }

    [Fact]
    public async Task ConcurrentDeviceConstructorsEnableOnceBeforeAnyCallerReturns()
    {
        var policy = new D3D12DebugLayerPolicy();
        int attempts = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            policy.BeforeDeviceCreation(true, () => { Interlocked.Increment(ref attempts); return true; }),
            TestContext.Current.CancellationToken)));
        Assert.All(results, value => Assert.True(value));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void FailedInitializationNeverEnablesDebugAfterADeviceMayHaveBeenCreated()
    {
        var policy = new D3D12DebugLayerPolicy();
        Assert.Throws<InvalidOperationException>(() => policy.BeforeDeviceCreation(true,
            () => throw new InvalidOperationException("Debug configuration failed.")));
        Assert.False(policy.BeforeDeviceCreation(true, () => throw new Xunit.Sdk.XunitException("Must not retry late.")));
    }

    [Fact, Trait("Category", "Gpu")]
    public void MeshAndFrameDevicesCanCoexistWithoutReconfiguringTheDebugLayer()
    {
        var frame = D3D12RayGiTests.Bounce();
        using var session = new D3D12FrameSession();
        var before = session.Capture(frame, 49, 37);
        using (var mesh = new D3D12MeshRenderer())
        {
            var image = mesh.Render(frame.Surface.Snapshot, 49, 37);
            Assert.Equal(49 * 37 * 4, image.Rgba.Length);
            Assert.Equal(before.Rgba, session.Capture(frame, 49, 37).Rgba);
        }
        Assert.Equal(before.Rgba, session.Capture(frame, 49, 37).Rgba);
    }
}
