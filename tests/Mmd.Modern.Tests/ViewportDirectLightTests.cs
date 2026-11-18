using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewportDirectLightTests
{
    private static SceneCamera Camera => new(new(6, 4, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AxisDragReachesThePointerProjectionInAnObliqueView(bool perspective)
    {
        var camera = Camera with { VerticalFovDegrees = perspective ? 45f : null };
        var position = Vector3.One;
        Assert.True(LightViewportInteraction.TryProject(position, camera, 800, 600, out var before));
        Assert.True(LightViewportInteraction.TryProject(position + Vector3.UnitX, camera, 800, 600, out var endpoint));
        var delta = Vector2.Normalize(endpoint - before) * 65;
        var moved = LightViewportInteraction.Translate(position, delta, camera, 800, 600, LightDragAxis.X);
        Assert.Equal(position.Y, moved.Y);
        Assert.Equal(position.Z, moved.Z);
        Assert.True(LightViewportInteraction.TryProject(moved, camera, 800, 600, out var after));
        Assert.InRange(Vector2.Distance(before + delta, after), 0, .002f);
    }

    [Fact]
    public void NearestLightIconWinsIndependentOfListOrderAndSelection()
    {
        var first = Light("first", Vector3.Zero);
        var second = Light("second", new(.1f, 0, 0));
        Assert.True(LightViewportInteraction.TryProject(first.Position, Camera, 800, 600, out var center));
        foreach (var lights in new[] { new SceneLightingSnapshot([first, second]), new SceneLightingSnapshot([second, first]) })
        {
            var hit = LightViewportInteraction.HitTest(lights, second.Id, center, Camera, 800, 600, 1, rotate: true);
            Assert.Equal(first.Id, hit?.LightId);
            Assert.Equal(LightDragAxis.Free, hit?.Axis);
        }
    }

    [Fact]
    public void OnlySelectedLightsHaveAxisHandlesAndRotationUsesRings()
    {
        var light = Light("test", Vector3.Zero);
        var lights = new SceneLightingSnapshot([light]);
        var markers = LightViewportInteraction.CreateOverlay(lights, null, Camera, 800, 600, 2).Lines;
        Assert.Equal(2, markers.Count);
        Assert.All(markers, marker =>
        {
            Assert.Equal(ViewportOverlayShape.Square, marker.Shape);
            Assert.Equal(6, marker.Radius);
        });
        var move = LightViewportInteraction.CreateOverlay(lights, light.Id, Camera, 800, 600, 2);
        var rotate = LightViewportInteraction.CreateOverlay(lights, light.Id, Camera, 800, 600, 2, rotate: true);
        Assert.True(rotate.Lines.Count > move.Lines.Count);
        Assert.True(LightViewportInteraction.TryProject(new(0, 2, 0), Camera, 800, 600, out var ringPoint));
        Assert.Null(LightViewportInteraction.HitTest(lights, null, ringPoint, Camera, 800, 600, 2, rotate: true));
        Assert.NotNull(LightViewportInteraction.HitTest(lights, light.Id, ringPoint, Camera, 800, 600, 2, rotate: true));
    }

    [Fact]
    public void LampIconsKeepBothDarkAndLightContrastIndependentOfLampColor()
    {
        var light = Light("test", Vector3.Zero);
        var whiteLamp = LightViewportInteraction.CreateOverlay(new([light]), null, Camera, 800, 600, 2);
        var blackLamp = LightViewportInteraction.CreateOverlay(new([light with { Color = Vector3.Zero }]), null, Camera, 800, 600, 2);
        Assert.Equal(whiteLamp.Lines, blackLamp.Lines);
        Assert.Collection(whiteLamp.Lines, border =>
        {
            Assert.True(border.Color.X < .25f && border.Color.Y < .25f && border.Color.Z < .25f);
            Assert.Equal(4.5f, border.Width);
        }, fill =>
        {
            Assert.True(fill.Color.X > .7f && fill.Color.Y > .7f && fill.Color.Z > .7f);
            Assert.Equal(1.5f, fill.Width);
        });
        var selected = LightViewportInteraction.CreateOverlay(new([light]), light.Id, Camera, 800, 600, 2);
        Assert.Equal(whiteLamp.Lines[0].Color, selected.Lines[0].Color);
        Assert.True(selected.Lines[1].Color.X > selected.Lines[1].Color.Y
            && selected.Lines[1].Color.Y > selected.Lines[1].Color.Z);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RingDragRotatesOnlyAroundTheChosenWorldAxis(bool leftHanded)
    {
        var camera = new SceneCamera(new(0, 0, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100, LeftHanded: leftHanded);
        Assert.True(LightViewportInteraction.TryProject(Vector3.UnitX * 2, camera, 800, 600, out var start));
        Assert.True(LightViewportInteraction.TryProject(Vector3.UnitY * 2, camera, 800, 600, out var end));
        var rotation = LightViewportInteraction.RotateOnAxis(Quaternion.Identity, Vector3.Zero, start, end, camera, 800, 600, LightDragAxis.Z);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, rotation), Vector3.UnitY), 0, .0001f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, rotation), Vector3.UnitZ), 0, .0001f);
    }

    private static NativeSceneLight Light(string name, Vector3 position) => new(Guid.NewGuid(), name,
        SceneLightKind.Point, position, Quaternion.Identity, Vector3.One);
}
