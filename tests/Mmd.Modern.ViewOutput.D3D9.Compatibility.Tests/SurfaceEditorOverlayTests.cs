using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class SurfaceEditorOverlayTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClassicPerspectiveRemapsWorldGuideDepthAndKeepsSelectionHelpersUnconstrained(bool leftHanded)
    {
        var camera = new SceneCamera(new(0, 0, 10), Vector3.Zero, Vector3.UnitY, 20, .1f, 10000, 45, leftHanded);
        float Depth(double distance, double near, double far) => (float)(far / (far - near) - far * near / (far - near) / distance);
        var scene = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(camera), []), editorOverlay: new([
            new(new(.2f), new(.8f), Vector4.One, DeviceDepth: new(Depth(10, .1, 10000), Depth(50, .1, 10000))),
            new(new(.5f), new(.5f), Vector4.One, Shape: ViewportOverlayShape.Circle)]));
        var frame = new PreviewFrame(800, 600, 0, scene, null, null, null) { ResolvedCamera = camera };
        var frozen = D3D9SurfaceEditorOverlay.Freeze(frame, classicPasses: true);
        Assert.Equal(2, frozen.Length);
        Assert.InRange(Math.Abs(frozen[0].DeviceDepth!.Value.X - Depth(10, 1, 100000)), 0, .00001);
        Assert.InRange(Math.Abs(frozen[0].DeviceDepth!.Value.Y - Depth(50, 1, 100000)), 0, .00001);
        Assert.Null(frozen[1].DeviceDepth);
        Assert.Same(scene.EditorOverlay, frame.Scene.EditorOverlay);
    }

    [Fact]
    public void ClassicNearPlaneClipsGuidesInsteadOfDrawingBehindTheCamera()
    {
        var camera = new SceneCamera(new(0, 0, 10), Vector3.Zero, Vector3.UnitY, 20, .1f, 10000, 45);
        var scene = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(camera), []), editorOverlay: new([
            new(Vector2.Zero, Vector2.One, Vector4.One, DeviceDepth: new(0, .99f)),
            new(Vector2.Zero, Vector2.One, Vector4.One, DeviceDepth: new(0, .5f))]));
        var frame = new PreviewFrame(800, 600, 0, scene, null, null, null) { ResolvedCamera = camera };
        var line = Assert.Single(D3D9SurfaceEditorOverlay.Freeze(frame, classicPasses: true));
        Assert.True(line.Start.X > 0); Assert.Equal(Vector2.One, line.End);
        Assert.InRange(line.DeviceDepth!.Value.X, 0, .000001f);
        Assert.InRange(line.DeviceDepth.Value.Y, 0, 1);
    }
}
