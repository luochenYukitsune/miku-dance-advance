using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Opt-in measurement of the real editor's light-slider release path on user-supplied models.
/// Configure with MDA_LIGHT_PROBE_MODELS (';'-separated PMX paths, first is the environment) and
/// MDA_LIGHT_PROBE_OUTPUT. Without them the probe is skipped; no repository asset is required.</summary>
[Collection(QualityLoadingCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class LightingCommitLatencyProbe : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Pump(EditorWindow window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static NativeSceneLight[] Lights(int authored, int enabled, int shadowed, int area)
    {
        return Enumerable.Range(0, authored).Select(i =>
        {
            bool isArea = i < area;
            var kind = isArea ? SceneLightKind.Area : i % 3 == 2 ? SceneLightKind.Spot : SceneLightKind.Point;
            var position = new Vector3((i % 8 - 4) * 5, 18, (i / 8 % 4 - 2) * 5);
            var rotation = kind == SceneLightKind.Point ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
            return new NativeSceneLight(Guid.NewGuid(), "Probe " + (i + 1), kind, position, rotation, Vector3.One,
                Intensity: isArea ? 90 : 8, Range: 100, Enabled: i < enabled, CastShadows: i < shadowed,
                AreaSize: isArea ? new Vector2(8, 10) : null);
        }).ToArray();
    }

    [Fact]
    public async Task ReleaseLatency() => await session.Dispatch(async () =>
    {
        string? models = Environment.GetEnvironmentVariable("MDA_LIGHT_PROBE_MODELS");
        string? output = Environment.GetEnvironmentVariable("MDA_LIGHT_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(models) || string.IsNullOrWhiteSpace(output))
        { Assert.Skip("Set MDA_LIGHT_PROBE_MODELS and MDA_LIGHT_PROBE_OUTPUT to run the latency probe."); return false; }
        string profile = Environment.GetEnvironmentVariable("MDA_LIGHT_PROBE_PROFILE") ?? "daily";
        int edits = int.TryParse(Environment.GetEnvironmentVariable("MDA_LIGHT_PROBE_EDITS"), out var e) ? e : 6;
        bool renderer = Environment.GetEnvironmentVariable("MDA_LIGHT_PROBE_RENDER") != "0";
        bool export = renderer && Environment.GetEnvironmentVariable("MDA_LIGHT_PROBE_EXPORT") == "1";
        Directory.CreateDirectory(output);
        var (authored, enabled, shadowed, area) = profile switch
        {
            "daily" => (16, 16, 4, 2),
            "stress" => (128, 32, 2, 2),
            "stress-shadows" => (128, 32, 32, 2),
            _ => throw new ArgumentException("Unknown MDA_LIGHT_PROBE_PROFILE: " + profile)
        };

        InteractionDiagnostics.Start();
        var stages = new List<object>();
        var shell = new EditorShellViewModel(); shell.Workspace.PackageCacheDirectory = Path.Combine(output, "cache");
        var window = new EditorWindow(false) { ShowInTaskbar = false };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false; viewport.ShowWorldGuides = false; window.DataContext = shell;
        window.Show();
        try
        {
            var load = Stopwatch.StartNew();
            var loaded = new List<string>(); var skipped = new List<object>();
            var paths = models.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < paths.Length; i++)
            {
                var role = i == 0 ? ModelRole.Environment : ModelRole.Character;
                var options = new EditorModelImportOptions(new ModelPurpose(role, role == ModelRole.Environment ? true : null));
                try
                {
                    var asset = EditorProjectWorkspace.LoadModelAsset(paths[i], TestContext.Current.CancellationToken);
                    shell.Workspace.AddModel(paths[i], asset, options);
                    loaded.Add(paths[i]);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    skipped.Add(new { path = paths[i], error = error.GetBaseException().Message });
                    Console.WriteLine($"skipped {Path.GetFileName(paths[i])}: {error.GetBaseException().Message}");
                }
                Pump(window);
            }
            double loadMs = load.Elapsed.TotalMilliseconds;
            Check(loaded.Count > 0 && shell.Workspace.Session is not null, "No model could be loaded.");
            var lights = Lights(authored, enabled, shadowed, area);
            shell.Workspace.EditLighting(new(new(lights, [], new(.08f, .1f)), lights[0].Id));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.Width = 1480; window.Height = 940; window.Measure(new(1480, 940)); window.Arrange(new(0, 0, 1480, 940)); Pump(window);
            var project = shell.Workspace.Session!;
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var controller = viewport.LightController!; controller.HelpersVisible = false; controller.Select(lights[0].Id); Pump(window);

            IEditorViewportRenderer? preview = null; ProbeWindow? nativeWindow = null;
            Func<Task> present = () => Task.CompletedTask;
            string adapter = "none";
            if (renderer)
            {
                var binding = shell.Workspace.CaptureRenderBinding();
                nativeWindow = new ProbeWindow("private", 960, 544);
                var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1);
                preview = binding.CreatePreview(nativeWindow.Handle, new(960, 544, quality));
                adapter = preview.AdapterName + (preview.IsWarp ? " (WARP)" : "");
                var captureFrame = typeof(EditorViewportHost).GetMethod("CaptureFrame", Hidden)!;
                var p = preview;
                present = async () => await p.PresentAsync((PreviewFrame)captureFrame.Invoke(viewport, [960, 544, p.SceneInputRequests, null])!);
                await present(); await present();
            }
            var timings = new List<object>();
            for (int i = 0; i < edits; i++)
            {
                var slider = inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Single(control => control.Name == "LightBrightnessSlider"
                    && control.DataContext is EditorNativeLightItem item && item.Id == lights[0].Id);
                var before = project.Lighting;
                var watch = Stopwatch.StartNew();
                slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
                slider.Value = EditorLightParameterScale.BrightnessPosition((decimal)before.Lights[0].Intensity + 10);
                double inputMs = watch.Elapsed.TotalMilliseconds;
                await present(); double previewMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
                double releaseMs = watch.Elapsed.TotalMilliseconds;
                Dispatcher.UIThread.RunJobs();
                double jobsMs = watch.Elapsed.TotalMilliseconds;
                window.UpdateLayout();
                double layoutMs = watch.Elapsed.TotalMilliseconds;
                Dispatcher.UIThread.RunJobs();
                double commitMs = watch.Elapsed.TotalMilliseconds;
                Check(!ReferenceEquals(before, project.Lighting), "Slider did not commit.");
                await present(); double restoredMs = watch.Elapsed.TotalMilliseconds;
                var list = window.FindControl<ListBox>("WorkspaceLightList")!;
                int realizedRows = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
                int inspectorPanels = inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Count();
                double listHeight = list.Bounds.Height;
                timings.Add(new { i, inputMs, previewMs, releaseMs, jobsMs, layoutMs, commitMs, restoredMs, realizedRows, inspectorPanels, listHeight });
                Console.WriteLine($"edit {i}: input {inputMs:F1} preview {previewMs:F1} release {releaseMs:F1} jobs {jobsMs:F1} layout {layoutMs:F1} commit {commitMs:F1} restored {restoredMs:F1} ms");
            }
            object? parity = null;
            if (export && preview is not null)
            {
                // Same check as the 2026-11-16 PR01 run: the committed lighting must give identical
                // preview readback and editor PNG export for the same source frame.
                var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1);
                var captureFrame = typeof(EditorViewportHost).GetMethod("CaptureFrame", Hidden)!;
                var final = (PreviewFrame)captureFrame.Invoke(viewport, [960, 544, preview.SceneInputRequests, null])!;
                Check(final.Scene.Lighting!.Lights.Count == authored, "Authored light count changed.");
                var nativeRenderer = (D3D12RealtimeViewRenderer)preview.GetType().GetFields(Hidden)
                    .Single(f => f.FieldType == typeof(D3D12RealtimeViewRenderer)).GetValue(preview)!;
                var presenter = (D3D12SwapchainPresenter)typeof(D3D12RealtimeViewRenderer).GetField("presenter", Hidden)!.GetValue(nativeRenderer)!;
                var resolved = D3D12ViewScene.Resolve(final.Scene, final.Camera, final.Light, final.SelfShadow, final.MorphChannels, quality,
                    final.ResolvedCamera, final.CameraParentTransform);
                var parityScene = new OriginalFrameScene(resolved.Surface, resolved.Outline, resolved.OutlineOptions,
                    (resolved.Options ?? new(OriginalAlphaBlendMode.Normal, true, false)) with { BackgroundColor = Vector4.One, AlphaOutput = FrameAlphaOutput.Straight },
                    resolved.Materials, resolved.Lighting);
                var previewPixels = presenter.Capture(parityScene).Rgba;
                var png = Path.Combine(output, "export.png");
                var plan = new OfflineFramePlan(960, 544, new(30), 0, 0);
                var preset = new ViewOutputPresetDefinition("lighting.probe", "Lighting probe", new(960, 544, quality), new(960, 544, new(30), quality));
                var draft = new OutputSettingsDraft(preset, new ExportRequest(plan, quality), OutputMediaKind.PngImage, png, false, Vector4.One, null);
                var committed = project.Lighting; double frame = project.CurrentFrame;
                var exportWatch = Stopwatch.StartNew();
                var result = await EditorExportService.ExportAsync(shell.Workspace, draft, null, null, token: TestContext.Current.CancellationToken);
                double exportMs = exportWatch.Elapsed.TotalMilliseconds;
                Check(result.CompletedFrames == 1 && File.Exists(png), "Editor PNG export failed.");
                Check(ReferenceEquals(committed, project.Lighting) && project.CurrentFrame == frame, "Export changed lighting or clock.");
                using var exported = SkiaSharp.SKBitmap.Decode(png);
                int mismatches = 0, maxError = 0;
                for (int y = 0; y < 544; y++) for (int x = 0; x < 960; x++)
                {
                    var pixel = exported.GetPixel(x, y); int p = (y * 960 + x) * 4; int[] actual = [pixel.Red, pixel.Green, pixel.Blue, pixel.Alpha];
                    for (int c = 0; c < 4; c++) { int error = Math.Abs(previewPixels[p + c] - actual[c]); maxError = Math.Max(maxError, error); if (error > 1) mismatches++; }
                }
                parity = new { exportMs, mismatchedChannels = mismatches, maximumError = maxError,
                    exportSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(png))) };
                Console.WriteLine($"export {exportMs:F0} ms; preview/export mismatched channels {mismatches}, max error {maxError}");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "timing.json"), JsonSerializer.Serialize(new
            {
                profile, models = loaded, skipped, authored, enabled, shadowed, area, adapter, loadMs, edits = timings, parity
            }, new JsonSerializerOptions { WriteIndented = true }));
            await InteractionDiagnostics.ExportAsync(Path.Combine(output, "diagnostics.json"));
            if (preview is not null) await preview.DisposeAsync();
            nativeWindow?.Dispose();
            return true;
        }
        finally
        {
            InteractionDiagnostics.Stop();
            typeof(EditorWindow).GetField("_closingConfirmed", Hidden)!.SetValue(window, true); window.Close(); Dispatcher.UIThread.RunJobs();
            shell.Workspace.Dispose();
        }
    }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(20), TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
