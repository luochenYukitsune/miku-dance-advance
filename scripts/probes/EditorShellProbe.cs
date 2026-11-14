using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.FFmpeg;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
internal static class ShellProbe {
private static string OutputDirectory = "";
private static int _passed;
private static readonly List<string> Checks = [];
[STAThread] public static void Main(string[] args) {
OutputDirectory = Path.GetFullPath(args.Single());
AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().SetupWithoutStarting();
Directory.CreateDirectory(OutputDirectory);
var window = new EditorWindow { DataContext = new EditorShellViewModel(), ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000,-20000) };
window.Show();
Dispatcher.UIThread.RunJobs();
window.UpdateLayout();
Capture(window, "shell-black-1480.png");
CaptureTop(window);
Avalonia.Media.RenderOptions.SetTextRenderingMode(window, Avalonia.Media.TextRenderingMode.SubpixelAntialias); CaptureTop(window, "typography-lcd-1x.png");
Avalonia.Media.RenderOptions.SetTextRenderingMode(window, Avalonia.Media.TextRenderingMode.Antialias);
Assert(window.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).All(b => b.Bounds.Height <= 30), "tool buttons stay within row height");
var shell = (EditorShellViewModel)window.DataContext!;
VerifyTimelineAndNumericInput(window, shell);
VerifyOutputContracts();
VerifyOutputRangeDefaults(window);
Assert(shell.ShowWorldGuides, "world guides default on by user choice");
Capture(window, "shell-black-guides.png");
var guideToggle = window.FindControl<ToggleButton>("WorldGuidesToggle")!;
guideToggle.IsChecked = false; Dispatcher.UIThread.RunJobs();
Assert(!shell.ShowWorldGuides, "viewport toggle changes presentation state");
Assert(!window.GetVisualDescendants().OfType<WorldGuides>().Single().IsActive, "guide toggle hides overlay");
Capture(window, "shell-black-guides-off.png");
shell.ShowWorldGuides = true; Dispatcher.UIThread.RunJobs();
Assert(guideToggle.IsChecked == true, "guide toggle follows shared state");
var objects = window.FindControl<ListBox>("SceneObjects")!;
Assert(shell.IsAnimationInspector, "initial inspector follows animation workflow");
objects.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
Assert(shell.IsLightingPage && shell.IsLightInspector
    && window.GetVisualDescendants().OfType<EditorMainLightInspector>().Single().IsEffectivelyVisible
    && window.FindControl<Control>("NativeLightInspectorArea")!.IsEffectivelyVisible
    && !window.FindControl<Control>("StandardSelectionInspector")!.IsEffectivelyVisible,
    "selecting the main light opens its inspector on the lighting page");
