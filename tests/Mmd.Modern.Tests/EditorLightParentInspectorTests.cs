using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightParentInspectorTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public void OrdinaryEditsRecoveryAndCopiesRetainTheParentAndLocalTransform()
    {
        var light = Light() with { Parent = new(Guid.NewGuid(), 2) };
        var item = new EditorNativeLightItem(light);
        Assert.Equal(light, item.Build());
        item.Intensity = 7;
        var restored = EditorNativeLightItem.RestoreRecoveryDraft(item.CaptureRecoveryDraft()).Build();
        Assert.Equal(light.Parent, restored.Parent);
        Assert.Equal(light.Position, restored.Position); Assert.Equal(light.Rotation, restored.Rotation);
        Assert.Equal(7f, restored.Intensity);
        var model = new EditorLightingViewModel(new([restored]), light.Id);
        Assert.True(model.DuplicateSelection());
        Assert.True(model.TryCreate(out var draft));
        var copy = draft!.Snapshot.Lights.Single(value => value.Id != light.Id);
        Assert.Equal(light.Parent, copy.Parent);
        Assert.Equal(light.Position, copy.Position); Assert.Equal(light.Rotation, copy.Rotation);
    }

    [Fact]
    public async Task ModelBoneSwitchAndDetachKeepWorldPoseWithOneCommitEach() => await session.Dispatch(() =>
    {
        var light = Light(); var objectId = Guid.NewGuid();
        var root = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(20, 3, -9);
        var bone = Matrix4x4.CreateRotationX(.3f) * Matrix4x4.CreateTranslation(2, 8, 1) * root;
        var inspector = Inspector(light, [new(objectId, "Model A", ["center", "hand"])],
            parent => parent.BoneIndex is null ? root : bone);
        var window = Host(inspector); int commits = 0, starts = 0;
        NativeSceneLight committed = light;
        inspector.EditStartingRequested = () => { starts++; return true; };
        inspector.CommitRequested = draft => { commits++; committed = Assert.Single(draft.Snapshot.Lights); return true; };
        try
        {
            var models = inspector.FindControl<ComboBox>("LightParentModelChoice")!;
            var bones = inspector.FindControl<ComboBox>("LightParentBoneChoice")!;
            Assert.False(bones.IsEnabled);
            // A valid pending parameter and the attachment are a single transaction.
            inspector.ViewModel!.SelectedLight!.Intensity = 6;
            inspector.ViewModel.SelectedLight.PositionX = 7;
            light = light with { Position = light.Position with { X = 7 } };
            models.SelectedItem = models.Items.Cast<EditorLightParentModelChoice>().Single(item => item.ObjectId == objectId);
            Assert.Equal(new SceneLightParent(objectId), committed.Parent);
            SameWorld(light, SceneLightTransform.ToWorld(committed, root));
            Assert.Equal(6f, committed.Intensity); Assert.Equal(1, commits); Assert.Equal(1, starts);
            Assert.True(bones.IsEnabled);
            Assert.True(inspector.TryCommit()); Assert.Equal(1, commits);
            Assert.Equal(new[] { "center", "hand" }, bones.Items.Cast<EditorLightParentBoneChoice>().Skip(1).Select(item => item.AuthoredName));
            bones.SelectedItem = bones.Items.Cast<EditorLightParentBoneChoice>().Single(item => item.BoneIndex == 1);
            Assert.Equal(new SceneLightParent(objectId, 1), committed.Parent);
            SameWorld(light, SceneLightTransform.ToWorld(committed, bone));
            Assert.Equal(2, commits);
            // Re-selecting the current attachment is inert.
            bones.SelectedItem = bones.Items.Cast<EditorLightParentBoneChoice>().Single(item => item.BoneIndex == 1);
            Assert.Equal(2, commits);
            models.SelectedItem = models.Items.Cast<EditorLightParentModelChoice>().Single(item => item.ObjectId is null);
            Assert.Null(committed.Parent); SameWorld(light, committed);
            Assert.False(bones.IsEnabled); Assert.Equal(3, commits); Assert.Equal(3, starts);
            Assert.True(inspector.TryCommit()); Assert.Equal(3, commits);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InvalidRawInputBlocksAttachmentBeforeTransformResolutionAndRestoresChoice() => await session.Dispatch(() =>
    {
        var light = Light(); var objectId = Guid.NewGuid(); int resolves = 0, commits = 0;
        var inspector = Inspector(light, [new(objectId, "Model A", ["hand"])], _ => { resolves++; return Matrix4x4.Identity; });
        inspector.CommitRequested = _ => { commits++; return true; };
        var window = Host(inspector);
        try
        {
            var numeric = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(input => input.Name == "InlineLightIntensity");
            numeric.Text = "unfinished";
            var models = inspector.FindControl<ComboBox>("LightParentModelChoice")!;
            models.SelectedItem = models.Items.Cast<EditorLightParentModelChoice>().Single(item => item.ObjectId == objectId);
            Assert.Equal(0, commits); Assert.Equal(0, resolves);
            Assert.Null(Assert.IsType<EditorLightParentModelChoice>(models.SelectedItem).ObjectId);
            Assert.Equal("unfinished", numeric.Text); Assert.True(inspector.ViewModel!.HasValidationMessage);
            Assert.Null(inspector.ViewModel.SelectedLight!.Build().Parent);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedTransformOrCommitRestoresParentAndPreservesPendingParameters(bool rejectCommit) => await session.Dispatch(() =>
    {
        var objectId = Guid.NewGuid(); var light = Light() with { Parent = new(objectId) };
        var inspector = Inspector(light, [new(objectId, "Model A", ["hand"])], parent =>
            parent.BoneIndex is not null && !rejectCommit ? throw new InvalidOperationException("unavailable pose") : Matrix4x4.Identity);
        int commits = 0; inspector.CommitRequested = _ => { commits++; return false; };
        var window = Host(inspector);
        try
        {
            var item = inspector.ViewModel!.SelectedLight!; item.Intensity = 9;
            var bones = inspector.FindControl<ComboBox>("LightParentBoneChoice")!;
            bones.SelectedItem = bones.Items.Cast<EditorLightParentBoneChoice>().Single(value => value.BoneIndex == 0);
            Assert.Equal(rejectCommit ? 1 : 0, commits);
            Assert.Same(item, inspector.ViewModel.SelectedLight); Assert.Equal(9m, item.Intensity);
            Assert.Equal(light.Parent, item.Build().Parent);
            Assert.Null(Assert.IsType<EditorLightParentBoneChoice>(bones.SelectedItem).BoneIndex);
            Assert.True(inspector.ViewModel.HasValidationMessage);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task LegacyAndMultiSelectionHideAndGuardParentControls() => await session.Dispatch(() =>
    {
        var first = Light(); var second = Light(); var objectId = Guid.NewGuid(); int commits = 0;
        var inspector = Inspector(first, [new(objectId, "Model A", ["hand"])], _ => Matrix4x4.Identity);
        inspector.SetLighting(new([first, second]), first.Id, Guid.NewGuid());
        inspector.CommitRequested = _ => { commits++; return true; };
        var window = Host(inspector);
        try
        {
            var panel = inspector.FindControl<StackPanel>("LightParentProperties")!;
            var models = inspector.FindControl<ComboBox>("LightParentModelChoice")!;
            inspector.UseWorkspaceBrowser(false);
            Assert.False(panel.IsEffectivelyVisible); Assert.False(models.IsEnabled);
            models.SelectedItem = models.Items.Cast<EditorLightParentModelChoice>().Single(item => item.ObjectId == objectId);
            Assert.Equal(0, commits);
            inspector.UseWorkspaceBrowser(true);
            Assert.True(panel.IsEffectivelyVisible);
            inspector.ViewModel!.SelectMany([first.Id, second.Id], first.Id);
            Assert.False(panel.IsEffectivelyVisible); Assert.False(models.IsEnabled);
            models.SelectedItem = models.Items.Cast<EditorLightParentModelChoice>().Single(item => item.ObjectId == objectId);
            Assert.Equal(0, commits);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EditorPopulatesCurrentModelBonesAndParentEditPausesPlaybackWithUndo() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var model = PmdPmxAdapter.Convert(PmdReader.Read(input));
            shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Character);
            var light = Light(); shell.Workspace.EditLighting(new(new([light]), light.Id));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var models = inspector.FindControl<ComboBox>("LightParentModelChoice")!;
            var target = Assert.Single(models.Items.Cast<EditorLightParentModelChoice>(), item => item.ObjectId is not null);
            Assert.Equal(Assert.Single(shell.Workspace.Session!.Objects).Name, target.AuthoredName);
            double frame = shell.Workspace.Session.CurrentFrame;
            shell.PlayCommand.Execute(null); Assert.True(shell.IsPlaying);
            models.SelectedItem = target;
            Assert.False(shell.IsPlaying); Assert.Equal(frame, shell.Workspace.Session.CurrentFrame);
            var attached = Assert.Single(shell.Workspace.Session.Lighting.Lights);
            Assert.Equal(new SceneLightParent(target.ObjectId!.Value), attached.Parent);
            SameWorld(light, SceneLightTransform.ToWorld(attached, shell.Workspace.Session.ResolveLightParentTransform(attached.Parent!)));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var bones = inspector.FindControl<ComboBox>("LightParentBoneChoice")!;
            Assert.Equal(model.Bones.Select(value => value.Name), bones.Items.Cast<EditorLightParentBoneChoice>().Skip(1).Select(value => value.AuthoredName));
            Assert.True(shell.Workspace.Undo()); Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.True(shell.Workspace.Redo()); Assert.Equal(attached, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            if (Environment.GetEnvironmentVariable("MDA_LIGHTING_LAYOUT_EVIDENCE") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                using var normal = window.CaptureRenderedFrame(); Assert.NotNull(normal);
                normal.Save(Path.Combine(evidence, "lighting-parent.png"));
                window.Width = 1100; window.Height = 720; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                using var minimum = window.CaptureRenderedFrame(); Assert.NotNull(minimum);
                minimum.Save(Path.Combine(evidence, "lighting-parent-minimum.png"));
            }
            return true;
        }
        finally
        {
            typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    private static EditorLightInspector Inspector(NativeSceneLight light, EditorLightParentModel[] models,
        Func<SceneLightParent, Matrix4x4> resolver)
    {
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true);
        inspector.ConfigureParentContext(models, resolver);
        inspector.SetLighting(new([light]), light.Id, Guid.NewGuid());
        return inspector;
    }
    private static Window Host(EditorLightInspector inspector)
    {
        var window = new Window { Width = 500, Height = 1100, Content = inspector };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); return window;
    }
    private static NativeSceneLight Light() => new(Guid.NewGuid(), "fill", SceneLightKind.Spot, new(3, 4, 5),
        Quaternion.CreateFromYawPitchRoll(.2f, -.4f, .1f), Vector3.One);
    private static void SameWorld(NativeSceneLight expected, NativeSceneLight actual)
    {
        Assert.InRange(Vector3.Distance(expected.Position, actual.Position), 0, .0001f);
        Assert.InRange(MathF.Abs(1 - MathF.Abs(Quaternion.Dot(expected.Rotation, actual.Rotation))), 0, .000001f);
        Assert.Equal(expected.Range, actual.Range);
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
