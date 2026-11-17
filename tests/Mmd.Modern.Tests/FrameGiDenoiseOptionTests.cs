using System.Reflection;
using System.Text.Json;
using Mmd.Modern.Demo;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameGiDenoiseOptionTests
{
    private static LightingCapabilities Device => new(RenderApi.Direct3D12, true, true, true, 0);
    private static LightingExecutionCapabilities Execution => new(Compute: true, ShaderModel: 65,
        RawRaytracingTier: 12, Device5: true, CommandList4: true, CompilerAvailable: true,
        SoftwareImplementationReady: true, HardwareImplementationReady: true);

    [Fact]
    public void DenoiseDefaultsOffAndDoesNotEnableAnyEnhancement()
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer"]);
        using var vm = new FrameViewportViewModel(options);
        Assert.Equal("Off", Denoiser(options.RayGi!));
        Assert.False(Enabled(vm)); Assert.False(Available(vm));
        Assert.False(vm.GiEnabled); Assert.False(vm.RoomEnabled); Assert.False(vm.ShadowFitEnabled);
        Assert.False(vm.NearOcclusionEnabled);
        Assert.Equal(0, vm.RoomEmissionStrength);
    }

    [Theory]
    [InlineData("spatial", "Spatial", true)]
    [InlineData("SpAtIaL", "Spatial", true)]
    [InlineData("off", "Off", false)]
    [InlineData("OFF", "Off", false)]
    public void ExplicitCliDenoiseSurvivesModeChangesWithoutEnablingGi(string value, string expected, bool enabled)
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--gi-denoiser", value, "--gi-composition", "toon"]);
        Assert.Equal(expected, Denoiser(options.RayGi!));
        using var vm = new FrameViewportViewModel(options);
        Assert.True(Available(vm)); Assert.Equal(enabled, Enabled(vm));
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled); Assert.False(vm.ShadowFitEnabled);
        Assert.False(vm.RoomEnabled);
        vm.UpdateLightingCapabilities(Device, Execution);
        foreach (var mode in new[] { LightingMode.SoftwareRayTracing, LightingMode.HardwareRayTracing, LightingMode.Legacy })
        {
            Assert.True(vm.TrySelectGiMode(mode));
            Assert.Equal(expected, Denoiser(vm.RayGiOptions));
        }
    }

    [Theory]
    [InlineData("off")]
    [InlineData("software")]
    [InlineData("hardware")]
    public void SpatialCliRequiresToonEvenWhenGiIsOff(string mode)
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(
            ["--frame-viewer", "--gi", mode, "--gi-denoiser", "spatial"]));

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("temporal")]
    public void InvalidDenoiserCliIsRejected(string value)
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi-denoiser", value]));

    [Fact]
    public void MissingDenoiserCliIsRejected()
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi-denoiser"]));

    [Fact]
    public void AdditiveRejectsDenoiseAndNotifiesCheckboxWithoutChangingFrameOrMode()
    {
        using var vm = new FrameViewportViewModel(new());
        vm.LoadModel(Fixture);
        var frame = vm.GetFrame(801, 603);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        SetEnabled(vm, true);
        Assert.False(Enabled(vm)); Assert.False(Available(vm)); Assert.Equal(0, vm.GiCompositionIndex);
        Assert.Equal("Off", Denoiser(vm.RayGiOptions)); Assert.False(vm.GiEnabled);
        Assert.Same(frame, vm.GetFrame(801, 603));
        Assert.Contains("GiDenoiseEnabled", changes); Assert.Contains(nameof(vm.Status), changes);
        Assert.Contains("三渲二", vm.Status);
    }

    [Fact]
    public void SwitchingToAdditiveClearsDenoiserAndReturningToToonKeepsItOff()
    {
        using var vm = new FrameViewportViewModel(new(RayGi: SpatialOptions()));
        Assert.True(Enabled(vm)); Assert.True(Available(vm));
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        vm.GiCompositionIndex = 0;
        Assert.False(Enabled(vm)); Assert.False(Available(vm)); Assert.Equal("Off", Denoiser(vm.RayGiOptions));
        Assert.Contains("GiDenoiseEnabled", changes); Assert.Contains("GiDenoiseAvailable", changes);
        Assert.Contains(nameof(vm.RayGiOptions), changes);
        vm.GiCompositionIndex = 1;
        Assert.True(Available(vm)); Assert.False(Enabled(vm)); Assert.False(vm.GiEnabled);
        SetEnabled(vm, true); Assert.True(Enabled(vm));
        changes.Clear(); SetEnabled(vm, true); Assert.Empty(changes);
        SetEnabled(vm, false); Assert.False(Enabled(vm)); Assert.Equal("Off", Denoiser(vm.RayGiOptions));
    }

    [Fact]
    public void CameraResizeAndDenoiseChangesReuseStaticRoomAndShadowFit()
    {
        using var vm = new FrameViewportViewModel(new(DemoRoom: true, FitShadow: true, RayGi: ToonOptions()));
        vm.LoadModel(Fixture);
        var before = vm.GetFrame(801, 603);
        Assert.Equal(1, vm.ShadowProjectionFitCount);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        SetEnabled(vm, true);
        var filtered = vm.GetFrame(801, 603);
        Assert.NotSame(before, filtered);
        Assert.Equal("Off", Denoiser(before.Options!.RayGi!));
        Assert.Equal("Spatial", Denoiser(filtered.Options!.RayGi!));
        Assert.Contains("GiDenoiseEnabled", changes); Assert.Contains(nameof(vm.RayGiOptions), changes);
        vm.Yaw = 35; vm.Pitch = 21;
        var resized = vm.GetFrame(1001, 701);
        SetEnabled(vm, false);
        var after = vm.GetFrame(1001, 701);
        foreach (var frame in new[] { filtered, resized, after })
        {
            Assert.Same(before.Surface, frame.Surface);
            Assert.Same(before.Surface.Snapshot, frame.Surface.Snapshot);
            Assert.Same(before.Outline, frame.Outline);
            Assert.Equal(before.Options.SelfShadow, frame.Options!.SelfShadow);
        }
        Assert.Equal(1, vm.ShadowProjectionFitCount);
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled);
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx");
    // Reflection/JSON keep the Red behavior test compilable before the new public option exists.
    private static RayGiOptions ToonOptions() => JsonSerializer.Deserialize<RayGiOptions>("""{"Composition":1}""")!;
    private static RayGiOptions SpatialOptions() => JsonSerializer.Deserialize<RayGiOptions>("""{"Composition":1,"Denoiser":1}""")!;
    private static PropertyInfo Property(string name) => Assert.IsAssignableFrom<PropertyInfo>(typeof(FrameViewportViewModel).GetProperty(name));
    private static bool Enabled(FrameViewportViewModel vm) => Assert.IsType<bool>(Property("GiDenoiseEnabled").GetValue(vm));
    private static bool Available(FrameViewportViewModel vm) => Assert.IsType<bool>(Property("GiDenoiseAvailable").GetValue(vm));
    private static void SetEnabled(FrameViewportViewModel vm, bool value) => Property("GiDenoiseEnabled").SetValue(vm, value);
    private static string Denoiser(RayGiOptions options) => Assert.IsType<string>(typeof(RayGiOptions).GetProperty("Denoiser")?.GetValue(options)?.ToString());
}
