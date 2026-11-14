using System.Reflection;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameRoomEmissionTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx");
    private static PropertyInfo StrengthProperty => Assert.IsAssignableFrom<PropertyInfo>(typeof(FrameViewportViewModel).GetProperty("RoomEmissionStrength"));
    private static double Strength(FrameViewportViewModel vm) => (double)StrengthProperty.GetValue(vm)!;
    private static void Set(FrameViewportViewModel vm, double value) => StrengthProperty.SetValue(vm, value);

    [Fact]
    public void DefaultIsZeroAndExplicitAmountDoesNotEnableRoomGiAoOrBloom()
    {
        using var ordinary = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer"]));
        Assert.Equal(0, Strength(ordinary));
        using var selected = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer", "--room-emission", "0.15"]));
        Assert.InRange(Strength(selected), .14999, .15001);
        Assert.False(selected.RoomEnabled); Assert.False(selected.GiEnabled);
        Assert.False(selected.NearOcclusionEnabled);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    [InlineData("0,15")]
    [InlineData("")]
    public void InvalidCliAmountIsRejected(string value)
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--room-emission", value]));

    [Fact]
    public void MissingCliAmountIsRejected()
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--room-emission"]));

    [Fact]
    public void EmissionRebuildsOnlyRoomAndItsProjectionWhileOrbitAndGiReuseThem()
    {
        using var vm = new FrameViewportViewModel(new(FitShadow: true));
        vm.LoadModel(Fixture);
        var role = vm.GetFrame(800, 600);
        vm.RoomEnabled = true;
        var plainRoom = vm.GetFrame(800, 600);
        Assert.Equal(2, vm.ShadowProjectionFitCount);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Set(vm, .15);
        var emissionRoom = vm.GetFrame(800, 600);
        Assert.NotSame(plainRoom.Surface.Snapshot, emissionRoom.Surface.Snapshot);
        Assert.True(emissionRoom.Surface.Snapshot.Materials.Count > plainRoom.Surface.Snapshot.Materials.Count);
        Assert.Equal(3, vm.ShadowProjectionFitCount);
        Assert.Contains("RoomEmissionStrength", notifications);
        Set(vm, .15);
        Assert.Same(emissionRoom, vm.GetFrame(800, 600));
        vm.Yaw = 40; vm.GiStrength = 1; vm.GiCompositionIndex = 1;
        Assert.Same(emissionRoom.Surface.Snapshot, vm.GetFrame(900, 700).Surface.Snapshot);
        Assert.Equal(3, vm.ShadowProjectionFitCount);
        vm.RoomEnabled = false;
        Assert.Same(role.Surface.Snapshot, vm.GetFrame(800, 600).Surface.Snapshot);
        Assert.Equal(3, vm.ShadowProjectionFitCount);
        vm.RoomEnabled = true;
        Assert.Same(emissionRoom.Surface.Snapshot, vm.GetFrame(800, 600).Surface.Snapshot);
        Set(vm, 0);
        var restored = vm.GetFrame(800, 600);
        Assert.Equal(plainRoom.Surface.Snapshot.Vertices.ToArray(), restored.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(plainRoom.Surface.Snapshot.Materials, restored.Surface.Snapshot.Materials);
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.01)]
    [InlineData(1.01)]
    public void InvalidUiAmountDoesNotMutateSelection(double value)
    {
        using var vm = new FrameViewportViewModel(new());
        var error = Assert.Throws<TargetInvocationException>(() => Set(vm, value));
        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        Assert.Equal(0, Strength(vm));
    }
}
