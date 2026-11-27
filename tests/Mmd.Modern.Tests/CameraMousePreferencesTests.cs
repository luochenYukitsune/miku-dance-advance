using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class CameraMousePreferencesTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-camera-preferences-" + Guid.NewGuid().ToString("N"));
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false, false, false, false, 0.2f, 1.0f, 3.0f)]
    [InlineData(true, false, false, true, true, 1.5f, 0.5f, 2.25f)]
    [InlineData(true, true, true, true, true, 3.0f, 3.0f, 0.2f)]
    public void SplitDirectionsAndSensitivitiesPersistIndependentlyAndCleanly(
        bool orbitX, bool orbitY, bool panX, bool panY, bool zoom,
        float orbitSensitivity, float panSensitivity, float zoomSensitivity)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        var preferences = new EditorPreferences
        {
            InvertCameraOrbitX = orbitX,
            InvertCameraOrbitY = orbitY,
            InvertCameraPanX = panX,
            InvertCameraPanY = panY,
            InvertCameraZoom = zoom,
            CameraOrbitSensitivity = orbitSensitivity,
            CameraPanSensitivity = panSensitivity,
            CameraZoomSensitivity = zoomSensitivity
        };
        preferences.Save(path);

        using (var doc = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            Assert.False(doc.RootElement.TryGetProperty("invertCameraMouseX", out _));
            Assert.False(doc.RootElement.TryGetProperty("invertCameraMouseY", out _));
            Assert.True(doc.RootElement.TryGetProperty("invertCameraOrbitX", out _));
            Assert.True(doc.RootElement.TryGetProperty("invertCameraPanX", out _));
            Assert.True(doc.RootElement.TryGetProperty("invertCameraZoom", out _));
            Assert.True(doc.RootElement.TryGetProperty("cameraOrbitSensitivity", out _));
            Assert.True(doc.RootElement.TryGetProperty("cameraPanSensitivity", out _));
            Assert.True(doc.RootElement.TryGetProperty("cameraZoomSensitivity", out _));
        }

        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(orbitX, loaded.InvertCameraOrbitX);
        Assert.Equal(orbitY, loaded.InvertCameraOrbitY);
        Assert.Equal(panX, loaded.InvertCameraPanX);
        Assert.Equal(panY, loaded.InvertCameraPanY);
        Assert.Equal(zoom, loaded.InvertCameraZoom);
        Assert.Equal(orbitSensitivity, loaded.CameraOrbitSensitivity);
        Assert.Equal(panSensitivity, loaded.CameraPanSensitivity);
        Assert.Equal(zoomSensitivity, loaded.CameraZoomSensitivity);

        File.WriteAllText(path, """{"schemaVersion":1,"themeIndex":0,"language":"zh-CN"}""");
        loaded = EditorPreferences.Load(out diagnostic, path);
        Assert.Null(diagnostic);
        Assert.False(loaded.InvertCameraOrbitX);
        Assert.False(loaded.InvertCameraOrbitY);
        Assert.False(loaded.InvertCameraPanX);
        Assert.False(loaded.InvertCameraPanY);
        Assert.False(loaded.InvertCameraZoom);
        Assert.Equal(1.0f, loaded.CameraOrbitSensitivity);
        Assert.Equal(1.0f, loaded.CameraPanSensitivity);
        Assert.Equal(1.0f, loaded.CameraZoomSensitivity);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LegacyMouseInvertFieldsMigrateToBothOrbitAndPanAndResaveCleanly(bool legacyX, bool legacyY)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "legacy.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            themeIndex = 0,
            language = "zh-CN",
            invertCameraMouseX = legacyX,
            invertCameraMouseY = legacyY
        }));

        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(legacyX, loaded.InvertCameraOrbitX);
        Assert.Equal(legacyX, loaded.InvertCameraPanX);
        Assert.Equal(legacyY, loaded.InvertCameraOrbitY);
        Assert.Equal(legacyY, loaded.InvertCameraPanY);
        Assert.False(loaded.InvertCameraZoom);
        Assert.Equal(1.0f, loaded.CameraOrbitSensitivity);
        Assert.Equal(1.0f, loaded.CameraPanSensitivity);
        Assert.Equal(1.0f, loaded.CameraZoomSensitivity);

        loaded.Save(path);
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            Assert.False(doc.RootElement.TryGetProperty("invertCameraMouseX", out _));
            Assert.False(doc.RootElement.TryGetProperty("invertCameraMouseY", out _));
        }

        var reloaded = EditorPreferences.Load(out diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(loaded, reloaded);
    }

    [Fact]
    public void SplitFieldsTakePrecedenceOverLegacyFieldsInMixedFiles()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "mixed.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "language": "zh-CN",
              "invertCameraMouseX": true,
              "invertCameraMouseY": true,
              "invertCameraOrbitX": false,
              "invertCameraPanY": false,
              "invertCameraZoom": true
            }
            """);

        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.False(loaded.InvertCameraOrbitX);
        Assert.True(loaded.InvertCameraPanX);
        Assert.True(loaded.InvertCameraOrbitY);
        Assert.False(loaded.InvertCameraPanY);
        Assert.True(loaded.InvertCameraZoom);

        string migrationSource = Path.Combine(directory, "migration-source.json");
        File.WriteAllText(migrationSource, """
            {
              "schemaVersion": 1,
              "language": "en",
              "invertCameraMouseX": true,
              "invertCameraOrbitX": false,
              "invertCameraPanX": false,
              "cameraOrbitSensitivity": 50.0,
              "cameraPanSensitivity": 0.01,
              "cameraZoomSensitivity": "NaN"
            }
            """);
        var migrated = EditorPreferences.LoadForMigration(migrationSource, Path.Combine(directory, "Recovery"));
        Assert.False(migrated.InvertCameraOrbitX);
        Assert.False(migrated.InvertCameraPanX);
        Assert.Equal(3.0f, migrated.CameraOrbitSensitivity);
        Assert.Equal(0.2f, migrated.CameraPanSensitivity);
        Assert.Equal(1.0f, migrated.CameraZoomSensitivity);
    }

    [Fact]
    public void InvalidNanInfinityAndOutOfRangeSensitivitiesAreSafelyClamped()
    {
        var constructed = new EditorPreferences
        {
            CameraOrbitSensitivity = float.NaN,
            CameraPanSensitivity = float.PositiveInfinity,
            CameraZoomSensitivity = float.NegativeInfinity
        };
        Assert.Equal(1.0f, constructed.CameraOrbitSensitivity);
        Assert.Equal(1.0f, constructed.CameraPanSensitivity);
        Assert.Equal(1.0f, constructed.CameraZoomSensitivity);

        var outOfRange = new EditorPreferences
        {
            CameraOrbitSensitivity = -4f,
            CameraPanSensitivity = 0.05f,
            CameraZoomSensitivity = 99f
        };
        Assert.Equal(0.2f, outOfRange.CameraOrbitSensitivity);
        Assert.Equal(0.2f, outOfRange.CameraPanSensitivity);
        Assert.Equal(3.0f, outOfRange.CameraZoomSensitivity);

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "clamped.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "language": "zh-CN",
              "cameraOrbitSensitivity": "NaN",
              "cameraPanSensitivity": "Infinity",
              "cameraZoomSensitivity": -12.5
            }
            """);
        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(1.0f, loaded.CameraOrbitSensitivity);
        Assert.Equal(1.0f, loaded.CameraPanSensitivity);
        Assert.Equal(0.2f, loaded.CameraZoomSensitivity);

        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "language": "zh-CN",
              "cameraOrbitSensitivity": 1e40,
              "cameraPanSensitivity": 42.0,
              "cameraZoomSensitivity": 0.19
            }
            """);
        loaded = EditorPreferences.Load(out diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(1.0f, loaded.CameraOrbitSensitivity);
        Assert.Equal(3.0f, loaded.CameraPanSensitivity);
        Assert.Equal(0.2f, loaded.CameraZoomSensitivity);

        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "language": "zh-CN",
              "invertCameraMouseX": null,
              "invertCameraMouseY": null,
              "cameraOrbitSensitivity": null,
              "cameraPanSensitivity": null,
              "cameraZoomSensitivity": null
            }
            """);
        loaded = EditorPreferences.Load(out diagnostic, path);
        Assert.Null(diagnostic);
        Assert.False(loaded.InvertCameraOrbitX);
        Assert.False(loaded.InvertCameraOrbitY);
        Assert.False(loaded.InvertCameraPanX);
        Assert.False(loaded.InvertCameraPanY);
        Assert.False(loaded.InvertCameraZoom);
        Assert.Equal(1.0f, loaded.CameraOrbitSensitivity);
        Assert.Equal(1.0f, loaded.CameraPanSensitivity);
        Assert.Equal(1.0f, loaded.CameraZoomSensitivity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentInvertsAndSensitivitiesAffectOrbitPanAndZoomInBothModes(bool pilotCamera) => await session.Dispatch(() =>
    {
        var host = new EditorViewportHost { Width = 800, Height = 600 };
        host.SetCameraPilotMode(pilotCamera);
        var beginDrag = typeof(EditorViewportHost).GetMethod("BeginCameraDrag", Hidden)!;
        PmmCameraPose ReadActivePose() => pilotCamera
            ? host.ManualCameraPose!.Value
            : host.FreeEditorCameraPose!.Value;

        host.CameraOrbitSensitivity = float.NaN;
        host.CameraPanSensitivity = 10f;
        host.CameraZoomSensitivity = -5f;
        Assert.Equal(1.0f, host.CameraOrbitSensitivity);
        Assert.Equal(3.0f, host.CameraPanSensitivity);
        Assert.Equal(0.2f, host.CameraZoomSensitivity);

        // Orbit: independent X/Y invert & sensitivity without affecting Pan
        host.CameraOrbitSensitivity = 1.0f;
        host.CameraPanSensitivity = 1.0f;
        host.CameraZoomSensitivity = 1.0f;
        host.InvertCameraOrbitX = false;
        host.InvertCameraOrbitY = false;
        host.InvertCameraPanX = true;
        host.InvertCameraPanY = true;

        beginDrag.Invoke(host, null);
        var beforeOrbit = (PmmCameraPose)typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.GetValue(host)!;
        host.ApplyOrbitDrag(20f, 10f);
        var defaultOrbit = ReadActivePose();
        var defaultOrbitDelta = defaultOrbit.Angle - beforeOrbit.Angle;
        Assert.True(defaultOrbitDelta.X > 0f && defaultOrbitDelta.Y > 0f);

        host.InvertCameraOrbitX = true;
        host.InvertCameraOrbitY = false;
        host.CameraOrbitSensitivity = 2.0f;
        typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.SetValue(host, beforeOrbit);
        host.ApplyOrbitDrag(20f, 10f);
        var orbitInvertX = ReadActivePose().Angle - beforeOrbit.Angle;
        Assert.InRange(MathF.Abs(orbitInvertX.Y - (-defaultOrbitDelta.Y * 2.0f)), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(orbitInvertX.X - (defaultOrbitDelta.X * 2.0f)), 0f, 1e-5f);

        host.InvertCameraOrbitX = false;
        host.InvertCameraOrbitY = true;
        host.CameraOrbitSensitivity = 0.5f;
        typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.SetValue(host, beforeOrbit);
        host.ApplyOrbitDrag(20f, 10f);
        var orbitInvertY = ReadActivePose().Angle - beforeOrbit.Angle;
        Assert.InRange(MathF.Abs(orbitInvertY.Y - (defaultOrbitDelta.Y * 0.5f)), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(orbitInvertY.X - (-defaultOrbitDelta.X * 0.5f)), 0f, 1e-5f);

        // Pan: independent X/Y invert & sensitivity without Orbit interfering
        host.InvertCameraOrbitX = true;
        host.InvertCameraOrbitY = true;
        host.InvertCameraPanX = false;
        host.InvertCameraPanY = false;
        host.CameraPanSensitivity = 1.0f;
        typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.SetValue(host, beforeOrbit);
        host.ApplyPanDrag(30f, 0f, 600f);
        var panRightDefault = ReadActivePose().LookAt - beforeOrbit.LookAt;
        Assert.True(panRightDefault.Length() > 0f);

        host.InvertCameraPanX = true;
        host.CameraPanSensitivity = 2.5f;
        typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.SetValue(host, beforeOrbit);
        host.ApplyPanDrag(30f, 0f, 600f);
        var panRightInverted = ReadActivePose().LookAt - beforeOrbit.LookAt;
        Assert.InRange(Vector3.Distance(panRightInverted, -panRightDefault * 2.5f), 0f, 1e-4f);

        host.InvertCameraPanX = false;
        host.InvertCameraPanY = false;
        host.CameraPanSensitivity = 1.0f;
        typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.SetValue(host, beforeOrbit);
        host.ApplyPanDrag(0f, 20f, 600f);
        var panDownDefault = ReadActivePose().LookAt - beforeOrbit.LookAt;
        Assert.True(panDownDefault.Length() > 0f);

        host.InvertCameraPanY = true;
        host.CameraPanSensitivity = 0.5f;
        typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.SetValue(host, beforeOrbit);
        host.ApplyPanDrag(0f, 20f, 600f);
        var panDownInverted = ReadActivePose().LookAt - beforeOrbit.LookAt;
        Assert.InRange(Vector3.Distance(panDownInverted, -panDownDefault * 0.5f), 0f, 1e-4f);

        // Zoom: independent invert & sensitivity
        var beforeZoom = ReadActivePose();
        host.InvertCameraZoom = false;
        host.CameraZoomSensitivity = 1.0f;
        host.ApplyZoomWheel(120);
        var zoomInDefault = ReadActivePose();
        Assert.InRange(MathF.Abs(MathF.Abs(zoomInDefault.CameraDepth) - MathF.Abs(beforeZoom.CameraDepth) * 0.9f), 0f, 1e-4f);

        host.InvertCameraZoom = true;
        host.CameraZoomSensitivity = 1.0f;
        host.ApplyZoomWheel(120);
        var zoomBackOut = ReadActivePose();
        Assert.InRange(MathF.Abs(MathF.Abs(zoomBackOut.CameraDepth) - MathF.Abs(beforeZoom.CameraDepth)), 0f, 1e-3f);

        host.InvertCameraZoom = false;
        host.CameraZoomSensitivity = 2.0f;
        host.ApplyZoomWheel(120);
        var zoomFast = ReadActivePose();
        Assert.InRange(MathF.Abs(MathF.Abs(zoomFast.CameraDepth) - MathF.Abs(zoomBackOut.CameraDepth) * 0.81f), 0f, 1e-3f);

        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RightClickContextMenuAndRightDragPanBindingsRemainUnchanged() => await session.Dispatch(() =>
    {
        var window = new Window { Width = 800, Height = 600 };
        var host = new EditorViewportHost { Width = 800, Height = 600, InvertCameraPanX = true, CameraPanSensitivity = 2.0f };
        window.Content = host;
        window.Show();
        try
        {
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            int contextClicks = 0;
            host.ContextClick += _ => contextClicks++;

            var rightClick = typeof(EditorViewportHost).GetField("rightClick", Hidden)!.GetValue(host)!;
            var begin = rightClick.GetType().GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var move = rightClick.GetType().GetMethod("Move", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var end = rightClick.GetType().GetMethod("End", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Short right-click opens context menu without panning.
            var beforeShort = host.ManualCameraPose;
            begin.Invoke(rightClick, [new Point(100, 100), 1000u]);
            end.Invoke(rightClick, [new Point(101, 101), 1050u]);
            Assert.Equal(1, contextClicks);
            Assert.Equal(beforeShort, host.ManualCameraPose);

            // Right-button drag beyond threshold pans without opening context menu.
            begin.Invoke(rightClick, [new Point(100, 100), 2000u]);
            var startPose = host.EditableCameraForTest();
            move.Invoke(rightClick, [new Point(140, 100)]);
            end.Invoke(rightClick, [new Point(140, 100), 2200u]);
            Assert.Equal(1, contextClicks);
            Assert.NotNull(host.ManualCameraPose);
            var expected = EditorViewportHost.PanCameraWithPreferences(startPose, 40f, 0f, (float)host.Bounds.Height, invertX: true, invertY: false, sensitivity: 2.0f);
            Assert.InRange(Vector3.Distance(host.ManualCameraPose!.Value.LookAt, expected.LookAt), 0f, 1e-4f);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PreferencesDialogDraftApplyCancelResetAndValidationWorkHeadlessly() => await session.Dispatch(async () =>
    {
        var source = new EditorPreferences
        {
            InvertCameraOrbitX = true,
            InvertCameraOrbitY = false,
            InvertCameraPanX = false,
            InvertCameraPanY = true,
            InvertCameraZoom = true,
            CameraOrbitSensitivity = 2.0f,
            CameraPanSensitivity = 0.5f,
            CameraZoomSensitivity = 1.6f
        };
        var owner = new Window();
        owner.Show();
        try
        {
            var dialog = new EditorPreferencesWindow(source);
            var result = dialog.ShowDialog<EditorPreferences?>(owner);

            var orbitXBox = dialog.FindControl<CheckBox>("PreferencesInvertCameraOrbitXBox")!;
            var orbitYBox = dialog.FindControl<CheckBox>("PreferencesInvertCameraOrbitYBox")!;
            var panXBox = dialog.FindControl<CheckBox>("PreferencesInvertCameraPanXBox")!;
            var panYBox = dialog.FindControl<CheckBox>("PreferencesInvertCameraPanYBox")!;
            var zoomBox = dialog.FindControl<CheckBox>("PreferencesInvertCameraZoomBox")!;
            var orbitNum = dialog.FindControl<EditorNumericUpDown>("PreferencesCameraOrbitSensitivityBox")!;
            var orbitSlider = dialog.FindControl<EditorValueSlider>("PreferencesCameraOrbitSensitivitySlider")!;
            var panNum = dialog.FindControl<EditorNumericUpDown>("PreferencesCameraPanSensitivityBox")!;
            var panSlider = dialog.FindControl<EditorValueSlider>("PreferencesCameraPanSensitivitySlider")!;
            var zoomNum = dialog.FindControl<EditorNumericUpDown>("PreferencesCameraZoomSensitivityBox")!;
            var zoomSlider = dialog.FindControl<EditorValueSlider>("PreferencesCameraZoomSensitivitySlider")!;
            var resetButton = dialog.FindControl<Button>("PreferencesResetCameraMouseButton")!;
            var applyButton = dialog.FindControl<Button>("ApplyPreferencesButton")!;
            var validation = dialog.FindControl<TextBlock>("PreferencesValidationMessage")!;

            Assert.True(orbitXBox.IsChecked);
            Assert.False(orbitYBox.IsChecked);
            Assert.False(panXBox.IsChecked);
            Assert.True(panYBox.IsChecked);
            Assert.True(zoomBox.IsChecked);
            Assert.Equal(2.0m, orbitNum.Value);
            Assert.Equal(2.0m, orbitSlider.Number);
            Assert.Equal(0.5m, panNum.Value);
            Assert.Equal(0.5m, panSlider.Number);
            Assert.Equal(1.6m, zoomNum.Value);
            Assert.Equal(1.6m, zoomSlider.Number);

            // Out-of-range typed input blocks Apply.
            orbitNum.Text = "9.5";
            Dispatcher.UIThread.RunJobs();
            Assert.False(orbitNum.IsInputValid);
            applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(result.IsCompleted);
            Assert.True(validation.IsVisible);

            // Recovering from invalid/empty/partial keyboard input keeps slider synchronized.
            orbitNum.Text = "1.5";
            Dispatcher.UIThread.RunJobs();
            Assert.True(orbitNum.IsInputValid);
            Assert.Equal(1.5m, orbitSlider.Number);

            orbitNum.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.False(orbitNum.IsInputValid);
            orbitNum.Text = "0.";
            Dispatcher.UIThread.RunJobs();
            Assert.False(orbitNum.IsInputValid);
            orbitNum.Text = "0.5";
            Dispatcher.UIThread.RunJobs();
            Assert.True(orbitNum.IsInputValid);
            Assert.Equal(0.5m, orbitSlider.Number);

            // Restore invalid input to verify one-click reset also clears invalid mouse input
            // while leaving unrelated invalid inputs (e.g. on another tab) preserved.
            orbitNum.Text = "9.5";
            Dispatcher.UIThread.RunJobs();
            Assert.False(orbitNum.IsInputValid);

            // One-click reset restores defaults in the draft (including clearing invalid text) without mutating _source.
            resetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(orbitXBox.IsChecked);
            Assert.False(orbitYBox.IsChecked);
            Assert.False(panXBox.IsChecked);
            Assert.False(panYBox.IsChecked);
            Assert.False(zoomBox.IsChecked);
            Assert.True(orbitNum.IsInputValid);
            Assert.Equal(1.0m, orbitNum.Value);
            Assert.Equal(1.0m, orbitSlider.Number);
            Assert.Equal(1.0m, panNum.Value);
            Assert.Equal(1.0m, panSlider.Number);
            Assert.Equal(1.0m, zoomNum.Value);
            Assert.Equal(1.0m, zoomSlider.Number);
            Assert.False(validation.IsVisible);
            Assert.True(source.InvertCameraOrbitX);

            // Slider & box stay synchronized before Apply.
            orbitSlider.Number = 2.4m;
            panNum.Value = 0.8m;
            zoomSlider.Number = 1.25m;
            orbitYBox.IsChecked = true;
            panXBox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2.4m, orbitNum.Value);
            Assert.Equal(0.8m, panSlider.Number);
            Assert.Equal(1.25m, zoomNum.Value);

            applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.IsCompletedSuccessfully);
            var applied = await result;
            Assert.NotNull(applied);
            Assert.False(applied!.InvertCameraOrbitX);
            Assert.True(applied.InvertCameraOrbitY);
            Assert.True(applied.InvertCameraPanX);
            Assert.False(applied.InvertCameraPanY);
            Assert.False(applied.InvertCameraZoom);
            Assert.Equal(2.4f, applied.CameraOrbitSensitivity);
            Assert.Equal(0.8f, applied.CameraPanSensitivity);
            Assert.Equal(1.25f, applied.CameraZoomSensitivity);
            Assert.True(source.InvertCameraOrbitX);

            // Cancel after clicking reset still preserves source; reset does not clear unrelated tab validation errors.
            var cancelled = new EditorPreferencesWindow(source);
            var cancelledResult = cancelled.ShowDialog<EditorPreferences?>(owner);
            var recoveryInterval = cancelled.FindControl<EditorNumericUpDown>("RecoveryIntervalBox")!;
            recoveryInterval.Text = "999";
            cancelled.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(cancelled.FindControl<TextBlock>("PreferencesValidationMessage")!.IsVisible);
            cancelled.FindControl<Button>("PreferencesResetCameraMouseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(cancelled.FindControl<TextBlock>("PreferencesValidationMessage")!.IsVisible);
            cancelled.Close(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(cancelledResult.IsCompletedSuccessfully);
            Assert.Null(await cancelledResult);
            Assert.True(source.InvertCameraOrbitX);
            Assert.Equal(2.0f, source.CameraOrbitSensitivity);
            return true;
        }
        finally { owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    [InlineData("ja")]
    public async Task PreferencesWindowRendersAllCameraMouseLabelsInAllLanguages(string language) => await session.Dispatch(() =>
    {
        string previous = EditorLocalization.CurrentLanguage;
        EditorLocalization.SetLanguage(language);
        var dialog = new EditorPreferencesWindow(new EditorPreferences { Language = language });
        try
        {
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            string[] texts =
            [
                dialog.FindControl<TextBlock>("PreferencesCameraMouseHeading")!.Text ?? "",
                dialog.FindControl<Button>("PreferencesResetCameraMouseButton")!.Content as string ?? "",
                dialog.FindControl<TextBlock>("PreferencesCameraOrbitHeading")!.Text ?? "",
                dialog.FindControl<CheckBox>("PreferencesInvertCameraOrbitXBox")!.Content as string ?? "",
                dialog.FindControl<CheckBox>("PreferencesInvertCameraOrbitYBox")!.Content as string ?? "",
                dialog.FindControl<TextBlock>("PreferencesCameraOrbitSensitivityLabel")!.Text ?? "",
                dialog.FindControl<TextBlock>("PreferencesCameraPanHeading")!.Text ?? "",
                dialog.FindControl<CheckBox>("PreferencesInvertCameraPanXBox")!.Content as string ?? "",
                dialog.FindControl<CheckBox>("PreferencesInvertCameraPanYBox")!.Content as string ?? "",
                dialog.FindControl<TextBlock>("PreferencesCameraPanSensitivityLabel")!.Text ?? "",
                dialog.FindControl<TextBlock>("PreferencesCameraZoomHeading")!.Text ?? "",
                dialog.FindControl<CheckBox>("PreferencesInvertCameraZoomBox")!.Content as string ?? "",
                dialog.FindControl<TextBlock>("PreferencesCameraZoomSensitivityLabel")!.Text ?? "",
                dialog.FindControl<TextBlock>("PreferencesCameraMouseHint")!.Text ?? ""
            ];
            Assert.All(texts, text =>
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("Mda.Text.", text);
                Assert.DoesNotContain("??", text);
                Assert.DoesNotContain('\uFFFD', text);
            });
            return true;
        }
        finally
        {
            dialog.Close();
            EditorLocalization.SetLanguage(previous);
            Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ApplyingPreferencesUpdatesExistingViewportWithoutDirtyingProject() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsVisible = false;
        window.DataContext = new EditorShellViewModel();
        window.Show();
        try
        {
            var shell = (EditorShellViewModel)window.DataContext;
            var bones = viewport.BoneController;
            var lights = viewport.LightController;
            typeof(EditorWindow).GetMethod("ApplyPreferences", Hidden)!.Invoke(window,
            [
                new EditorPreferences
                {
                    InvertCameraOrbitX = true,
                    InvertCameraOrbitY = false,
                    InvertCameraPanX = false,
                    InvertCameraPanY = true,
                    InvertCameraZoom = true,
                    CameraOrbitSensitivity = 2.2f,
                    CameraPanSensitivity = 0.6f,
                    CameraZoomSensitivity = 1.75f
                }
            ]);
            Assert.True(viewport.InvertCameraOrbitX);
            Assert.False(viewport.InvertCameraOrbitY);
            Assert.False(viewport.InvertCameraPanX);
            Assert.True(viewport.InvertCameraPanY);
            Assert.True(viewport.InvertCameraZoom);
            Assert.Equal(2.2f, viewport.CameraOrbitSensitivity);
            Assert.Equal(0.6f, viewport.CameraPanSensitivity);
            Assert.Equal(1.75f, viewport.CameraZoomSensitivity);
            Assert.False(shell.Workspace.IsDirty);
            Assert.Same(bones, viewport.BoneController);
            Assert.Same(lights, viewport.LightController);

            typeof(EditorWindow).GetMethod("ApplyPreferences", Hidden)!.Invoke(window, [new EditorPreferences()]);
            Assert.False(viewport.InvertCameraOrbitX);
            Assert.False(viewport.InvertCameraOrbitY);
            Assert.False(viewport.InvertCameraPanX);
            Assert.False(viewport.InvertCameraPanY);
            Assert.False(viewport.InvertCameraZoom);
            Assert.Equal(1.0f, viewport.CameraOrbitSensitivity);
            Assert.Equal(1.0f, viewport.CameraPanSensitivity);
            Assert.Equal(1.0f, viewport.CameraZoomSensitivity);
            return true;
        }
        finally
        {
            typeof(EditorWindow).GetField("_closingConfirmed", Hidden)!.SetValue(window, true);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(session.Dispose).ConfigureAwait(false); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

file static class ViewportTestExtensions
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static PmmCameraPose EditableCameraForTest(this EditorViewportHost host)
        => (PmmCameraPose)typeof(EditorViewportHost).GetField("dragCamera", Hidden)!.GetValue(host)!;
}
