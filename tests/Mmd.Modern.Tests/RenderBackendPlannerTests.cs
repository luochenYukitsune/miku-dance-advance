using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RenderBackendPlannerTests
{
    private static RenderCapabilities Caps(RenderApi api, bool rayTracing = false) =>
        new(api, $"{api} device", "test-driver", rayTracing, true, true, true, 16384, "SM 6.0");

    [Fact]
    public void ModernWindowsPrefersVulkan()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(),
            [Caps(RenderApi.Direct3D11), Caps(RenderApi.Vulkan), Caps(RenderApi.Direct3D12)],
            windows: true);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.Vulkan, result.Plan!.Api);
        Assert.False(result.Plan.FallbackUsed);
        Assert.False(result.Plan.RayTracing);
    }

    [Fact]
    public void ModernFallsBackInDeclaredOrderAndReportsIt()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(),
            [Caps(RenderApi.Direct3D11)],
            windows: true);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.Direct3D11, result.Plan!.Api);
        Assert.True(result.Plan.FallbackUsed);
        Assert.Contains("Fallback", result.Plan.Reason);
        Assert.Equal([RenderApi.Vulkan, RenderApi.Direct3D12, RenderApi.Direct3D11], result.Plan.AttemptOrder);
    }

    [Fact]
    public void CompatD3D9DoesNotSilentlyUseD3D11()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(RenderMode.CompatD3D9),
            [Caps(RenderApi.Direct3D11), Caps(RenderApi.Vulkan)],
            windows: true);

        Assert.False(result.Success);
        Assert.Contains("CompatD3D9", result.FailureReason);
    }

    [Fact]
    public void CompatD3D9UsesRealD3D9Device()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(RenderMode.CompatD3D9, PreferRayTracing: true),
            [Caps(RenderApi.Direct3D9), Caps(RenderApi.Direct3D11)],
            windows: true);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.Direct3D9, result.Plan!.Api);
        Assert.False(result.Plan.RayTracing, "Ray tracing must never be enabled in compatibility mode.");
        Assert.False(result.Plan.FallbackUsed);
    }

    [Fact]
    public void RayTracingPreferencePromotesCapableBackend()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(PreferRayTracing: true),
            [Caps(RenderApi.Vulkan), Caps(RenderApi.Direct3D12, rayTracing: true), Caps(RenderApi.Direct3D11)],
            windows: true);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.Direct3D12, result.Plan!.Api);
        Assert.True(result.Plan.RayTracing);
    }

    [Fact]
    public void RayTracingRequestWithoutSupportKeepsRasterAndSaysSo()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(PreferRayTracing: true),
            [Caps(RenderApi.Vulkan), Caps(RenderApi.Direct3D12)],
            windows: true);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.Vulkan, result.Plan!.Api);
        Assert.False(result.Plan.RayTracing);
        Assert.Contains("no support", result.Plan.Reason);
    }

    [Fact]
    public void FallbackCanBeDisabled()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(AllowFallback: false),
            [Caps(RenderApi.Direct3D11)],
            windows: true);

        Assert.False(result.Success);
        Assert.Contains("fallback is disabled", result.FailureReason);
    }

    [Fact]
    public void ForcedApiMustBelongToMode()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(RenderMode.CompatOpenGL, ForcedApi: RenderApi.Vulkan),
            [Caps(RenderApi.Vulkan), Caps(RenderApi.OpenGL)],
            windows: true);

        Assert.False(result.Success);
        Assert.Contains("not allowed", result.FailureReason);
    }

    [Fact]
    public void ForcedApiReordersWithinMode()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(ForcedApi: RenderApi.Direct3D11),
            [Caps(RenderApi.Vulkan), Caps(RenderApi.Direct3D12), Caps(RenderApi.Direct3D11)],
            windows: true);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.Direct3D11, result.Plan!.Api);
    }

    [Fact]
    public void LinuxModernOrderIsVulkanThenOpenGL()
    {
        Assert.Equal(
            [RenderApi.Vulkan, RenderApi.OpenGL],
            RenderBackendPlanner.Order(RenderMode.Modern, windows: false));

        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(),
            [Caps(RenderApi.OpenGL)],
            windows: false);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.OpenGL, result.Plan!.Api);
    }

    [Fact]
    public void SoftwareModeNeedsNoGpu()
    {
        var result = RenderBackendPlanner.Select(
            new RenderBackendRequest(RenderMode.Software),
            [Caps(RenderApi.Software)],
            windows: false);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(RenderApi.Software, result.Plan!.Api);
    }

    [Fact]
    public void EmptyProbeFailsWithProbedList()
    {
        var result = RenderBackendPlanner.Select(new RenderBackendRequest(), [], windows: true);

        Assert.False(result.Success);
        Assert.Contains("Probed:", result.FailureReason);
    }
}