Capture(window, "shell-black-light.png");
objects.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
Assert(window.FindControl<StackPanel>("CameraInspector")!.IsVisible, "selecting camera opens camera inspector");
Capture(window, "shell-black-camera.png");
var animationElements = window.FindControl<TabControl>("AnimationElements")!;
animationElements.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
Assert(shell.IsAnimationInspector && shell.HierarchyTab == 1, "morph tab returns to animation context");
Capture(window, "shell-black-morph.png");
VerifyAnimationInspector(window, shell, true);
animationElements.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
VerifyAnimationInspector(window, shell, false);
Capture(window, "shell-black-bone.png");
VerifyOutputDialog(window, shell, "black");
var settings = window.FindControl<Button>("SettingsButton")!;
var preferences = (Flyout)settings.Flyout!;
preferences.ShowAt(settings); Dispatcher.UIThread.RunJobs();
Assert(preferences.Content is Control settingsContent && ReferenceEquals(settingsContent.DataContext, shell), "settings flyout inherits active editor state");
if (preferences.Content is Control popupContent && popupContent.GetVisualRoot() is Control popupRoot)
{
    popupRoot.UpdateLayout();
    CaptureControl(popupRoot, "shell-settings.png");
    CaptureControl(popupContent, "shell-settings-content.png");
    File.WriteAllText(Path.Combine(OutputDirectory, "settings-render-state.json"), System.Text.Json.JsonSerializer.Serialize(new {
        root = popupRoot.GetType().FullName, bounds = popupRoot.Bounds.ToString(),
        mode = Avalonia.Media.RenderOptions.GetTextRenderingMode(popupRoot).ToString(),
        text = popupContent.GetVisualDescendants().OfType<TextBlock>().Select(t => new { t.Text, foreground = t.Foreground?.ToString(), font = t.FontFamily.ToString(), mode = Avalonia.Media.RenderOptions.GetTextRenderingMode(t).ToString(), bounds = t.Bounds.ToString() })
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
preferences.Hide(); Dispatcher.UIThread.RunJobs();
var camera = Mmd.Modern.Core.SceneCamera.Default;
var normal = WorldGuides.BuildOverlay(camera, 900, 600);
Assert(normal.Lines.Count > 0 && normal.Lines.Count <= 45, "bounded world overlay");
Assert(WorldGuides.BuildOverlay(null, 900, 600).Lines.Count == 0, "missing camera has no guides");
Assert(WorldGuides.BuildOverlay(camera, 0, 600).Lines.Count == 0, "zero viewport has no guides");
Assert(normal.Lines.SequenceEqual(WorldGuides.BuildOverlay(camera with { ProjectionJitterPixels = new System.Numerics.Vector2(.4f,-.3f) }, 900, 600).Lines), "guides ignore temporal jitter");
foreach(var perspectiveCamera in new[] { camera with { VerticalFovDegrees = 45 }, camera with { Eye = new(0,1,-1), Target = new(0,0,0), Near = .5f, VerticalFovDegrees = 70 }, camera with { LeftHanded = true, VerticalFovDegrees = 45 } }) {
var overlay = WorldGuides.BuildOverlay(perspectiveCamera, 900,600);
Assert(overlay.Lines.Count <= 45 && overlay.Lines.All(l => InRange(l.Start) && InRange(l.End)), "perspective/near-plane normalized clipping");
}
window.FindControl<Button>("ThemeToggle")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
Assert(window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light, "theme switches to white");
Capture(window, "shell-white-1480.png");
VerifyOutputDialog(window, shell, "white");
VerifyOutputHostApplication(window, shell);
preferences.ShowAt(settings); Dispatcher.UIThread.RunJobs();
Assert(preferences.Content is Panel settingsPanel && settingsPanel.Background is Avalonia.Media.ISolidColorBrush settingsBrush && settingsBrush.Color == Avalonia.Media.Colors.White, "settings surface follows white theme");
if (preferences.Content is Control whitePopupContent && whitePopupContent.GetVisualRoot() is Control whitePopupRoot)
    CaptureControl(whitePopupRoot, "shell-settings-white.png");
preferences.Hide(); Dispatcher.UIThread.RunJobs();
((EditorShellViewModel)window.DataContext!).SelectedThemeIndex = 0; Dispatcher.UIThread.RunJobs();
window.Width = 1100; window.Height = 720; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
Capture(window, "shell-black-1100.png");
var replacement = new EditorShellViewModel { SelectedThemeIndex = 1 };
window.DataContext = replacement; Dispatcher.UIThread.RunJobs();
Assert(window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light, "replacement view model applies theme");
shell.SelectedThemeIndex = 1; shell.SelectedThemeIndex = 0; Dispatcher.UIThread.RunJobs();
Assert(window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light, "old view model no longer changes theme");
window.Close();
var dots = new LoadingDots();
var parent = new Border { Child = dots, Width = 160, Height = 60 };
var host = new Window { Content = parent, Width = 200, Height = 100, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000,-20000) };
host.Show(); Dispatcher.UIThread.RunJobs();
AssertClockStopped(dots, "idle has no clock");
dots.IsActive = true;
AssertClockRunning(dots, "busy starts animation");
parent.IsVisible = false; AssertClockStopped(dots, "hidden ancestor stops animation");
parent.IsVisible = true; AssertClockRunning(dots, "shown ancestor restarts animation");
dots.AnimationsEnabled = false; AssertClockStopped(dots, "reduced motion has no clock");
dots.AnimationsEnabled = true; AssertClockRunning(dots, "enable animation restarts");
dots.IsActive = false; AssertClockStopped(dots, "idle stops clock");
dots.IsActive = true; parent.Child = null; AssertClockStopped(dots, "detach stops clock");
parent.Child = dots; AssertClockRunning(dots, "reattach restarts");
host.Close(); AssertClockStopped(dots, "close releases clock");
File.WriteAllText(Path.Combine(OutputDirectory,"shell-verification.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = _passed, platform = "Windows native Avalonia 11.3.22", inputVerification = "Avalonia routed-event simulation in native offscreen windows; not physical OS mouse/keyboard input", shadersOrBackendChanged = false, checks = Checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"GUI shell renders and {_passed} checks passed.");
}

private static void VerifyTimelineAndNumericInput(EditorWindow window, EditorShellViewModel shell)
{
    var numeric = window.FindControl<NumericUpDown>("CurrentFrameInput")!;
    var spinner = numeric.GetVisualDescendants().OfType<ButtonSpinner>().SingleOrDefault(control => control.Name == "PART_Spinner");
    var textBox = numeric.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_TextBox");
    Assert(spinner is not null && !spinner.ShowButtonSpinner, "compact numeric template keeps native PART_Spinner without visible step buttons");
    shell.CurrentFrame = 12; Pump(window);
    Assert(numeric.Value == 12, "current-frame numeric follows view-model frame");
    textBox.Focus(); Pump(window);
    RaiseKey(textBox, Key.Up);
    Assert(numeric.Value == 13 && shell.CurrentFrame == 13, "native numeric Up key spins and updates current-frame binding");
    RaiseKey(textBox, Key.Down);
    Assert(numeric.Value == 12 && shell.CurrentFrame == 12, "native numeric Down key spins and preserves current-frame binding");
    textBox.Text = "120"; RaiseKey(textBox, Key.Enter); Pump(window);
    Assert(numeric.Value == 120 && shell.CurrentFrame == 120, "typed integer frame commits through the native numeric textbox");
    textBox.Text = "120.5"; RaiseKey(textBox, Key.Enter); Pump(window);
    Assert(numeric.Value == 120 && shell.CurrentFrame == 120, "fractional frame text cannot move the integer frame cursor");
    textBox.Text = "120"; RaiseKey(textBox, Key.Enter); Pump(window);

    var timeline = window.FindControl<EditorTimeline>("FrameTimeline")!;
    shell.CurrentFrame = 0; shell.FirstVisibleFrame = 0; shell.PixelsPerFrame = 8; Pump(window);
    Assert(timeline.Bounds.Width > 100 && timeline.Bounds.Height >= EditorTimeline.RulerHeight, "timeline has a measurable native ruler region");
    var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
    var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
    var move = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
    var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
    var rightEdge = new Point(timeline.Bounds.Width - 2, 10);
    int expected = timeline.FrameAtX(rightEdge.X);
    timeline.RaiseEvent(new PointerPressedEventArgs(timeline, pointer, window, RootPoint(timeline, window, rightEdge), 1UL, down, KeyModifiers.None));
    Pump(window);
    Assert(pointer.Captured == timeline && shell.CurrentFrame == expected, "routed ruler press captures pointer and selects the geometrically mapped frame");
    timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, pointer, window, RootPoint(timeline, window, rightEdge), 2UL, up, KeyModifiers.None, MouseButton.Left));
    Pump(window);
    Assert(shell.CurrentFrame == expected && timeline.CurrentFrame == expected, "same-point right-edge press and release cannot jump a second time");
    Assert(pointer.Captured is null, "ruler release clears native pointer capture");

    shell.CurrentFrame = 0; shell.FirstVisibleFrame = 0; Pump(window);
    timeline.RaiseEvent(new PointerPressedEventArgs(timeline, pointer, window, RootPoint(timeline, window, new(80, 10)), 3UL, down, KeyModifiers.None));
    timeline.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, timeline, pointer, window, RootPoint(timeline, window, new(240, 10)), 4UL, move, KeyModifiers.None));
    Assert(shell.CurrentFrame == 30, "captured ruler drag updates shared frame through the existing binding");
    timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, pointer, window, RootPoint(timeline, window, new(240, 10)), 5UL, up, KeyModifiers.None, MouseButton.Left));
    Assert(pointer.Captured is null && shell.CurrentFrame == 30, "ruler drag release keeps final frame and releases capture");

    timeline.RaiseEvent(new PointerPressedEventArgs(timeline, pointer, window, RootPoint(timeline, window, new(80, 10)), 6UL, down, KeyModifiers.None));
    int frameBeforeCaptureLoss = shell.CurrentFrame;
    pointer.Capture(null);
    timeline.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, timeline, pointer, window, RootPoint(timeline, window, new(400, 10)), 7UL, move, KeyModifiers.None));
    Assert(shell.CurrentFrame == frameBeforeCaptureLoss, "lost native pointer capture ends timeline dragging");

    shell.CurrentFrame = 40; shell.FirstVisibleFrame = 20; shell.PixelsPerFrame = 8; Pump(window);
    double zoomX = Math.Min(200, timeline.Bounds.Width / 2);
    double anchoredFrame = timeline.FirstVisibleFrame + zoomX / timeline.PixelsPerFrame;
    timeline.RaiseEvent(new PointerWheelEventArgs(timeline, pointer, window, RootPoint(timeline, window, new(zoomX, 10)), 8UL, up, KeyModifiers.Control, new Vector(0, 1)));
    Pump(window);
    Assert(timeline.PixelsPerFrame > 8 && Math.Abs(timeline.FirstVisibleFrame + zoomX / timeline.PixelsPerFrame - anchoredFrame) < .000001,
        "Ctrl-wheel zoom retains the frame under the pointer");
    Assert(shell.PixelsPerFrame == timeline.PixelsPerFrame && shell.FirstVisibleFrame == timeline.FirstVisibleFrame,
        "routed zoom preserves both viewport bindings");

    timeline.Focus(); Pump(window);
    Assert(timeline.IsFocused, "native ruler owns visible keyboard focus");
    RaiseKey(timeline, Key.Right);
    Assert(shell.CurrentFrame == 41, "focused ruler Right key advances one frame");
    RaiseKey(timeline, Key.Left, KeyModifiers.Shift);
    Assert(shell.CurrentFrame == 31, "focused ruler Shift-Left key moves ten frames");
    RaiseKey(timeline, Key.Right, KeyModifiers.Control);
    Assert(shell.CurrentFrame == 31, "ruler does not steal modified editor shortcut keys");

    shell.CurrentFrame = 10000; Pump(window);
    double playheadX = (timeline.CurrentFrame - timeline.FirstVisibleFrame) * timeline.PixelsPerFrame;
    Assert(timeline.CurrentFrame == 10000 && numeric.Value == 10000 && playheadX >= 0 && playheadX <= timeline.Bounds.Width,
        "external frame changes bring playhead into view and retain numeric binding");
    shell.CurrentFrame = 10; Pump(window);
    Assert(timeline.CurrentFrame == 10 && timeline.FirstVisibleFrame <= 10, "timeline binding survives pointer, zoom, keyboard and external seeks");
    shell.GoToStartCommand.Execute(null); Pump(window);
    Assert(shell.CurrentFrame == 0 && shell.FirstVisibleFrame == 0 && timeline.FirstVisibleFrame == 0,
        "go-to-start resets frame and ruler viewport together");

    Assert(EditorTimeline.FrameAtX(double.NegativeInfinity, 0, 8) == 0 &&
        EditorTimeline.FrameAtX(double.PositiveInfinity, 0, 8) == int.MaxValue &&
        EditorTimeline.FrameAtX(double.NaN, double.NaN, double.NaN) == 0,
        "timeline input geometry clamps nonfinite and extreme frame positions");
    foreach (var sample in new[] { (Width: 1480d, First: 0d, Zoom: .5d), (Width: 1e15, First: 0d, Zoom: .5d),
        (Width: 1480d, First: (double)int.MaxValue - 500, Zoom: 64d), (Width: 1480d, First: double.NaN, Zoom: double.NaN) })
    {
        var ticks = EditorTimeline.BuildVisibleTicks(sample.Width, sample.First, sample.Zoom);
        Assert(ticks.Count <= EditorTimeline.MaximumVisibleTickCount && ticks.All(tick => tick.Frame >= 0 && double.IsFinite(tick.X) && tick.X >= 0 && tick.X <= sample.Width),
            "timeline visible ticks stay bounded and finite at ordinary or extreme viewport ranges");
    }
    Assert(EditorTimeline.BuildVisibleTicks(double.PositiveInfinity, 0, 8).Count == 0 &&
        EditorTimeline.BuildVisibleTicks(0, 0, 8).Count == 0, "invalid timeline viewport allocates no ticks");
    shell.PixelsPerFrame = 8; Pump(window);
}

