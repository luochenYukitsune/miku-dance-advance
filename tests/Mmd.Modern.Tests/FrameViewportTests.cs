using System.Numerics;
using Mmd.Modern.Demo;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameViewportTests
{
    [Fact]
    public void SlowExecutingFrameDoesNotQueueSuccessorsAndStarveInput()
    {
        var queued = new Queue<Action>();
        ViewportFrameDispatch? dispatch = null;
        dispatch = new(queued.Enqueue, () =>
        {
            // The periodic producer can fire repeatedly while Present/recording owns the UI thread.
            for (int tick = 0; tick < 100; tick++) dispatch!.Pulse();
            Assert.Empty(queued);
        });
        dispatch.Start(); dispatch.Pulse(); queued.Dequeue()();
        Assert.Empty(queued);
        dispatch.Pulse(); Assert.Single(queued);
    }

    [Fact]
    public void FailedFrameReleasesInFlightGateForRecovery()
    {
        var queued = new Queue<Action>();
        bool fail = true;
        var dispatch = new ViewportFrameDispatch(queued.Enqueue, () =>
        {
            if (fail) throw new InvalidOperationException("fixture");
        });
        dispatch.Start(); dispatch.Pulse();
        Assert.Throws<InvalidOperationException>(queued.Dequeue());
        fail = false; dispatch.Pulse(); Assert.Single(queued); queued.Dequeue()();
        Assert.Empty(queued);
    }

    [Fact]
    public void PeriodicFrameDispatchCoalescesTicksToOneQueuedCallback()
    {
        var queued = new Queue<Action>(); int draws = 0;
        var dispatch = new ViewportFrameDispatch(queued.Enqueue, () => draws++);
        dispatch.Start();
        for (int tick = 0; tick < 100; tick++) dispatch.Pulse();
        Assert.Single(queued);
        Assert.Equal(0, draws);
        queued.Dequeue()();
        Assert.Equal(1, draws);
        dispatch.Pulse();
        Assert.Single(queued);
    }

    [Fact]
    public void PeriodicFrameDispatchInvalidatesQueuedFramesOnStopAndRestart()
    {
        var queued = new Queue<Action>(); int draws = 0;
        var dispatch = new ViewportFrameDispatch(queued.Enqueue, () => draws++);
        dispatch.Start(); dispatch.Pulse(); dispatch.Stop(); dispatch.Start();
        queued.Dequeue()();
        Assert.Equal(0, draws);
        dispatch.Pulse(); queued.Dequeue()();
        Assert.Equal(1, draws);
        dispatch.Stop(); dispatch.Pulse();
        Assert.Empty(queued);
    }

    [Fact]
    public void ModernStartupKeepsEnhancementsOptIn()
    {
        Assert.False(FrameViewportOptions.Parse([]).Enabled);
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--model", "character.pmx"]);
        Assert.True(options.Enabled);
        Assert.False(options.SelfTest);
        using var vm = new FrameViewportViewModel(options);
        Assert.True(vm.OutlineEnabled);
        Assert.True(vm.SelfShadowEnabled);
        Assert.True(vm.GroundShadowEnabled);
        Assert.False(vm.TransparencyDiagnostic);
        Assert.False(vm.GiEnabled);
        Assert.False(vm.RayTracingEnabled);

        Assert.Equal(.5f, options.RayGi!.Strength);
        Assert.Equal(16, options.RayGi.SamplesPerPixel);
        Assert.Equal(0, options.RayGi.OcclusionStrength);
        Assert.False(vm.NearOcclusionEnabled);
        Assert.Equal(.5, vm.GiStrength);
    }

    [Fact]
    public void SelfTestMustBeBoundedAndUseAnExplicitModel()
    {
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--viewport-self-test"]));
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--model"]));
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--model", "a.pmx", "--self-test-frames", "299"]));
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--model", "a.pmx", "--unknown"]));
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--model", "a.pmx", "--viewport-self-test", "--self-test-frames", "320"]);
        Assert.True(options.SelfTest);
        Assert.Equal(320, options.SelfTestFrames);
    }

    private static LightingCapabilities GiDevice => new(RenderApi.Direct3D12, true, true, true, 0);
    private static LightingExecutionCapabilities GiExecution => new(Compute: true, ShaderModel: 65,
        RawRaytracingTier: 12, Device5: true, CommandList4: true, CompilerAvailable: true,
        SoftwareImplementationReady: true, HardwareImplementationReady: true);

    [Fact]
    public void GiMenuRequiresObservedExecutionAndNeverFabricatesSdf()
    {
        using var vm = new FrameViewportViewModel(new());
        Assert.False(vm.SoftwareGiAvailable);
        Assert.False(vm.TrySelectGiMode(LightingMode.SoftwareRayTracing));
        Assert.False(vm.GiEnabled);
        vm.UpdateLightingCapabilities(GiDevice, GiExecution with { SoftwareImplementationReady = false });
        Assert.False(vm.SoftwareGiAvailable);
        Assert.False(vm.TrySelectGiMode(LightingMode.SoftwareRayTracing));
        vm.UpdateLightingCapabilities(GiDevice, GiExecution);
        Assert.True(vm.SoftwareGiAvailable);
        Assert.True(vm.HardwareGiAvailable);
        Assert.True(vm.TrySelectGiMode(LightingMode.SoftwareRayTracing));
        Assert.True(vm.GiEnabled);
        Assert.Equal(16, vm.RayGiOptions.SamplesPerPixel);
        Assert.Equal(.5f, vm.RayGiOptions.Strength);
        Assert.False(vm.NearOcclusionEnabled);
        Assert.Equal(0, vm.RayGiOptions.OcclusionStrength);
        vm.UpdateLightingCapabilities(GiDevice, GiExecution, compatMode: true);
        Assert.Equal(LightingMode.Legacy, vm.GiMode);
        Assert.False(vm.SoftwareGiAvailable);
        Assert.False(vm.TrySelectGiMode(LightingMode.HardwareRayTracing));
    }

    [Fact]
    public void TransparencyDiagnosticAndGiAreExplicitlyExclusive()
    {
        using var vm = new FrameViewportViewModel(new());
        vm.UpdateLightingCapabilities(GiDevice, GiExecution);
        vm.TransparencyDiagnostic = true;
        Assert.False(vm.TrySelectGiMode(LightingMode.SoftwareRayTracing));
        Assert.Contains("透明诊断", vm.GiAvailabilityDetails);
        vm.TransparencyDiagnostic = false;
        Assert.True(vm.TrySelectGiMode(LightingMode.SoftwareRayTracing));
        vm.TransparencyDiagnostic = true;
        Assert.False(vm.TransparencyDiagnostic);
        Assert.Contains("透明诊断", vm.Status);
    }

    [Fact]
    public void GiCommandLineIsExplicitValidatedAndSelfTestStartsOff()
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--gi", "software", "--gi-strength", "7", "--gi-quality", "high"]);
        Assert.Equal(LightingMode.SoftwareRayTracing, options.RayGi!.Mode);
        Assert.Equal(64, options.RayGi.SamplesPerPixel);
        using var vm = new FrameViewportViewModel(options);
        Assert.False(vm.GiEnabled); // Hardware readiness has not been observed yet.
        vm.UpdateLightingCapabilities(GiDevice, GiExecution);
        Assert.Equal(LightingMode.SoftwareRayTracing, vm.GiMode);
        Assert.Equal(7, vm.GiStrength);
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi", "fake"]));
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi-strength", "NaN"]));
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi-quality", "ultra"]));
        var self = FrameViewportOptions.Parse(["--frame-viewer", "--model", "a.pmx", "--ray-gi-self-test"]);
        Assert.True(self.SelfTest && self.RayGiSelfTest);
        Assert.Equal(LightingMode.Legacy, self.RayGi!.Mode);
        Assert.Equal(4, self.RayGi.SamplesPerPixel);
        Assert.Equal(.5f, self.RayGi.Strength);
    }

    [Theory]
    [InlineData("off", LightingMode.Legacy)]
    [InlineData("software", LightingMode.SoftwareRayTracing)]
    [InlineData("hardware", LightingMode.HardwareRayTracing)]
    public void GiCommandLineUsesConservativePreviewStrength(string requested, LightingMode mode)
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--gi", requested]);
        Assert.Equal(.5f, options.RayGi!.Strength);
        Assert.Equal(16, options.RayGi.SamplesPerPixel);
        Assert.Equal(0, options.RayGi.OcclusionStrength);
        using var vm = new FrameViewportViewModel(options);
        Assert.False(vm.GiEnabled);
        vm.UpdateLightingCapabilities(GiDevice, GiExecution);
        Assert.Equal(mode, vm.GiMode);
        Assert.Equal(.5, vm.GiStrength);
        Assert.False(vm.NearOcclusionEnabled);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void GiNearOcclusionRemainsAnExplicitIndependentOverride(string requested, bool enabled)
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--gi", "hardware", "--gi-occlusion", requested]);
        Assert.Equal(enabled ? 1 : 0, options.RayGi!.OcclusionStrength);
        using var vm = new FrameViewportViewModel(options);
        Assert.False(vm.GiEnabled);
        Assert.Equal(enabled, vm.NearOcclusionEnabled);
        vm.UpdateLightingCapabilities(GiDevice, GiExecution);
        Assert.Equal(LightingMode.HardwareRayTracing, vm.GiMode);
        Assert.Equal(enabled, vm.NearOcclusionEnabled);
        Assert.True(vm.TrySelectGiMode(LightingMode.Legacy));
        Assert.True(vm.TrySelectGiMode(LightingMode.SoftwareRayTracing));
        Assert.Equal(enabled, vm.NearOcclusionEnabled);
        vm.NearOcclusionEnabled = !enabled;
        Assert.Equal(!enabled, vm.NearOcclusionEnabled);
        Assert.Equal(enabled ? 0 : 1, vm.RayGiOptions.OcclusionStrength);
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi-occlusion", "auto"]));
    }

    [Fact]
    public void DiagnosticStrengthAndSelfTestQualityRemainExplicitOverrides()
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--gi", "hardware", "--gi-strength", "6"]);
        using var vm = new FrameViewportViewModel(options);
        vm.UpdateLightingCapabilities(GiDevice, GiExecution);
        Assert.Equal(LightingMode.HardwareRayTracing, vm.GiMode);
        Assert.Equal(6, vm.GiStrength);
        Assert.Equal(16, vm.RayGiOptions.SamplesPerPixel);
        var self = FrameViewportOptions.Parse(["--frame-viewer", "--model", "a.pmx", "--ray-gi-self-test", "--gi-quality", "medium", "--gi-strength", "6"]);
        Assert.Equal(LightingMode.Legacy, self.RayGi!.Mode);
        Assert.Equal(16, self.RayGi.SamplesPerPixel);
        Assert.Equal(6, self.RayGi.Strength);
    }

    [Fact]
    public void AutoFrameFitsNarrowViewportAndOrbitKeepsTarget()
    {
        var camera = new FrameViewportCamera(new(-4, 0, -1), new(4, 10, 1));
        var portrait = camera.CreateCamera(200, 800);
        Assert.True(portrait.OrthographicHeight >= 32);
        Assert.Equal(new Vector3(0, 5, 0), portrait.Target);
        camera.YawDegrees = 75;
        camera.PitchDegrees = 999;
        camera.Zoom = .001;
        var moved = camera.CreateCamera(801, 603);
        Assert.NotEqual(portrait.Eye, moved.Eye);
        Assert.Equal(portrait.Target, moved.Target);
        Assert.True(float.IsFinite(moved.ViewProjection(801, 603).M11));
        Assert.InRange(camera.PitchDegrees, -85, 85);
        Assert.InRange(camera.Zoom, .15, 8);
    }

    [Fact]
    public void OrbitAutoFrameKeepsTheWholeProjectedModelInsideViewport()
    {
        var orbit = new FrameViewportCamera(new(-5, -5, -5), new(5, 5, 5)) { YawDegrees = 45, PitchDegrees = 35 };
        var camera = orbit.CreateCamera(801, 603);
        foreach (int x in new[] { -5, 5 })
        foreach (int y in new[] { -5, 5 })
        foreach (int z in new[] { -5, 5 })
        {
            var clip = Vector4.Transform(new Vector4(x, y, z, 1), camera.ViewProjection(801, 603));
            Assert.InRange(clip.X / clip.W, -.91f, .91f);
            Assert.InRange(clip.Y / clip.W, -.91f, .91f);
            Assert.InRange(clip.Z / clip.W, 0, 1);
        }
    }

    [Fact]
    public void PixelSizeSuspendsAtZeroAndRejectsInvalidInputs()
    {
        Assert.True(D3D12ViewportSize.FromLogical(0, 700, 1).IsSuspended);
        Assert.True(D3D12ViewportSize.FromLogical(700, 0, 1).IsSuspended);
        Assert.Equal(new D3D12ViewportSize(961, 1081), D3D12ViewportSize.FromLogical(768.8, 864.8, 1.25));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12ViewportSize.FromLogical(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12ViewportSize.FromLogical(1, 1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12ViewportSize.FromLogical(4097, 1, 1));
    }

    [Fact]
    public void PresenterRejectsNullWindowBeforeCreatingGpuObjects()
        => Assert.Throws<ArgumentException>(() => new D3D12SwapchainPresenter(IntPtr.Zero, 257, 191));

    [Theory]
    [InlineData(0, 191)]
    [InlineData(257, -1)]
    [InlineData(4097, 191)]
    public void PresenterRejectsInvalidDimensionsBeforeCreatingGpuObjects(int width, int height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12SwapchainPresenter(new IntPtr(1), width, height));
}
