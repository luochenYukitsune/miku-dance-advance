using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorMainLightDirectionTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task DirectDirectionEditingKeepsColorAndIntensityAndRegistersTheExactPose()
    {
        var model = new EditorSceneInspectorViewModel { SceneSelectionIndex = 2, LightColorR = .25m,
            LightColorG = .5m, LightColorB = .75m, LightIntensity = 3m };
        var color = model.LightPose.Color;
        var direction = Vector3.Normalize(new Vector3(.3f, -.6f, .7f));
        VmdMotion? registered = null;
        model.CommitSceneMotion = value => registered = value;
        model.LightDirectionVector = direction;
        Assert.Equal(color, model.LightPose.Color);
        Assert.Equal(3m, model.LightIntensity);
        Assert.Equal(.25m, model.LightColorR);
        Assert.Equal(direction, model.LightPose.Direction);
        Assert.True(model.HasLightDraft);
        Assert.Null(registered);
        await model.RegisterLightKeyframeAsync();
        var key = Assert.Single(registered!.LightKeyframes);
        Assert.Equal(direction, key.Direction);
        Assert.Equal(color, key.Color);
    }

    [Fact]
    public void InvalidDirectDirectionCannotPoisonTheExistingPose()
    {
        var model = new EditorSceneInspectorViewModel();
        var before = model.LightPose;
        foreach (var direction in new[] { Vector3.Zero, new Vector3(float.NaN, 1, 0), new Vector3(1, float.PositiveInfinity, 0) })
        {
            model.LightDirectionVector = direction;
            Assert.Equal(before, model.LightPose);
            Assert.False(model.HasLightDraft);
        }
    }

    [Fact]
    public void AnglesRoundTripAndKeepTheVectorLength()
    {
        var model = new EditorSceneInspectorViewModel();
        float length = model.LightPose.Direction.Length();
        model.LightAzimuth = 90; model.LightElevation = 30;
        var direction = model.LightPose.Direction;
        Assert.InRange(direction.Length(), length - 1e-4f, length + 1e-4f);
        // Source on the right and above: the light travels left and down.
        Assert.True(direction.X < 0 && direction.Y < 0);
        Assert.Equal(90m, model.LightAzimuth);
        Assert.Equal(30m, model.LightElevation);
        Assert.True(model.HasLightDraft);
    }

    [Fact]
    public void QuickAimsAndDefaultAreSingleDraftChanges()
    {
        var model = new EditorSceneInspectorViewModel();
        var original = model.LightPose.Direction;
        model.AimLight("top");
        var top = Vector3.Normalize(model.LightPose.Direction);
        Assert.InRange(top.Y, -1.0001f, -.9999f);
        model.AimLight("back");
        Assert.True(model.LightPose.Direction.Z < 0); // from behind the subject, towards the camera
        model.AimLight("default");
        Assert.Equal(original, model.LightPose.Direction);
        model.AimLight("unknown");
        Assert.Equal(original, model.LightPose.Direction);
    }

    [Fact]
    public void LightFromCameraFollowsTheCameraView()
    {
        var model = new EditorSceneInspectorViewModel();
        model.AimLightFromCamera();
        var camera = model.CameraPose.CreateCamera();
        var forward = Vector3.Normalize(camera.Target - camera.Eye);
        Assert.InRange(Vector3.Dot(Vector3.Normalize(model.LightPose.Direction), forward), .9999f, 1.0001f);
    }

    [Fact]
    public void RevertLightDraftAndFlipHemisphereWorkIndependentlyOfSceneSelection()
    {
        var model = new EditorSceneInspectorViewModel { SceneSelectionIndex = 1 };
        model.AimLight("left");
        Assert.True(model.HasLightDraft);
        Assert.False(model.RevertDraftCommand.CanExecute(null));
        Assert.True(model.RevertLightDraftCommand.CanExecute(null));
        model.RevertLightDraftCommand.Execute(null);
        Assert.False(model.HasLightDraft);
        Assert.Equal(EditorSceneInspectorViewModel.DefaultLightDirection, model.LightPose.Direction);

        model.AimLight("top");
        bool beforeBack = model.IsMainLightBacklit;
        model.FlipMainLightHemisphere();
        Assert.NotEqual(beforeBack, model.IsMainLightBacklit);
        Assert.True(MathF.Abs(model.LightPose.Direction.Z) > 0.05f);
    }

    [Fact]
    public async Task PanelAngleInputAndFlipEditTheSameDraft() => await session.Dispatch(() =>
    {
        var model = new EditorSceneInspectorViewModel();
        var inspector = new EditorMainLightInspector { DataContext = model };
        var window = new Window { Content = inspector };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var azimuth = inspector.FindControl<EditorNumericUpDown>("LightAzimuth")!;
            azimuth.Value = 65m;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(65m, model.LightAzimuth);
            var before = model.LightPose.Direction;
            inspector.FindControl<Button>("MainLightFlipHemisphereButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Vector3(before.X, before.Y, -before.Z), model.LightPose.Direction);
            Assert.Equal(model.LightAzimuth, azimuth.Value);
            Assert.True(model.HasLightDraft);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
