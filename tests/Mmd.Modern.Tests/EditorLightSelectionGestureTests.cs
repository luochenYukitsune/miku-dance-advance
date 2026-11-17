using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>CPU presentation bookkeeping and the actual viewport gesture controller; no GPU is created.</summary>
public sealed class EditorLightSelectionGestureTests
{
    private static SceneCamera Camera => new(new(0, 0, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);

    [Fact]
    public void ParentedAndWorldLightsDragTogetherInWorldSpaceWhileCommittingLocalTransforms()
    {
        using var workspace = Workspace();
        var parent = workspace.Session!.Objects[0];
        var parentRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f);
        workspace.Session.Edit(edit => edit.Composition.SetTransform(parent.Id, new(2, 0, 0), parentRotation, 2), TestContext.Current.CancellationToken);
        var local = Light(new(0, 1, 0)) with { Parent = new(parent.Id) };
        var free = Light(new(-2, -1, 0));
        workspace.EditLighting(new(new([local, free])));
        var parentWorld = workspace.Session.ResolveLightParentTransform(local.Parent!);
        var world = SceneLightTransform.ToWorld(local, parentWorld);
        int commits = 0;
        var controller = new EditorLightViewportController(workspace, draft => { commits++; workspace.EditLighting(draft); }, () => { });
        controller.SelectMany([local.Id, free.Id], local.Id); Present(workspace, controller);
        var start = Pixel(world.Position);
        Assert.True(controller.TryBeginDrag(start.X, start.Y, 800, 600, Camera));
        Assert.True(controller.MoveDrag(start.X, start.Y));
        Assert.Equal(local, controller.PreviewLighting.Lights[0]);
        Assert.True(controller.MoveDrag(start.X + 60, start.Y + 30));
        var preview = controller.PreviewLighting;
        var movedWorld = SceneLightTransform.ToWorld(preview.Lights[0], parentWorld);
        Assert.InRange(Vector3.Distance(movedWorld.Position - world.Position, preview.Lights[1].Position - free.Position), 0, .00001f);
        var sampled = workspace.Session.CaptureRenderSnapshot();
        var displayed = controller.DecorateFrame(new PreviewFrame(800, 600, sampled.SourceFrame, sampled.Scene,
            sampled.Camera, sampled.Light, sampled.SelfShadow) { ResolvedCamera = Camera });
        Assert.InRange(Vector3.Distance(movedWorld.Position, displayed.Scene.Lighting!.Lights[0].Position), 0, .00001f);
        Assert.Equal(local.Parent, preview.Lights[0].Parent); Assert.True(controller.EndDrag()); Assert.Equal(1, commits);
        Assert.Equal(preview.Lights, workspace.Session.Lighting.Lights);
        Assert.True(workspace.Undo()); Assert.Equal(new[] { local, free }, workspace.Session.Lighting.Lights);
        Present(workspace, controller);
        Assert.True(controller.TryBeginDrag(start.X, start.Y, 800, 600, Camera));
        Assert.True(controller.MoveDrag(start.X, start.Y)); Assert.False(controller.EndDrag()); Assert.Equal(1, commits);
    }

    [Fact]
    public void ParameterPreviewKeepsParentedLightAtTheSameWorldPose()
    {
        using var workspace = Workspace(); var parent = workspace.Session!.Objects[0];
        workspace.Session.Edit(edit => edit.Composition.SetTransform(parent.Id, new(3, 0, 0), Quaternion.Identity, 2), TestContext.Current.CancellationToken);
        var light = Light(Vector3.UnitY) with { Parent = new(parent.Id) };
        workspace.EditLighting(new(new([light])));
        var controller = new EditorLightViewportController(workspace, workspace.EditLighting, () => { });
        controller.Select(light.Id);
        Assert.True(controller.SetParameterPreview(new(new([light with { Intensity = 4 }]), light.Id)));
        var sample = workspace.Session.CaptureRenderSnapshot();
        var frame = controller.DecorateFrame(new PreviewFrame(800, 600, sample.SourceFrame, sample.Scene, sample.Camera,
            sample.Light, sample.SelfShadow) { ResolvedCamera = Camera });
        Assert.Equal(new(3, 2, 0), Assert.Single(frame.Scene.Lighting!.Lights).Position);
        Assert.Equal(4, frame.Scene.Lighting.EffectiveLights[0].Intensity);
        Assert.Equal(light, Assert.Single(workspace.Session.Lighting.Lights));
    }

