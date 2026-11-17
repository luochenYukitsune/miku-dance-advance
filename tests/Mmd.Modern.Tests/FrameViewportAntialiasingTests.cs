using System.Reflection;
using System.Text.Json;
using Mmd.Modern.Demo;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameViewportAntialiasingTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx");
    private static LightingCapabilities Device => new(RenderApi.Direct3D12, true, true, true, 0);
    private static LightingExecutionCapabilities Execution => new(Compute: true, ShaderModel: 65,
        RawRaytracingTier: 12, Device5: true, CommandList4: true, CompilerAvailable: true,
        SoftwareImplementationReady: true, HardwareImplementationReady: true);

    [Fact]
    public void DefaultsOffAndPreservesExistingEnhancementDefaults()
    {
        Assert.Equal("Off", Aa(new FrameViewportOptions()));
        Assert.Equal("Off", Aa(FrameViewportOptions.Parse([])));
        var options = FrameViewportOptions.Parse(["--frame-viewer"]);
        using var vm = new FrameViewportViewModel(options);
        vm.LoadModel(Fixture);
        Assert.Equal("Off", Aa(options)); Assert.Equal("Off", Aa(vm)); Assert.Equal(0, Index(vm));
        Assert.Equal("Off", Aa(vm.GetFrame(801, 603).Options!));
        Assert.False(vm.GiEnabled); Assert.False(vm.RoomEnabled); Assert.False(vm.ShadowFitEnabled);
        Assert.False(vm.GiDenoiseEnabled); Assert.False(vm.NearOcclusionEnabled);
        Assert.Equal(0, vm.RoomEmissionStrength);
    }

    [Theory]
    [InlineData("off", "Off", 0)]
    [InlineData("OFF", "Off", 0)]
    [InlineData("smaa", "Smaa1X", 1)]
    [InlineData("SmAa", "Smaa1X", 1)]
    public void CliSelectionReachesOriginalFrameWithGiOff(string value, string expected, int index)
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--antialiasing", value]);
        using var vm = new FrameViewportViewModel(options);
        vm.LoadModel(Fixture);
        Assert.Equal(expected, Aa(options)); Assert.Equal(expected, Aa(vm)); Assert.Equal(index, Index(vm));
        var frame = vm.GetFrame(801, 603);
        Assert.Equal(expected, Aa(frame.Options!)); Assert.False(frame.Options!.RayGi!.Enabled);
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled); Assert.False(vm.GiDenoiseEnabled);
        Assert.False(vm.RoomEnabled); Assert.False(vm.ShadowFitEnabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("taa")]
    [InlineData("smaa1x")]
    [InlineData("1")]
    public void InvalidCliSelectionIsRejected(string value)
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--antialiasing", value]));

    [Fact]
    public void MissingCliSelectionDoesNotConsumeTheNextOption()
    {
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--antialiasing"]));
        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--antialiasing", "--demo-room"]));
    }

    [Fact]
    public void IndexAndEnumNotifyEachOtherAndNoOpKeepsCachedFrame()
    {
        using var vm = new FrameViewportViewModel(new());
        vm.LoadModel(Fixture);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        SetIndex(vm, 1);
        Assert.Equal("Smaa1X", Aa(vm)); Assert.Equal(1, Index(vm));
        Assert.Contains("Antialiasing", changes); Assert.Contains("AntialiasingIndex", changes);
        var selected = vm.GetFrame(801, 603);
        changes.Clear(); SetIndex(vm, 1);
        Assert.Empty(changes); Assert.Same(selected, vm.GetFrame(801, 603));
        SetAa(vm, 0);
        Assert.Equal("Off", Aa(vm)); Assert.Equal(0, Index(vm));
        Assert.Contains("Antialiasing", changes); Assert.Contains("AntialiasingIndex", changes);
        Assert.NotSame(selected, vm.GetFrame(801, 603));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void InvalidUiSelectionRestoresBothBindingsAndPreservesFrame(int value)
    {
        using var vm = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer", "--antialiasing", "smaa"]));
        vm.LoadModel(Fixture);
        var frame = vm.GetFrame(801, 603);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        SetIndex(vm, value);
        Assert.Equal("Smaa1X", Aa(vm)); Assert.Equal(1, Index(vm)); Assert.Same(frame, vm.GetFrame(801, 603));
        Assert.Contains("Antialiasing", changes); Assert.Contains("AntialiasingIndex", changes);
        Assert.Contains(nameof(vm.Status), changes); Assert.Contains("SMAA", vm.Status);
        changes.Clear(); SetAa(vm, value);
        Assert.Equal("Smaa1X", Aa(vm)); Assert.Same(frame, vm.GetFrame(801, 603));
        Assert.Contains("Antialiasing", changes); Assert.Contains("AntialiasingIndex", changes);
        Assert.Contains(nameof(vm.Status), changes);
    }

    [Fact]
    public void InvalidProgrammaticStartupSelectionIsRejected()
    {
        var options = JsonSerializer.Deserialize<FrameViewportOptions>("""{"Antialiasing":99}""")!;
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameViewportViewModel(options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AaToggleCameraAndResizeReuseStaticGeometryAndShadowFit(bool room)
    {
        using var vm = new FrameViewportViewModel(new(DemoRoom: room, FitShadow: true,
            RoomEmission: .15f, RayGi: new(Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.Spatial)));
        vm.LoadModel(Fixture);
        var before = vm.GetFrame(801, 603);
        var gi = vm.RayGiOptions; var sources = vm.Sources.ToArray();
        SetIndex(vm, 1);
        var selected = vm.GetFrame(801, 603);
        Assert.NotSame(before, selected); Assert.Equal("Off", Aa(before.Options!)); Assert.Equal("Smaa1X", Aa(selected.Options!));
        vm.Yaw = 35; vm.Pitch = 21;
        var resized = vm.GetFrame(1001, 701);
        SetIndex(vm, 0);
        var restored = vm.GetFrame(1001, 701);
        foreach (var frame in new[] { selected, resized, restored })
        {
            Assert.Same(before.Surface, frame.Surface); Assert.Same(before.Surface.Snapshot, frame.Surface.Snapshot);
            Assert.Same(before.Outline, frame.Outline); Assert.Equal(before.Options!.SelfShadow, frame.Options!.SelfShadow);
            Assert.Equal(gi, frame.Options.RayGi);
        }
        Assert.Equal(1, vm.ShadowProjectionFitCount); Assert.Equal(sources, vm.Sources);
        Assert.Equal(room, vm.RoomEnabled); Assert.InRange(vm.RoomEmissionStrength, .14999, .15001);
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled);
    }

    [Fact]
    public void CompositionGiRoomEmissionAndCompatibilityChangesPreserveAa()
    {
        using var vm = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer", "--antialiasing", "smaa"]));
        vm.LoadModel(Fixture);
        vm.UpdateLightingCapabilities(Device, Execution);
        foreach (var mode in new[] { LightingMode.SoftwareRayTracing, LightingMode.HardwareRayTracing, LightingMode.Legacy })
        {
            Assert.True(vm.TrySelectGiMode(mode));
            foreach (int composition in new[] { 1, 0 })
            {
                vm.GiCompositionIndex = composition;
                Assert.Equal("Smaa1X", Aa(vm.GetFrame(801, 603).Options!));
            }
        }
        vm.GiStrength = 0; vm.RoomEnabled = true; vm.RoomEmissionStrength = .3;
        vm.GiCompositionIndex = 1; vm.GiDenoiseEnabled = true; vm.NearOcclusionEnabled = true;
        vm.UpdateLightingCapabilities(Device, Execution, compatMode: true);
        Assert.False(vm.TrySelectGiMode(LightingMode.HardwareRayTracing));
        Assert.Equal("Smaa1X", Aa(vm)); Assert.Equal("Smaa1X", Aa(vm.GetFrame(801, 603).Options!));
        vm.ScenePresetIndex = 0; vm.TransparencyDiagnostic = true;
        Assert.Equal("Smaa1X", Aa(vm.GetFrame(801, 603).Options!));
        Assert.Equal("Smaa1X", Aa(vm));
    }

    // Reflection keeps these behavior tests runnable for Red before the new Core/App option exists.
    private static PropertyInfo Property(object value, string name) => Assert.IsAssignableFrom<PropertyInfo>(value.GetType().GetProperty(name));
    private static string Aa(object value) => Assert.IsType<string>(Property(value, "Antialiasing").GetValue(value)?.ToString());
    private static int Index(FrameViewportViewModel vm) => Assert.IsType<int>(Property(vm, "AntialiasingIndex").GetValue(vm));
    private static void SetIndex(FrameViewportViewModel vm, int value) => Property(vm, "AntialiasingIndex").SetValue(vm, value);
    private static void SetAa(FrameViewportViewModel vm, int value)
    {
        var property = Property(vm, "Antialiasing"); property.SetValue(vm, Enum.ToObject(property.PropertyType, value));
    }
}
