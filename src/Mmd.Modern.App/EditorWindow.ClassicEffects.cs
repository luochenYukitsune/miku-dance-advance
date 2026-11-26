using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private readonly Queue<string> classicEffectLog = new();
    private DispatcherTimer? classicEffectTimer;
    private Dictionary<string, DateTime> classicEffectStamps = new(StringComparer.OrdinalIgnoreCase);
    private bool classicReloadRunning;
    private string? classicMappingExportPath;
    private Guid classicMappingDocument;
    private bool classicEffectsDialogOpen;
    private EditorClassicDocumentState? classicStampMetadata;
    private DateTime nextClassicReloadAttempt;
    private readonly SemaphoreSlim classicMappingWrite = new(1, 1);
    private static string MmeText(string key) => EditorLocalization.Text("Mda.Text.Mme." + key);

    private void InitializeClassicEffects()
    {
        classicEffectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        classicEffectTimer.Tick += PollClassicEffects;
        classicEffectTimer.Start();
        Closed += (_, _) => { classicEffectTimer.Stop(); classicEffectTimer.Tick -= PollClassicEffects; };
    }

    private void RefreshClassicEffectsMenu()
    {
        bool classic = _shell?.Workspace.RenderEngine is EditorClassicRenderEngine;
        this.FindControl<MenuItem>("ClassicEffectsMenu")!.IsVisible = classic;
        if (_shell?.Workspace.RenderEngine is EditorClassicRenderEngine engine)
            this.FindControl<MenuItem>("ClassicEffectsEnabled")!.IsChecked = engine.EffectsEnabled;
        bool project = classic && _shell?.Workspace.ProjectMetadata is EditorClassicDocumentState;
        bool hasMapping = classic && _shell?.Workspace.ProjectMetadata is EditorClassicDocumentState { Emm: not null };
        this.FindControl<MenuItem>("ClassicEffectAssignment")!.IsEnabled = hasMapping;
        this.FindControl<MenuItem>("ClassicEffectsReload")!.IsEnabled = hasMapping;
        this.FindControl<MenuItem>("ClassicEffectsAutoReload")!.IsEnabled = hasMapping;
        this.FindControl<MenuItem>("ClassicMappingAutoSave")!.IsEnabled = hasMapping;
        this.FindControl<MenuItem>("ClassicToonSettings")!.IsEnabled = project;
        this.FindControl<MenuItem>("ClassicToonSlots")!.IsEnabled = project;
        if (classicMappingDocument != _shell?.Workspace.DocumentId)
        {
            classicMappingExportPath = null;
            this.FindControl<MenuItem>("ClassicMappingAutoSave")!.IsChecked = false;
        }
    }

    private async void OpenClassicEffects(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Workspace.RenderEngine is not EditorClassicRenderEngine || classicEffectsDialogOpen) return;
        classicEffectsDialogOpen = true; shell.StopPlayback();
        try
        {
            await new EditorClassicEffectsWindow(this, shell.Workspace).ShowDialog(this);
            await AutoSaveClassicMappingAsync();
        }
        catch (Exception error) { LogClassicEffect(error.ToString()); await ShowErrorAsync(error); }
        finally { classicEffectsDialogOpen = false; }
    }

    private void ToggleClassicEffects(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Workspace.RenderEngine is not EditorClassicRenderEngine engine) return;
        engine.EffectsEnabled = !engine.EffectsEnabled;
        RefreshClassicEffectsMenu(); _mappedRevision = -1; _mappedBindingRevision = -1; RefreshViewport(this, EventArgs.Empty);
    }

    private async void ReloadClassicEffects(object? sender, RoutedEventArgs args) => await ReloadClassicEffectsAsync(true);
    private async Task ReloadClassicEffectsAsync(bool showError)
    {
        if (classicReloadRunning || classicEffectsDialogOpen || _shell is not { IsBusy: false } shell
            || shell.Workspace.ProjectMetadata is not EditorClassicDocumentState { Emm: { } emm }) return;
        classicReloadRunning = true;
        try
        {
            shell.StopPlayback();
            await shell.RunOperationAsync(MmeText("Preparing"), () => shell.Workspace.ApplyClassicAssignmentsAsync(emm.Document, true));
            LogClassicEffect(MmeText("Reloaded")); CaptureClassicEffectStamps();
        }
        catch (Exception error) { nextClassicReloadAttempt = DateTime.UtcNow.AddSeconds(10); LogClassicEffect(error.ToString()); if (showError) await ShowErrorAsync(error); }
        finally { classicReloadRunning = false; }
    }

    private void CaptureClassicEffectStamps()
    {
        classicEffectStamps = new(StringComparer.OrdinalIgnoreCase);
        if (_shell?.Workspace.ProjectMetadata is not EditorClassicDocumentState state) return;
        classicStampMetadata = state;
        foreach (var node in state.Tree?.DependencyGraph?.Nodes ?? [])
            if (File.Exists(node.OriginAbsolutePath)) classicEffectStamps[node.OriginAbsolutePath] = File.GetLastWriteTimeUtc(node.OriginAbsolutePath);
    }
    private async void PollClassicEffects(object? sender, EventArgs args)
    {
        try { await PollClassicEffectsAsync(); }
        catch (Exception error) { nextClassicReloadAttempt = DateTime.UtcNow.AddSeconds(10); LogClassicEffect(error.ToString()); }
    }
    private async Task PollClassicEffectsAsync()
    {
        if (_shell?.Workspace.RenderEngine is not EditorClassicRenderEngine || _shell.IsBusy || _shell.IsPlaying
            || classicEffectsDialogOpen || classicReloadRunning || DateTime.UtcNow < nextClassicReloadAttempt
            || this.FindControl<MenuItem>("ClassicEffectsAutoReload")!.IsChecked != true) return;
        if (!ReferenceEquals(classicStampMetadata, _shell.Workspace.ProjectMetadata))
        {
            CaptureClassicEffectStamps(); return;
        }
        bool changed = false;
        foreach (string path in classicEffectStamps.Keys.ToArray())
        {
            if (!File.Exists(path)) continue;
            DateTime next = File.GetLastWriteTimeUtc(path);
            if (next != classicEffectStamps[path]) changed = true;
        }
        if (changed) await ReloadClassicEffectsAsync(false);
    }

    private async void ChooseClassicMappingAutoSave(object? sender, RoutedEventArgs args)
    {
        var menu = this.FindControl<MenuItem>("ClassicMappingAutoSave")!;
        if (!menu.IsChecked) { classicMappingExportPath = null; return; }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = MmeText("MappingFile"), DefaultExtension = "emm",
                SuggestedFileName = "effects.emm", ShowOverwritePrompt = true, FileTypeChoices = [new("MMEffect") { Patterns = ["*.emm"] }] });
            classicMappingExportPath = file?.TryGetLocalPath();
            classicMappingDocument = _shell?.Workspace.DocumentId ?? Guid.Empty;
            menu.IsChecked = classicMappingExportPath is not null;
            await AutoSaveClassicMappingAsync();
        }
        catch (Exception error) { menu.IsChecked = false; classicMappingExportPath = null; LogClassicEffect(error.ToString()); await ShowErrorAsync(error); }
    }
    private async void OnClassicMappingChanged(object? sender, EventArgs args)
    {
        try { await AutoSaveClassicMappingAsync(); }
        catch (Exception error)
        {
            this.FindControl<MenuItem>("ClassicMappingAutoSave")!.IsChecked = false;
            LogClassicEffect(error.ToString()); await ShowErrorAsync(error);
        }
    }
    private async Task AutoSaveClassicMappingAsync()
    {
        await classicMappingWrite.WaitAsync();
        try
        {
        if (classicMappingExportPath is not { } path || classicMappingDocument != _shell?.Workspace.DocumentId
            || this.FindControl<MenuItem>("ClassicMappingAutoSave")!.IsChecked != true
            || _shell?.Workspace.ProjectMetadata is not EditorClassicDocumentState { Emm: { } emm }) return;
        // Explicitly selected sidecar only. PMC embeds EMM independently of this switch.
        byte[] bytes = Mmd.Modern.Rendering.D3D9.Compatibility.EmmDocumentEditor.ExportAbsolutePaths(emm.ImportPlan);
        await Task.Run(() =>
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".mda-emm-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        }
        finally { classicMappingWrite.Release(); }
    }
    private void LogClassicEffect(string message)
    {
        classicEffectLog.Enqueue($"{DateTimeOffset.Now:HH:mm:ss}  {message[..Math.Min(message.Length, 16000)]}");
        while (classicEffectLog.Count > 100) classicEffectLog.Dequeue();
    }
    private async void ShowClassicEffectLog(object? sender, RoutedEventArgs args)
    {
        var window = new Window { Title = MmeText("Log"), Width = 720, Height = 440, MinWidth = 480, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant,
            FontFamily = FontFamily, Background = Background, Foreground = Foreground,
            Content = new TextBox { Text = string.Join(Environment.NewLine + Environment.NewLine, classicEffectLog),
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(16) } };
        await window.ShowDialog(this);
    }
    private async void ShowClassicEffectAbout(object? sender, RoutedEventArgs args)
        => await EditorDialogs.ChooseAsync(this, "MMEffect", MmeText("AboutText"), [MmeText("Close")]);
}