private static void VerifyAnimationInspector(EditorWindow window, EditorShellViewModel shell, bool morph)
{
    string panelName = morph ? "MorphInspector" : "BoneInspector";
    string otherName = morph ? "BoneInspector" : "MorphInspector";
    var panel = window.FindControl<StackPanel>(panelName)!;
    var fields = panel.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
    Assert(shell.IsAnimationInspector && panel.IsVisible && !window.FindControl<StackPanel>(otherName)!.IsVisible,
        $"{panelName} follows bone or morph selection context exclusively");
    Assert(fields.Length == (morph ? 1 : 6) && fields.All(field => !field.IsEffectivelyEnabled && field.Value is null),
        $"{panelName} displays disabled empty numeric values until a real model selection exists");
    Assert(fields.All(field => field.Bounds.Width >= 40 && field.Bounds.Height >= 26),
        $"{panelName} numeric fields have readable native layout");
    Assert(!window.FindControl<TextBox>(morph ? "MorphSearchBox" : "BoneSearchBox")!.IsEffectivelyEnabled,
        $"{panelName} search is unavailable while no model is loaded");
}

private static void VerifyOutputContracts()
{
    var defaults = new OutputSettingsViewModel();
    Assert(defaults.TryCreate(out var baseline) && baseline is not null, "default output draft passes existing ViewOutput contracts");
    if (baseline is null) throw new InvalidOperationException("Default output draft is missing.");
    Assert(baseline.VideoOptions is { Encoder: VideoEncoder.HevcSoftware, RateControl: VideoRateControl.ConstantQuality, Quality: 23 } &&
        baseline.MediaKind == OutputMediaKind.Video, "output defaults use software HEVC constant quality");
    Assert(baseline.Request.Plan.Width == 1920 && baseline.Request.Plan.Height == 1080 && baseline.Request.Plan.FrameRate.Numerator == 30,
        "output defaults retain 1080p at 30 fps");
    Assert(defaults.FramesPerSecond == 30 && defaults.Summary.Contains("30 fps", StringComparison.Ordinal) && !defaults.Summary.Contains('/'),
        "output presents one frames-per-second value and a decimal fps summary");
    RejectOutput(vm => vm.Width = 1919, "odd compressed-video width is rejected without silent padding");
    RejectOutput(vm => { vm.StartFrame = 30; vm.EndFrame = 20; }, "reversed output interval is rejected");
    RejectOutput(vm => vm.FramesPerSecond = 0, "zero output frames per second is rejected");
    RejectOutput(vm => vm.FramesPerSecond = null, "empty output frames per second is rejected");
    RejectOutput(vm => vm.PreRoll = 1, "physics pre-roll before frame zero is rejected");
    RejectOutput(vm => vm.Width = 1920.5m, "fractional output pixel sizes are rejected");
    RejectOutput(vm => vm.Quality = 52, "HEVC constant-quality range is checked by the encoder contract");
    RejectOutput(vm => vm.TransparentBackground = true, "video cannot request a transparent background");
    var image = new OutputSettingsViewModel { MediaKindIndex = 0, StartFrame = 7, EndFrame = 80, TransparentBackground = true,
        AntialiasingIndex = 2 };
    image.SmaaIndex = image.SmaaQualities.ToList().IndexOf("高");
    image.SpatialIndex = image.SpatialSamples.ToList().IndexOf("4×");
    Assert(image.TryCreate(out var alpha) && alpha is not null && alpha.VideoOptions is null && alpha.Request.Plan.FrameCount == 1 &&
        alpha.Request.Plan.StartFrame == 7 && alpha.BackgroundColor.W == 0 && alpha.Request.Quality?.SpatialSamples == 4,
        "transparent PNG keeps one chosen frame with SMAA and SSAA and no video options");

    var decimalRate = new OutputSettingsViewModel { FramesPerSecond = 29.97m };
    Assert(decimalRate.TryCreate(out var decimalDraft) && decimalDraft?.Request.Plan.FrameRate == new OfflineFrameRate(2997, 100) &&
        decimalDraft.Preset.Export.FrameRate == new OfflineFrameRate(2997, 100),
        "decimal 29.97 fps converts exactly to 2997/100 without guessing an NTSC rate");
    var rationalExport = baseline.Preset.Export with { FrameRate = new(30000, 1001) };
    var rationalSource = baseline with
    {
        Preset = new ViewOutputPresetDefinition("probe.rational-fps", "Rational fps", baseline.Preset.Preview,
            rationalExport, baseline.Preset.Encoding),
        Request = rationalExport.CreateRequest(baseline.Request.Plan.StartFrame, baseline.Request.Plan.EndFrame,
            baseline.Request.Plan.PhysicsPreRollFrames)
    };
    var rationalEditor = new OutputSettingsViewModel(rationalSource);
    Assert(rationalEditor.TryCreate(out var rationalDraft) && rationalDraft?.Request.Plan.FrameRate == new OfflineFrameRate(30000, 1001) &&
        rationalDraft.Preset.Export.FrameRate == new OfflineFrameRate(30000, 1001) && !rationalEditor.Summary.Contains('/'),
        "unchanged fractional source fps preserves 30000/1001 while presenting a decimal summary");

    var preview = new ViewRenderRequest(1600, 900, new(Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 2));
    var nullQualityPreset = new ViewOutputPresetDefinition("probe.output", "Probe output", preview,
        baseline.Preset.Export with { Quality = null }, baseline.Preset.Encoding);
    var nullableSource = baseline with { Preset = nullQualityPreset, Request = baseline.Request with { Quality = null } };
    var nullableEditor = new OutputSettingsViewModel(nullableSource);
    Assert(nullableEditor.TryCreate(out var nullableResult) && nullableResult is not null &&
        nullableResult.Request.Quality?.Antialiasing is null && nullableResult.Request.Quality?.SmaaQuality is null &&
        nullableResult.Request.Quality?.SpatialSamples is null && nullableResult.Preset.Export.Quality?.Antialiasing is null &&
        nullableResult.Preset.Export.Quality?.SmaaQuality is null && nullableResult.Preset.Export.Quality?.SpatialSamples is null,
        "applying an unchanged output draft preserves nullable inherited AA and sample overrides");
    Assert(nullableResult!.Preset.Preview == preview, "editing export configuration preserves independent preview configuration");

    var encoding = baseline.VideoOptions! with { RateControl = VideoRateControl.TargetBitrate, TargetBitrate = 12_000_000, Quality = -7 };
    var bitrateSource = baseline with { VideoOptions = encoding,
        Preset = new ViewOutputPresetDefinition("probe.bitrate", "Probe bitrate", preview, baseline.Preset.Export, FfmpegEncodingPreset.FromOptions(encoding)) };
    bitrateSource.Validate();
    var bitrateEditor = new OutputSettingsViewModel(bitrateSource);
    Assert(bitrateEditor.TryCreate(out var bitrateResult) && bitrateResult?.VideoOptions is { RateControl: VideoRateControl.TargetBitrate, Quality: -7 },
        "target-bitrate editing preserves the inactive source quality value");
}