    [Fact]
    public void GroupIconDragMovesAllSelectedLightsOnceAndCancelPreservesHistory()
    {
        using var workspace = Workspace();
        var group = new SceneLightGroup(Guid.NewGuid(), "窗光", Intensity: 2);
        var first = Light(new(-2, 0, 0), group.Id); var second = Light(new(2, 0, 0), group.Id);
        var other = Light(new(0, 3, 0));
        var original = new SceneLightingSnapshot([first, second, other], [group]);
        workspace.EditLighting(new(original));
        var committed = workspace.Session!.Lighting;
        int commits = 0;
        var controller = new EditorLightViewportController(workspace, draft => { commits++; workspace.EditLighting(draft); }, () => { });
        controller.SelectMany([second.Id, first.Id], first.Id);
        Assert.Equal(new[] { first.Id, second.Id }, controller.SelectedLightIds);
        Present(workspace, controller);
        var start = Pixel(first.Position);
        Assert.True(controller.TryBeginDrag(start.X, start.Y, 800, 600, Camera));
        Assert.True(controller.MoveDrag(start.X + 80, start.Y + 30));
        Assert.True(controller.MoveDrag(start.X + 100, start.Y + 50));
        Assert.Same(committed, workspace.Session!.Lighting);
        Assert.Equal(second.Position - first.Position,
            controller.PreviewLighting.Lights[1].Position - controller.PreviewLighting.Lights[0].Position);
        Assert.Equal(other, controller.PreviewLighting.Lights[2]);
        Assert.Equal(group, Assert.Single(controller.PreviewLighting.Groups));
        Assert.True(controller.EndDrag()); Assert.Equal(1, commits);
        var moved = workspace.Session.Lighting;
        Assert.NotEqual(first.Position, moved.Lights[0].Position);
        Assert.Equal(second.Position - first.Position, moved.Lights[1].Position - moved.Lights[0].Position);
        Assert.True(workspace.Undo());
        Assert.Equal(original.Lights, workspace.Session.Lighting.Lights); Assert.Equal(original.Groups, workspace.Session.Lighting.Groups);
        Present(workspace, controller);
        Assert.True(controller.TryBeginDrag(start.X, start.Y, 800, 600, Camera));
        Assert.True(controller.MoveDrag(start.X + 40, start.Y));
        controller.CancelDrag(); Assert.False(controller.EndDrag()); Assert.Equal(1, commits);
        Assert.Equal(original.Lights, controller.PreviewLighting.Lights);
        Assert.True(workspace.Redo()); Assert.Equal(moved.Lights, workspace.Session.Lighting.Lights);
    }

    [Fact]
    public void SharedRotationRingOrbitsTheSelectionAndRotatesDirectionsAroundItsCentre()
    {
        using var workspace = Workspace();
        var first = Light(new(-2, 0, 0)); var second = Light(new(2, 0, 0)); var other = Light(new(0, 3, 0));
        workspace.EditLighting(new(new([first, second, other])));
        var controller = new EditorLightViewportController(workspace, workspace.EditLighting, () => { });
        controller.SelectMany([first.Id, second.Id], first.Id); controller.RotateHandle = true;
        Present(workspace, controller);
        float size = LightViewportInteraction.Translate(Vector3.Zero, new(70, 0), Camera, 800, 600).Length();
        var start = Pixel(new Vector3(1, 1, 0) * (size / MathF.Sqrt(2)));
        var finish = Pixel(new Vector3(-1, 1, 0) * (size / MathF.Sqrt(2)));
        Assert.True(controller.TryBeginDrag(start.X, start.Y, 800, 600, Camera));
        Assert.True(controller.MoveDrag(finish.X, finish.Y)); Assert.True(controller.EndDrag());
        var lights = workspace.Session!.Lighting.Lights;
        Assert.InRange((lights[0].Position + lights[1].Position).Length(), 0, .00001f);
        Assert.InRange(Vector3.Distance(lights[0].Position, lights[1].Position), 3.9999f, 4.0001f);
        Assert.InRange(MathF.Abs(lights[0].Position.X), 0, .05f); Assert.InRange(MathF.Abs(lights[0].Position.Y), 1.95f, 2.05f);
        Assert.NotEqual(first.Rotation, lights[0].Rotation); Assert.Equal(lights[0].Rotation, lights[1].Rotation);
        Assert.Equal(other, lights[2]);
        Assert.True(workspace.Undo()); Assert.Equal(new[] { first, second, other }, workspace.Session.Lighting.Lights);
    }

    [Fact]
    public void OutsideEditCancelsTheWholeGestureAndPrunesRemovedMembers()
    {
        using var workspace = Workspace();
        var first = Light(new(-2, 0, 0)); var second = Light(new(2, 0, 0));
        workspace.EditLighting(new(new([first, second])));
        int commits = 0;
        var controller = new EditorLightViewportController(workspace, _ => commits++, () => { });
        controller.SelectMany([first.Id, second.Id], first.Id); Present(workspace, controller);
        var start = Pixel(first.Position);
        Assert.True(controller.TryBeginDrag(start.X, start.Y, 800, 600, Camera));
        controller.MoveDrag(start.X + 70, start.Y);
        workspace.EditLighting(new(new([second with { Intensity = 8 }])));
        controller.Refresh();
        Assert.False(controller.IsDragging); Assert.False(controller.EndDrag()); Assert.Equal(0, commits);
        Assert.Equal(new[] { second.Id }, controller.SelectedLightIds); Assert.Equal(second.Id, controller.SelectedLightId);
        Assert.Equal(8, Assert.Single(controller.PreviewLighting.Lights).Intensity);
        Assert.Throws<ArgumentException>(() => controller.SelectMany([second.Id, Guid.NewGuid()], second.Id));
        Assert.Equal(new[] { second.Id }, controller.SelectedLightIds);
    }

    private static EditorProjectWorkspace Workspace()
    {
        var workspace = new EditorProjectWorkspace();
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        using var input = File.OpenRead(path);
        var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
        workspace.AddModel(path, asset, ModelRole.Character);
        return workspace;
    }
    private static NativeSceneLight Light(Vector3 position, Guid? group = null) =>
        new(Guid.NewGuid(), "fill", SceneLightKind.Spot, position, Quaternion.Identity, Vector3.One, GroupId: group);
    private static (int X, int Y) Pixel(Vector3 position)
    {
        Assert.True(LightViewportInteraction.TryProject(position, Camera, 800, 600, out var point));
        return ((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
    }
    private static void Present(EditorProjectWorkspace workspace, EditorLightViewportController controller)
    {
        var snapshot = workspace.Session!.CaptureRenderSnapshot();
        var frame = controller.DecorateFrame(new PreviewFrame(800, 600, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            { ResolvedCamera = Camera });
        controller.FramePresented(frame);
    }
}
