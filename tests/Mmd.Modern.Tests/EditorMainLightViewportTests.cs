using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorMainLightViewportTests
{
    private static readonly SceneCamera Camera = SceneCamera.Default;
    private static readonly PreviewFrame Frame = new(800, 600, 0,
        new(new(SceneSnapshot.CreateEmpty(Camera), [])), null, null, null);
    private static Vector2 Source(PreviewFrame frame) =>
        frame.Scene.EditorOverlay!.Lines.First(line => line.Shape == ViewportOverlayShape.Circle).Start * new Vector2(800, 600);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrabBodyOrArrowPreviewsAndRegistersOnlyTheReleasedPose(bool grabArrow)
    {
        var model = new EditorSceneInspectorViewModel { CurrentFrame = 17 };
        var original = model.LightPose;
        VmdMotion? registered = null;
        model.CommitSceneMotion = motion => registered = motion;
        var controller = new EditorMainLightViewport(model, () => true, () => { });
        var image = controller.Decorate(Frame, Camera);
        var source = Source(image);
        var grab = grabArrow ? Vector2.Lerp(source, new(400, 300), .55f) : source;
        Assert.False(controller.TryBegin(grab, Camera, 800, 600));
        controller.FramePresented(image);
        Assert.True(controller.TryBegin(grab, Camera, 800, 600));
        controller.Move(grab + new Vector2(20, -12));
        var preview = controller.Decorate(Frame, Camera).Light!.Value;
        Assert.NotEqual(original.Direction, preview.Direction);
        Assert.Equal(original, model.LightPose);
        Assert.False(model.HasLightDraft);

        controller.End(grab + new Vector2(60, 18));
        Assert.False(controller.IsDragging);
        Assert.True(model.HasLightDraft);
        Assert.Null(registered);
        Assert.NotEqual(preview.Direction, model.LightPose.Direction);
        Assert.Equal(original.Color, model.LightPose.Color);
        Assert.InRange(MathF.Abs(original.Direction.Length() - model.LightPose.Direction.Length()), 0, .00001f);
        await model.RegisterLightKeyframeAsync();
        var key = Assert.Single(registered!.LightKeyframes);
        Assert.Equal(17u, key.Frame);
        Assert.Equal(model.LightPose.Direction, key.Direction);
    }

    [Fact]
    public void EscapeRestoresExistingDraftAndFrameChangesRejectStaleRelease()
    {
        var model = new EditorSceneInspectorViewModel();
        model.AimLight("left");
        var original = model.LightPose;
        var controller = new EditorMainLightViewport(model, () => true, () => { });
        var frame = controller.Decorate(Frame with { Light = original }, Camera);
        var source = Source(frame);
        controller.FramePresented(frame);
        Assert.True(controller.TryBegin(source, Camera, 800, 600));
        controller.Move(source + new Vector2(23, -42));
        controller.Cancel();
        Assert.True(model.HasLightDraft);
        Assert.Equal(original, model.LightPose);
        Assert.True(controller.TryBegin(source, Camera, 800, 600));
        model.CurrentFrame++;
        var newPose = model.LightPose;
        controller.End(source + new Vector2(56, 12));
        Assert.Equal(newPose, model.LightPose);
        Assert.False(model.HasLightDraft);
        Assert.False(controller.TryBegin(source, Camera, 800, 600));
    }

    [Fact]
    public void EmptySpaceAndObsoleteGeometryLeaveOtherViewportToolsAvailable()
    {
        var model = new EditorSceneInspectorViewModel();
        var controller = new EditorMainLightViewport(model, () => true, () => { });
        var frame = controller.Decorate(Frame, Camera);
        controller.FramePresented(frame);
        var source = Source(frame);
        Assert.False(controller.TryBegin(new(0, 0), Camera, 800, 600));
        Assert.False(controller.TryBegin(source, Camera, 1600, 1200));
        Assert.False(controller.TryBegin(source, Camera with { Eye = Camera.Eye + Vector3.UnitX }, 800, 600));
        Assert.True(controller.TryBegin(source, Camera, 800, 600));
        controller.End(source);
        Assert.False(model.HasLightDraft);
        controller.ClearPresentation();
        controller.FramePresented(frame);
        Assert.False(controller.TryBegin(source, Camera, 800, 600));
    }
}