private static void VerifyOutputRangeDefaults(EditorWindow owner)
{
    var emptyShell = new EditorShellViewModel { CurrentFrame = 999 };
    var empty = emptyShell.CreateOutputSettingsViewModel();
    Assert(EditorExportRange.GetLastKeyframe(null) == 0 && empty.StartFrame == 0 && empty.EndFrame == 0,
        "empty editor exports frame zero independently of a later timeline cursor");
    var fixture = CreateRangeProject();
    using var project = fixture.Project;
    Assert(project.DurationFrames == 240 && project.Actors.Single().DurationFrames == 90 &&
        EditorExportRange.GetLastKeyframe(project) == 240,
        "output range reads actual actor and scene metadata including unordered bone, morph, IK, camera, light and shadow keys");
    project.Seek(999);
    Assert(project.CurrentFrame == 999 && EditorExportRange.GetLastKeyframe(project) == 240,
        "seeking past the last authored key does not extend the export default");
    var shell = new EditorShellViewModel(SceneCamera.Default, project) { CurrentFrame = 999 };
    var automatic = shell.CreateOutputSettingsViewModel();
    Assert(automatic.StartFrame == 0 && automatic.EndFrame == 240,
        "injected project supplies the output interval instead of the presentation cursor");
    Assert(automatic.TryCreate(out var automaticDraft) && automaticDraft is { EndFrameFollowsAnimation: true },
        "new valid output drafts record that their end follows authored animation");
    shell.ApplyOutputSettings(automaticDraft!);
    var grownScene = new VmdMotion { ModelName = "scene", CameraKeyframes = fixture.SceneMotion.CameraKeyframes,
        LightKeyframes = [new() { Frame = 400, Color = Vector3.One, Direction = new(0, -1, 0) }],
        SelfShadowKeyframes = fixture.SceneMotion.SelfShadowKeyframes };
    project.Edit(edit => edit.SceneMotion = SceneMotionAsset.FromMotion(grownScene));
    Assert(shell.CreateOutputSettingsViewModel().EndFrame == 400,
        "reopening an automatic applied draft refreshes the end after an actual scene edit");
    Assert(project.Undo() && shell.CreateOutputSettingsViewModel().EndFrame == 240,
        "undoing a longer scene track refreshes the automatic output end again");

    var dialog = new OutputSettingsWindow(shell.CreateOutputSettingsViewModel())
    { WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
    var result = dialog.ShowDialog<OutputSettingsDraft?>(owner);
    Pump(dialog);
    var endInput = dialog.FindControl<NumericUpDown>("EndFrameBox")!;
    var endText = endInput.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_TextBox");
    endText.Focus(); endText.Text = "150"; RaiseKey(endText, Key.Enter); Pump(dialog);
    Assert(endInput.Value == 150 && dialog.ViewModel.EndFrame == 150,
        "typing an output end commits through the native numeric textbox");
    if (dialog.IsVisible)
    {
        dialog.FindControl<Button>("ApplyOutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(owner);
    }
    Assert(result.IsCompleted && result.GetAwaiter().GetResult() is { EndFrameFollowsAnimation: false } manual &&
        manual.Request.Plan.EndFrame == 150, "applying a typed end records a manual output interval");
    shell.ApplyOutputSettings(result.GetAwaiter().GetResult()!);
    project.Edit(edit => edit.SceneMotion = SceneMotionAsset.FromMotion(grownScene));
    var reopenedManual = shell.CreateOutputSettingsViewModel();
    Assert(reopenedManual.StartFrame == 0 && reopenedManual.EndFrame == 150 &&
        new OutputSettingsViewModel(result.GetAwaiter().GetResult(), 999, 400).EndFrame == 150,
        "reopening a manual applied range preserves its end after animation grows to frame 400");

    var sceneEditor = new SceneTrackEditor(grownScene);
    sceneEditor.Apply(sceneEditor.PrepareDelete([new(SceneTrackKind.Light, 400)]));
    project.Edit(edit => edit.SceneMotion = SceneMotionAsset.FromMotion(sceneEditor.ExportMotion()));
    Assert(EditorExportRange.GetLastKeyframe(project) == 150 &&
        new OutputSettingsViewModel(automaticDraft, 999, EditorExportRange.GetLastKeyframe(project)).EndFrame == 150,
        "deleting the last light key shrinks automatic output to the retained self-shadow key");
    sceneEditor.Apply(sceneEditor.PrepareDelete([new(SceneTrackKind.SelfShadow, 150)]));
    project.Edit(edit => edit.SceneMotion = SceneMotionAsset.FromMotion(sceneEditor.ExportMotion()));
    Assert(EditorExportRange.GetLastKeyframe(project) == 120,
        "removing the later shadow exposes the actual scene-camera end");
    project.Edit(edit => edit.SceneMotion = null);
    Assert(EditorExportRange.GetLastKeyframe(project) == 90,
        "removing scene motion exposes the retained actor IK and visibility end");
    project.Edit(edit => { var actor = edit.Motions[0]; edit.Motions[0] = actor with { Motion = new()
        { ModelName = "model", BoneKeyframes = actor.Motion.BoneKeyframes, MorphKeyframes = actor.Motion.MorphKeyframes } }; });
    Assert(EditorExportRange.GetLastKeyframe(project) == 75,
        "unmatched retained morph keys still contribute their authored frame to output");
    project.Edit(edit => { var actor = edit.Motions[0]; edit.Motions[0] = actor with { Motion = new()
        { ModelName = "model", BoneKeyframes = actor.Motion.BoneKeyframes } }; });
    Assert(EditorExportRange.GetLastKeyframe(project) == 30,
        "unordered actor bone records use their maximum authored frame");
}

private static (SceneProjectSession Project, VmdMotion SceneMotion) CreateRangeProject()
{
    var model = new PmxModel
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "model", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
        Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
            SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = [], DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = [],
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
    };
    var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    var composition = new SceneCompositionEditor();
    var actor = composition.Add("actor", Path.Combine(OutputDirectory, "in-memory-range-fixture.pmx"), ModelRole.Character, rest);
    var actorMotion = new VmdMotion { ModelName = "model",
        BoneKeyframes = [new() { BoneName = "root", Frame = 30 }, new() { BoneName = "root", Frame = 0 }],
        MorphKeyframes = [new() { MorphName = "missing", Frame = 75, Weight = .5f }],
        IkKeyframes = [new() { Frame = 90, Visible = true, Switches = [new("missing", true)] }] };
    var sceneMotion = new VmdMotion { ModelName = "scene",
        CameraKeyframes = [new() { Frame = 120, Distance = -45, Interest = new(0, 10, 0), ViewAngle = 30 }],
        LightKeyframes = [new() { Frame = 240, Color = Vector3.One, Direction = new(0, -1, 0) }],
        SelfShadowKeyframes = [new() { Frame = 150, Mode = 2, Distance = .001f }] };
    return (new(composition, new(), [new(actor.Id, model, actorMotion, PhysicsEnabled: false)],
        new NoPhysicsProvider(), sceneMotion: SceneMotionAsset.FromMotion(sceneMotion)), sceneMotion);
}

private sealed class NoPhysicsProvider : IProjectPhysicsBackendProvider
{
    public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        => throw new InvalidOperationException("The output-range fixture has no physical bodies.");
    public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        => throw new InvalidOperationException("The output-range fixture has no shared physics world.");
}

private static void RejectOutput(Action<OutputSettingsViewModel> change, string message)
{
    var model = new OutputSettingsViewModel();
    change(model);
    Assert(!model.TryCreate(out var draft) && draft is null && model.HasValidationMessage, message);
}

private static void VerifyOutputDialog(EditorWindow owner, EditorShellViewModel shell, string theme)
{
    var before = shell.OutputSettings;
    var dialog = new OutputSettingsWindow(new OutputSettingsViewModel(before, (uint)shell.CurrentFrame))
    { WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
    var result = dialog.ShowDialog<OutputSettingsDraft?>(owner);
    Pump(dialog);
    Assert(dialog.ActualThemeVariant == owner.ActualThemeVariant, $"{theme} output dialog inherits native editor theme");
    var tabs = dialog.FindControl<TabControl>("OutputTabs")!;
    Assert(tabs.Items.Count == 3 && !dialog.FindControl<Button>("StartExportButton")!.IsEffectivelyEnabled,
        $"{theme} output dialog exposes three configuration tabs while export execution remains unavailable");
    Assert(new[] { "OutputAdvancedExpander", "QualityAdvancedExpander", "VideoAdvancedExpander" }
        .All(name => dialog.FindControl<Expander>(name) is { IsExpanded: false }),
        $"{theme} output keeps secondary configuration collapsed by default");
    Assert(!dialog.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "背景 RGB"),
        $"{theme} output removes RGB editing from the visible surface");
    CaptureOutputTabs(dialog, tabs, theme, "920");
    dialog.Width = 1000; dialog.Height = 720; Pump(dialog);
    CaptureOutputTabs(dialog, tabs, theme, "1000");
    dialog.Width = 800; dialog.Height = 540; Pump(dialog);
    Assert(dialog.Bounds.Width >= 800 && dialog.Bounds.Height >= 540, $"{theme} output dialog respects 800 by 540 minimum size");
    CaptureOutputTabs(dialog, tabs, theme, "800");
    CancelButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump(owner);
    Assert(result.IsCompleted && result.GetAwaiter().GetResult() is null && ReferenceEquals(shell.OutputSettings, before),
        $"{theme} native modal Cancel returns null and leaves the applied draft unchanged");
}

private static void CaptureOutputTabs(OutputSettingsWindow dialog, TabControl tabs, string theme, string width)
{
    for (int index = 0; index < 3; index++)
    {
        tabs.SelectedIndex = index; Pump(dialog);
        var apply = dialog.FindControl<Button>("ApplyOutputSettingsButton")!;
        Point location = apply.TranslatePoint(new Point(), dialog)!.Value;
        Assert(apply.IsEffectivelyEnabled && apply.Bounds.Width >= 50 && location.Y >= 0 &&
            location.Y + apply.Bounds.Height <= dialog.Bounds.Height + 1,
            $"{theme} output tab {index + 1} keeps Apply visible at {width} pixels");
        Capture(dialog, $"output-{theme}-{width}-tab{index + 1}.png");
        if (index == 0 && width == "1000")
        {
            var combo = dialog.FindControl<ComboBox>("MediaKindBox")!;
            combo.IsDropDownOpen = true; Pump(dialog);
            var popup = combo.GetVisualDescendants().OfType<Popup>().Single(control => control.Name == "PART_Popup");
            Assert(popup.IsOpen && popup.Child is Control { Bounds.Height: > 30 }, $"{theme} compact output dropdown presents its native choice list");
            if (popup.Child?.GetVisualRoot() is Control root) CaptureControl(root, $"output-{theme}-media-dropdown.png");
            combo.IsDropDownOpen = false; Pump(dialog);
        }
        if (index == 1 && width == "1000")
        {
            var checkbox = dialog.FindControl<CheckBox>("DiffusionBox")!;
            var presenter = checkbox.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single(control => control.Name == "PART_ContentPresenter");
            Assert(presenter.Foreground is Avalonia.Media.ISolidColorBrush caption && dialog.Foreground is Avalonia.Media.ISolidColorBrush expected &&
                caption.Color == expected.Color, $"{theme} checkbox caption foreground follows the output theme");
        }
        if (index == 2 && width == "800")
        {
            var advanced = dialog.FindControl<Expander>("VideoAdvancedExpander")!;
            advanced.IsExpanded = true; Pump(dialog);
            var profile = dialog.FindControl<ComboBox>("VideoProfileBox")!;
            Assert(profile.Bounds.Width > 40 && profile.GetVisualAncestors().All(ancestor => ancestor.IsVisible && ancestor.Opacity > .5) && apply.IsEffectivelyEnabled,
                $"{theme} detailed encoding remains available in the advanced section");
            Capture(dialog, $"output-{theme}-800-advanced.png");
            advanced.IsExpanded = false; Pump(dialog);
        }
    }
}

private static void VerifyOutputHostApplication(EditorWindow owner, EditorShellViewModel shell)
{
    var seed = new OutputSettingsViewModel();
    Assert(seed.TryCreate(out var initial) && initial is not null, "host output integration begins with a valid detached draft");
    if (initial is null) throw new InvalidOperationException("Host output draft is missing.");
    var preview = new ViewRenderRequest(1440, 810, new(Antialiasing: FrameAntialiasing.Smaa1X, SpatialSamples: 2));
    initial = initial with { Preset = new ViewOutputPresetDefinition("probe.host", "Host probe", preview,
        initial.Preset.Export, initial.Preset.Encoding) };
    var sceneBefore = shell.Scene;
    var cameraBefore = shell.Camera;
    shell.ApplyOutputSettings(initial);
    owner.FindControl<Button>("OutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump(owner);
    var dialog = owner.OwnedWindows.OfType<OutputSettingsWindow>().Single();
    dialog.ViewModel.Width = 1600; dialog.ViewModel.Height = 900; Pump(dialog);
    var frameRateInput = dialog.FindControl<NumericUpDown>("FrameRateBox")!;
    var frameRateText = frameRateInput.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_TextBox");
    frameRateText.Focus(); frameRateText.Text = "60"; RaiseKey(frameRateText, Key.Enter); Pump(dialog);
    Assert(frameRateInput.Value == 60 && dialog.ViewModel.FramesPerSecond == 60 &&
        dialog.FindControl<NumericUpDown>("FpsNumeratorBox") is null && dialog.FindControl<NumericUpDown>("FpsDenominatorBox") is null,
        "single native fps textbox commits typed 60 without numerator or denominator controls");
    dialog.FindControl<Button>("ApplyOutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump(owner);
    Assert(!dialog.IsVisible && shell.OutputSettings?.Request.Plan is { Width: 1600, Height: 900 },
        "editor output button opens native modal and Apply stores the validated detached draft");
    Assert(shell.OutputSettings?.Request.Plan.FrameRate == new OfflineFrameRate(60),
        "native Apply stores the typed frames-per-second value in the output plan");
    Assert(shell.OutputSettings!.Preset.Preview == preview && ReferenceEquals(shell.Scene, sceneBefore) && shell.Camera == cameraBefore,
        "native output Apply preserves independent preview settings and live scene");
    var applied = shell.OutputSettings;
    owner.FindControl<Button>("OutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump(owner);
    dialog = owner.OwnedWindows.OfType<OutputSettingsWindow>().Single();
    dialog.ViewModel.Width = 2000; Pump(dialog);
    CancelButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump(owner);
    Assert(ReferenceEquals(shell.OutputSettings, applied), "editor output Cancel discards edits after a previous Apply");

    var bitrate = applied!.VideoOptions! with { RateControl = VideoRateControl.TargetBitrate, TargetBitrate = 12_000_000, Quality = -7 };
    var fractionalExport = applied.Preset.Export with { Quality = null, FrameRate = new(30000, 1001) };
    var inheritedPreset = new ViewOutputPresetDefinition("probe.native-roundtrip", "Native roundtrip", preview,
        fractionalExport, FfmpegEncodingPreset.FromOptions(bitrate));
    shell.ApplyOutputSettings(applied with { Preset = inheritedPreset,
        Request = fractionalExport.CreateRequest(applied.Request.Plan.StartFrame, applied.Request.Plan.EndFrame,
            applied.Request.Plan.PhysicsPreRollFrames), VideoOptions = bitrate });
    owner.FindControl<Button>("OutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(owner);
    dialog = owner.OwnedWindows.OfType<OutputSettingsWindow>().Single();
    dialog.FindControl<TabControl>("OutputTabs")!.SelectedIndex = 2; Pump(dialog);
    var qualityBox = dialog.FindControl<NumericUpDown>("VideoQualityBox")!;
    Assert(qualityBox.Value == -7 && !qualityBox.IsEffectivelyEnabled, "native bitrate dialog retains inactive quality without numeric clamping");
    dialog.FindControl<Button>("ApplyOutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(owner);
    Assert(!dialog.IsVisible && shell.OutputSettings?.VideoOptions?.Quality == -7 &&
        shell.OutputSettings.Request.Quality?.SmaaQuality is null && shell.OutputSettings.Request.Quality?.SpatialSamples is null,
        "native unchanged Apply preserves inactive encoder quality and inherited scene quality overrides");
    Assert(shell.OutputSettings?.Request.Plan.FrameRate == new OfflineFrameRate(30000, 1001) &&
        shell.OutputSettings.Preset.Export.FrameRate == new OfflineFrameRate(30000, 1001),
        "native unchanged Apply preserves fractional source fps beyond the displayed decimal precision");

    shell.ApplyOutputSettings(applied);
    owner.FindControl<Button>("OutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(owner);
    dialog = owner.OwnedWindows.OfType<OutputSettingsWindow>().Single();
    dialog.ViewModel.Quality = 52; Pump(dialog);
    dialog.FindControl<Button>("ApplyOutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(dialog);
    Assert(dialog.IsVisible && dialog.ViewModel.HasValidationMessage && ReferenceEquals(shell.OutputSettings, applied),
        "invalid active encoder quality keeps the modal open and preserves the applied draft");
    dialog.ViewModel.MediaKindIndex = 0; Pump(dialog);
    dialog.FindControl<Button>("ApplyOutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(owner);
    Assert(!dialog.IsVisible && shell.OutputSettings?.MediaKind == OutputMediaKind.PngImage && shell.OutputSettings.VideoOptions is null,
        "switching to PNG ignores inactive invalid video quality");
    shell.ApplyOutputSettings(applied);
}

private static Button CancelButton(OutputSettingsWindow dialog) =>
    dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content is string text && text == "取消");
private static Point RootPoint(Control control, Window window, Point local) => control.TranslatePoint(local, window)!.Value;
private static void RaiseKey(Control control, Key key, KeyModifiers modifiers = KeyModifiers.None)
{
    control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
    Dispatcher.UIThread.RunJobs();
}
private static void Pump(Control control)
{
    Dispatcher.UIThread.RunJobs(); control.UpdateLayout(); Dispatcher.UIThread.RunJobs();
}
private static readonly StyledProperty<double> LoadingProgress = (StyledProperty<double>)typeof(LoadingDots)
    .GetField("AnimationProgressProperty", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
private static void PumpClock()
{
    var frame = new DispatcherFrame();
    using var timeout = new System.Threading.Timer(_ => Dispatcher.UIThread.Post(() => frame.Continue = false,
        DispatcherPriority.Send), null, 100, Timeout.Infinite);
    Dispatcher.UIThread.PushFrame(frame);
}
private static void AssertClockStopped(LoadingDots dots, string message)
{
    bool initiallyStopped = !dots.IsAnimating(LoadingProgress) && !dots.Classes.Contains(":animating");
    double progress = dots.GetValue(LoadingProgress); PumpClock();
    Assert(initiallyStopped && !dots.IsAnimating(LoadingProgress) && !dots.Classes.Contains(":animating")
        && dots.GetValue(LoadingProgress) == progress, message);
}
private static void AssertClockRunning(LoadingDots dots, string message)
{
    PumpClock(); double progress = dots.GetValue(LoadingProgress); PumpClock();
    Assert(dots.IsAnimating(LoadingProgress) && dots.Classes.Contains(":animating") && dots.GetValue(LoadingProgress) != progress, message);
}
static void CaptureTop(Window target, string name = "typography-native-1x.png") { using var bitmap = new RenderTargetBitmap(new PixelSize(650,180), new Vector(96,96)); bitmap.Render(target); bitmap.Save(Path.Combine(OutputDirectory, name)); }
static bool InRange(System.Numerics.Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && point.X >= 0 && point.X <= 1 && point.Y >= 0 && point.Y <= 1;
static void Capture(Window target, string name) { var scale = target.RenderScaling; using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(target.Bounds.Width*scale), (int)Math.Ceiling(target.Bounds.Height*scale)), new Vector(96*scale,96*scale)); bitmap.Render(target); bitmap.Save(Path.Combine(OutputDirectory, name)); }
static void CaptureControl(Control target, string name) { if(target.Bounds.Width < 1 || target.Bounds.Height < 1) return; using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(target.Bounds.Width), (int)Math.Ceiling(target.Bounds.Height)), new Vector(96,96)); bitmap.Render(target); bitmap.Save(Path.Combine(OutputDirectory, name)); }
static void Assert(bool value, string message) { if(!value) throw new Exception(message); _passed++; Checks.Add(message); Console.WriteLine("PASS " + message); }
}
