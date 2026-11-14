using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameViewportDemoRoomTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx");

    [Fact]
    public void RoomCommandLineSelectsOnlyTheSceneAndPreservesEnhancementDefaults()
    {
        var ordinary = FrameViewportOptions.Parse(["--frame-viewer"]);
        Assert.False(ordinary.DemoRoom);
        var selected = FrameViewportOptions.Parse(["--frame-viewer", "--demo-room"]);
        Assert.True(selected.DemoRoom);
        Assert.Equal(LightingMode.Legacy, selected.RayGi!.Mode);
        Assert.Equal(.5f, selected.RayGi.Strength);
        Assert.Equal(16, selected.RayGi.SamplesPerPixel);
        using var vm = new FrameViewportViewModel(selected);
        Assert.True(vm.RoomEnabled);
        Assert.Equal(1, vm.ScenePresetIndex);
        Assert.False(vm.GiEnabled);

        Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--demo-room", "true"]));
    }

    [Fact]
    public void SwitchingScenesRestoresOriginalSnapshotAndReusesRoomAfterOptionChanges()
    {
        using var vm = new FrameViewportViewModel(new());
        vm.LoadModel(Fixture);
        var provenance = vm.Sources.ToArray();
        var blank = vm.GetFrame(900, 1050);
        Assert.Same(blank, vm.GetFrame(900, 1050));
        var changes = new List<string?>();
        vm.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        vm.ScenePresetIndex = 1;
        var room = vm.GetFrame(900, 1050);
        Assert.True(vm.RoomEnabled);
        Assert.NotSame(blank.Surface.Snapshot, room.Surface.Snapshot);
        Assert.True(room.Surface.Snapshot.Indices.Length > blank.Surface.Snapshot.Indices.Length);
        Assert.Null(room.Options!.GroundShadow);
        Assert.Same(room, vm.GetFrame(900, 1050));
        vm.RoomEnabled = false;
        var unchangedBlank = vm.GetFrame(900, 1050);
        Assert.Same(blank.Surface.Snapshot, unchangedBlank.Surface.Snapshot);
        Assert.Equal(blank.Options, unchangedBlank.Options);
        Assert.Equal(blank.OutlineOptions, unchangedBlank.OutlineOptions);
        vm.RoomEnabled = true;
        Assert.Same(room.Surface.Snapshot, vm.GetFrame(900, 1050).Surface.Snapshot);
        vm.Yaw = 30;
        vm.GiStrength = 1;
        vm.OutlineEnabled = false;
        var changed = vm.GetFrame(1200, 1000);
        Assert.NotSame(room, changed);
        Assert.Same(room.Surface, changed.Surface);
        Assert.Same(room.Surface.Snapshot, changed.Surface.Snapshot);
        Assert.Same(room.Outline, changed.Outline);
        Assert.Equal(1f, changed.Options!.RayGi!.Strength);
        Assert.False(changed.OutlineOptions!.Eligible);
        vm.RoomEnabled = false;
        var restored = vm.GetFrame(900, 1050);
        Assert.Equal(0, vm.ScenePresetIndex);
        Assert.Same(blank.Surface, restored.Surface);
        Assert.Same(blank.Surface.Snapshot, restored.Surface.Snapshot);
        Assert.Same(blank.Outline, restored.Outline);
        Assert.NotNull(restored.Options!.GroundShadow);
        vm.RoomEnabled = true;
        Assert.Same(room.Surface.Snapshot, vm.GetFrame(900, 1050).Surface.Snapshot);
        Assert.Equal(provenance, vm.Sources);
        Assert.Contains(nameof(vm.RoomEnabled), changes);
        Assert.Contains(nameof(vm.ScenePresetIndex), changes);
        Assert.False(vm.GiEnabled);

    }

    [Fact]
    public void RoomCameraFitsTheSceneAndSupportsOrbitZoomAndReset()
    {
        using var vm = new FrameViewportViewModel(new(DemoRoom: true));
        vm.LoadModel(Fixture);
        var initial = vm.GetFrame(900, 1050).Options!.CameraOverride!;
        Assert.Equal(22, vm.Yaw);
        Assert.Equal(16, vm.Pitch);
        Assert.True(initial.Eye.Y > initial.Target.Y);
        Assert.Equal(0, vm.GetFramingDiagnostics(900, 1050).ClippedReferencedVertices);
        vm.Yaw = 25; vm.Pitch = 20; vm.Zoom = .8;
        var moved = vm.GetFrame(900, 1050).Options!.CameraOverride!;
        Assert.NotEqual(initial.Eye, moved.Eye);
        Assert.Equal(initial.Target, moved.Target);
        Assert.NotEqual(initial.OrthographicHeight, moved.OrthographicHeight);
        vm.ResetCamera();
        Assert.Equal(initial, vm.GetFrame(900, 1050).Options!.CameraOverride!);
        vm.ScenePresetIndex = 0;
        vm.ResetCamera();
        Assert.Equal(0, vm.Yaw);
        Assert.Equal(0, vm.Pitch);
        Assert.False(vm.RoomEnabled);
        vm.ScenePresetIndex = 8;
        Assert.Equal(0, vm.ScenePresetIndex);
    }

    [Fact]
    public void ReloadAndDisposeInvalidateModelAndRoomCaches()
    {
        var vm = new FrameViewportViewModel(new(DemoRoom: true));
        vm.LoadModel(Fixture);
        var firstRoom = vm.GetFrame(900, 1050);
        vm.LoadModel(Fixture);
        var secondRoom = vm.GetFrame(900, 1050);
        Assert.NotSame(firstRoom.Surface.Snapshot, secondRoom.Surface.Snapshot);
        Assert.True(vm.HasScene);
        vm.Dispose();
        Assert.False(vm.HasScene);
        Assert.Throws<InvalidOperationException>(() => vm.GetFrame(900, 1050));
    }
}
