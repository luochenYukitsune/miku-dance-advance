using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Protected detached edit; the owner alone publishes the returned coherent workspace transaction.</summary>
public partial class EditorModelPropertiesWindow : Window
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly EditorProjectWorkspace? workspace;
    private string jsonSnapshot = "";
    private string? importedProfilePath;
    private bool browsing;
    private bool closed;
    private CancellationTokenSource? copyPathFeedbackCts;
    private bool copyPathFeedbackActive;
    private bool copyPathFeedbackInitialized;
    private Func<string>? windowValidation;
    private string lastWindowValidation = "";

    public EditorModelPropertiesWindow() => InitializeComponent();

    public EditorModelPropertiesWindow(EditorProjectWorkspace workspace, Guid objectId)
        : this(workspace, objectId, 0, null) { }

    /// <summary>Same detached edit, optionally focused on one tab or material row; navigation is best-effort and never fatal.</summary>
    public EditorModelPropertiesWindow(EditorProjectWorkspace workspace, Guid objectId, int initialTabIndex = 0, int? initialMaterialIndex = null) : this()
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        DataContext = new EditorModelPropertiesViewModel(workspace, objectId);
        SetJson(ViewModel.ExportProfile());
        Observe(this, static window => window.RefreshLocalization());
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is { } owner) { FontFamily = owner.FontFamily; Resources["OutputFont"] = owner.FontFamily; }
        };
        Closed += (_, _) =>
        {
            closed = true;
            copyPathFeedbackCts?.Cancel();
            copyPathFeedbackCts?.Dispose();
            copyPathFeedbackCts = null;
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || browsing) return;
            Close(null); args.Handled = true;
        };
        // The AXAML defines exactly three pages (model properties, materials, JSON); unknown page requests fall back to page 0.
        ModelPropertiesTabs.SelectedIndex = initialTabIndex is >= 0 and <= 2 ? initialTabIndex : 0;
        // A stale right-rail material index must not fail the dialog: keep the view model's own default row instead.
        if (initialMaterialIndex is int materialIndex
            && ViewModel.Materials.FirstOrDefault(item => item.LocalMaterialIndex == materialIndex) is { } material)
            ViewModel.SelectedMaterial = material;
    }

    public EditorModelPropertiesViewModel ViewModel => (EditorModelPropertiesViewModel)DataContext!;
    private bool HasUnloadedJson => !string.Equals(ModelProfileJsonBox.Text ?? "", jsonSnapshot, StringComparison.Ordinal);

    private void ApplyProperties(object? sender, RoutedEventArgs args)
    {
        if (browsing) return;
        if (this.GetVisualDescendants().OfType<Control>().Any(control => control.IsEffectivelyEnabled && control.IsEffectivelyVisible && DataValidationErrors.GetHasErrors(control)))
        { SetValidationMessage("请先修正标出的输入值，再应用属性。"); return; }
        if (HasUnloadedJson)
        { SetValidationMessage("JSON 有尚未载入的修改，请先点“载入 JSON 草稿”，或“刷新为当前草稿”放弃这些文本修改。"); return; }
        if (ViewModel.TryCreate(out var draft)) Close(draft);
    }

    private void CancelProperties(object? sender, RoutedEventArgs args) { if (!browsing) Close(null); }

    private async void CopyModelPath(object? sender, RoutedEventArgs args)
    {
        if (browsing || closed) return;
        string path = ViewModel.SourcePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            SetValidationMessage("Mda.Text.Ux.ModelProps.CopyPathUnavailable");
            return;
        }
        try
        {
            await clipboard.SetTextAsync(path);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            if (!closed) SetValidationMessage("Mda.Text.Ux.ModelProps.CopyPathUnavailable");
            return;
        }
        if (closed) return;

        copyPathFeedbackCts?.Cancel();
        copyPathFeedbackCts?.Dispose();
        var cts = new CancellationTokenSource();
        copyPathFeedbackCts = cts;
        copyPathFeedbackInitialized = true;
        copyPathFeedbackActive = true;
        UpdateCopyPathButtonText();
        try
        {
            await Task.Delay(1800, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!closed && ReferenceEquals(copyPathFeedbackCts, cts))
        {
            copyPathFeedbackActive = false;
            UpdateCopyPathButtonText();
        }
    }

    private void UpdateCopyPathButtonText()
    {
        if (!copyPathFeedbackInitialized) return;
        CopyModelPathButton.Content = Text(copyPathFeedbackActive
            ? "Mda.Text.Ux.ModelProps.PathCopied"
            : "Mda.Text.Ux.ModelProps.CopyPath");
        ToolTip.SetTip(CopyModelPathButton, Text(copyPathFeedbackActive
            ? "Mda.Text.Ux.ModelProps.PathCopied"
            : "Mda.Text.Ux.ModelProps.CopyPathTooltip"));
    }

    private void QuarterOutline(object? sender, RoutedEventArgs args)
    { if (ViewModel.SelectedMaterial is { SupportsOutlineScaling: true } material) material.OutlinePercent = 25; }

    private void ResetOutline(object? sender, RoutedEventArgs args)
    { if (ViewModel.SelectedMaterial is { SupportsOutlineScaling: true } material) material.OutlinePercent = 100; }

    private void SampleBoneBasis(object? sender, RoutedEventArgs args)
    {
        try { ViewModel.CaptureBoneBasis(workspace?.Session ?? throw Error("当前工程已关闭。")); }
        catch (Exception error) when (IsExpected(error)) { SetValidationError(error); }
    }

    private void SampleModeBoneBasis(object? sender, RoutedEventArgs args)
    {
        try
        {
            var modeItem = (sender as Control)?.DataContext as EditorStyleModeItem;
            ViewModel.CaptureBoneBasis(workspace?.Session ?? throw Error("当前工程已关闭。"), modeItem);
        }
        catch (Exception error) when (IsExpected(error)) { SetValidationError(error); }
    }

    private void ResetStyleMode(object? sender, RoutedEventArgs args)
    {
        if ((sender as Control)?.DataContext is EditorStyleModeItem modeItem)
            modeItem.ResetToDefaults();
    }

    private void ResetPmxTreatment(object? sender, RoutedEventArgs args)
    {
        ViewModel.SelectedMaterial?.ResetPmxTreatment();
    }

    private void RefreshJson(object? sender, RoutedEventArgs args)
    {
        try { SetJson(ViewModel.ExportProfile()); SetValidationMessage("已用当前材质草稿刷新 JSON。"); }
        catch (Exception error) when (IsExpected(error)) { SetValidationError(error); }
    }

    private void ApplyJson(object? sender, RoutedEventArgs args)
    {
        try
        {
            string text = ModelProfileJsonBox.Text ?? "";
            if (Encoding.UTF8.GetByteCount(text) > D3D12MaterialBindingProfileJson.MaxProfileBytes * 4)
                throw Error("排版后的 JSON 超出 1 MiB 上限，请减少内容。");
            // Whitespace in the editable presentation does not consume the renderer's compact payload budget.
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
            ViewModel.ImportProfile(JsonSerializer.SerializeToUtf8Bytes(document.RootElement));
            SetJson(ViewModel.ExportProfile());
        }
        catch (Exception error) when (IsExpected(error)) { SetValidationError(error); }
    }

    private async void ImportMapping(object? sender, RoutedEventArgs args)
    {
        if (browsing) return;
        if (!StorageProvider.CanOpen) { SetValidationMessage("当前无法打开文件选择器，可在 JSON 区粘贴映射并载入。"); return; }
        SetBrowsing(true);
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = Text("导入此模型的材质映射"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Text("MDA 材质映射 JSON")) { Patterns = ["*.json"] }]
            });
            if (files.FirstOrDefault() is not { } file) return;
            await using var input = await file.OpenReadAsync();
            byte[] buffer = new byte[D3D12MaterialBindingProfileJson.MaxProfileBytes + 1];
            int used = 0;
            while (used < buffer.Length)
            {
                int count = await input.ReadAsync(buffer.AsMemory(used));
                if (count == 0) break;
                used += count;
            }
            if (used > D3D12MaterialBindingProfileJson.MaxProfileBytes) throw Error("映射文件超出 256 KiB 上限。");
            ViewModel.ImportProfile(buffer.AsMemory(0, used));
            importedProfilePath = file.TryGetLocalPath();
            SetJson(ViewModel.ExportProfile());
        }
        catch (Exception error) when (IsExpected(error)) { SetValidationError(error, "无法载入材质映射：{0}"); }
        finally { SetBrowsing(false); }
    }

    private async void ExportMapping(object? sender, RoutedEventArgs args)
    {
        if (browsing) return;
        if (HasUnloadedJson) { SetValidationMessage("请先载入 JSON 修改，再导出当前草稿。"); return; }
        if (!StorageProvider.CanSave) { SetValidationMessage("当前无法打开保存选择器，可从 JSON 区复制完整映射。"); return; }
        SetBrowsing(true);
        try
        {
            byte[] bytes = ViewModel.ExportProfile();
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = Text("另存材质映射副本"), SuggestedFileName = "model-material-mapping.json", DefaultExtension = "json", ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(Text("MDA 材质映射 JSON")) { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            string path = file.TryGetLocalPath() ?? throw Error("请选择本机 JSON 文件。");
            if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) throw Error("映射文件需以 .json 结尾。");
            if (IsSourcePath(path)) throw Error("请选择新的 JSON 文件；原模型、贴图和导入的映射保持只读。");
            // Open only after all draft and path validation has completed.
            await File.WriteAllBytesAsync(path, bytes);
            SetValidationMessage("材质映射副本已导出；工程仍需点击“应用”才会改变。");
        }
        catch (Exception error) when (IsExpected(error)) { SetValidationError(error, "无法导出材质映射：{0}"); }
        finally { SetBrowsing(false); }
    }

    private bool IsSourcePath(string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        bool Same(string? candidate)
        {
            if (candidate is null) return false;
            try { return string.Equals(output, Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
        }
        if (Same(importedProfilePath)) return true;
        foreach (var item in workspace?.Session?.Objects ?? [])
        {
            if (Same(item.SourcePath)) return true;
            string? folder = Path.GetDirectoryName(Path.GetFullPath(item.SourcePath));
            if (folder is null) continue;
            foreach (var texture in item.Source.Surface.Snapshot.Textures)
                if (Same(Path.Combine(folder, texture.Name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)))) return true;
        }
        return false;
    }

    private void SetJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        jsonSnapshot = JsonSerializer.Serialize(document.RootElement, DisplayJsonOptions);
        ModelProfileJsonBox.Text = jsonSnapshot;
    }
    private void SetBrowsing(bool value) { browsing = value; ModelPropertiesTabs.IsEnabled = !value; ApplyModelPropertiesButton.IsEnabled = !value; }
    private void SetValidationMessage(string source) => SetWindowValidation(() => Text(source));
    private void SetValidationError(Exception error, string? template = null) =>
        SetWindowValidation(() => template is null ? error.Message : Format(template, error.Message));
    private void SetWindowValidation(Func<string> message)
    {
        windowValidation = message;
        lastWindowValidation = message();
        ViewModel.SetValidationMessage(lastWindowValidation);
    }
    private void RefreshLocalization()
    {
        UpdateCopyPathButtonText();
        // Keep the JSON editor, material selection and unfinished numeric draft untouched.
        // A later validation produced by the view model takes precedence over a window message.
        if (windowValidation is null) return;
        string current = ViewModel.ValidationMessage;
        if (current != lastWindowValidation && current != Text(lastWindowValidation)) { windowValidation = null; return; }
        SetWindowValidation(windowValidation);
    }
    private static bool IsExpected(Exception error) => error is ArgumentException or InvalidOperationException or InvalidDataException
        or NotSupportedException or IOException or UnauthorizedAccessException or OverflowException or JsonException;
}
