using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
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

    [Fact]
    public void EdgeOnAndHorizonRingDragRemainsSmoothWithoutHalfTurnJump()
    {
        var edgeCamera = new SceneCamera(new(0, 1, -20), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);
        float previousYaw = 0;
        for (int x = 360; x <= 440; x += 10)
        {
            var rot = LightViewportInteraction.RotateOnAxis(Quaternion.Identity, Vector3.Zero,
                new(350, 300), new(x, 300), edgeCamera, 800, 600, LightDragAxis.Y);
            var rotated = Vector3.Transform(Vector3.UnitZ, rot);
            float yaw = MathF.Atan2(rotated.X, rotated.Z);
            if (x > 360)
            {
                float step = yaw - previousYaw;
                Assert.True(MathF.Abs(step) < 0.2f);
                Assert.True(MathF.Abs(step) > 0.01f);
            }
            previousYaw = yaw;
        }

        var horizonCamera = new SceneCamera(new(0, 12, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100, VerticalFovDegrees: 45f);
        var beforeHorizon = LightViewportInteraction.RotateOnAxis(Quaternion.Identity, Vector3.Zero,
            new(350, 320), new(350, 300), horizonCamera, 800, 600, LightDragAxis.Y);
        var pastHorizon = LightViewportInteraction.RotateOnAxis(Quaternion.Identity, Vector3.Zero,
            new(350, 320), new(350, 5), horizonCamera, 800, 600, LightDragAxis.Y);
        float dot = Vector3.Dot(Vector3.Transform(Vector3.UnitX, beforeHorizon), Vector3.Transform(Vector3.UnitX, pastHorizon));
        Assert.True(dot > 0f);
    }

    [Fact]
    public void AxialTranslateNearVanishingPointDoesNotCrossBehindCamera()
    {
        var perspective = new SceneCamera(new(0, 2, -10), new(0, 2, 0), Vector3.UnitY, 10, .1f, 100, VerticalFovDegrees: 45f);
        var position = Vector3.Zero;
        Assert.True(LightViewportInteraction.TryProject(position, perspective, 800, 600, out var p0));
        Assert.True(LightViewportInteraction.TryProject(position + Vector3.UnitZ * 4, perspective, 800, 600, out var p1));
        var hugeDelta = Vector2.Normalize(p1 - p0) * 5000f;
        var moved = LightViewportInteraction.Translate(position, hugeDelta, perspective, 800, 600, LightDragAxis.Z);
        Assert.True(float.IsFinite(moved.X) && float.IsFinite(moved.Y) && float.IsFinite(moved.Z));
        Assert.Equal(position.X, moved.X);
        Assert.Equal(position.Y, moved.Y);
        var forward = Vector3.Normalize(perspective.Target - perspective.Eye);
        Assert.True(Vector3.Dot(moved - perspective.Eye, forward) > 0f);
    }

    [Fact]
    public void TargetDragNearLampPositionClampsCleanlyAndCanContinue()
    {
        using var workspace = new EditorProjectWorkspace();
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        using var input = File.OpenRead(path);
        var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path, TestContext.Current.CancellationToken);
        workspace.AddModel(path, asset, ModelRole.Character);

        var camera = new SceneCamera(new(0, 0, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);
        var spot = new NativeSceneLight(Guid.NewGuid(), "spot", SceneLightKind.Spot, new(0, 0, -2),
            Quaternion.Identity, Vector3.One, AimDistance: 4f);
        workspace.EditLighting(new(new([spot]), spot.Id));

        var controller = new EditorLightViewportController(workspace, draft => workspace.EditLighting(draft), () => { });
        var snap = workspace.Session!.CaptureRenderSnapshot();
        var frame = controller.DecorateFrame(new PreviewFrame(800, 600, snap.SourceFrame, snap.Scene, snap.Camera, snap.Light, snap.SelfShadow)
            { ResolvedCamera = camera });
        controller.FramePresented(frame);

        Assert.True(LightViewportInteraction.TryProject(spot.Target, camera, 800, 600, out var targetPx));
        Assert.True(LightViewportInteraction.TryProject(spot.Position, camera, 800, 600, out var posPx));
        Assert.True(LightViewportInteraction.TryProject(spot.Position + new Vector3(3, 1, -2), camera, 800, 600, out var farPx));

        Assert.True(controller.TryBeginDrag((int)MathF.Round(targetPx.X), (int)MathF.Round(targetPx.Y), 800, 600, camera));
        Assert.True(controller.MoveDrag((int)MathF.Round(posPx.X), (int)MathF.Round(posPx.Y)));
        var nearLamp = Assert.Single(controller.PreviewLighting.Lights);
        Assert.InRange(nearLamp.EffectiveAimDistance, 0.049f, 5f);
        Assert.True(controller.MoveDrag((int)MathF.Round(farPx.X), (int)MathF.Round(farPx.Y)));
        var draggedOut = Assert.Single(controller.PreviewLighting.Lights);
        Assert.True(draggedOut.EffectiveAimDistance > 1f);
        Assert.True(controller.EndDrag());
    }

    private static NativeSceneLight Light(string name, Vector3 position) => new(Guid.NewGuid(), name,
        SceneLightKind.Point, position, Quaternion.Identity, Vector3.One);
}
