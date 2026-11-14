using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using NativeAnimation = Avalonia.Animation.Animation;

namespace Mmd.Modern.App;

/// <summary>A branded busy surface for the real editor initialization, never a timed preview.</summary>
public sealed partial class EditorStartupWindow : Window
{
    private CancellationTokenSource? logoEntrance;
    private IDisposable? motionPreferenceObserver;
    private readonly DispatcherTimer copyFeedbackTimer;
    private string technicalDetails = string.Empty;
    private bool windowClosed;
    private string copyFeedback = "复制详情";

    public EditorStartupWindow()
    {
        InitializeComponent();
        copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        copyFeedbackTimer.Tick += (_, _) =>
        {
            copyFeedbackTimer.Stop();
            copyFeedback = "复制详情";
            RefreshLocalizedText();
        };
        EditorLocalization.Observe(this, static window => window.RefreshLocalizedText());
        RefreshLocalizedText();
        Opened += (_, _) =>
        {
            motionPreferenceObserver ??= EditorMotionPreferences.Observe(this, allowed =>
            {
                if (!allowed) StopLogoEntrance();
            });
            StartLogoEntrance();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StopLogoEntrance();
            motionPreferenceObserver?.Dispose();
            motionPreferenceObserver = null;
        };
        Closed += (_, _) =>
        {
            windowClosed = true;
            copyFeedbackTimer.Stop();
            StopAnimation();
            motionPreferenceObserver?.Dispose();
            motionPreferenceObserver = null;
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };
    }

    internal void ApplyPreferences(EditorPreferences preferences)
    {
        RequestedThemeVariant = preferences.ThemeIndex == 1 ? ThemeVariant.Light : ThemeVariant.Dark;
        // Startup applies preferences before its owner installs the language. SetLanguage is
        // idempotent and keeps this already-visible surface in the requested language as well.
        EditorLocalization.SetLanguage(preferences.Language);
        RefreshLocalizedText();
    }

    internal void ShowFailure(Exception error)
    {
        ResetUpdateSurface();
        StopAnimation();
        copyFeedbackTimer.Stop();
        copyFeedback = "复制详情";
        technicalDetails = error.ToString();
        this.FindControl<StackPanel>("BrandContent")!.IsVisible = false;
        this.FindControl<Grid>("NormalRail")!.IsVisible = false;
        this.FindControl<SelectableTextBlock>("FailureDetails")!.Text = technicalDetails;
        this.FindControl<Expander>("FailureDetailsExpander")!.IsExpanded = false;
        this.FindControl<StackPanel>("FailureContent")!.IsVisible = true;
        this.FindControl<Grid>("FailureActions")!.IsVisible = true;
        RefreshLocalizedText();
        this.FindControl<Button>("FailureClose")!.Focus();
    }

    private void RefreshLocalizedText()
    {
        this.FindControl<Button>("CloseStartup")!.Content = EditorLocalization.Text("Mda.Text.3fd47edce45b");
        this.FindControl<Button>("FailureClose")!.Content = EditorLocalization.Text("Mda.Text.3fd47edce45b");
        this.FindControl<TextBlock>("StartupStatus")!.Text = EditorLocalization.Text("正在准备编辑器…");
        this.FindControl<TextBlock>("EscapeHint")!.Text = EditorLocalization.Text("Esc 关闭");
        this.FindControl<TextBlock>("FailureTitle")!.Text = EditorLocalization.Text("MDA 无法启动");
        // Startup currently carries no typed failure category. A generic accurate summary is
        // preferable to inferring a device/settings failure from an English message or HRESULT.
        string summary = EditorLocalization.Text("启动 MDA 时出现问题。请查看技术详情，然后关闭窗口。");
        var message = this.FindControl<TextBlock>("FailureMessage")!;
        message.Text = summary;
        ToolTip.SetTip(message, summary);
        this.FindControl<Expander>("FailureDetailsExpander")!.Header = EditorLocalization.Text("技术详情");
        this.FindControl<TextBlock>("StartupStopped")!.Text = EditorLocalization.Text("启动已停止");
        var copy = this.FindControl<Button>("CopyFailureDetails")!;
        copy.Content = EditorLocalization.Text(copyFeedback);
        ToolTip.SetTip(copy, EditorLocalization.Text(copyFeedback == "复制失败"
            ? "未能复制详情。请在技术详情中选择并复制。" : "复制详情"));
    }

    private async void CopyDetailsClicked(object? sender, RoutedEventArgs args)
    {
        if (technicalDetails.Length == 0) return;
        copyFeedbackTimer.Stop();
        try
        {
            var clipboard = Clipboard;
            if (clipboard is null) throw new InvalidOperationException("Clipboard is unavailable.");
            await clipboard.SetTextAsync(technicalDetails);
            if (windowClosed) return;
            copyFeedback = "已复制";
        }
        catch (Exception error)
        {
            if (windowClosed) return;
            copyFeedback = "复制失败";
            this.FindControl<Expander>("FailureDetailsExpander")!.IsExpanded = true;
            System.Diagnostics.Trace.TraceWarning("MDA startup details clipboard: {0}", error.GetType().Name);
        }
        RefreshLocalizedText();
        copyFeedbackTimer.Start();
    }

    internal void StopAnimation()
    {
        this.FindControl<LoadingDots>("StartupDots")!.IsActive = false;
        StopLogoEntrance();
    }

    private void StopLogoEntrance()
    {
        var entrance = logoEntrance;
        logoEntrance = null;
        entrance?.Cancel();
    }

    private void StartLogoEntrance()
    {
        StopLogoEntrance();
        var dots = this.FindControl<LoadingDots>("StartupDots")!;
        if (windowClosed || !IsVisible || !IsEffectivelyEnabled || WindowState == WindowState.Minimized ||
            !dots.IsActive || !dots.AnimationsEnabled || !EditorMotionPreferences.ClientAnimationsEnabled()) return;
        logoEntrance = new CancellationTokenSource();
        var animation = new NativeAnimation
        {
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, .78d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } }
            }
        };
        // Opacity is one by default and remains visible throughout. Startup never awaits
        // this decorative entrance, so an already-ready editor can interrupt it immediately.
        _ = RunLogoEntranceAsync(animation, logoEntrance);
    }

    private async Task RunLogoEntranceAsync(NativeAnimation animation, CancellationTokenSource entrance)
    {
        try { await animation.RunAsync(this.FindControl<Border>("BrandLogo")!, entrance.Token); }
        catch (OperationCanceledException) when (entrance.IsCancellationRequested) { }
        catch (Exception error)
        {
            // The logo's default visible state is the recovery for an unavailable animation.
            System.Diagnostics.Trace.TraceWarning("MDA startup logo animation: {0}", error.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(logoEntrance, entrance)) logoEntrance = null;
            entrance.Dispose();
        }
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(args);
    }
}
