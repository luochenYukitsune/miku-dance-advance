using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
/// editor's native viewport is suspended before Show, so this probe tests the single loading clock,
/// static status symbols, outcome states, model/physics preparation and project I/O without starting
/// D3D12. Screenshots omit the child HWND. Diagnostic task labels are not approved product copy.
/// </summary>
internal static class EditorLoadingExperienceProbe
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<string> Checks = [];
    private static readonly List<string> Skipped = [];
    private static readonly List<object> WorkEvidence = [];
    private static readonly List<object> StateEvidence = [];
    private static readonly HashSet<EditorActivityState> ObservedStates = [];
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
    private static LoadingDots? top;
    private static PathIcon? statusIcon;
    private static Button? cancelButton, detailsButton;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Usage: loading-experience-probe <output-directory> --pmx=<real absolute PMX> [--pma=<real absolute PMA>] [--vmd=<real absolute VMD>] --expected-app=<published app DLL> --expected-app-sha256=<SHA256> [--expected-product-manifest=<payload JSON>] [--visible-editor]");
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
            statusIcon = window.FindControl<PathIcon>("OperationStatusIcon") ?? throw new InvalidOperationException("Missing formal static OperationStatusIcon.");
            cancelButton = window.FindControl<Button>("CancelOperationButton") ?? throw new InvalidOperationException("Missing formal top cancellation button.");
            detailsButton = window.FindControl<Button>("OperationDetailsButton") ?? throw new InvalidOperationException("Missing formal operation details button.");
            Assert(window.GetVisualDescendants().OfType<LoadingDots>().SequenceEqual([top]), "formal workbench contains exactly one five-dot loading indicator");
            Assert(window.FindControl<Control>("StatusLoadingDots") is null, "status bar uses a static PathIcon and no second LoadingDots");
            Assert(!shell.IsBusy && !Flag("IsLoading") && !Flag("IsPreparingPreview"), "the real editor starts without invented loading activity");
            Assert(!top.IsActive && top.Styles.Count == 0, "idle top indicator releases its animation style");
            AssertActivity(EditorActivityState.Idle, "circle", "initial idle");

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
            VerifyPreviewRetryContracts();
            Assert(Enum.GetValues<EditorActivityState>().All(ObservedStates.Contains), "all actual activity states have explicit presentation evidence");
            // Own workspace only: discard unsaved probe edits so Close cannot open an unsaved prompt.
            shell.Workspace.New(); SetPreviewLoading(true); PumpFrames(4);
            Assert(top.IsActive && top.Styles.Count == 1, "the only loading clock is active immediately before the owned window closes");
            window.Close(); Dispatcher.UIThread.RunJobs();
            Assert(!window.IsVisible && top.Styles.Count == 0 && statusIcon.Styles.Count == 0, "closing the owned formal editor releases the loading clock and retains no icon animation");
            VerifyOriginals();
            VerifyProduct("after-close");
            WriteReport(null);
            Console.WriteLine($"PASS {Checks.Count} loading-experience checks; {Skipped.Count} explicitly skipped. Evidence: {output}");
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
                        top!.GetValue(ProgressProperty), shell!.IsBusy, Flag("IsLoading"), top.IsActive, Visible(top),
                        shell.ActivityState, statusIcon!.Data?.ToString(), statusIcon.Opacity,
                        statusIcon.RenderTransform?.ToString(), statusIcon.Styles.Count);
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
                distinctStatusIconPresentations = samples.Select(value => (value.IconGeometry, value.IconOpacity, value.IconTransform)).Distinct().Count(),
                actualWorkOnly = true, injectedDelayOrGate = false });
        }
        if (sampleFailure is not null) throw new InvalidOperationException(label + " frame sampling failed.", sampleFailure);
        Assert(samples.Count >= 3, label + ": at least three actual render callbacks occur before the real worker finishes");
        Assert(samples.All(value => value.IsBusy && value.IsLoading && value.TopActive && value.TopVisible && value.State == EditorActivityState.Busy),
            label + ": the only formal animation stays active and visible with real Busy state during sampled real work");
        Assert(samples.Select(value => Math.Round(value.TopProgress, 6)).Distinct().Count() >= 3,
            label + ": private native animation progress changes across actual render frames while the real worker is active");
        Assert(samples.All(value => value.IconStyleCount == 0)
            && samples.Select(value => (value.IconGeometry, value.IconOpacity, value.IconTransform)).Distinct().Count() == 1,
            label + ": bottom PathIcon remains static across actual worker render frames");
        Assert(samples.Select(value => value.RenderMilliseconds).Distinct().Count() >= 3,
            label + ": samples come from distinct Avalonia render times");
        Assert(!shell!.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0,
            label + ": completing real work clears loading and releases the only clock");
        AssertActivity(EditorActivityState.Completed, "check-circle", label + " completed");
    }

    private static void VerifyBusyContracts()
    {
        // Gates are confined to asynchronous state/cancellation contracts, never real-work evidence.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task completed = shell!.RunOperationAsync("diagnostic contract: failed cancelled completed", () => gate.Task);
        try
        {
            PumpFrames(4);
            Assert(shell.IsBusy && Flag("IsLoading") && !shell.CanEditWorkspace, "explicit busy operation blocks edits and activates loading");
            AssertActivity(EditorActivityState.Busy, "dot-outline", "busy with arbitrary diagnostic text");
            Assert(Visible(cancelButton!) && cancelButton!.IsEnabled && !Visible(detailsButton!), "busy operation exposes an enabled top cancel action without error details");
            Assert(RootBounds(cancelButton!).Bottom < RootBounds(statusIcon!).Top && WithinWindow(cancelButton!), "cancel action belongs to the top loading band above the static bottom status icon");
        }
        finally { gate.TrySetResult(); Await(completed, "complete busy contract"); }
        Assert(!shell.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0, "completion clears explicit busy loading and its only clock");
        AssertActivity(EditorActivityState.Completed, "check-circle", "completed contract");
        Assert(shell.ActivitySummary == EditorLocalization.Text("操作已完成。"), "unmodified diagnostic task label is replaced by the real localized completion result");
        VerifyOutcomeLocalization(EditorActivityState.Completed, "check-circle", "操作已完成。", "completed contract");

        var commitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task committing = shell.RunOperationAsync("diagnostic committing contract", () => commitGate.Task);
        try
        {
            PumpFrames(3); SetCanCancel(false); PumpFrames(3);
            AssertActivity(EditorActivityState.Committing, "lock-simple", "non-cancellable commit phase");
            Assert(shell.IsBusy && !shell.CanCancelOperation && Visible(cancelButton!) && !cancelButton!.IsEnabled,
                "commit phase keeps the top cancel action visible and disabled");
            Assert(!InvokeShell<bool>("RequestOperationCancellation"), "commit phase rejects cancellation through the actual state API");
            Assert(shell.ActivitySummary.Contains(EditorLocalization.Text("（不可取消）"), StringComparison.Ordinal)
                && Equals(ToolTip.GetTip(cancelButton!), shell.CancelOperationHint), "commit phase explains its non-cancellable state in status and tooltip");
        }
        finally { commitGate.TrySetResult(); Await(committing, "complete commit contract"); }
        AssertActivity(EditorActivityState.Completed, "check-circle", "completed commit contract");

        // The owned CTS is installed only to exercise the formal button's existing handler.
        // Holding the cancellation gate makes the transient Cancelling presentation observable;
        // this gate never contributes to actual worker timings or animation-progress evidence.
        using var cancellation = new CancellationTokenSource();
        var cancelledGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationField = typeof(EditorWindow).GetField("_operationCancellation", InstanceMembers)
            ?? throw new MissingFieldException(typeof(EditorWindow).FullName, "_operationCancellation");
        Assert(cancellationField.GetValue(window) is null, "state-contract CTS does not replace an existing formal operation");
        Task cancelled = shell.RunOperationAsync("diagnostic cancellation contract", async () =>
        { await cancelledGate.Task; cancellation.Token.ThrowIfCancellationRequested(); });
        cancellationField.SetValue(window, cancellation);
        try
        {
            PumpFrames(3);
            cancelButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpFrames(3);
            Assert(cancellation.IsCancellationRequested, "formal top cancel button requests cancellation of the owned CTS");
            AssertActivity(EditorActivityState.Cancelling, "dot-outline", "cancel requested before completion");
            Assert(shell.IsBusy && Flag("IsLoading") && !shell.CanCancelOperation && !cancelButton.IsEnabled,
                "cancellation pending retains truthful busy animation and disables repeated cancel");
            Assert(!InvokeShell<bool>("RequestOperationCancellation"), "pending cancellation rejects repeated state requests");
        }
        finally
        {
            cancellationField.SetValue(window, null);
            cancelledGate.TrySetResult(); Await(cancelled, "cancel busy contract");
        }
        Assert(!shell.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0, "cancellation clears busy loading and the only clock");
        AssertActivity(EditorActivityState.Cancelled, "prohibit", "cancelled contract");
        Assert(!shell.HasActivityDetails && !Visible(detailsButton!) && !Visible(cancelButton!), "cancelled result leaves no stale error details or cancel action");
        VerifyOutcomeLocalization(EditorActivityState.Cancelled, "prohibit", "操作已取消。", "cancelled contract");

        // A cancellation request can race an already completed atomic commit. Normal return is
        // a successful outcome; only an actual OCE or explicit cancelled outcome means Cancelled.
        var racedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task raced = shell.RunOperationAsync("diagnostic cancellation-versus-commit race", () => racedGate.Task);
        try
        {
            PumpFrames(3);
            Assert(InvokeShell<bool>("RequestOperationCancellation"), "atomic-commit race accepts the pending cancellation request");
            PumpFrames(3);
            AssertActivity(EditorActivityState.Cancelling, "dot-outline", "atomic-commit race pending request");
        }
        finally { racedGate.TrySetResult(); Await(raced, "normal completion after cancellation request"); }
        AssertActivity(EditorActivityState.Completed, "check-circle", "normal return after a cancellation request");
        Assert(!shell.IsBusy && !Flag("IsLoading") && !shell.HasActivityDetails && top!.Styles.Count == 0,
            "normal completion after a late cancellation request releases loading with successful outcome");
        VerifyOutcomeLocalization(EditorActivityState.Completed, "check-circle", "操作已完成。", "atomic-commit race completed");

        const string marker = "owned-loading-probe-error";
        var exception = new InvalidOperationException(marker, new IOException("owned technical inner marker"));
        Task failed = shell.RunOperationAsync("diagnostic exception contract", () => Task.FromException(exception));
        bool received = false;
        try { Await(failed, "fail busy contract"); }
        catch (InvalidOperationException error) when (error.Message == marker) { received = true; }
        PumpLayout();
        Assert(received && !shell.IsBusy && !Flag("IsLoading") && top!.Styles.Count == 0,
            "exception is propagated while busy loading and its only animation clock are cleared");
        AssertActivity(EditorActivityState.Failed, "warning-circle", "failed contract");
        VerifyErrorSeparation(exception, "ordinary failure");
        Assert(!Visible(cancelButton!) && Visible(detailsButton!), "failure exposes technical details after hiding cancellation");
        Capture("loading-failed-summary-controls-hwnd-omitted.png");

        Task recovered = shell.RunOperationAsync("diagnostic recovery contract", () => Task.CompletedTask);
        Await(recovered, "recover after failure");
        AssertActivity(EditorActivityState.Completed, "check-circle", "successful recovery");
        Assert(!shell.HasActivityDetails && shell.ActivityDetails.Length == 0 && !Visible(detailsButton!), "successful next operation clears prior technical details");
        shell.OperationStatus = EditorLocalization.Text("就绪"); PumpLayout();
        AssertActivity(EditorActivityState.Idle, "circle", "idle after ordinary status update");
    }

    private static void VerifyPreviewAndPresentation()
    {
        shell!.OperationStatus = EditorLocalization.Text("就绪");
        SetPreviewLoading(true); PumpFrames(5);
        Assert(!shell.IsBusy && Flag("IsPreparingPreview") && Flag("IsLoading") && shell.CanEditWorkspace,
            "preview preparation activates loading while preserving animation editing");
        Assert(top!.IsActive && top.Styles.Count == 1, "the only formal animation binds to aggregate loading activity");
        AssertActivity(EditorActivityState.Busy, "dot-outline", "background preview preparation");
        Assert(shell.ImportModelCommand.CanExecute(null) && shell.SaveProjectCommand.CanExecute(null) == shell.HasProject,
            "background preview preparation preserves editing command availability");
        Assert(window!.FindControl<Control>("CurrentFrameInput") is { IsEffectivelyEnabled: true }
            && window!.FindControl<Control>("ModelBrowserSurface") is { IsEffectivelyEnabled: true },
            "background preview preparation keeps the real frame editor and model browser enabled");
        Assert(!Visible(cancelButton!), "preview-only preparation exposes no invented cancel action");
        Assert(Accent(top), "the sole indicator retains the selected theme loading accent under window styles");
        double before = top.GetValue(ProgressProperty); PumpFrames(5);
        Assert(top.GetValue(ProgressProperty) != before, "preview-only loading advances the native animation clock");

        string originalLanguage = EditorLocalization.CurrentLanguage;
        try
        {
        foreach (string language in new[] { "zh-CN", "en-US", "ja-JP" })
        foreach (int theme in new[] { 0, 1 })
        {
            EditorLocalization.SetLanguage(language); shell.SelectedThemeIndex = theme;
            if (theme == 0)
            {
                string diffusionLabel = new EditorPreviewSettingsViewModel().DiffusionEnabledLabel;
                Assert(diffusionLabel == EditorLocalization.Text("Mda.Text.1251a1baefb5")
                    && (language == "zh-CN" || (diffusionLabel != "启用 Diffusion" && diffusionLabel != "启用柔光")),
                    $"{language}: preview diffusion toggle uses the catalog key without a Chinese fallback");
            }
            window!.Width = window.MinWidth; window.Height = window.MinHeight;
            shell.OperationStatus = new string('W', 4096); PumpFrames(3); PumpLayout();
            Assert(WithinWindow(top!) && WithinWindow(statusIcon!), $"{language} theme {theme}: animation and static icon fit the minimum window with long status text");
            Assert(Accent(top!), $"{language} theme {theme}: five-dot color survives language and theme changes");
            AssertActivity(EditorActivityState.Busy, "dot-outline", $"{language} theme {theme} preview");
            Assert(shell.ActivitySummary == EditorLocalization.Text("正在准备预览…"), $"{language} theme {theme}: preview summary follows the selected language");
            VerifyStatusLayout($"{language} theme {theme} preview");
            Capture($"loading-preview-{language}-{(theme == 0 ? "black" : "white")}-minimum-long-status-controls-hwnd-omitted.png");
        }
        // The cancel affordance belongs to cancellable busy work; a preview-only task must not fake one.
        SetPreviewLoading(false);
        var layoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task layoutOperation = shell.RunOperationAsync("diagnostic long status layout contract", () => layoutGate.Task);
        try
        {
            foreach (string language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (int theme in new[] { 0, 1 })
            {
                EditorLocalization.SetLanguage(language); shell.SelectedThemeIndex = theme;
                shell.OperationStatus = new string('W', 4096); PumpFrames(3); PumpLayout();
                Assert(Visible(cancelButton!) && WithinWindow(cancelButton!) && WithinWindow(top!) && WithinWindow(statusIcon!),
                    $"{language} theme {theme}: long status cannot push animation, static icon or top cancellation outside the minimum window");
                Assert(Equals(cancelButton!.Content, EditorLocalization.Text("取消")), $"{language} theme {theme}: top cancellation action follows the selected language");
                VerifyStatusLayout($"{language} theme {theme} busy");
                Capture($"loading-busy-{language}-{(theme == 0 ? "black" : "white")}-minimum-long-status-controls-hwnd-omitted.png");
            }
        }
        finally { layoutGate.TrySetResult(); Await(layoutOperation, "release long-status contract"); }

        var previewError = new InvalidOperationException("owned-preview-internal-marker", new IOException("owned-preview-inner-marker"));
        InvokeShell("ReportActivityFailure", [previewError, true]); PumpLayout();
        foreach (string language in new[] { "zh-CN", "en-US", "ja-JP" })
        foreach (int theme in new[] { 0, 1 })
        {
            EditorLocalization.SetLanguage(language); shell.SelectedThemeIndex = theme; PumpFrames(3); PumpLayout();
            AssertActivity(EditorActivityState.Failed, "warning-circle", $"{language} theme {theme} preview failure");
            VerifyErrorSeparation(previewError, $"{language} theme {theme} preview failure");
            Assert(shell.ActivityFailureSummary == EditorLocalization.Text("预览暂不可用。请重试，或查看技术详情。"),
                $"{language} theme {theme}: preview failure summary is localized");
            Assert(Visible(detailsButton!) && WithinWindow(detailsButton!), $"{language} theme {theme}: failure details action remains visible inside the minimum window");
            VerifyStatusLayout($"{language} theme {theme} failure");
            Capture($"loading-failed-{language}-{(theme == 0 ? "black" : "white")}-minimum-controls-hwnd-omitted.png");
        }
        }
        finally { EditorLocalization.SetLanguage(originalLanguage); }

        shell.OperationStatus = EditorLocalization.Text("就绪");
        SetPreviewLoading(true); PumpFrames(3);
        var ancestor = top!.GetVisualParent() as Control ?? throw new InvalidOperationException("Loading control has no control parent.");
        bool wasVisible = ancestor.IsVisible;
        ancestor.IsVisible = false; PumpFrames(2);
        Assert(top.Styles.Count == 0, "hiding the ProjectBar ancestor releases the top animation clock");
        ancestor.IsVisible = wasVisible; PumpFrames(3);
        Assert(top.Styles.Count == 1, "showing the ProjectBar ancestor restarts its active native clock");
        top.AnimationsEnabled = false; PumpFrames(3);
        Assert(top.IsActive && top.Styles.Count == 0 && top.GetValue(ProgressProperty) == 0 && statusIcon!.Styles.Count == 0,
            "disabled animations keep truthful active indicators without an animation clock");
        Capture("loading-reduced-motion-controls-hwnd-omitted.png");
        top.AnimationsEnabled = true; PumpFrames(5);
        Assert(top.Styles.Count == 1 && top.GetValue(ProgressProperty) > 0 && statusIcon!.Styles.Count == 0,
            "reenabling animations resumes only the top native progress clock");
        SetPreviewLoading(false); PumpFrames(2);
        Assert(!Flag("IsLoading") && !top.IsActive && top.Styles.Count == 0,
            "finishing preview preparation clears aggregate loading and the sole clock");
        AssertActivity(EditorActivityState.Idle, "circle", "idle after preview preparation");
        Assert(window!.FindControl<EditorViewportHost>("SceneViewport")!.Statistics is null,
            "suspended native viewport performs no D3D12 presentation during this loading-control probe");
    }

    private static void VerifyPreviewRetryContracts()
    {
        // A busy/idle notification or a fallback frame is not evidence of a fresh preview
        // attempt. Only the actual BeginPreviewAttempt boundary may retire an old preview error.
        // The viewport remains suspended; no fallback FramePresented event is fabricated here.
        SetPreviewLoading(false);
        var previous = new InvalidOperationException("owned-previous-preview-failure");
        InvokeShell("ReportActivityFailure", [previous, true]); PumpLayout();
        AssertActivity(EditorActivityState.Failed, "warning-circle", "previous preview attempt failed");
        VerifyErrorSeparation(previous, "previous preview attempt failed");

        SetPreviewLoading(false); PumpLayout();
        Assert(shell!.ActivityState == EditorActivityState.Failed && shell.ActivityDetails == previous.ToString(),
            "ordinary preview-idle notification preserves the previous failure and technical details");
        SetPreviewLoading(true); PumpFrames(3);
        AssertActivity(EditorActivityState.Failed, "warning-circle", "ordinary preview-busy notification after failure");
        Assert(shell.HasActivityDetails && shell.ActivityDetails == previous.ToString() && Visible(detailsButton!),
            "ordinary preview-busy notification does not claim recovery or discard the previous failure");

        InvokeShell("BeginPreviewAttempt"); PumpFrames(3);
        AssertActivity(EditorActivityState.Busy, "dot-outline", "explicit retry while preview is preparing");
        Assert(!shell.HasActivityDetails && shell.ActivityDetails.Length == 0 && !Visible(detailsButton!)
            && shell.IsPreparingPreview && shell.CanEditWorkspace,
            "explicit retry retires only stale preview details and preserves editable background preparation");
        SetPreviewLoading(false); PumpLayout();
        AssertActivity(EditorActivityState.Idle, "circle", "explicit retry no longer preparing");

        var retried = new InvalidOperationException("owned-retried-preview-failure");
        InvokeShell("ReportActivityFailure", [retried, true]); PumpLayout();
        AssertActivity(EditorActivityState.Failed, "warning-circle", "retried preview fails again");
        VerifyErrorSeparation(retried, "retried preview fails again");
        InvokeShell("BeginPreviewAttempt"); PumpLayout();
        AssertActivity(EditorActivityState.Idle, "circle", "explicit retry before preparation becomes busy");
        Assert(!shell.HasActivityDetails && shell.ActivityDetails.Length == 0 && !Visible(detailsButton!),
            "explicit retry also retires stale preview details when no preparation is currently active");

        var ordinary = new InvalidOperationException("owned-ordinary-operation-failure");
        InvokeShell("ReportActivityFailure", [ordinary, false]); PumpLayout();
        InvokeShell("BeginPreviewAttempt"); PumpLayout();
        AssertActivity(EditorActivityState.Failed, "warning-circle", "ordinary operation failure survives preview retry");
        VerifyErrorSeparation(ordinary, "ordinary operation failure survives preview retry");
        Assert(Visible(detailsButton!), "preview retry preserves technical-details access for an unrelated operation failure");

        shell.OperationStatus = EditorLocalization.Text("就绪"); SetPreviewLoading(false); PumpLayout();
        AssertActivity(EditorActivityState.Idle, "circle", "retry contracts release their owned failure state");
    }

    private static void AssertActivity(EditorActivityState expected, string icon, string label)
    {
        Assert(shell!.ActivityState == expected, label + ": state comes from the real activity enum");
        Assert(window!.TryFindResource("Phosphor." + icon, out var geometry) && geometry is Geometry
            && ReferenceEquals(statusIcon!.Data, geometry), label + ": static PathIcon matches the designed state symbol");
        Assert(Visible(statusIcon!) && statusIcon!.Styles.Count == 0, label + ": bottom state icon is visible without a local animation style");
        var status = window!.FindControl<TextBlock>("OperationStatusText") ?? throw new InvalidOperationException("Missing bound ActivitySummary text.");
        Assert(status.Text == shell.ActivitySummary, label + ": status line binds the real activity summary");
        StateEvidence.Add(new { label, state = shell.ActivityState.ToString(), language = EditorLocalization.CurrentLanguage,
            theme = shell.SelectedThemeIndex, summary = shell.ActivitySummary, iconResource = "Phosphor." + icon,
            iconStyleCount = statusIcon!.Styles.Count, shell.IsBusy, shell.IsLoading, shell.CanEditWorkspace,
            shell.CanCancelOperation, shell.HasActivityDetails });
        ObservedStates.Add(shell.ActivityState);
    }
    private static void VerifyErrorSeparation(Exception error, string label)
    {
        Assert(shell!.HasActivityDetails && shell.ActivityDetails == error.ToString(), label + ": original exception text is preserved exactly in technical details");
        Assert(!shell.ActivitySummary.Contains(error.Message, StringComparison.Ordinal)
            && !shell.ActivityFailureSummary.Contains(error.Message, StringComparison.Ordinal)
            && !shell.OperationStatus.Contains(error.Message, StringComparison.Ordinal), label + ": internal exception marker is absent from the user-facing summaries");
        Assert(error.InnerException is null || !shell.ActivitySummary.Contains(error.InnerException.Message, StringComparison.Ordinal),
            label + ": inner technical error does not leak into the status summary");
    }
    private static void VerifyOutcomeLocalization(EditorActivityState state, string icon, string source, string label)
    {
        string original = EditorLocalization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "zh-CN", "en", "ja" })
            {
                EditorLocalization.SetLanguage(language); PumpLayout();
                AssertActivity(state, icon, label + " " + language);
                string expected = EditorLocalization.Text(source);
                Assert(shell!.ActivitySummary == expected && shell.OperationStatus == expected,
                    label + " " + language + ": outcome retains its localizable source after the operation ends");
            }
        }
        finally { EditorLocalization.SetLanguage(original); PumpLayout(); }
    }
    private static void VerifyStatusLayout(string label)
    {
        var status = window!.FindControl<TextBlock>("OperationStatusText") ?? throw new InvalidOperationException("Missing status line.");
        Assert(status.TextTrimming != TextTrimming.None && WithinWindow(status) && WithinWindow(statusIcon!)
            && !RootBounds(status).Intersects(RootBounds(statusIcon!))
            && (!Visible(cancelButton!) || !RootBounds(status).Intersects(RootBounds(cancelButton!)))
            && (!Visible(detailsButton!) || !RootBounds(status).Intersects(RootBounds(detailsButton!))),
            label + ": trimmed status occupies its own column without overlapping icons or actions");
    }
    private static object? InvokeShell(string name, object?[]? arguments = null) =>
        (typeof(EditorShellViewModel).GetMethod(name, InstanceMembers) ?? throw new MissingMethodException(typeof(EditorShellViewModel).FullName, name))
        .Invoke(shell, arguments);
    private static T InvokeShell<T>(string name, object?[]? arguments = null) => (T)(InvokeShell(name, arguments)
        ?? throw new InvalidOperationException(name + " returned null."));
    private static void SetCanCancel(bool value) => (typeof(EditorShellViewModel).GetProperty("CanCancelOperation", InstanceMembers)
        ?.GetSetMethod(true) ?? throw new MissingMethodException(typeof(EditorShellViewModel).FullName, "set_CanCancelOperation"))
        .Invoke(shell, [value]);

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
    private static void WriteReport(string? failure) => File.WriteAllText(Path.Combine(output, "loading-experience-checks.json"), JsonSerializer.Serialize(new
    {
        success = failure is null, passed = Checks.Count, checks = Checks, skipped = Skipped, realWork = WorkEvidence, captures = Captures,
        states = StateEvidence, observedStates = ObservedStates.Select(value => value.ToString()), setupCheckpoints = SetupCheckpoints,
        expectedApp, expectedAppSha256 = expectedAppSha, loadedProductAssemblies = LoadedAssemblies,
        inputHashes = Originals, publishedPayloadFilesChecked = ProductHashes.Count,
        clockSampling = "The only LoadingDots AnimationProgressProperty and static PathIcon geometry/opacity/transform are sampled on distinct native RequestAnimationFrame callbacks only while the real worker is active; no injected wait in real-work samples.",
        contractGates = "TaskCompletionSource used only for separate completion/commit/cancellation/layout state contracts. Cancellation uses the real top button handler with an owned CTS; no OS pointer input is claimed.",
        diagnosticTaskLabels = "Probe labels identify measured operations and are not formal localized UI copy. Presentation states are asserted against EditorActivityState and real state APIs, never guessed from translated text.",
        previewRetryScope = "Real shell state APIs verify that ordinary preview busy/idle notifications preserve failure, explicit BeginPreviewAttempt clears only stale preview failure, repeated retry failure remains visible, and unrelated operation failure survives retry. No fallback FramePresented event or GPU recovery is simulated or claimed.",
        screenshotScope = "Avalonia RenderTargetBitmap of the formal editor controls; native child HWND viewport omitted. No full-window GPU capture or visual model approval claimed.",
        graphicsScope = "SceneViewport.IsSuspended=true before Show; the resulting initial unpresentable preview-wait state is explicitly cleared in this test only. Native first-frame waiting is outside this probe. No D3D12 build, render or GPU readback requested.",
        startupScope = "Ordinary startup first presentation/cancellation remains covered by EditorStartupAnimationProbe. This probe covers workbench loading experience only.", failure
    }, Json));
    private sealed record FrameSample(double ElapsedMilliseconds, double RenderMilliseconds, double TopProgress,
        bool IsBusy, bool IsLoading, bool TopActive, bool TopVisible, EditorActivityState State,
        string? IconGeometry, double IconOpacity, string? IconTransform, int IconStyleCount);
}
