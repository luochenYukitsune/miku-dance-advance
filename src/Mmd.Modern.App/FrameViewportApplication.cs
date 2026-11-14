using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.App;

internal sealed partial class FrameViewportApplication
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly FrameViewportOptions options;
    private readonly FrameViewportViewModel viewModel;
    private readonly List<object> events = [];
    private readonly List<object> captures = [];
    private readonly List<D3D12PresentationStatistics> sessions = [];
    private readonly List<double> milliseconds = [];
    private readonly List<double> recordMilliseconds = [], presentMilliseconds = [];
    private readonly List<PerformanceSample> performance = [];
    private readonly Stopwatch wallWatch = Stopwatch.StartNew();
    private readonly TimeSpan startingCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private double loadWallMilliseconds, loadCpuMilliseconds;
    private readonly List<D3D12PresentationStatistics> sampledFrames = [];
    private readonly Dictionary<LightingMode, D3D12PresentationStatistics> giWarm = [], giStable = [];
    private readonly List<object> giTransitions = [];
    private readonly List<object> giDenoiseSamples = [];
    private readonly List<object> antialiasingSamples = [];
    private D3D12PresentationStatistics? giCameraBefore, giCameraAfter;
    private readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromSeconds(600) };
    private readonly string output;
    private D3D12PresentationStatistics? warmup, stable;
    private RenderedFrame? front, orbit;
    private RenderedFrame? softwareBeforeCamera, softwareAfterCamera, hardwareBeforeResize, hardwareAfterResize, offAfterGi;
    private int windowNumber;
    private bool finished, minimized, restored;
    private string? adapter;
    private bool warp, debugRequested, debugLayer, infoQueue;

    private FrameViewportApplication(IClassicDesktopStyleApplicationLifetime desktop, FrameViewportOptions options)
    {
        this.desktop = desktop; this.options = options; viewModel = new(options);
        output = Path.GetFullPath(options.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory,
            options.RayGiSelfTest ? "artifacts/stage-20261018-ray-gi/ui-integration" : "artifacts/stage-20261018-frame-chain/viewport-author",
            "modern_d3d12"));
        if (options.SelfTest)
        {
            Directory.CreateDirectory(output); desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            watchdog.Tick += (_, _) => Finish(new TimeoutException("Viewport self-test exceeded 600 seconds.")); watchdog.Start();
        }
    }
    internal static void Start(IClassicDesktopStyleApplicationLifetime desktop, FrameViewportOptions options)
        => new FrameViewportApplication(desktop, options).OpenWindow();

    private void OpenWindow()
    {
        windowNumber++;
        var window = new FrameViewportWindow(viewModel) { DisposeViewModelOnClose = !options.SelfTest };
        desktop.MainWindow = window;
        window.Viewport.Failed += error => { if (options.SelfTest) Finish(error); };
        window.Opened += async (_, _) =>
        {
            events.Add(new { action = "opened", windowNumber, ownChildHwnd = window.Viewport.HasChildWindow });
            if (viewModel.HasScene || (!options.EngineDemo&&options.ModelPath is null&&options.ProjectPath is null)) return;
            try
            {
                viewModel.Status = "正在读取 PMX 与纹理…";
                var loadWatch = Stopwatch.StartNew(); var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                if(options.EngineDemo)viewModel.LoadEngineDemo();
                else if(options.ProjectPath is {} projectPath)
                {
                    viewModel.Status="正在读取PMM工程、模型和纹理…";
                    using var candidate=await Task.Run(()=>viewModel.PrepareProject(projectPath));
                    viewModel.CommitProject(candidate);if(options.AutoPlayMotion)viewModel.PlayMotion();
                }
                else await Task.Run(() => viewModel.LoadModel(options.ModelPath!));
                loadWallMilliseconds = loadWatch.Elapsed.TotalMilliseconds;
                loadCpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
            }
            catch (Exception error)
            {
                viewModel.Status = $"模型加载失败：{error.Message}";
                if (options.SelfTest) Finish(error);
            }
        };
        if (options.SelfTest) window.Viewport.FramePresented += (statistics, elapsed) => OnFrame(window, statistics, elapsed);
        window.Closed += (_, _) =>
        {
            events.Add(new { action = "closed", windowNumber });
            if (options.SelfTest && !finished && windowNumber == 1)
                Dispatcher.UIThread.Post(OpenWindow);
        };
        if (windowNumber > 1) window.Show();
    }

    private void OnFrame(FrameViewportWindow window, D3D12PresentationStatistics statistics, double elapsed)
    {
        if (finished) return;
        if(options.EngineDemo){OnDemoSmokeFrame(window,statistics);return;}
        try
        {
            Require(statistics.Frame is { SubmissionCount: 1, ReadbackCount: 0, SceneDrawCalls: > 0 }, "Every present must contain actual frame draws, one submission and zero readbacks.");
            Require(statistics.Frame!.PendingFrames <= 3, "The three-slot runtime must keep bounded frames in flight.");
            Require(window.Viewport.HasChildWindow, "Presentation must target the owned child HWND.");
            Require(statistics.Frame.EffectPassDrawCalls == 0, "Modern D3D12 frames never execute MME passes.");
            Require(statistics.Frame.Antialiasing == viewModel.Antialiasing
                && statistics.Frame.AntialiasingPassDrawCalls == (viewModel.Antialiasing == FrameAntialiasing.Smaa1X ? 3 : 0),
                "Actual AA type and draw calls must match the requested selection: Off skips AA, SMAA1x executes all three passes.");
            if (options.RayGiSelfTest)
            {
                if (viewModel.GiEnabled)
                    Require(statistics.RayGi is { DispatchCount: > 0, GBufferDrawCalls: > 0 }
                        && statistics.RayGi.Effective == viewModel.GiMode
                        && statistics.RayGi.SamplesPerPixel == viewModel.RayGiOptions.SamplesPerPixel
                        && statistics.RayGi.Composition == viewModel.RayGiOptions.Composition,
                        "GI must report the requested actual backend, raster GBuffer draws and SPP.");
                else Require(statistics.RayGi is null or { DispatchCount: 0 }, "Off must not retain a previous frame's GI dispatch.");
            }
            var expectedDenoiser = viewModel.GiEnabled && viewModel.RayGiOptions.Strength > 0
                ? viewModel.RayGiOptions.Denoiser : RayGiDenoiser.Off;
            var actualDenoiser = statistics.RayGi?.Denoiser ?? RayGiDenoiser.Off;
            int denoiseDispatches = statistics.RayGi?.DenoiseDispatchCount ?? 0;
            Require(actualDenoiser == expectedDenoiser
                && (expectedDenoiser == RayGiDenoiser.Spatial ? denoiseDispatches > 0 : denoiseDispatches == 0),
                "Actual GI denoiser and filter dispatches must match the requested filter; GI Off or Strength=0 must skip filtering.");
            milliseconds.Add(elapsed);
            if (statistics.Timing is { } timing)
            {
                recordMilliseconds.Add(timing.RecordAndSubmitMilliseconds);
                presentMilliseconds.Add(timing.PresentWaitMilliseconds);
                string phase = windowNumber > 1 ? "reopened" : statistics.PresentCount <= 40 ? "warmup_1_to_40"
                    : statistics.PresentCount < 80 ? "static_41_to_79" : statistics.PresentCount < 180 ? "camera_80_to_179" : "resized_180_onward";
                if (options.RayGiSelfTest) phase += "_" + viewModel.GiMode;
                var frame = statistics.Frame;
                performance.Add(new(windowNumber, statistics.PresentCount, phase, elapsed, timing.InterPresentMilliseconds, timing.RecordAndSubmitMilliseconds, frame.RecordCpuMilliseconds, frame.LastSlotWaitMilliseconds,
                    frame.GeometryCacheMilliseconds, frame.TextureBindingMilliseconds, frame.SceneRecordMilliseconds, frame.SubmitCpuMilliseconds, timing.PresentWaitMilliseconds));
            }
            if (statistics.PresentCount == 1 || statistics.PresentCount % 40 == 0)
            {
                sampledFrames.Add(statistics);
                giDenoiseSamples.Add(new { windowNumber, statistics.PresentCount,
                    requested = viewModel.RayGiOptions.Denoiser.ToString(), actual = actualDenoiser.ToString(), denoiseDispatches });
                antialiasingSamples.Add(new { windowNumber, statistics.PresentCount,
                    requested = viewModel.Antialiasing.ToString(), actual = statistics.Frame.Antialiasing.ToString(),
                    drawCalls = statistics.Frame.AntialiasingPassDrawCalls });
                File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(new { windowNumber, statistics,
                    lastFrameMs = elapsed, averageMs = milliseconds.Average(), lifecycle = events }, new JsonSerializerOptions { WriteIndented = true }));
            }
            adapter = window.Viewport.AdapterName; warp = window.Viewport.IsWarp;
            debugRequested = window.Viewport.DebugRequested; debugLayer = window.Viewport.DebugLayerEnabled; infoQueue = window.Viewport.DebugInfoQueueAvailable;
            if (windowNumber == 1)
            {
                if (options.RayGiSelfTest) OnGiFrame(window, statistics);
                switch (statistics.PresentCount)
                {
                    case 40: front = Capture(window, "camera-front"); break;
                    case 80:
                        viewModel.Yaw = 45; viewModel.Pitch = 12;
                        events.Add(new { action = "camera-update", yaw = viewModel.Yaw, pitch = viewModel.Pitch }); break;
                    case 120: orbit = Capture(window, "camera-orbit"); break;
                    case 130: warmup = statistics; break;
                    case 136 when options.RayGiSelfTest: stable = statistics; break;
                    case 160 when !options.RayGiSelfTest: stable = statistics; break;
                    case 180:
                        window.Width = 1127; window.Height = 809;
                        window.Viewport.Width = 803; window.Viewport.Height = 601;
                        window.Viewport.HorizontalAlignment = HorizontalAlignment.Left;
                        window.Viewport.VerticalAlignment = VerticalAlignment.Top;
                        events.Add(new { action = "resize-request", logicalWidth = 803, logicalHeight = 601 }); break;
                    case 200 when !options.RayGiSelfTest:
                    case 220 when options.RayGiSelfTest:
                        minimized = true; window.WindowState = WindowState.Minimized;
                        events.Add(new { action = "minimized", presented = statistics.PresentCount });
                        RestoreLater(window, statistics.PresentCount); break;
                    case 240: Capture(window, "resized-restored"); break;
                }
                if (statistics.PresentCount >= options.SelfTestFrames)
                {
                    window.Viewport.WaitForIdle(); sessions.Add(window.Viewport.Statistics!);
                    Require(restored, "The window must restore after minimize.");
                    window.Close();
                }
            }
            else if (statistics.PresentCount == 32)
            {
                Capture(window, "reopened"); window.Viewport.WaitForIdle(); sessions.Add(window.Viewport.Statistics!);
                Finish(null); window.Close();
            }
        }
        catch (Exception error) { Finish(error); window.Close(); }
    }
    private void OnGiFrame(FrameViewportWindow window, D3D12PresentationStatistics statistics)
    {
        switch (statistics.PresentCount)
        {
            case 140: SelectGi(LightingMode.SoftwareRayTracing); break;
            case 148: giWarm[viewModel.GiMode] = statistics; break;
            case 158: giStable[viewModel.GiMode] = statistics; break;
            case 160: giCameraBefore = statistics; softwareBeforeCamera = Capture(window, "gi-software-before-camera"); viewModel.Yaw = 70; viewModel.Pitch = 20;
                events.Add(new { action = "gi-camera-update", yaw = viewModel.Yaw, pitch = viewModel.Pitch }); break;
            case 168: giCameraAfter = statistics; softwareAfterCamera = Capture(window, "gi-software-camera"); break;
            case 170: SelectGi(LightingMode.HardwareRayTracing); break;
            // The normal lifecycle resizes at 180; compare warm cache before it.
            case 174: giWarm[viewModel.GiMode] = statistics; break;
            case 178: giStable[viewModel.GiMode] = statistics; hardwareBeforeResize = Capture(window, "gi-hardware-before-resize"); break;
            case 198: hardwareAfterResize = Capture(window, "gi-hardware-resized"); break;
            case 280: SelectGi(LightingMode.Legacy); break;
            case 290: offAfterGi = Capture(window, "gi-off-restored"); break;
        }
        void SelectGi(LightingMode mode)
        {
            Require(viewModel.TrySelectGiMode(mode), $"GI lifecycle requires {mode}: {viewModel.Status}");
            giTransitions.Add(new { requested = mode, afterPresent = statistics.PresentCount,
                samplesPerPixel = viewModel.RayGiOptions.SamplesPerPixel, strength = viewModel.RayGiOptions.Strength,
                denoiser = viewModel.RayGiOptions.Denoiser.ToString(),
                capabilities = window.Viewport.RayGiCapabilities });
        }
    }
    private async void RestoreLater(FrameViewportWindow window, long presentsAtMinimize)
    {
        await Task.Delay(250);
        if (finished) return;
        try
        {
            Require(window.Viewport.Statistics?.PresentCount == presentsAtMinimize, "No frames should present while minimized.");
            window.WindowState = WindowState.Normal; restored = true;
            events.Add(new { action = "restored", presented = window.Viewport.Statistics?.PresentCount });
        }
        catch (Exception error) { Finish(error); window.Close(); }
    }
    private RenderedFrame Capture(FrameViewportWindow window, string name)
    {
        var frame = window.Viewport.Capture();
        var framing = viewModel.GetFramingDiagnostics(frame.Width, frame.Height);
        FrameViewportCapture.SavePng(frame, Path.Combine(output, name + ".png"));
        File.WriteAllBytes(Path.Combine(output, name + ".rgba"), frame.Rgba);
        File.WriteAllBytes(Path.Combine(output, name + ".bmp"), FrameImage.ToBmp(frame));
        int nonBackground = 0;
        for (int i = 0; i < frame.Rgba.Length; i += 4)
            if (frame.Rgba[i] != 20 || frame.Rgba[i + 1] != 24 || frame.Rgba[i + 2] != 32) nonBackground++;
        captures.Add(new { name, frame.Width, frame.Height, frame.DrawCalls, framing, nonBackgroundPixels = nonBackground,
            rawSha256 = Hash(frame.Rgba), statistics = window.Viewport.Statistics });
        Require(nonBackground > 10000, "The explicit capture must contain visible model geometry.");
        Require(framing.ClippedReferencedVertices == 0 && framing.Zoom == 1, "The automatic camera must frame the whole referenced model at zoom 1.");
        return frame;
    }
    private void Finish(Exception? error)
    {
        if (finished) return;
        finished = true; watchdog.Stop();
        int changed = 0;
        try
        {
            if (error is null)
            {
                Require(front is not null && orbit is not null, "Both camera captures must exist.");
                Require(front!.Width == orbit!.Width && front.Height == orbit.Height, "Camera comparison requires equal dimensions.");
                for (int i = 0; i < front.Rgba.Length; i += 4)
                    if (!front.Rgba.AsSpan(i, 4).SequenceEqual(orbit.Rgba.AsSpan(i, 4))) changed++;
                Require(changed > 10000, "Camera changes must alter the actual frame pixels.");
                Require(warmup?.Frame is not null && stable?.Frame is not null, "Warmup and stable statistics must exist.");
                Require(warmup!.Frame!.BufferUploads == stable!.Frame!.BufferUploads && warmup.Frame.TextureUploads == stable.Frame.TextureUploads
                    && warmup.Frame.ShaderCompilations == stable.Frame.ShaderCompilations && warmup.Frame.PipelineCreations == stable.Frame.PipelineCreations,
                    "Static frames after warmup must stop buffer/texture uploads, shader compilation and PSO creation.");
                Require(warmup.Frame.CachedResources == stable.Frame.CachedResources && warmup.Frame.LiveFrameResources == stable.Frame.LiveFrameResources
                    && warmup.Frame.DescriptorHighWater == stable.Frame.DescriptorHighWater, "Owned resource and descriptor counts must stabilize after warmup.");
                if (options.RayGiSelfTest)
                {
                    Require(giTransitions.Count == 3 && giStable.Count == 2, "Off -> Software -> Hardware -> Off must complete with stable snapshots.");
                    foreach (var mode in new[] { LightingMode.SoftwareRayTracing, LightingMode.HardwareRayTracing })
                    {
                        var before = giWarm[mode]; var after = giStable[mode];
                        Require(before.RayGi is not null && after.RayGi is not null, "Real GI statistics must exist.");
                        Require(before.RayGi!.BufferUploads == after.RayGi!.BufferUploads
                            && before.RayGi.AccelerationBuilds == after.RayGi.AccelerationBuilds
                            && before.RayGi.CachedResources == after.RayGi.CachedResources,
                            "Static GI warm cache must stop uploads/AS builds and retain bounded resources.");
                        Require(before.Frame!.CachedResources == after.Frame!.CachedResources
                            && before.Frame.LiveFrameResources == after.Frame.LiveFrameResources
                            && before.Frame.DescriptorHighWater == after.Frame.DescriptorHighWater,
                            "All frame-slot resources must stabilize during each GI backend.");
                    }
                    Require(giCameraBefore?.RayGi is not null && giCameraAfter?.RayGi is not null
                        && giCameraBefore.RayGi.AccelerationBuilds == giCameraAfter.RayGi.AccelerationBuilds,
                        "A camera-only change must not rebuild immutable GI acceleration geometry.");
                    Require(PixelDifference(softwareBeforeCamera, softwareAfterCamera) is { changedPixels: > 10000 },
                        "Moving the camera with software GI enabled must update actual output pixels.");
                    Require(PixelDifference(orbit, softwareBeforeCamera) is { changedPixels: > 0 },
                        "Software GI must change the same-camera Off frame.");
                    Require(PixelDifference(hardwareAfterResize, offAfterGi) is { changedPixels: > 0 },
                        "Returning to Off must remove the actual hardware GI contribution.");
                }
                Require(sessions.Count == 2 && sessions[0].PresentCount >= 300 && sessions[1].PresentCount == 32,
                    "A complete session and a reopened window must both present.");
                Require(sessions[0].ResizeCount > 0 && minimized && restored, "Resize and minimize/restore must execute.");
                Require((sessions[0].Width & 1) == 1 && (sessions[0].Height & 1) == 1, "The resize acceptance uses odd pixel dimensions.");
                Require(sessions.All(s => s.Frame!.SubmittedFence == s.Frame.CompletedFence && s.Frame.PendingFrames == 0), "Idle fences must complete before closing each window.");
                int expectedCaptures = options.RayGiSelfTest ? 9 : 4;
                Require(sessions.Sum(s => s.ExplicitCaptureCount) == expectedCaptures && sessions.Sum(s => s.Frame!.TotalReadbacks) == expectedCaptures,
                    "Every readback must correspond to an explicitly requested screenshot.");
                Require(sessions.Sum(s => s.Frame!.TotalSubmissions) == sessions.Sum(s => s.PresentCount + s.ExplicitCaptureCount),
                    "Every present and explicit capture must have exactly one submission.");
                foreach (var source in viewModel.Sources) Require(new FileInfo(source.Path).Length == source.Bytes && Hash(File.ReadAllBytes(source.Path)) == source.Sha256,
                    "User assets must remain unchanged.");
            }
        }
        catch (Exception verification) { error = verification; }
        double totalWall = wallWatch.Elapsed.TotalMilliseconds;
        double totalCpu = (Process.GetCurrentProcess().TotalProcessorTime - startingCpuTime).TotalMilliseconds;
        var report = new
        {
            success = error is null, error = error?.ToString(), mode = "modern_d3d12",
            presentPath = "Complete OriginalFrame recorder -> same D3D12 queue -> GPU CopyResource -> own child HWND swapchain Present",
            cpuBitmapPresentation = false, perFrameReadbacks = 0, explicitCaptureCount = captures.Count,
            adapter, warp, debugValidation = new { requested = debugRequested, enabled = debugLayer, infoQueueAvailable = infoQueue,
                status = infoQueue ? "available" : "unavailable; empty messages do not establish debug cleanliness" },
            totalPresentCount = sessions.Sum(s => s.PresentCount), sessions, warmup, stable, sampledFrames, lifecycle = events,
            captures, cameraChangedPixels = changed, sourceAssets = viewModel.Sources, diagnostics = viewModel.Diagnostics,
            timings = new { totalFrame = Distribution(milliseconds), recordAndSubmit = Distribution(recordMilliseconds),
                presentApiWait = Distribution(presentMilliseconds), modelLoad = new { wallMs = loadWallMilliseconds, processCpuMs = loadCpuMilliseconds },
                process = new { wallMs = totalWall, cpuMs = totalCpu, logicalProcessors = Environment.ProcessorCount,
                    oneCoreEquivalentPercent = totalCpu / totalWall * 100,
                    taskManagerTotalEquivalentPercent = totalCpu / totalWall / Environment.ProcessorCount * 100 },
                interpretation = "RecordAndSubmit includes command recording, driver submission and any frame-slot fence wait; PresentApiWait is timed separately. Process CPU is measured, not inferred from wall time." },
            performanceByPhase = performance.GroupBy(p => p.Phase).ToDictionary(g => g.Key, g => new
            {
                totalFrame = Distribution(g.Select(p => p.TotalMs).ToList()), recordAndSubmit = Distribution(g.Select(p => p.RecordAndSubmitMs).ToList()),
                presentCadence = Distribution(g.Where(p => p.InterPresentMs > 0).Select(p => p.InterPresentMs).ToList()),
                actualPresentFps = g.Where(p => p.InterPresentMs > 0).Sum(p => p.InterPresentMs) is > 0 and var span
                    ? g.Count(p => p.InterPresentMs > 0) * 1000 / span : 0,
                cpuRecording = Distribution(g.Select(p => p.CpuRecordingMs).ToList()), frameSlotWait = Distribution(g.Select(p => p.SlotWaitMs).ToList()),
                geometryCache = Distribution(g.Select(p => p.GeometryCacheMs).ToList()), textureBinding = Distribution(g.Select(p => p.TextureBindingMs).ToList()),
                sceneRecord = Distribution(g.Select(p => p.SceneRecordMs).ToList()), submitCpu = Distribution(g.Select(p => p.SubmitCpuMs).ToList()),
                presentApiWait = Distribution(g.Select(p => p.PresentWaitMs).ToList())
            }),
            performanceSamples = performance,
            giEnabled = viewModel.GiEnabled, rayTracingEnabled = viewModel.RayTracingEnabled,
            rayGiLifecycleTest = options.RayGiSelfTest, giTransitions, giWarm, giStable,
            giDenoiseSamples,
            antialiasingSamples,
            giCameraBefore, giCameraAfter,
            giImageComparisons = new { offToSoftware = PixelDifference(orbit, softwareBeforeCamera),
                softwareCamera = PixelDifference(softwareBeforeCamera, softwareAfterCamera),
                softwareToHardware = PixelDifference(softwareAfterCamera, hardwareBeforeResize),
                hardwareToOff = PixelDifference(hardwareAfterResize, offAfterGi) },
            rayGiTiming = "Per-frame CPU recording and actual Present cadence measured; GPU timestamp timing unavailable and not inferred.",
            selectedPolicies = new { antialiasing = viewModel.Antialiasing.ToString(), giComposition = viewModel.RayGiOptions.Composition.ToString(), giDenoiser = viewModel.RayGiOptions.Denoiser.ToString(), shadowMap = $"1024x1024 R32F, {viewModel.ShadowProjectionPolicy},linear clamp,LessEqual",
                shadowFit = viewModel.ShadowFitEnabled, shadowProjectionFitCount = viewModel.ShadowProjectionFitCount,
                roomEmissionStrength = viewModel.RoomEmissionStrength,
                ground = "selected propagation(.35,-1,.2),RGBA(.2,.2,.2,.5),alpha blend,stencil", camera = "selected RH orthographic orbit camera; original camera UI parity unverified",
                edge = "selected FovDegrees30,ModelWidth1; synced camera distance", alpha = "source material order,write depth; diagnostic trans p opt-in" },
            limitations = new[] { "Static PMX rest pose; no animation or physics", "No original executable full-frame pixel equivalence claim",
                "Modern D3D12 never loads MME; legacy compatibility rendering is a separate module", "GPU target PNGs are explicit captures; they do not capture other desktop windows" }
        };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        viewModel.Dispose();
        Dispatcher.UIThread.Post(() => desktop.Shutdown(error is null ? 0 : 1));
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record PixelComparison(int changedPixels, double meanAbsoluteRgbDifference, int maximumRgbDifference);
    private static PixelComparison? PixelDifference(RenderedFrame? left, RenderedFrame? right)
    {
        if (left is null || right is null || left.Width != right.Width || left.Height != right.Height) return null;
        int changed = 0, maximum = 0; long total = 0;
        for (int i = 0; i < left.Rgba.Length; i += 4)
        {
            bool different = false;
            for (int c = 0; c < 3; c++)
            {
                int delta = Math.Abs(left.Rgba[i + c] - right.Rgba[i + c]);
                total += delta; maximum = Math.Max(maximum, delta); different |= delta != 0;
            }
            if (different) changed++;
        }
        return new(changed, total / (left.Width * (double)left.Height * 3), maximum);
    }
    private sealed record PerformanceSample(int WindowNumber, long PresentNumber, string Phase, double TotalMs, double InterPresentMs, double RecordAndSubmitMs, double CpuRecordingMs, double SlotWaitMs,
        double GeometryCacheMs, double TextureBindingMs, double SceneRecordMs, double SubmitCpuMs, double PresentWaitMs);
    private static object Distribution(List<double> values)
    {
        var ordered = values.Order().ToArray();
        return new { count = ordered.Length, averageMs = ordered.Length == 0 ? 0 : ordered.Average(),
            p50Ms = ordered.Length == 0 ? 0 : ordered[ordered.Length / 2], p95Ms = ordered.Length == 0 ? 0 : ordered[(int)((ordered.Length - 1) * .95)],
            minMs = ordered.Length == 0 ? 0 : ordered[0], maxMs = ordered.Length == 0 ? 0 : ordered[^1] };
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
