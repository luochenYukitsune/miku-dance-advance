using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>
/// Runs the published App's ordinary desktop lifetime, including its real D3D12 first present.
/// The probe creates no substitute window or workspace, introduces no startup delay, and never
/// changes or saves preferences. Only the windows created by this harness's App are closed.
/// </summary>
internal static class EditorStartupAnimationProbe
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Stopwatch Clock = new();
    private static readonly List<string> Checks = [];
    private static readonly List<string> Skipped = [];
    private static readonly List<PhaseSample> Phases = [];
    private static readonly List<AnimationSample> AnimationSamples = [];
    private static readonly List<object> Presentations = [];
    private static readonly List<string> Captures = [];
    private static readonly Dictionary<string, FileState> PreferencesBefore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FileState> PreferencesAfter = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ProductBefore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<object> LoadedAssemblies = [];
    private static readonly EventHandler ChangedHandler = (_, _) => Guard(ObservePhase);
    private static string output = "", mode = "", expectedApp = "", expectedAppSha = "", productRoot = "";
    private static Type? startupType;
    private static object? startupApplication;
    private static IClassicDesktopStyleApplicationLifetime? desktop;
    private static EditorStartupWindow? startup;
    private static EditorWindow? editor;
    private static EditorViewportHost? viewport;
    private static LoadingDots? dots;
    private static StyledProperty<double>? progressProperty;
    private static DispatcherTimer? monitor;
    private static Exception? failure;
    private static string? viewportFailure;
    private static bool observing, startupClosed, editorClosed, cancellationIssued, completed, exiting;
    private static bool readyPresentationRequested, captureQueued;
    private static int presented, readyBaseline, desktopExitCode;
    private static double? firstPresentationMilliseconds, readyMilliseconds, lastCapturedProgress;
    private static List<NativeWindow> beforeCleanupWindows = [], afterLifetimeWindows = [];
    private static ViewportCleanupState? viewportCleanup;
    private static nint presentedChildWindow;
    private static int lifetimeExitCode;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Usage: startup-probe <output-directory> --mode=ready|cancel --expected-app=<absolute published App DLL> --expected-app-sha256=<SHA256>");
        output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        try
        {
            var options = args.Skip(1).Select(ParseOption)
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
            mode = Required(options, "mode");
            if (mode is not ("ready" or "cancel")) throw new ArgumentException("--mode must be ready or cancel.");
            expectedApp = ExistingAbsoluteFile(Required(options, "expected-app"));
            expectedAppSha = Required(options, "expected-app-sha256").ToLowerInvariant();
            if (expectedAppSha.Length != 64 || !expectedAppSha.All(Uri.IsHexDigit))
                throw new ArgumentException("Invalid --expected-app-sha256.");
            productRoot = Path.GetDirectoryName(expectedApp)!;
            Assert(Hash(expectedApp) == expectedAppSha, "supplied published App has the required SHA256 before startup");
            Assert(Path.GetFullPath(typeof(App).Assembly.Location).Equals(expectedApp, StringComparison.OrdinalIgnoreCase),
                "harness references the supplied published App DLL directly");
            foreach (string path in Directory.EnumerateFiles(productRoot, "Mmd.Modern.*.dll", SearchOption.TopDirectoryOnly))
                ProductBefore.Add(Path.GetFullPath(path), Hash(path));
            foreach (string path in new[] { EditorPreferences.DefaultPath, EditorPreferences.DefaultOutputPath,
                Path.Combine(EditorPreferences.DirectoryPath, "editor-preview.json") })
                PreferencesBefore.Add(path, ReadState(path));
            startupType = typeof(App).Assembly.GetType("Mmd.Modern.App.EditorStartupApplication", throwOnError: true)!;
            Assert(ReadCurrent() is null, "ordinary App startup has not been replaced or pre-created by the probe");
            Clock.Start();
            lifetimeExitCode = AppBuilder.Configure<App>().UsePlatformDetect().UseSkia()
                .AfterSetup(_ => InstallObserver()).StartWithClassicDesktopLifetime(args);
            monitor?.Stop();
            observing = false;
            // Main returns on the same UI thread after the real lifetime has exited.
            afterLifetimeWindows = OwnNativeWindows();
            viewportCleanup = ReadViewportCleanup();
            if (failure is null)
            {
                if (mode == "cancel") VerifyCancelled();
                Assert(completed, "requested startup scenario completed before desktop lifetime exit");
                Assert(exiting && desktopExitCode == 0 && lifetimeExitCode == 0, "real desktop lifetime exits successfully");
                Assert(ReadCurrent() is null, "desktop exit releases EditorStartupApplication.Current");
                Assert(startupClosed && startup is { IsVisible: false }, "startup window remains closed after lifetime exit");
                Assert(editor is null || editorClosed && !editor.IsVisible, "the harness's prepared editor is closed after lifetime exit");
                VerifyViewportCleanup();
                Assert(afterLifetimeWindows.All(value => !value.Visible), "no visible native top-level window remains in the harness process");
                Assert(desktop is not null && !desktop.Windows.Any(value => value is EditorWindow or EditorStartupWindow),
                    "desktop retains no startup or editor window after shutdown");
            }
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            monitor?.Stop(); observing = false;
            if (startupApplication is not null)
                try { startupType?.GetEvent("Changed", InstanceMembers)?.GetRemoveMethod(true)?.Invoke(startupApplication, [ChangedHandler]); } catch { }
            // Never repair or restore preference files: even an unexpected mutation must stay visible in evidence.
            try { VerifyReadOnlyFiles(); } catch (Exception error) { failure ??= error; }
            if (failure is not null) CleanupOwnWindows();
            WriteReport();
        }
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine($"PASS {Checks.Count} real-startup {mode} checks; {Skipped.Count} explicitly skipped. Evidence: {output}");
        return 0;
    }

    private static void InstallObserver()
    {
        // Reading this static field initializes LoadingDots and its SolidColorBrush. It must
        // happen after AppBuilder setup has installed the platform's real UI dispatcher.
        progressProperty = (StyledProperty<double>)(typeof(LoadingDots).GetField("AnimationProgressProperty", StaticMembers)?.GetValue(null)
            ?? throw new MissingFieldException(typeof(LoadingDots).FullName, "AnimationProgressProperty"));
        monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        monitor.Tick += (_, _) => Guard(ObserveCurrent);
        monitor.Start();
        // Discover Current before the first timed poll where possible. This does not start,
        // hold, or advance the product's startup state machine.
        Dispatcher.UIThread.Post(() => Guard(ObserveCurrent), DispatcherPriority.Send);
    }

    private static void ObserveCurrent()
    {
        if (exiting || failure is not null) return;
        if (Clock.Elapsed.TotalSeconds > 120)
            throw new TimeoutException("The real App startup/lifetime did not complete within 120 seconds.");
        if (completed) return;
        if (startupApplication is null)
        {
            startupApplication = ReadCurrent();
            if (startupApplication is null) return;
            desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("The real App does not own a classic desktop lifetime.");
            desktop.Exit += (_, args) =>
            {
                exiting = true; desktopExitCode = args.ApplicationExitCode;
                monitor?.Stop(); observing = false;
            };
            startup = Member<EditorStartupWindow>("StartupWindow")
                ?? throw new InvalidOperationException("The real App has no startup window.");
            startup.Closed += (_, _) => startupClosed = true;
            dots = startup.FindControl<LoadingDots>("StartupDots")
                ?? throw new InvalidOperationException("Missing real StartupDots control.");
            var changed = startupType!.GetEvent("Changed", InstanceMembers)
                ?? throw new MissingMemberException(startupType.FullName, "Changed");
            (changed.GetAddMethod(true) ?? throw new MissingMethodException(startupType.FullName, "add_Changed"))
                .Invoke(startupApplication, [ChangedHandler]);
            Assert(ReferenceEquals(desktop.MainWindow, startup), "ordinary App initially assigns the real startup window as MainWindow");
            observing = true;
            startup.RequestAnimationFrame(SampleAnimationFrame);
            ObservePhase();
        }
        ObserveObjects();
        if (mode == "ready" && Phase() == "Ready") AdvanceReadyVerification();
    }

    private static void ObservePhase()
    {
        string phase = Phase();
        if (Phases.Count == 0 || Phases[^1].Phase != phase)
        {
            var sample = new PhaseSample(Clock.Elapsed.TotalMilliseconds, phase);
            Phases.Add(sample);
            File.AppendAllText(Path.Combine(output, "startup-phases.jsonl"), JsonSerializer.Serialize(sample) + Environment.NewLine);
            Console.WriteLine($"PHASE {phase} {sample.ElapsedMilliseconds:F1}ms");
        }
        ObserveObjects();
        if (phase == "Failed")
            throw new InvalidOperationException("The real App startup entered Failed.", Member<Exception>("LastError"));
        if (phase == "Ready")
        {
            readyMilliseconds ??= Clock.Elapsed.TotalMilliseconds;
            if (mode == "cancel") throw new InvalidOperationException("Cancellation probe unexpectedly reached Ready.");
            Dispatcher.UIThread.Post(() => Guard(AdvanceReadyVerification), DispatcherPriority.Background);
        }
        if (mode == "cancel" && phase == "CreatingWorkspace" && !cancellationIssued)
        {
            cancellationIssued = true;
            Assert(startup is { IsVisible: true }, "cancel is issued to the harness's visible startup window at CreatingWorkspace");
            Assert(Member<EditorShellViewModel>("workspace") is null, "cancel boundary precedes real workspace construction");
            if (Captures.Count == 0) CaptureStartup("startup-before-cancel.png");
            // Synchronous Changed observation prevents this boundary from being missed by a 16ms poll.
            startup!.Close();
        }
    }

    private static void ObserveObjects()
    {
        if (editor is null && Member<EditorWindow>("EditorWindow") is { } prepared)
        {
            editor = prepared;
            editor.Closed += (_, _) => editorClosed = true;
        }
        if (viewport is null && editor is not null)
        {
            viewport = editor.FindControl<EditorViewportHost>("SceneViewport")
                ?? throw new InvalidOperationException("The prepared editor lacks its real SceneViewport.");
            viewport.Error += error => viewportFailure = error.ToString();
            viewport.FramePresented += _ =>
            {
                presented++;
                firstPresentationMilliseconds ??= Clock.Elapsed.TotalMilliseconds;
                if (Presentations.Count < 32) Presentations.Add(new { elapsedMilliseconds = Clock.Elapsed.TotalMilliseconds,
                    phase = Phase(), number = presented, viewport.HasChildWindow, viewport.IsWarp, viewport.AdapterName });
            };
        }
    }

    private static void SampleAnimationFrame(TimeSpan renderTime)
    {
        if (!observing || startup is null || dots is null || !startup.IsVisible || exiting) return;
        Guard(() =>
        {
            double progress = dots.GetValue(progressProperty!);
            if (AnimationSamples.Count < 10000)
                AnimationSamples.Add(new(Clock.Elapsed.TotalMilliseconds, renderTime.TotalMilliseconds, Phase(),
                    progress, dots.IsActive, dots.AnimationsEnabled, dots.IsEffectivelyVisible,
                    dots.IsAnimating(progressProperty!), dots.Classes.Contains(":animating")));
            if (!captureQueued && (Captures.Count == 0 || Captures.Count < 3 && dots.IsActive && dots.AnimationsEnabled
                && Math.Abs(progress - (lastCapturedProgress ?? progress)) >= .035))
            {
                captureQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    captureQueued = false;
                    // RequestAnimationFrame executes inside composition. RenderTargetBitmap
                    // and UpdateLayout are deferred, and must still observe actual startup.
                    if (!observing || exiting || failure is not null || startup?.IsVisible != true
                        || Phase() is "Ready" or "Failed" or "Cancelled") return;
                    Guard(() =>
                    {
                        CaptureStartup($"startup-native-animation-{Captures.Count + 1}.png");
                        lastCapturedProgress = dots.GetValue(progressProperty!);
                    });
                }, DispatcherPriority.Background);
            }
        });
        Dispatcher.UIThread.Post(() =>
        {
            if (observing && startup?.IsVisible == true && failure is null) startup.RequestAnimationFrame(SampleAnimationFrame);
        }, DispatcherPriority.Background);
    }

    private static void AdvanceReadyVerification()
    {
        if (completed || failure is not null || exiting || Phase() != "Ready") return;
        ObserveObjects();
        if (!readyPresentationRequested)
        {
            Assert(viewport is not null && editor is not null && startup is not null && dots is not null,
                "Ready belongs to the real prepared editor and startup controls");
            Assert(presented > 0 && firstPresentationMilliseconds is { } first && readyMilliseconds is { } ready && first <= ready,
                "a real SceneViewport.FramePresented event precedes Ready");
            readyBaseline = presented; readyPresentationRequested = true;
            viewport!.RequestRender();
            return;
        }
        if (presented <= readyBaseline) return;
        Assert(viewportFailure is null, "real viewport reports no error during startup and visible first presentation");
        Assert(Member<Exception>("LastError") is null, "successful real startup retains no initialization or cleanup error");
        Assert(viewport!.HasChildWindow && !viewport.IsWarp && viewport.Statistics is not null,
            "Ready is followed by a real hardware D3D12 viewport presentation");
        presentedChildWindow = (nint)(typeof(EditorViewportHost).GetField("childWindow", InstanceMembers)?.GetValue(viewport)
            ?? throw new MissingFieldException(typeof(EditorViewportHost).FullName, "childWindow"));
        Assert(presentedChildWindow != nint.Zero && IsWindow(presentedChildWindow)
            && GetClientRect(presentedChildWindow, out var client) && client.Right > client.Left && client.Bottom > client.Top,
            "presented hardware viewport owns a live measurable native child HWND");
        Assert(editor!.IsVisible && editor.ShowInTaskbar && ReferenceEquals(desktop!.MainWindow, editor),
            "desktop hands MainWindow and taskbar ownership to the visible real editor");
        Assert(OnAnyScreen(editor), "prepared editor is moved from its offscreen construction position onto a real screen");
        Assert(startupClosed && !startup!.IsVisible && !dots!.IsActive && ClockStopped(dots),
            "Ready closes the startup window and releases its loading animation clock");
        Assert(typeof(EditorStartupWindow).GetField("logoEntrance", InstanceMembers)?.GetValue(startup) is null,
            "Ready releases the startup logo entrance cancellation owner");
        var lifetime = desktop ?? throw new InvalidOperationException("The real desktop lifetime was lost during handoff.");
        Assert(lifetime.Windows.Count(value => value is EditorWindow or EditorStartupWindow) == 1
            && lifetime.Windows.Contains(editor), "no second product startup/editor window remains after handoff");
        VerifyPhaseOrder(["WaitingForStartupFrame", "LoadingPreferences", "CreatingEditor", "CreatingWorkspace",
            "AttachingWorkspace", "InitializingViewport", "Ready"]);
        VerifyAnimation();
        beforeCleanupWindows = OwnNativeWindows();
        Assert(beforeCleanupWindows.Where(value => value.Visible).All(OnAnyScreen),
            "no visible offscreen native top-level window is stranded by handoff");
        completed = true; observing = false;
        editor.Close();
        if (editor.IsVisible) throw new InvalidOperationException("The harness's clean editor refused to close.");
    }

    private static void VerifyCancelled()
    {
        Assert(cancellationIssued && Phase() == "Cancelled", "closing startup at CreatingWorkspace reaches Cancelled");
        Assert(Member<Exception>("LastError") is null, "startup cancellation reports no cleanup failure");
        Assert(Member<EditorWindow>("EditorWindow") is null && Member<EditorShellViewModel>("workspace") is null,
            "cancel releases both prepared editor and workspace ownership");
        Assert(editor is not null && editorClosed && !editor.IsVisible,
            "cancel closes the actual editor prepared before CreatingWorkspace");
        Assert(startupClosed && startup is { IsVisible: false } && dots is { IsActive: false } && ClockStopped(dots),
            "cancel closes startup and releases the real loading clock");
        Assert(typeof(EditorStartupWindow).GetField("logoEntrance", InstanceMembers)?.GetValue(startup) is null,
            "cancel releases the startup logo entrance cancellation owner");
        Assert(presented == 0 && (viewport is null || !viewport.HasChildWindow),
            "cancel before workspace creation does not initialize or strand a native viewport child");
        Assert(!Phases.Any(value => value.Phase is "Ready" or "Failed"), "cancel neither exposes Ready nor reports failure");
        VerifyPhaseOrder(["WaitingForStartupFrame", "LoadingPreferences", "CreatingEditor", "CreatingWorkspace", "Cancelled"]);
        VerifyAnimation();
        completed = true;
    }

    private static void VerifyAnimation()
    {
        var active = AnimationSamples.Where(value => value.IsActive && value.Visible).ToArray();
        Assert(active.Length > 0 && Captures.Count > 0, "startup animation is sampled and captured while the actual startup window is visible");
        Assert(active.All(value => double.IsFinite(value.Progress) && value.Progress is >= 0 and <= 1),
            "native animation samples remain within the real AnimationProgress range");
        if (active.Any(value => value.AnimationsEnabled))
        {
            var animated = active.Where(value => value.AnimationsEnabled).ToArray();
            Assert(animated.All(value => value.AnimationSelected) && animated.Any(value => value.IsAnimating),
                "real enabled startup dots select and run their native animation clock while visible");
            Assert(animated.Select(value => Math.Round(value.Progress, 6)).Distinct().Count() > 1,
                "AnimationProgress advances on distinct native RequestAnimationFrame callbacks during actual startup");
        }
        else
        {
            Assert(active.All(value => !value.IsAnimating && !value.AnimationSelected && value.Progress == 0),
                "system reduced-motion startup keeps steady dots without an animation clock");
            Skipped.Add("Windows client-area animation is disabled: no animated progress assertion; the probe preserves that system setting.");
        }
    }

    private static void VerifyPhaseOrder(string[] expected)
    {
        Assert(Phases.Select(value => value.Phase).SequenceEqual(expected),
            "ordinary App follows the complete expected startup phase order: " + string.Join(" -> ", expected));
    }

    private static void CaptureStartup(string filename)
    {
        if (startup is null || !startup.IsVisible) return;
        startup.UpdateLayout();
        double scale = startup.RenderScaling;
        var pixels = new PixelSize((int)Math.Ceiling(startup.Bounds.Width * scale), (int)Math.Ceiling(startup.Bounds.Height * scale));
        if (pixels.Width <= 0 || pixels.Height <= 0) throw new InvalidOperationException("Startup has no measurable render area.");
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(startup); bitmap.Save(Path.Combine(output, filename)); Captures.Add(filename);
    }

    private static void VerifyReadOnlyFiles()
    {
        foreach (string path in PreferencesBefore.Keys) PreferencesAfter[path] = ReadState(path);
        foreach (var before in PreferencesBefore)
        {
            Assert(PreferencesAfter[before.Key] == before.Value,
                "preference existence, size and SHA256 preserved: " + Path.GetFileName(before.Key));
        }
        foreach (var before in ProductBefore)
            Assert(File.Exists(before.Key) && Hash(before.Key) == before.Value,
                "published product DLL remains byte-identical: " + Path.GetFileName(before.Key));
        if (expectedApp.Length == 0) return;
        Assert(Path.GetFullPath(typeof(App).Assembly.Location).Equals(expectedApp, StringComparison.OrdinalIgnoreCase)
            && Hash(expectedApp) == expectedAppSha, "formal App assembly path and required SHA256 remain exact after startup");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(value =>
            value.GetName().Name?.StartsWith("Mmd.Modern.", StringComparison.Ordinal) == true))
        {
            string path = Path.GetFullPath(assembly.Location), hash = Hash(path);
            LoadedAssemblies.Add(new { name = assembly.GetName().Name, path, sha256 = hash });
            Assert(Path.GetDirectoryName(path)!.Equals(productRoot, StringComparison.OrdinalIgnoreCase)
                && ProductBefore.TryGetValue(path, out var before) && hash == before,
                "loaded product assembly comes unchanged from the supplied published directory: " + assembly.GetName().Name);
        }
    }

    private static ViewportCleanupState? ReadViewportCleanup()
    {
        if (viewport is null) return null;
        object? Field(string name) => (typeof(EditorViewportHost).GetField(name, InstanceMembers)
            ?? throw new MissingFieldException(typeof(EditorViewportHost).FullName, name)).GetValue(viewport);
        var gate = Field("workerGate") ?? throw new InvalidOperationException("Viewport worker gate is missing.");
        lock (gate)
        {
            var worker = (Task)(Field("workerTask") ?? throw new InvalidOperationException("Viewport worker task is missing."));
            nint storedChild = (nint)Field("childWindow")!;
            return new(storedChild.ToInt64().ToString("X"), storedChild != nint.Zero && IsWindow(storedChild),
                presentedChildWindow != nint.Zero && IsWindow(presentedChildWindow), (bool)Field("disposed")!,
                Field("renderer") is null, (bool)Field("accepting")!, (bool)Field("rendererReady")!,
                (bool)Field("workerRunning")!, Field("pending") is not null,
                worker.IsCompletedSuccessfully, worker.Status.ToString(), worker.Exception?.ToString(),
                (nint)Field("previousWindowProcedure")! == nint.Zero && Field("inputProcedure") is null);
        }
    }

    private static void VerifyViewportCleanup()
    {
        if (viewportCleanup is not { } cleanup) return;
        // Dispose joins the renderer, while destruction of the parent HWND can destroy its
        // native child without clearing this managed field. A nonzero cached handle alone
        // is therefore insufficient evidence of an OS window or renderer leak.
        Assert(!cleanup.PresentedChildStillExists && !cleanup.StoredChildStillExists,
            "Win32 confirms both the presented and cached viewport child HWND no longer exist after lifetime exit");
        Assert(cleanup.RendererReleased && !cleanup.Accepting && !cleanup.RendererReady && !cleanup.WorkerRunning
            && !cleanup.PendingWork && cleanup.WorkerCompletedSuccessfully,
            "viewport renderer is released, rejects work, and its serialized worker finishes successfully after lifetime exit");
        Assert(cleanup.InputDetached, "viewport native input hook and delegate are released after lifetime exit");
    }

    private static void Guard(Action action)
    {
        if (failure is not null) return;
        try { action(); }
        catch (Exception error)
        {
            failure = error; observing = false; monitor?.Stop();
            // An observer can fail inside a native animation callback. Both capture and
            // closing owned windows must leave composition before touching window layout.
            Dispatcher.UIThread.Post(() =>
            {
                try { if (!exiting && startup?.IsVisible == true) CaptureStartup("startup-failure.png"); } catch { }
                CleanupOwnWindows();
            }, DispatcherPriority.Background);
        }
    }

    private static void CleanupOwnWindows()
    {
        observing = false; monitor?.Stop();
        desktop ??= Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        try
        {
            if (editor is not null)
            {
                // Only the fresh App instance created by this harness, never another process's editor.
                typeof(EditorWindow).GetField("_closingConfirmed", InstanceMembers)?.SetValue(editor, true);
                editor.Close();
            }
        }
        catch { }
        try { if (startup?.IsVisible == true) startup.Close(); } catch { }
        try { if (!exiting) desktop?.Shutdown(1); } catch { }
    }

    private static object? ReadCurrent() => startupType!.GetProperty("Current", StaticMembers)?.GetValue(null);
    private static T? Member<T>(string name) where T : class => startupApplication is null ? null
        : (startupType!.GetProperty(name, InstanceMembers)?.GetValue(startupApplication)
            ?? startupType.GetField(name, InstanceMembers)?.GetValue(startupApplication)) as T;
    private static string Phase() => startupApplication is null ? "Unobserved"
        : startupType!.GetProperty("Phase", InstanceMembers)?.GetValue(startupApplication)?.ToString() ?? "Missing";
    private static FileState ReadState(string path) => File.Exists(path)
        ? new(true, new FileInfo(path).Length, Hash(path)) : new(false, null, null);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static KeyValuePair<string, string> ParseOption(string value)
    {
        if (!value.StartsWith("--", StringComparison.Ordinal) || !value.Contains('=')) throw new ArgumentException("Expected --key=value: " + value);
        var pair = value[2..].Split('=', 2); return new(pair[0], pair[1]);
    }
    private static string Required(IReadOnlyDictionary<string, string> options, string key) => options.TryGetValue(key, out var value)
        ? value : throw new ArgumentException("Missing --" + key + ".");
    private static string ExistingAbsoluteFile(string path) => Path.IsPathFullyQualified(path) && File.Exists(path)
        ? Path.GetFullPath(path) : throw new ArgumentException("Expected existing absolute file: " + path);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); Checks.Add(message); }
    private static bool OnAnyScreen(EditorWindow window)
    {
        var size = window.FrameSize ?? window.ClientSize;
        return OnAnyScreen(window.Position.X, window.Position.Y,
            window.Position.X + (int)Math.Ceiling(size.Width * window.RenderScaling),
            window.Position.Y + (int)Math.Ceiling(size.Height * window.RenderScaling));
    }
    private static bool OnAnyScreen(NativeWindow window) => OnAnyScreen(window.Left, window.Top, window.Right, window.Bottom);
    private static bool OnAnyScreen(int left, int top, int right, int bottom) => startup is not null && startup.Screens.All.Any(screen =>
        left < screen.WorkingArea.Right && right > screen.WorkingArea.X && top < screen.WorkingArea.Bottom && bottom > screen.WorkingArea.Y);
    private static List<NativeWindow> OwnNativeWindows()
    {
        var result = new List<NativeWindow>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint process);
            if (process != (uint)Environment.ProcessId) return true;
            var title = new StringBuilder(512); GetWindowTextW(handle, title, title.Capacity);
            var name = new StringBuilder(256); GetClassNameW(handle, name, name.Capacity);
            GetWindowRect(handle, out var rect);
            result.Add(new(handle.ToInt64().ToString("X"), title.ToString(), name.ToString(), IsWindowVisible(handle),
                rect.Left, rect.Top, rect.Right, rect.Bottom));
            return true;
        }, nint.Zero);
        return result;
    }

    private static void WriteReport() => File.WriteAllText(Path.Combine(output, "startup-animation-checks.json"), JsonSerializer.Serialize(new
    {
        success = failure is null && completed, mode, passed = Checks.Count, checks = Checks, skipped = Skipped,
        expectedApp, expectedAppSha256 = expectedAppSha, loadedProductAssemblies = LoadedAssemblies,
        publishedProductDllCount = ProductBefore.Count, preferencesBefore = PreferencesBefore, preferencesAfter = PreferencesAfter,
        elapsedMilliseconds = Clock.Elapsed.TotalMilliseconds, phases = Phases, animationSamples = AnimationSamples,
        distinctAnimationProgress = AnimationSamples.Where(value => value.IsActive && value.Visible)
            .Select(value => Math.Round(value.Progress, 6)).Distinct().Count(),
        captures = Captures, presentations = Presentations, presentationCount = presented, readyBaseline,
        firstPresentationMilliseconds, readyMilliseconds, viewportFailure, startupLastError = Member<Exception>("LastError")?.ToString(),
        presentedChildHwnd = presentedChildWindow.ToInt64().ToString("X"), startupClosed, editorClosed,
        viewportCleanupAfterLifetime = viewportCleanup,
        cancellationIssued, completed, desktopExitObserved = exiting, desktopExitCode, lifetimeExitCode,
        nativeTopLevelsBeforeOwnedCleanup = beforeCleanupWindows, nativeTopLevelsAfterLifetime = afterLifetimeWindows,
        entryPoint = "AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().AfterSetup(observer).StartWithClassicDesktopLifetime(args)",
        clockSampling = "Real StartupDots AnimationProgressProperty on distinct native RequestAnimationFrame callbacks during actual ordinary App startup. No startup delay, fake workload, minimum display time, or altered animation preference.",
        captureScheduling = "Native animation callbacks only sample values. Captures and subsequent callback registration run at DispatcherPriority.Background; each queued capture rechecks startup visibility and its real nonterminal phase.",
        screenshotScope = "RenderTargetBitmap of the actual visible startup window, which has no native child HWND. These are control-render evidence, not OS desktop screenshots or full editor/GPU captures.",
        graphicsScope = mode == "ready" ? "Real hardware EditorViewportHost.FramePresented required before Ready and again after visible handoff. No model import, GPU utilization, broad playback, installer, or visual approval claim."
            : "Cancellation at the real CreatingWorkspace event, before workspace construction and native child creation. No GPU success claimed in cancellation mode.",
        fileScope = "No product rebuild or user preference write. Three preference files are compared by existence, size and SHA256; unexpected changes are reported without restoration.",
        failure = failure?.ToString()
    }, Json));

    private sealed record FileState(bool Exists, long? Bytes, string? Sha256);
    private sealed record PhaseSample(double ElapsedMilliseconds, string Phase);
    private static bool ClockStopped(LoadingDots value) => !value.IsAnimating(progressProperty!) && !value.Classes.Contains(":animating");
    private sealed record AnimationSample(double ElapsedMilliseconds, double RenderMilliseconds, string Phase, double Progress,
        bool IsActive, bool AnimationsEnabled, bool Visible, bool IsAnimating, bool AnimationSelected);
    private sealed record NativeWindow(string Hwnd, string Title, string ClassName, bool Visible, int Left, int Top, int Right, int Bottom);
    private sealed record ViewportCleanupState(string StoredChildHwnd, bool StoredChildStillExists, bool PresentedChildStillExists,
        bool Disposed, bool RendererReleased, bool Accepting, bool RendererReady, bool WorkerRunning, bool PendingWork,
        bool WorkerCompletedSuccessfully, string WorkerStatus, string? WorkerException, bool InputDetached);
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
}
