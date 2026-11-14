using System.Numerics;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class WorldGuideDepthContractTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ProjectedGroundAndAxesRetainClippedDeviceDepth(bool leftHanded, bool perspective)
    {
        var camera = new SceneCamera(new(12, 16, -28), new(0, 5, 0), Vector3.UnitY, 25, .1f, 100,
            perspective ? 40 : null, leftHanded);
        var guides = WorldGuides.BuildOverlay(camera, 1280, 720);
        Assert.NotEmpty(guides.Lines);
        Assert.All(guides.Lines, line =>
        {
            Assert.NotNull(line.DeviceDepth);
            Assert.InRange(line.DeviceDepth!.Value.X, 0, 1);
            Assert.InRange(line.DeviceDepth.Value.Y, 0, 1);
        });
        var jittered = WorldGuides.BuildOverlay(camera with { ProjectionJitterPixels = new(.4f, -.3f) }, 1280, 720);
        Assert.Equal(guides.Lines, jittered.Lines);
    }

    [Theory]
    [InlineData(-.01f, .5f)] [InlineData(.5f, 1.01f)]
    [InlineData(float.NaN, .5f)] [InlineData(.5f, float.PositiveInfinity)]
    public void RejectsInvalidDeviceDepth(float start, float end)
        => Assert.Throws<ArgumentException>(() => new ViewportOverlaySnapshot([
            new(Vector2.Zero, Vector2.One, Vector4.One, DeviceDepth: new(start, end))]));

    [Fact]
    public void OnlySegmentsAcceptDepthAndSelectionHelpersRemainUnconstrained()
    {
        Assert.Throws<ArgumentException>(() => new ViewportOverlaySnapshot([
            new(Vector2.Zero, Vector2.One, Vector4.One, Shape: ViewportOverlayShape.Circle, DeviceDepth: new(.5f))]));
        Assert.Null(new ViewportOverlaySnapshot([new(Vector2.Zero, Vector2.One, Vector4.One)]).Lines[0].DeviceDepth);
    }

    [Fact]
    public void NearPlaneCrossingsKeepFiniteDepthAndScreenCoordinates()
    {
        var camera = new SceneCamera(new(.01f, .11f, -.2f), new(0, .1f, 1), Vector3.UnitY, 2, .1f, 100, 70, true);
        Assert.All(WorldGuides.BuildOverlay(camera, 800, 600).Lines, line =>
        {
            Assert.InRange(line.Start.X, 0, 1); Assert.InRange(line.Start.Y, 0, 1);
            Assert.InRange(line.End.X, 0, 1); Assert.InRange(line.End.Y, 0, 1);
            Assert.InRange(line.DeviceDepth!.Value.X, 0, 1); Assert.InRange(line.DeviceDepth.Value.Y, 0, 1);
        });
    }
}
