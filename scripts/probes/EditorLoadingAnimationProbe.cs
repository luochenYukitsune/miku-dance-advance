using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>
/// References the supplied published product assemblies; never builds product sources. The real
/// editor's native viewport is suspended before Show, so this probe tests Avalonia loading feedback,
/// model/physics preparation and project I/O without starting D3D12. Screenshots omit the child HWND.
/// </summary>
internal static class EditorLoadingAnimationProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<string> Checks = [];
    private static readonly List<string> Skipped = [];
    private static readonly List<object> WorkEvidence = [];
    private static readonly List<string> Captures = [];
    private static readonly Dictionary<string, string> Originals = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ProductHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<object> LoadedAssemblies = [];
    private static readonly List<object> SetupCheckpoints = [];
    // Do not reflect a LoadingDots static field before platform setup: its static constructor
    // creates a SolidColorBrush, whose AvaloniaObject base can initialize an unconfigured dispatcher.
    private static StyledProperty<double>? progressProperty;
    private static StyledProperty<double> ProgressProperty => progressProperty ??= (StyledProperty<double>)(typeof(LoadingDots)
        .GetField("AnimationProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new MissingFieldException(typeof(LoadingDots).FullName, "AnimationProgressProperty"));
    private static string output = "", expectedApp = "", expectedAppSha = "", productRoot = "";
    private static EditorWindow? window;
    private static EditorShellViewModel? shell;
    private static LoadingDots? top, bottom;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Usage: loading-probe <output-directory> --pmx=<real absolute PMX> [--pma=<real absolute PMA>] [--vmd=<real absolute VMD>] --expected-app=<published app DLL> --expected-app-sha256=<SHA256> [--expected-product-manifest=<payload JSON>] [--visible-editor]");
        output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var options = args.Skip(1).Where(value => value.StartsWith("--", StringComparison.Ordinal) && value.Contains('='))
            .Select(value => value[2..].Split('=', 2)).ToDictionary(value => value[0], value => value[1], StringComparer.Ordinal);
        try
        {
            string pmx = Input(Required(options, "pmx"));
            string? pma = options.TryGetValue("pma", out var pmaOption) ? Input(pmaOption) : null;
            string? vmd = options.TryGetValue("vmd", out var vmdOption) ? Input(vmdOption) : null;
            ConfigureProduct(options);
            VerifyProduct("before");
            Remember(pmx); if (pma is not null) Remember(pma); if (vmd is not null) Remember(vmd);
            RememberTextures(pmx);
            Checkpoint("before-platform-setup", inspectDispatcher: false);
            AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().SetupWithoutStarting();
            Checkpoint("after-platform-setup");
            window = new EditorWindow(false)
            {
                ShowInTaskbar = args.Contains("--visible-editor", StringComparer.Ordinal),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = args.Contains("--visible-editor", StringComparer.Ordinal) ? new PixelPoint(30, 30) : new PixelPoint(-20000, -20000)
            };
            Checkpoint("after-editor-construction");
            // Set this before attaching the shell and before creation of the native child HWND.
            window.FindControl<EditorViewportHost>("SceneViewport")!.IsSuspended = true;
            Checkpoint("after-viewport-suspended");
            shell = new EditorShellViewModel(); Checkpoint("after-shell-construction");
            window.DataContext = shell; Checkpoint("after-shell-attached");
            window.Show(); Checkpoint("after-window-show"); PumpLayout();
            // Suspending the viewport deliberately prevents first presentation. Clear that
            // artificial initial wait only in this no-GPU probe; native workflow covers it.
            SetPreviewLoading(false); PumpLayout();
            top = window.FindControl<LoadingDots>("BusyLoadingDots") ?? throw new InvalidOperationException("Missing formal ProjectBar BusyLoadingDots.");
            bottom = window.FindControl<LoadingDots>("StatusLoadingDots") ?? throw new InvalidOperationException("Missing formal StatusLoadingDots.");
            Assert(!shell.IsBusy && !Flag("IsLoading") && !Flag("IsPreparingPreview"), "the real editor starts without invented loading activity");
            Assert(!top.IsActive && !bottom.IsActive && top.Styles.Count == 0 && bottom.Styles.Count == 0, "both idle indicators release their animation styles");

            Guid modelId = Guid.Empty;
            RunRealWork("pmx-model-load-add", () =>
            {
                var asset = EditorProjectWorkspace.LoadModelAssetWithLimits(pmx, shell.Workspace.Preferences.PmxMaxBytes);
                modelId = shell.Workspace.AddModel(pmx, asset, ModelRole.Character);
            }, () => shell.RefreshWorkspace(modelId));
            Assert(modelId != Guid.Empty && shell.Workspace.Motions.Any(actor => actor.ObjectId == modelId), "real PMX parsing and physics-backed AddModel publish the loaded actor");
            var actor = shell.Workspace.Motions.Single(item => item.ObjectId == modelId);
            RunRealWork("motion-bind", () =>
            {
                // Without --vmd, rebind the actual imported actor's authored motion unchanged.
                // This still runs the real project transaction and solver preparation, without fake bytes.
                var motion = vmd is not null ? EditorProjectInterchange.PrepareVmd(vmd, actor.Model)
                    : actor.Asset ?? throw new InvalidOperationException("The imported actor has no serializable authored motion; supply --vmd.");
                shell.Workspace.BindMotion(modelId, motion);
            });
            if (pma is not null)
            {
                RunRealWork("pma-open", () => shell.Workspace.Open(pma));
                Assert(shell.Workspace.Session is { Objects.Count: > 0 } && shell.Workspace.ProjectPath == pma, "the supplied real PMA opens through the published workspace");
            }
            else Skipped.Add("No --pma supplied: real portable-project open not exercised.");

            VerifyBusyContracts();
            VerifyPreviewAndPresentation();
            VerifyOriginals();
            VerifyProduct("after");
            // Own workspace only: discard unsaved probe edits so Close cannot open an unsaved prompt.
            shell.Workspace.New(); SetPreviewLoading(true); PumpFrames(4);
            Assert(top.IsActive && bottom.IsActive, "loading is active immediately before the owned window closes");
            window.Close(); Dispatcher.UIThread.RunJobs();
            Assert(!window.IsVisible && top.Styles.Count == 0 && bottom.Styles.Count == 0, "closing the owned formal editor releases both loading clocks");
            WriteReport(null);
            Console.WriteLine($"PASS {Checks.Count} loading-animation checks; {Skipped.Count} explicitly skipped. Evidence: {output}");
            return 0;
        }
        catch (Exception error)
        {
            try { if (window?.IsVisible == true) Capture("loading-failure-controls-hwnd-omitted.png"); } catch { }
            WriteReport(error.ToString()); Console.Error.WriteLine(error); return 1;
        }
        finally
        {
            if (window?.IsVisible == true)
            {
                try { SetPreviewLoading(false); shell?.Workspace.New(); window.Close(); } catch { }
            }
        }
    }

    private static void RunRealWork(string label, Action work, Action? publish = null)
    {
        int workerActive = 0;
        var clock = Stopwatch.StartNew();
        var samples = new List<FrameSample>();
        bool sampling = true;
        Exception? sampleFailure = null;
        int captures = 0;
        double lastCapture = -1;
        Task? operation = null;
        void ObserveFrame(TimeSpan renderTime)
        {
            // Await pumps any queued layout work before returning. Stop as soon as the
            // operation completes, or this observer can keep RunJobs draining forever.
            // A null operation means the initial callback was registered before startup.
            if (!sampling || operation?.IsCompleted == true) return;
            try
            {
                if (Volatile.Read(ref workerActive) != 0 && operation?.IsCompleted != true)
                {
                    var sample = new FrameSample(clock.Elapsed.TotalMilliseconds, renderTime.TotalMilliseconds,
                        top!.GetValue(ProgressProperty), bottom!.GetValue(ProgressProperty), shell!.IsBusy,
                        Flag("IsLoading"), top.IsActive, bottom.IsActive, Visible(top), Visible(bottom));
                    samples.Add(sample);
                    if (captures < 2 && sample.TopProgress > .02 && (lastCapture < 0 || Math.Abs(sample.TopProgress - lastCapture) >= .1))
                    {
                        string name = $"{label}-busy-{++captures}-controls-hwnd-omitted.png";
                        lastCapture = sample.TopProgress;
                        // RequestAnimationFrame runs inside rendering. Drawing/UpdateLayout belongs
                        // outside that callback; check real work again when the capture is dispatched.
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!sampling || Volatile.Read(ref workerActive) == 0 || operation?.IsCompleted != false) return;
                            try { Capture(name); }
                            catch (Exception error) { sampleFailure = error; }
                        }, DispatcherPriority.Background);
                    }
                }
            }
            catch (Exception error) { sampleFailure = error; }
            if (sampling && operation?.IsCompleted != true) Dispatcher.UIThread.Post(() =>
            {
                if (sampling && operation?.IsCompleted != true) window!.RequestAnimationFrame(ObserveFrame);
            }, DispatcherPriority.Background);
        }
        window!.RequestAnimationFrame(ObserveFrame);
        try
        {
            operation = shell!.RunOperationAsync(label, async () =>
            {
                await Task.Run(() =>
                {
                    Volatile.Write(ref workerActive, 1);
                    try { work(); }
                    finally { Volatile.Write(ref workerActive, 0); }
                });
                publish?.Invoke();
            });
            Await(operation, label, 180, beforeFinalPump: () => sampling = false);
        }
        finally
        {
            sampling = false;
            WorkEvidence.Add(new { label, elapsedMilliseconds = clock.Elapsed.TotalMilliseconds, samples,
                distinctTopProgress = samples.Select(value => Math.Round(value.TopProgress, 6)).Distinct().Count(),
                distinctBottomProgress = samples.Select(value => Math.Round(value.BottomProgress, 6)).Distinct().Count(),
                actualWorkOnly = true, injectedDelayOrGate = false });
        }
        if (sampleFailure is not null) throw new InvalidOperationException(label + " frame sampling failed.", sampleFailure);
        Assert(samples.Count >= 3, label + ": at least three actual render callbacks occur before the real worker finishes");
        Assert(samples.All(value => value.IsBusy && value.IsLoading && value.TopActive && value.BottomActive && value.TopVisible && value.BottomVisible),
            label + ": both formal indicators stay active and visible during sampled real work");
        Assert(samples.Select(value => Math.Round(value.TopProgress, 6)).Distinct().Count() >= 3
            && samples.Select(value => Math.Round(value.BottomProgress, 6)).Distinct().Count() >= 3,
            label + ": private native animation progress changes across actual render frames while the real worker is active");
        Assert(samples.Select(value => value.RenderMilliseconds).Distinct().Count() >= 3,
            label + ": samples come from distinct Avalonia render times");
        Assert(!shell!.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0 && bottom!.Styles.Count == 0,
            label + ": completing real work clears loading and releases both clocks");
    }

    private static void VerifyBusyContracts()
    {
        // Gates are confined to asynchronous state/cancellation contracts, never real-work evidence.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task completed = shell!.RunOperationAsync("busy contract gate", () => gate.Task);
        PumpFrames(4);
        Assert(shell.IsBusy && Flag("IsLoading") && !shell.CanEditWorkspace, "explicit busy operation blocks edits and activates loading");
        gate.SetResult(); Await(completed, "complete busy contract");
        Assert(!shell.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0 && bottom!.Styles.Count == 0, "completion clears explicit busy loading");

        using var cancellation = new CancellationTokenSource();
        var cancelledGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cancelled = shell.RunOperationAsync("cancellation contract gate", () => cancelledGate.Task.WaitAsync(cancellation.Token));
        PumpFrames(3); cancellation.Cancel(); Await(cancelled, "cancel busy contract");
        Assert(!shell.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0 && bottom!.Styles.Count == 0, "cancellation clears busy loading and both clocks");

        const string marker = "owned-loading-probe-error";
        Task failed = shell.RunOperationAsync("exception contract", () => Task.FromException(new InvalidOperationException(marker)));
        bool received = false;
        try { Await(failed, "fail busy contract"); }
        catch (InvalidOperationException error) when (error.Message == marker) { received = true; }
        Assert(received && !shell.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0 && bottom!.Styles.Count == 0,
            "exception is propagated while busy loading and animation clocks are cleared");
    }

    private static void VerifyPreviewAndPresentation()
    {
        SetPreviewLoading(true); PumpFrames(5);
        Assert(!shell!.IsBusy && Flag("IsPreparingPreview") && Flag("IsLoading") && shell.CanEditWorkspace,
            "preview preparation activates loading while preserving animation editing");
        Assert(top!.IsActive && bottom!.IsActive && top.Styles.Count == 1 && bottom.Styles.Count == 1, "both formal indicators bind to aggregate loading activity");
        Assert(Accent(top) && Accent(bottom!), "both indicators explicitly retain the selected theme loading accent under window styles");
        double before = top.GetValue(ProgressProperty); PumpFrames(5);
        Assert(top.GetValue(ProgressProperty) != before, "preview-only loading advances the native animation clock");

        foreach (int theme in new[] { 0, 1 })
        {
            shell.SelectedThemeIndex = theme; window!.Width = window.MinWidth; window.Height = window.MinHeight;
            shell.OperationStatus = new string('W', 4096); PumpFrames(3); PumpLayout();
            Assert(WithinWindow(top!) && WithinWindow(bottom!), $"theme {theme}: both loading indicators fit the minimum native window despite long status text");
            Assert(Accent(top!) && Accent(bottom!), $"theme {theme}: five-dot color survives theme changes");
            Capture($"loading-preview-{(theme == 0 ? "black" : "white")}-minimum-long-status-controls-hwnd-omitted.png");
        }
        // The cancel affordance belongs to cancellable busy work; a preview-only task must not fake one.
        SetPreviewLoading(false);
        var layoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task layoutOperation = shell.RunOperationAsync("long status layout contract", () => layoutGate.Task);
        try
        {
            foreach (int theme in new[] { 0, 1 })
            {
                shell.SelectedThemeIndex = theme; shell.OperationStatus = new string('W', 4096); PumpFrames(3); PumpLayout();
                var cancel = window!.FindControl<Button>("CancelOperationButton") ?? window!.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => Visible(button) && Equals(button.Content, EditorLocalization.Text("取消")))
                    ?? throw new InvalidOperationException("Cannot find the real cancel-operation button.");
                Assert(Visible(cancel) && WithinWindow(cancel) && WithinWindow(top!) && WithinWindow(bottom!),
                    $"theme {theme}: long status cannot push loading or cancellation outside the minimum window");
                var status = window!.FindControl<TextBlock>("OperationStatusText");
                if (status is not null)
                    Assert(status.TextTrimming != TextTrimming.None && !RootBounds(status).Intersects(RootBounds(bottom!))
                        && !RootBounds(status).Intersects(RootBounds(cancel)), $"theme {theme}: status text is truncated within its own layout column");
                Capture($"loading-busy-{(theme == 0 ? "black" : "white")}-minimum-long-status-controls-hwnd-omitted.png");
            }
        }
        finally { layoutGate.TrySetResult(); Await(layoutOperation, "release long-status contract"); }

        SetPreviewLoading(true); PumpFrames(3);
        var ancestor = top!.GetVisualParent() as Control ?? throw new InvalidOperationException("Loading control has no control parent.");
        bool wasVisible = ancestor.IsVisible;
        ancestor.IsVisible = false; PumpFrames(2);
        Assert(top.Styles.Count == 0, "hiding the ProjectBar ancestor releases the top animation clock");
        ancestor.IsVisible = wasVisible; PumpFrames(3);
        Assert(top.Styles.Count == 1, "showing the ProjectBar ancestor restarts its active native clock");
        top.AnimationsEnabled = false; bottom!.AnimationsEnabled = false; PumpFrames(3);
        Assert(top.IsActive && bottom.IsActive && top.Styles.Count == 0 && bottom.Styles.Count == 0
            && top.GetValue(ProgressProperty) == 0 && bottom.GetValue(ProgressProperty) == 0,
            "disabled animations keep truthful active indicators without an animation clock");
        Capture("loading-reduced-motion-controls-hwnd-omitted.png");
        top.AnimationsEnabled = true; bottom.AnimationsEnabled = true; PumpFrames(5);
        Assert(top.Styles.Count == 1 && bottom.Styles.Count == 1 && top.GetValue(ProgressProperty) > 0 && bottom.GetValue(ProgressProperty) > 0,
            "reenabling animations resumes both native progress clocks");
        SetPreviewLoading(false); PumpFrames(2);
        Assert(!Flag("IsLoading") && !top.IsActive && !bottom.IsActive && top.Styles.Count == 0 && bottom.Styles.Count == 0,
            "finishing preview preparation clears aggregate loading and both clocks");
        Assert(window!.FindControl<EditorViewportHost>("SceneViewport")!.Statistics is null,
            "suspended native viewport performs no D3D12 presentation during this loading-control probe");
    }

    private static bool Flag(string name) => (bool)(typeof(EditorShellViewModel).GetProperty(name)?.GetValue(shell)
        ?? throw new MissingMemberException(typeof(EditorShellViewModel).FullName, name));
    private static void SetPreviewLoading(bool value) => (typeof(EditorShellViewModel).GetMethod("SetPreviewLoading",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(bool)], null)
        ?? throw new MissingMethodException(typeof(EditorShellViewModel).FullName, "SetPreviewLoading(bool)"))
        .Invoke(shell, [value]);
    private static bool Accent(LoadingDots dots) => dots.Foreground is ISolidColorBrush brush
        && brush.Color == Color.Parse(shell!.SelectedThemeIndex == 1 ? "#117A6D" : "#45C6B0");
    private static bool Visible(Control control) => control.IsVisible && control.GetVisualAncestors().All(parent => parent.IsVisible);
    private static Rect RootBounds(Control control) => new(control.TranslatePoint(new Point(), window!)
        ?? throw new InvalidOperationException("Cannot map " + control.Name + " into the editor."), control.Bounds.Size);
    private static bool WithinWindow(Control control)
    {
        if (!Visible(control) || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        var bounds = RootBounds(control);
        return bounds.X >= -.5 && bounds.Y >= -.5 && bounds.Right <= window!.Bounds.Width + .5 && bounds.Bottom <= window.Bounds.Height + .5;
    }
    private static void PumpLayout([CallerMemberName] string caller = "", [CallerLineNumber] int line = 0)
    {
        Checkpoint($"pump {caller}:{line} before-RunJobs");
        Dispatcher.UIThread.RunJobs();
        Checkpoint($"pump {caller}:{line} after-RunJobs");
        window?.UpdateLayout();
        Checkpoint($"pump {caller}:{line} after-UpdateLayout");
        Dispatcher.UIThread.RunJobs();
        Checkpoint($"pump {caller}:{line} complete");
    }
    private static void Checkpoint(string label, bool inspectDispatcher = true)
    {
        // Read-only diagnostics: never change DisabledProcessingCount or replace dispatcher internals.
        int? disabled = inspectDispatcher ? (int?)typeof(Dispatcher).GetProperty("DisabledProcessingCount",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Dispatcher.UIThread) : null;
        string? implementation = inspectDispatcher ? typeof(Dispatcher).GetField("_impl", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(Dispatcher.UIThread)?.GetType().FullName : null;
        SetupCheckpoints.Add(new { label, utc = DateTimeOffset.UtcNow, threadId = Environment.CurrentManagedThreadId,
            synchronizationContext = SynchronizationContext.Current?.GetType().FullName, disabledProcessingCount = disabled,
            dispatcherImplementation = implementation });
        File.WriteAllText(Path.Combine(output, "loading-setup-checkpoints.json"), JsonSerializer.Serialize(SetupCheckpoints, Json));
    }
    private static void PumpFrames(int count)
    {
        int remaining = count;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        bool active = true;
        void Next(TimeSpan _)
        {
            if (!active) return;
            if (--remaining <= 0) frame.Continue = false;
            else Dispatcher.UIThread.Post(() =>
            { if (active) window!.RequestAnimationFrame(Next); }, DispatcherPriority.Background);
        }
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => { failure = new TimeoutException("Native animation frames did not arrive."); frame.Continue = false; };
        window!.RequestAnimationFrame(Next); timeout.Start();
        try { Dispatcher.UIThread.PushFrame(frame); }
        finally { active = false; timeout.Stop(); }
        PumpLayout(); if (failure is not null) throw failure;
    }
    private static void Await(Task task, string label, int timeoutSeconds = 30, Action? beforeFinalPump = null)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); Exception? failure = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) =>
            {
                if (task.IsCompleted) frame.Continue = false;
                else if (clock.Elapsed.TotalSeconds > timeoutSeconds) { failure = new TimeoutException(label); frame.Continue = false; }
            };
            timer.Start(); try { Dispatcher.UIThread.PushFrame(frame); } finally { timer.Stop(); }
            if (failure is not null) throw failure;
        }
        try { task.GetAwaiter().GetResult(); }
        finally { beforeFinalPump?.Invoke(); }
        PumpLayout();
    }
    private static void Capture(string name)
    {
        window!.UpdateLayout(); double scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
            (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, name)); Captures.Add(name);
    }
    private static string Required(IReadOnlyDictionary<string, string> options, string name) => options.TryGetValue(name, out var value)
        ? value : throw new ArgumentException("Missing --" + name + ".");
    private static string Input(string path) => Path.IsPathFullyQualified(path) && File.Exists(path) ? Path.GetFullPath(path)
        : throw new ArgumentException("Input must be an existing absolute file: " + path);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static void Remember(string path) { if (!Originals.ContainsKey(path)) Originals.Add(path, Hash(path)); }
    private static void RememberTextures(string pmx)
    {
        using var stream = File.OpenRead(pmx); var model = PmxReader.Read(stream);
        foreach (var texture in model.Textures.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pmx)!, texture.Replace('\\', Path.DirectorySeparatorChar)));
            if (File.Exists(path)) Remember(path);
        }
    }
    private static void VerifyOriginals()
    { foreach (var item in Originals) Assert(Hash(item.Key) == item.Value, "read-only original remains byte-identical: " + Path.GetFileName(item.Key)); }
    private static void ConfigureProduct(IReadOnlyDictionary<string, string> options)
    {
        expectedApp = Input(Required(options, "expected-app")); expectedAppSha = Required(options, "expected-app-sha256");
        productRoot = Path.GetDirectoryName(expectedApp)!;
        if (expectedAppSha.Length != 64 || !expectedAppSha.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid expected app SHA256.");
        if (options.TryGetValue("expected-product-manifest", out var manifestPath))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Input(manifestPath)));
            foreach (var file in manifest.RootElement.EnumerateArray())
            {
                string path = Path.GetFullPath(Path.Combine(productRoot, file.GetProperty("path").GetString()!));
                if (!path.StartsWith(productRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload manifest leaves the expected product root.");
                ProductHashes.Add(path, file.GetProperty("sha256").GetString()!);
            }
        }
        else ProductHashes.Add(expectedApp, expectedAppSha);
    }
    private static void VerifyProduct(string phase)
    {
        foreach (var item in ProductHashes)
            Assert(File.Exists(item.Key) && Hash(item.Key).Equals(item.Value, StringComparison.OrdinalIgnoreCase), phase + ": published payload SHA256 preserved: " + Path.GetRelativePath(productRoot, item.Key));
        string actualApp = Path.GetFullPath(typeof(App).Assembly.Location);
        Assert(actualApp.Equals(expectedApp, StringComparison.OrdinalIgnoreCase) && Hash(actualApp).Equals(expectedAppSha, StringComparison.OrdinalIgnoreCase),
            phase + ": formal editor directly loads the supplied published app assembly");
        LoadedAssemblies.Clear();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(value => value.GetName().Name?.StartsWith("Mmd.Modern.", StringComparison.Ordinal) == true))
        {
            string path = Path.GetFullPath(assembly.Location), hash = Hash(path);
            Assert(Path.GetDirectoryName(path)!.Equals(productRoot, StringComparison.OrdinalIgnoreCase), phase + ": product assembly comes from the supplied payload: " + assembly.GetName().Name);
            if (ProductHashes.TryGetValue(path, out var expected)) Assert(hash.Equals(expected, StringComparison.OrdinalIgnoreCase), phase + ": loaded product assembly has expected SHA256: " + assembly.GetName().Name);
            LoadedAssemblies.Add(new { name = assembly.GetName().Name, path, sha256 = hash });
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); Checks.Add(message); }
    private static void WriteReport(string? failure) => File.WriteAllText(Path.Combine(output, "loading-animation-checks.json"), JsonSerializer.Serialize(new
    {
        success = failure is null, passed = Checks.Count, checks = Checks, skipped = Skipped, realWork = WorkEvidence, captures = Captures,
        setupCheckpoints = SetupCheckpoints,
        expectedApp, expectedAppSha256 = expectedAppSha, loadedProductAssemblies = LoadedAssemblies,
        inputHashes = Originals, publishedPayloadFilesChecked = ProductHashes.Count,
        clockSampling = "Private Avalonia AnimationProgressProperty sampled on distinct native RequestAnimationFrame callbacks only while real worker is active; no injected wait in real-work samples.",
        contractGates = "TaskCompletionSource used only for separate completion/cancellation/layout state contracts.",
        screenshotScope = "Avalonia RenderTargetBitmap of the formal editor controls; native child HWND viewport omitted. No full-window GPU capture or visual model approval claimed.",
        graphicsScope = "SceneViewport.IsSuspended=true before Show; the resulting initial unpresentable preview-wait state is explicitly cleared in this test only. Native first-frame waiting is outside this probe. No D3D12 build, render or GPU readback requested.", failure
    }, Json));
    private sealed record FrameSample(double ElapsedMilliseconds, double RenderMilliseconds, double TopProgress, double BottomProgress,
        bool IsBusy, bool IsLoading, bool TopActive, bool BottomActive, bool TopVisible, bool BottomVisible);
}
