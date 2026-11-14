using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mmd.Modern.Application;
using System.Globalization;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>A detached edit. The owner persists and applies the returned snapshot after Apply succeeds.</summary>
public partial class EditorPreferencesWindow : Window
{
    private static readonly string[] Languages = ["zh-CN", "en", "ja"];
    private readonly EditorPreferences _source;
    private bool _browsing;
    private bool _qualityOpen;
    private string _validationSource = "";
    private Exception? _validationError;
    private bool _diagnosticsBusy;
    private bool _diagnosticsHasReport;
    private string? _diagnosticsExportPath;
    private string? _diagnosticsError;
    // Resolve application-authored errors again when the open window changes language.
    private string? _diagnosticsErrorKey;

    public Func<Window, Task>? OpenPreviewQualityAsync { get; init; }
    public Func<EditorPreviewRuntimeInfo>? GetPreviewRuntimeInfo { get; init; }
    public Func<Task<IReadOnlyDictionary<string, string>>>? GetInteractionDiagnosticsMetadata { get; init; }

    public EditorPreferencesWindow() : this(new EditorPreferences()) { }

    public EditorPreferencesWindow(EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        _source = preferences with { };
        InitializeComponent();
        PreferencesThemeBox.ItemsSource = new[] { Text("黑"), Text("白") };
        PreferencesThemeBox.SelectedIndex = _source.ThemeIndex;
        PreferencesLanguageBox.ItemsSource = new[] { "简体中文", "English", "日本語" };
        PreferencesLanguageBox.SelectedIndex = Array.IndexOf(Languages, _source.Language);
        PreferencesWorldGuidesBox.IsChecked = _source.ShowWorldGuides;
        PreferencesInvertCameraMouseXBox.IsChecked = _source.InvertCameraMouseX;
        PreferencesInvertCameraMouseYBox.IsChecked = _source.InvertCameraMouseY;
        PreferencesFfmpegBox.Text = _source.FfmpegPath;
        PreferencesFfprobeBox.Text = _source.FfprobePath;
        SetBudget(PreferencesPmxBudgetBox, _source.PmxMaxBytes, wholeGibibytes: true);
        SetBudget(PreferencesPmaPackageBudgetBox, _source.PmaPackageMaxBytes, wholeGibibytes: true);
        SetBudget(PreferencesPmaExpandedBudgetBox, _source.PmaExpandedMaxBytes, wholeGibibytes: true);
        SetBudget(PreferencesPmaEntryBudgetBox, _source.PmaEntryMaxBytes, wholeGibibytes: true);
        RecoveryEnabledBox.IsChecked = _source.AutoSaveEnabled;
        RecoveryIntervalBox.Value = _source.AutoSaveIntervalMinutes;
        RecoveryVersionsBox.Value = _source.RecoveryMaxVersions;
        SetBudget(RecoveryBudgetBox, _source.RecoveryMaxTotalBytes);
        RecoveryBudgetBox.Maximum = 100;
        RecoveryDirectoryBox.Text = _source.RecoveryDirectory;
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is { } owner)
            {
                FontFamily = owner.FontFamily;
                FontSize = owner.FontSize;
                Resources["OutputFont"] = owner.FontFamily;
            }
            RefreshPreviewQuality();
            _diagnosticsHasReport = InteractionDiagnostics.Snapshot().StartedTimestamp != 0;
            RefreshInteractionDiagnostics();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Close(null); args.Handled = true;
        };
        Observe(this, static window => window.RefreshLocalization());
        RefreshPreviewQuality();
        RefreshCameraMousePreferences();
        RefreshInteractionDiagnostics();
    }

    private async void StartInteractionDiagnostics(object? sender, RoutedEventArgs args)
    {
        if (_diagnosticsBusy || _browsing || _qualityOpen || InteractionDiagnostics.IsEnabled
            || GetInteractionDiagnosticsMetadata is not { } capture) return;
        _diagnosticsBusy = true; _diagnosticsError = _diagnosticsErrorKey = null;
        RefreshInteractionDiagnostics();
        try
        {
            var metadata = await capture();
            if (!IsVisible) return;
            InteractionDiagnostics.Start(new() { SampleThreadCpu = true, SampleThreads = true, BridgePoseEventSources = true }, metadata);
            _diagnosticsHasReport = true; _diagnosticsExportPath = null;
            InteractionDiagnostics.Record("diagnostics-start");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _diagnosticsError = error.Message; }
        finally { _diagnosticsBusy = false; RefreshInteractionDiagnostics(); }
    }

    private void StopInteractionDiagnostics(object? sender, RoutedEventArgs args)
    {
        if (_diagnosticsBusy) return;
        InteractionDiagnostics.Record("diagnostics-stop");
        InteractionDiagnostics.Stop();
        _diagnosticsError = _diagnosticsErrorKey = null;
        RefreshInteractionDiagnostics();
    }

    private async void ExportInteractionDiagnostics(object? sender, RoutedEventArgs args)
    {
        if (_diagnosticsBusy || _browsing || _qualityOpen || !_diagnosticsHasReport) return;
        if (!StorageProvider.CanSave)
        {
            _diagnosticsErrorKey = "Mda.Text.Preferences.Diagnostics.SaveDialogUnavailable";
            RefreshInteractionDiagnostics(); return;
        }
        _diagnosticsBusy = true; _diagnosticsError = _diagnosticsErrorKey = null;
        RefreshInteractionDiagnostics();
        try
        {
            var selected = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = Text("Mda.Text.Preferences.Diagnostics.ExportTitle"),
                SuggestedFileName = "MDA-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json",
                DefaultExtension = "json", ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (selected is null) return;
            if (selected.TryGetLocalPath() is not { } path)
            {
                _diagnosticsErrorKey = "Mda.Text.Preferences.Diagnostics.LocalFileRequired";
                return;
            }
            await InteractionDiagnostics.ExportAsync(path);
            _diagnosticsExportPath = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _diagnosticsError = error.Message; }
        finally { _diagnosticsBusy = false; RefreshInteractionDiagnostics(); }
    }

    private void RefreshInteractionDiagnostics()
    {
        bool active = InteractionDiagnostics.IsEnabled;
        PreferencesDiagnosticsHeading.Text = Text("Mda.Text.Preferences.Diagnostics.Heading");
        PreferencesDiagnosticsHint.Text = Text("Mda.Text.Preferences.Diagnostics.Hint");
        PreferencesDiagnosticsStart.Content = Text("Mda.Text.Preferences.Diagnostics.Start");
        PreferencesDiagnosticsStop.Content = Text("Mda.Text.Preferences.Diagnostics.Stop");
        PreferencesDiagnosticsExport.Content = Text("Mda.Text.Preferences.Diagnostics.Export");
        bool available = !_diagnosticsBusy && !_browsing && !_qualityOpen;
        ApplyPreferencesButton.IsEnabled = available;
        PreferencesQualityButton.IsEnabled = available && OpenPreviewQualityAsync is not null;
        PreferencesFfmpegBrowse.IsEnabled = available;
        PreferencesFfprobeBrowse.IsEnabled = available;
        RecoveryDirectoryBrowse.IsEnabled = available;
        PreferencesDiagnosticsStart.IsEnabled = available && !active && GetInteractionDiagnosticsMetadata is not null;
        PreferencesDiagnosticsStop.IsEnabled = available && active;
        PreferencesDiagnosticsExport.IsEnabled = available && _diagnosticsHasReport;
        if (_diagnosticsBusy) PreferencesDiagnosticsStatus.Text = Text("Mda.Text.Preferences.Diagnostics.Busy");
        else if ((_diagnosticsErrorKey is { } key ? Text(key) : _diagnosticsError) is { } error)
            PreferencesDiagnosticsStatus.Text = Text("Mda.Text.Preferences.Diagnostics.ActionFailedPrefix") + error;
        else if (_diagnosticsExportPath is { } path) PreferencesDiagnosticsStatus.Text = Text("Mda.Text.Preferences.Diagnostics.ReportSavedPrefix") + path
            + (active ? Text("Mda.Text.Preferences.Diagnostics.RecordingContinuesSuffix") : "");
        else if (active) PreferencesDiagnosticsStatus.Text = Text("Mda.Text.Preferences.Diagnostics.Recording");
        else PreferencesDiagnosticsStatus.Text = _diagnosticsHasReport
            ? Text("Mda.Text.Preferences.Diagnostics.Stopped")
            : Text("Mda.Text.Preferences.Diagnostics.NotStarted");
    }

    private void RefreshCameraMousePreferences()
    {
        PreferencesCameraMouseHeading.Text = Text("Mda.Text.Preferences.CameraMouse.Heading");
        PreferencesInvertCameraMouseXBox.Content = Text("Mda.Text.Preferences.CameraMouse.InvertX");
        PreferencesInvertCameraMouseYBox.Content = Text("Mda.Text.Preferences.CameraMouse.InvertY");
        PreferencesCameraMouseHint.Text = Text("Mda.Text.Preferences.CameraMouse.Hint");
    }

    private async void AdjustPreviewQuality(object? sender, RoutedEventArgs args)
    {
        if (_qualityOpen || _browsing || _diagnosticsBusy || OpenPreviewQualityAsync is not { } open) return;
        _qualityOpen = true;
        PreferencesQualityButton.IsEnabled = false;
        ApplyPreferencesButton.IsEnabled = false;
        RefreshInteractionDiagnostics();
        try { await open(this); }
        catch (Exception error) { SetValidationMessage(error.Message); }
        finally
        {
            _qualityOpen = false;
            ApplyPreferencesButton.IsEnabled = true;
            RefreshPreviewQuality();
            RefreshInteractionDiagnostics();
        }
    }

    private void RefreshPreviewQuality()
    {
        PreferencesQualityTab.Header = Text("画质");
        PreferencesQualityHeading.Text = Text("工作预览画质");
        PreferencesQualityHint.Text = Text("调整边缘平滑、精细采样、超分、间接光照和柔光。只影响本机工作预览，图片与视频在输出窗口单独设置。");
        PreferencesQualityButton.Content = Text("调整预览画质…");
        PreferencesQualityButton.IsEnabled = !_qualityOpen && !_diagnosticsBusy && !_browsing && OpenPreviewQualityAsync is not null;
        PreferencesRuntimeHeading.Text = Text("当前视口");
        var runtime = GetPreviewRuntimeInfo?.Invoke();
        PreferencesDeviceSummary.Text = runtime?.DeviceSummary ?? Text("视口尚未启动，设备信息将在首次呈现后显示。");
        PreferencesResolutionSummary.Text = runtime?.ResolutionSummary ?? "";
        PreferencesPerformanceSummary.Text = runtime?.PerformanceSummary ?? "";
        PreferencesPerformanceHint.Text = runtime?.PerformanceHint ?? "";
    }

    private static void SetBudget(NumericUpDown control, long bytes, bool wholeGibibytes = false)
    {
        // Preserve exact existing byte values behind the displayed GiB number.
        control.Minimum = 1m / EditorPreferences.Gibibyte;
        control.Maximum = (decimal)long.MaxValue / EditorPreferences.Gibibyte;
        control.Increment = 1;
        if (wholeGibibytes) control.TextConverter = new WholeGibibyteTextConverter(control);
        control.Value = (decimal)bytes / EditorPreferences.Gibibyte;
    }

    private sealed class WholeGibibyteTextConverter(NumericUpDown control) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string text) return null;
            // Keep the control's existing-value round trip, including sub-GiB limits shown as 0.
            // This is also used by EditorNumericUpDown's validation on focus changes.
            return string.Equals(text, Format(control.Value), StringComparison.Ordinal)
                ? control.Value
                : decimal.Parse(text, control.ParsingNumberStyle, control.NumberFormat);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Format(value);

        private string? Format(object? value) => value is decimal number
            ? decimal.Truncate(number).ToString("0", control.NumberFormat)
            : null;
    }

    private static long ReadBudget(NumericUpDown control, string label)
    {
        if (control.Value is not { } value || value <= 0)
            throw Error("{0}必须大于 0。", Label(label));
        decimal bytes = decimal.Round(value * EditorPreferences.Gibibyte, 0, MidpointRounding.AwayFromZero);
        if (bytes < 1 || bytes > long.MaxValue) throw Error("{0}超出支持范围。", Label(label));
        return checked((long)bytes);
    }

    private static int ReadRecoveryWhole(NumericUpDown control, string invalidMessage)
    {
        if (control.Value is not { } value || decimal.Truncate(value) != value
            || value < control.Minimum || value > control.Maximum)
            throw Error(invalidMessage);
        return checked((int)value);
    }

    private static long ReadRecoveryBudget(NumericUpDown control)
    {
        if (control.Value is not { } value || value <= 0 || value > 100)
            throw Error("Mda.Text.RecoveryPreferences.InvalidBudget");
        decimal bytes = decimal.Round(value * EditorPreferences.Gibibyte, 0, MidpointRounding.AwayFromZero);
        if (bytes < 1 || bytes > 100 * EditorPreferences.Gibibyte)
            throw Error("Mda.Text.RecoveryPreferences.InvalidBudget");
        return checked((long)bytes);
    }

    private void ApplyPreferences(object? sender, RoutedEventArgs args)
    {
        if (_browsing || _qualityOpen || _diagnosticsBusy) return;
        if (!RecoveryIntervalBox.IsInputValid || !RecoveryVersionsBox.IsInputValid || !RecoveryBudgetBox.IsInputValid
            || this.GetVisualDescendants().OfType<Control>().Any(control =>
            control.IsEffectivelyEnabled && DataValidationErrors.GetHasErrors(control)))
        { SetValidationMessage("请先修正标出的输入值，再应用设置。"); return; }
        try
        {
            if (PreferencesLanguageBox.SelectedIndex is < 0 or > 2)
                throw Error("请选择界面语言。");
            var result = _source with
            {
                ThemeIndex = PreferencesThemeBox.SelectedIndex,
                Language = Languages[PreferencesLanguageBox.SelectedIndex],
                ShowWorldGuides = PreferencesWorldGuidesBox.IsChecked == true,
                InvertCameraMouseX = PreferencesInvertCameraMouseXBox.IsChecked == true,
                InvertCameraMouseY = PreferencesInvertCameraMouseYBox.IsChecked == true,
                FfmpegPath = (PreferencesFfmpegBox.Text ?? "").Trim(),
                FfprobePath = (PreferencesFfprobeBox.Text ?? "").Trim(),
                PmxMaxBytes = ReadBudget(PreferencesPmxBudgetBox, "PMX 上限"),
                PmaPackageMaxBytes = ReadBudget(PreferencesPmaPackageBudgetBox, "PMA 整包上限"),
                PmaExpandedMaxBytes = ReadBudget(PreferencesPmaExpandedBudgetBox, "PMA 展开上限"),
                PmaEntryMaxBytes = ReadBudget(PreferencesPmaEntryBudgetBox, "PMA 单资源上限"),
                AutoSaveEnabled = RecoveryEnabledBox.IsChecked == true,
                AutoSaveIntervalMinutes = ReadRecoveryWhole(RecoveryIntervalBox, "Mda.Text.RecoveryPreferences.InvalidInterval"),
                RecoveryMaxVersions = ReadRecoveryWhole(RecoveryVersionsBox, "Mda.Text.RecoveryPreferences.InvalidVersions"),
                RecoveryMaxTotalBytes = ReadRecoveryBudget(RecoveryBudgetBox),
                RecoveryDirectory = (RecoveryDirectoryBox.Text ?? "").Trim()
            };
            result.Validate(requireExecutableFiles: true);
            Close(result);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or NotSupportedException)
        { _validationError = error; RefreshValidation(); }
    }

    private void CancelPreferences(object? sender, RoutedEventArgs args) => Close(null);

    private async void BrowseRecoveryDirectory(object? sender, RoutedEventArgs args)
    {
        if (_browsing || _diagnosticsBusy || _qualityOpen) return;
        if (!StorageProvider.CanPickFolder)
        { SetValidationMessage("Mda.Text.RecoveryPreferences.PickerUnavailable"); return; }
        _browsing = true;
        RefreshInteractionDiagnostics();
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = Text("Mda.Text.RecoveryPreferences.BrowseDirectory"),
                AllowMultiple = false
            });
            if (!IsVisible || folders.FirstOrDefault() is not { } selected) return;
            if (selected.TryGetLocalPath() is not { } path)
            { SetValidationMessage("Mda.Text.RecoveryPreferences.InvalidDirectory"); return; }
            (_source with { RecoveryDirectory = path }).Validate();
            RecoveryDirectoryBox.Text = path;
            SetValidationMessage("");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (error is ArgumentException) { _validationError = error; RefreshValidation(); }
            else SetValidationMessage("Mda.Text.RecoveryPreferences.PickerUnavailable");
        }
        finally { _browsing = false; RefreshInteractionDiagnostics(); }
    }

    private async void BrowseExecutable(object? sender, RoutedEventArgs args)
    {
        if (_browsing || _diagnosticsBusy || _qualityOpen || sender is not Button button) return;
        if (!StorageProvider.CanOpen)
        { SetValidationMessage("当前无法打开文件选择器，请直接填写完整 .exe 路径。"); return; }
        _browsing = true;
        button.IsEnabled = false;
        ApplyPreferencesButton.IsEnabled = false;
        RefreshInteractionDiagnostics();
        try
        {
            string tool = button.Tag as string == "ffprobe" ? "ffprobe" : "FFmpeg";
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = Format("选择 {0} 可执行文件", tool),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Text("可执行文件")) { Patterns = ["*.exe"] }]
            });
            if (files.FirstOrDefault() is not { } selected) return;
            if (selected.TryGetLocalPath() is not { } path)
            { SetValidationMessage("请选择本机 .exe 文件，或直接填写完整路径。"); return; }
            if (button.Tag as string == "ffprobe") PreferencesFfprobeBox.Text = path;
            else PreferencesFfmpegBox.Text = path;
            SetValidationMessage("");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { SetValidationMessage("暂时无法打开文件选择器，请直接填写完整路径。"); }
        finally
        {
            _browsing = false;
            button.IsEnabled = true;
            ApplyPreferencesButton.IsEnabled = true;
            RefreshInteractionDiagnostics();
        }
    }

    private void SetValidationMessage(string message)
    {
        _validationSource = message;
        _validationError = null;
        RefreshValidation();
    }

    private void RefreshValidation()
    {
        string message = _validationError is { } error ? Text(error.Message) : Text(_validationSource);
        PreferencesValidationMessage.Text = message;
        PreferencesValidationMessage.IsVisible = message.Length > 0;
    }

    private void RefreshLocalization()
    {
        // Replacing labels must preserve the detached theme choice and every unfinished input.
        int selectedTheme = PreferencesThemeBox.SelectedIndex;
        PreferencesThemeBox.ItemsSource = new[] { Text("黑"), Text("白") };
        PreferencesThemeBox.SelectedIndex = selectedTheme;
        RefreshPreviewQuality();
        RefreshCameraMousePreferences();
        RefreshInteractionDiagnostics();
        RefreshValidation();
    }
}
