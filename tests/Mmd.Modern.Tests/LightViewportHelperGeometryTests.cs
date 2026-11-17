using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class LightViewportHelperGeometryTests
{
    private static SceneCamera Camera => new(new(0, 0, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);
    private static NativeSceneLight Light(SceneLightKind kind) => new(Guid.NewGuid(), "helper", kind,
        Vector3.Zero, Quaternion.Identity, Vector3.One, Range: 3,
        AreaSize: kind == SceneLightKind.Area ? new(4, 2) : null);

    [Fact]
    public void UnselectedLightsStayCompactAndOnlyThePrimarySelectionGetsRangeGeometry()
    {
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxLights).Select(index => Light(SceneLightKind.Point)
            with { Enabled = index < SceneLightingSnapshot.MaxActiveLights }).ToArray();
        var snapshot = new SceneLightingSnapshot(lights);
        Assert.Equal(lights.Length * 2, LightViewportInteraction.CreateOverlay(snapshot, null, Camera, 800, 600, 1).Lines.Count);
        var selected = lights.Select(light => light.Id).ToArray();
        var overlay = LightViewportInteraction.CreateOverlay(snapshot, lights[0].Id, Camera, 800, 600, 1,
            rotate: true, selectedIds: selected);
        Assert.InRange(overlay.Lines.Count, lights.Length * 2 + 96, ViewportOverlaySnapshot.MaxLines);
        Assert.Equal(96, overlay.Lines.Count(line => line.Width == 1.25f));
    }

    [Fact]
    public void PointRangeIsThreeWorldCirclesAndDirectionalLightHasNoRange()
    {
        var point = Light(SceneLightKind.Point) with { Position = Vector3.One };
        var segments = LightViewportInteraction.DetailSegments(point).ToArray();
        Assert.Equal(96, segments.Length);
        Assert.All(segments, segment =>
        {
            Assert.InRange(MathF.Abs(Vector3.Distance(segment.Start, point.Position) - point.Range), 0, .00001f);
            Assert.InRange(MathF.Abs(Vector3.Distance(segment.End, point.Position) - point.Range), 0, .00001f);
        });
        Assert.Empty(LightViewportInteraction.DetailSegments(Light(SceneLightKind.Directional)));
    }

    [Fact]
    public void SpotRangeUsesFiniteSphereBoundedConeEvenAtNinetyDegreeHalfAngle()
    {
        var spot = Light(SceneLightKind.Spot) with { OuterConeRadians = MathF.PI / 2 };
        var segments = LightViewportInteraction.DetailSegments(spot).ToArray();
        Assert.Equal(36, segments.Length);
        Assert.All(segments, segment =>
        {
            Assert.InRange(Vector3.Distance(segment.Start, spot.Position), 0, spot.Range + .00001f);
            Assert.InRange(Vector3.Distance(segment.End, spot.Position), 0, spot.Range + .00001f);
        });
    }

    [Fact]
    public void AreaRectangleUsesFullLocalXYSizeAndRotation()
    {
        var area = Light(SceneLightKind.Area) with { Position = new(3, 2, 1),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f) };
        var segments = LightViewportInteraction.DetailSegments(area).ToArray();
        Assert.Equal(4, segments.Length);
        foreach (var (start, end) in segments)
        {
            foreach (var point in new[] { start, end })
            {
                var local = Vector3.Transform(point - area.Position, Quaternion.Conjugate(area.Rotation));
                Assert.InRange(MathF.Abs(MathF.Abs(local.X) - 2), 0, .00001f);
                Assert.InRange(MathF.Abs(MathF.Abs(local.Y) - 1), 0, .00001f);
                Assert.InRange(MathF.Abs(local.Z), 0, .00001f);
            }
        }
    }

    [Fact]
    public void RangeSegmentCrossingTheViewIsClippedInsteadOfDisappearing()
    {
        Assert.True(LightViewportInteraction.TryProjectSegment(new(-1000, 0, 0), new(1000, 0, 0),
            Camera, 800, 600, out var start, out var end));
        Assert.InRange(start.X, 0, 800); Assert.InRange(end.X, 0, 800);
        Assert.InRange(start.Y, 299.99f, 300.01f); Assert.InRange(end.Y, 299.99f, 300.01f);
        Assert.False(LightViewportInteraction.TryProjectSegment(new(0, 0, -100), new(0, 0, -90),
            Camera, 800, 600, out _, out _));
    }
}
