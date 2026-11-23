using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorLightInteractionPreviewTests
{
    private static SceneCamera Camera => new(new(0, 0, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);
    private static ViewQualityOptions Quality => new(Gi: RayGiOptions.Off with { Mode = LightingMode.SoftwareRayTracing });

    [Fact]
    public void HiddenHelpersKeepRealLightingAndDisablePickingWithoutChangingTheDocument()
    {
        using var workspace = Workspace();
        var light = Light(); workspace.EditLighting(new(new([light])));
        var original = workspace.Session!.Lighting;
        var controller = new EditorLightViewportController(workspace, workspace.EditLighting, () => { });
        controller.Select(light.Id); Present(workspace, controller);
        controller.HelpersVisible = false;
        var frame = Present(workspace, controller);
        Assert.Empty(frame.Scene.EditorOverlay?.Lines ?? []);
        Assert.Same(original, frame.Scene.Lighting);
        Assert.False(controller.TryBeginDrag(400, 300, 800, 600, Camera));
        Assert.Same(original, workspace.Session.Lighting);
        controller.HelpersVisible = true; Present(workspace, controller);
        Assert.True(controller.TryBeginDrag(400, 300, 800, 600, Camera));
        controller.CancelDrag();
    }

    [Fact]
    public void DragSimplifiesOnlyPreviewAndRestoresOnCommitCancelAndDisable()
    {
        using var workspace = Workspace(); workspace.EditLighting(new(new([Light()])));
        var controller = new EditorLightViewportController(workspace, workspace.EditLighting, () => { });
        controller.Select(workspace.Session!.Lighting.Lights[0].Id);
        var quality = Quality;
        Assert.Same(quality, controller.InteractionQuality(quality));
        foreach (var finish in new[] { "commit", "cancel", "disable" })
        {
            var original = workspace.Session.Lighting; Present(workspace, controller);
            Assert.True(controller.TryBeginDrag(400, 300, 800, 600, Camera));
            Assert.True(controller.IsInteracting);
            Assert.Equal(LightingMode.Legacy, controller.InteractionQuality(quality).EffectiveGi.Mode);
            Assert.False(Assert.Single(Present(workspace, controller).Scene.Lighting!.Lights).CastShadows);
            Assert.True(Assert.Single(original.Lights).CastShadows);
            if (finish == "commit") controller.EndDrag();
            else if (finish == "cancel") controller.CancelDrag();
            else controller.IsEnabled = false;
            Assert.False(controller.IsInteracting);
            Assert.Same(quality, controller.InteractionQuality(quality));
            Assert.True(Assert.Single(Present(workspace, controller).Scene.Lighting!.Lights).CastShadows);
        }
    }

    [Fact]
    public void ParentParameterPreviewPreservesSameFrameMetadataAndExternalEditRestoresQuality()
    {
        using var workspace = Workspace(); var parentId = workspace.Session!.Objects[0].Id;
        workspace.Session.Edit(edit => edit.Composition.SetTransform(parentId, new(3, 0, 0), Quaternion.Identity, 2),
            TestContext.Current.CancellationToken);
        var light = Light() with { Parent = new(parentId), Position = Vector3.UnitY,
            Receivers = new(SceneLightReceiverMode.Include, [parentId]) };
        workspace.EditLighting(new(new([light])));
        var controller = new EditorLightViewportController(workspace, workspace.EditLighting, () => { });
        controller.Select(light.Id); controller.HelpersVisible = false;
        Assert.True(controller.SetParameterPreview(new(new([light with { Intensity = 4 }]), light.Id)));
        Assert.True(controller.IsInteracting);
        var rendered = Present(workspace, controller).Scene.Lighting!;
        Assert.Equal(new(3, 2, 0), rendered.Lights[0].Position);
        Assert.False(rendered.Lights[0].CastShadows);
        Assert.Equal(light.Receivers, rendered.Lights[0].Receivers);
        Assert.Equal(light.Parent, rendered.AuthoredLighting.Lights[0].Parent);
        Assert.True(rendered.ParentTransforms.ContainsKey(light.Parent!));
        Assert.Equal(4, rendered.Lights[0].Intensity);
        workspace.EditLighting(new(new([light with { Intensity = 2 }])));
        controller.Refresh();
        Assert.False(controller.IsInteracting);
        var quality = Quality; Assert.Same(quality, controller.InteractionQuality(quality));
        Assert.True(Present(workspace, controller).Scene.Lighting!.Lights[0].CastShadows);
        Assert.Equal(2, controller.PreviewLighting.Lights[0].Intensity);
    }

    [Fact]
    public void RepeatedPointerSamplesKeepTheLastPreviewWithoutAnotherRenderRequest()
    {
        using var workspace = Workspace();
        var light = Light();
        workspace.EditLighting(new(new([light])));
        int renders = 0;
        var controller = new EditorLightViewportController(workspace, workspace.EditLighting, () => renders++);
        controller.Select(light.Id);
        Present(workspace, controller);
        Assert.True(controller.TryBeginDrag(400, 300, 800, 600, Camera));
        int atStart = renders;
        Assert.True(controller.MoveDrag(400, 300));
        Assert.Equal(atStart, renders);
        Assert.True(controller.MoveDrag(430, 310));
        var moved = controller.PreviewLighting;
        Assert.True(controller.MoveDrag(430, 310));
        Assert.Equal(atStart + 1, renders);
        Assert.Same(moved, controller.PreviewLighting);
        Assert.True(controller.MoveDrag(400, 300));
        Assert.Equal(light, controller.PreviewLighting.Lights[0]);
        Assert.False(controller.EndDrag());
    }

    private static NativeSceneLight Light() => new(Guid.NewGuid(), "fill", SceneLightKind.Spot,
        Vector3.Zero, Quaternion.Identity, Vector3.One);
    private static EditorProjectWorkspace Workspace()
    {
        var workspace = new EditorProjectWorkspace();
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        using var input = File.OpenRead(path);
        var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
        workspace.AddModel(path, asset, ModelRole.Character); return workspace;
    }
    private static PreviewFrame Present(EditorProjectWorkspace workspace, EditorLightViewportController controller)
    {
        var snapshot = workspace.Session!.CaptureRenderSnapshot();
        var frame = controller.DecorateFrame(new PreviewFrame(800, 600, snapshot.SourceFrame, snapshot.Scene,
            snapshot.Camera, snapshot.Light, snapshot.SelfShadow) { ResolvedCamera = Camera });
        controller.FramePresented(frame); return frame;
    }
}
