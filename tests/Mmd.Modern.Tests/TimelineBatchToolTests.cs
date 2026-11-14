using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class TimelineBatchToolTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] ToolNames = ["TimelineRangeStartInput", "TimelineRangeEndInput", "SelectTimelineRangeButton",
        "ClearTimelineSelectionButton", "CopyTimelineKeysButton", "PasteTimelineKeysButton", "DeleteTimelineKeysButton"];

    [Fact]
    public async Task TypedClosedRangeSelectsCurrentActorsKeysWithoutMovingPlayheadOrBone() => await session.Dispatch(() =>
    {
        var (window, shell, viewport, actor) = CreateEditor();
        try
        {
            shell.Browser.SelectedBoneIndex = 0;
            shell.CurrentFrame = 7;
            shell.Animation.SetTimelineTrackMode(EditorTimelineTrackMode.FollowSelection);
            var source = shell.Workspace.Motions.Single(value => value.ObjectId == actor).Motion;
            Enter(window, "TimelineRangeStartInput", "10"); Enter(window, "TimelineRangeEndInput", "20");
            Assert.Equal("10", shell.Animation.TimelineRangeStartText); Assert.Equal("20", shell.Animation.TimelineRangeEndText);
            Click(window, "SelectTimelineRangeButton");
            var expected = new[] { KeyframeAddress.Bone("root", 10), KeyframeAddress.Bone("hand", 15), KeyframeAddress.Morph("smile", 20) };
            Assert.Equal(expected, shell.Animation.SelectedKeyframes);
            Assert.Equal(expected, window.FindControl<EditorTimeline>("FrameTimeline")!.SelectedKeyframes);
            Assert.Equal(EditorTimelineTrackMode.AllTracks, shell.Animation.TimelineTrackMode);
            Assert.Equal(7, shell.CurrentFrame); Assert.Equal(7, shell.Workspace.Session!.CurrentFrame);
            Assert.Equal(0, shell.Browser.SelectedBoneIndex); Assert.Equal(0, shell.Animation.SelectedBoneIndex);
            Assert.Same(source, shell.Workspace.Motions.Single(value => value.ObjectId == actor).Motion);
            Assert.Empty(shell.Animation.TimelineRangeValidationMessage);
            Click(window, "ClearTimelineSelectionButton");
            Assert.Empty(shell.Animation.SelectedKeyframes);
            Assert.Empty(window.FindControl<EditorTimeline>("FrameTimeline")!.SelectedKeyframes);
            Assert.Equal(7, shell.CurrentFrame); Assert.Equal(0, shell.Browser.SelectedBoneIndex);
            Assert.False(viewport.HasChildWindow);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    [Theory]
    [InlineData("oops", "20")]
    [InlineData("", "20")]
    [InlineData("-1", "20")]
    [InlineData("20", "10")]
    [InlineData("0", "2147483648")]
    public async Task InvalidTypedRangeKeepsSelectionAndShowsFeedback(string start, string end) => await session.Dispatch(() =>
    {
        var (window, shell, _, _) = CreateEditor();
        try
        {
            shell.Browser.SelectedBoneIndex = 0; shell.CurrentFrame = 7;
            Enter(window, "TimelineRangeStartInput", "10"); Enter(window, "TimelineRangeEndInput", "10"); Click(window, "SelectTimelineRangeButton");
            var before = shell.Animation.SelectedKeyframes.ToArray();
            Enter(window, "TimelineRangeStartInput", start); Enter(window, "TimelineRangeEndInput", end);
            Click(window, "SelectTimelineRangeButton");
            Assert.Equal(before, shell.Animation.SelectedKeyframes);
            Assert.False(string.IsNullOrWhiteSpace(shell.Animation.TimelineRangeValidationMessage));
            Assert.DoesNotContain("Mda.Text.", shell.Animation.TimelineRangeValidationMessage);
            Assert.Equal(start, window.FindControl<TextBox>("TimelineRangeStartInput")!.Text);
            Assert.Equal(end, window.FindControl<TextBox>("TimelineRangeEndInput")!.Text);
            Assert.Equal(7, shell.CurrentFrame); Assert.Equal(0, shell.Browser.SelectedBoneIndex);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    [Fact]
    public async Task ActorSwitchClearsOldSelectionAndRangeNeverTouchesOtherActors() => await session.Dispatch(() =>
    {
        var (window, shell, _, first) = CreateEditor();
        try
        {
            Guid second = AddActor(shell, "second", new VmdMotion { ModelName = "second", BoneKeyframes = [new() { BoneName = "root", Frame = 12 }] });
            shell.RefreshWorkspace(first);
            Enter(window, "TimelineRangeStartInput", "10"); Enter(window, "TimelineRangeEndInput", "20"); Click(window, "SelectTimelineRangeButton");
            Assert.Equal(3, shell.Animation.SelectedKeyframes.Count);
            shell.Browser.SelectedModel = shell.Browser.Models.Single(value => value.ObjectId == second);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(shell.Animation.SelectedKeyframes); Assert.Equal(second, shell.Animation.ObjectId);
            Assert.Equal("0", window.FindControl<TextBox>("TimelineRangeStartInput")!.Text);
            Assert.Equal("12", window.FindControl<TextBox>("TimelineRangeEndInput")!.Text);
            Enter(window, "TimelineRangeStartInput", "10"); Enter(window, "TimelineRangeEndInput", "20"); Click(window, "SelectTimelineRangeButton");
            Assert.Equal(KeyframeAddress.Bone("root", 12), Assert.Single(shell.Animation.SelectedKeyframes));
            Assert.Equal(3, shell.Workspace.Motions.Single(value => value.ObjectId == first).Motion.BoneKeyframes.Count);
            Assert.Single(shell.Workspace.Motions.Single(value => value.ObjectId == second).Motion.BoneKeyframes);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    [Fact]
    public async Task TimelineButtonsUseProductionCopyPasteDeleteAndWorkspaceUndo() => await session.Dispatch(() =>
    {
        var (window, shell, _, actor) = CreateEditor();
        try
        {
            Assert.Same(shell.Animation.CopyKeyframesCommand, window.FindControl<Button>("CopyTimelineKeysButton")!.Command);
            Assert.Same(shell.Animation.PasteKeyframesCommand, window.FindControl<Button>("PasteTimelineKeysButton")!.Command);
            Assert.Same(shell.Animation.DeleteKeyframesCommand, window.FindControl<Button>("DeleteTimelineKeysButton")!.Command);
            Enter(window, "TimelineRangeStartInput", "10"); Enter(window, "TimelineRangeEndInput", "20"); Click(window, "SelectTimelineRangeButton");
            Click(window, "CopyTimelineKeysButton"); Assert.True(shell.Animation.CanPaste);
            shell.CurrentFrame = 50; Dispatcher.UIThread.RunJobs(); Click(window, "PasteTimelineKeysButton");
            var pasted = shell.Workspace.Motions.Single(value => value.ObjectId == actor).Motion;
            Assert.Contains(pasted.BoneKeyframes, key => key.BoneName == "root" && key.Frame == 50);
            Assert.Contains(pasted.BoneKeyframes, key => key.BoneName == "hand" && key.Frame == 55);
            Assert.Contains(pasted.MorphKeyframes, key => key.MorphName == "smile" && key.Frame == 60);
            Enter(window, "TimelineRangeStartInput", "50"); Enter(window, "TimelineRangeEndInput", "60"); Click(window, "SelectTimelineRangeButton");
            Click(window, "DeleteTimelineKeysButton");
            var deleted = shell.Workspace.Motions.Single(value => value.ObjectId == actor).Motion;
            Assert.DoesNotContain(deleted.BoneKeyframes, key => key.Frame >= 50);
            Assert.DoesNotContain(deleted.MorphKeyframes, key => key.Frame >= 50);
            shell.UndoCommand.Execute(null);
            Assert.Contains(shell.Workspace.Motions.Single(value => value.ObjectId == actor).Motion.BoneKeyframes, key => key.Frame == 50);
            shell.UndoCommand.Execute(null);
            Assert.DoesNotContain(shell.Workspace.Motions.Single(value => value.ObjectId == actor).Motion.BoneKeyframes, key => key.Frame >= 50);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    [Theory]
    [InlineData("zh-CN", 1100, 720)] [InlineData("zh-CN", 1480, 960)]
    [InlineData("en", 1100, 720)] [InlineData("en", 1480, 960)]
    [InlineData("ja", 1100, 720)] [InlineData("ja", 1480, 960)]
    public async Task ThreeLanguageToolControlsStayInsideTimelineAndWindow(string language, int width, int height) => await session.Dispatch(() =>
    {
        string previous = EditorLocalization.CurrentLanguage;
        var (window, shell, viewport, _) = CreateEditor(width, height);
        try
        {
            EditorLocalization.SetLanguage(language); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var tools = window.FindControl<Border>("TimelineBatchTools")!;
            Assert.True(tools.Bounds.Width > 0); Assert.True(tools.Bounds.Height > 0);
            foreach (string name in ToolNames)
            {
                var control = window.FindControl<Control>(name)!;
                Assert.True(control.IsVisible); Assert.True(control.Bounds.Width > 0); Assert.True(control.Bounds.Height > 0);
                AssertInside(control, tools); AssertInside(control, window);
                if (control is Button button) Assert.DoesNotContain("Mda.Text.", button.Content?.ToString() ?? "");
            }
            Assert.Same(shell.Animation.SelectTimelineRangeCommand, window.FindControl<Button>("SelectTimelineRangeButton")!.Command);
            Assert.Same(shell.Animation.ClearTimelineSelectionCommand, window.FindControl<Button>("ClearTimelineSelectionButton")!.Command);
            Assert.False(viewport.HasChildWindow);
        }
        finally { EditorLocalization.SetLanguage(previous); Close(window, shell); }
        return true;
    }, Token);

    private static (EditorWindow Window, EditorShellViewModel Shell, EditorViewportHost Viewport, Guid Actor) CreateEditor(int width = 1480, int height = 960)
    {
        var window = new EditorWindow(false) { Width = width, Height = height };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsVisible = false; // Formal UI and workspace only: no native child window or GPU renderer.
        var shell = new EditorShellViewModel();
        Guid actor = AddActor(shell, "batch", new VmdMotion
        {
            ModelName = "batch", BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10 }, new() { BoneName = "hand", Frame = 15 }],
            MorphKeyframes = [new() { MorphName = "smile", Frame = 20, Weight = .5f }], IkKeyframes = [new() { Frame = 30 }],
            CameraKeyframes = [new() { Frame = 15 }], LightKeyframes = [new() { Frame = 15 }]
        });
        shell.RefreshWorkspace(actor); window.DataContext = shell; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return (window, shell, viewport, actor);
    }

    private static Guid AddActor(EditorShellViewModel shell, string name, VmdMotion motion)
    {
        var basis = MorphApplicationTests.Model([MorphApplicationTests.Position("smile", Vector3.UnitY)]);
        var model = new PmxModel
        {
            Header = basis.Header, Name = name, EnglishName = "", Comment = "", EnglishComment = "", Vertices = basis.Vertices,
            Faces = basis.Faces, Textures = [], Materials = basis.Materials, Morphs = basis.Morphs, RigidBodies = [], Joints = [], SoftBodies = [],
            Bones = [basis.Bones[0], new() { Name = "hand", EnglishName = "", Position = Vector3.UnitY, ParentIndex = 0,
                DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate }], DisplayFrames = []
        };
        string path = Path.Combine(Path.GetTempPath(), "mda-timeline-batch-fixture", name + ".pmx");
        Guid id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Prop);
        shell.Workspace.CommitMotion(id, motion); return id;
    }

    private static void Enter(EditorWindow window, string name, string text)
    {
        var box = window.FindControl<TextBox>(name)!;
        Assert.True(box.Focus()); box.SelectAll();
        if (text.Length == 0)
        {
            window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Back, RawInputModifiers.None, PhysicalKey.None, null);
        }
        else window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(text, box.Text);
    }

    private static void Click(EditorWindow window, string name)
    {
        window.UpdateLayout(); var button = window.FindControl<Button>(name)!;
        Assert.True(button.IsEnabled, name + " must be enabled");
        Point point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }

    private static void AssertInside(Control child, Control parent)
    {
        Point point = child.TranslatePoint(default, parent)!.Value;
        Assert.True(point.X >= -.1 && point.Y >= -.1 && point.X + child.Bounds.Width <= parent.Bounds.Width + .1
            && point.Y + child.Bounds.Height <= parent.Bounds.Height + .1,
            $"{child.Name} at {point} size {child.Bounds.Size} spills outside {parent.Name} size {parent.Bounds.Size}");
    }

    private static void Close(EditorWindow window, EditorShellViewModel shell)
    {
        window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs();
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
