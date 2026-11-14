using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Mmd.Modern.Application.MotionEditing;

namespace Mmd.Modern.App;

/// <summary>Only returns a detached draft; the workspace owner supplies the single Undo boundary.</summary>
public partial class EditorRigSettingsWindow : Window
{
    public EditorRigSettingsWindow() => InitializeComponent();
    public EditorRigSettingsWindow(EditorProjectWorkspace workspace, Guid objectId, int? selectedBoneIndex = null) : this()
    {
        DataContext = new EditorRigSettingsViewModel(workspace, objectId, selectedBoneIndex);
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is { } owner) { FontFamily = owner.FontFamily; Resources["OutputFont"] = owner.FontFamily; }
        };
        KeyDown += (_, args) => { if (args.Key == Key.Escape && !ViewModel.IsBusy) { Close(null); args.Handled = true; } };
    }
    public EditorRigSettingsViewModel ViewModel => (EditorRigSettingsViewModel)DataContext!;
    public Func<MotionEditPlan, Task<bool>>? ConfirmOverwriteAsync
    { get => ViewModel.ConfirmOverwriteAsync; set => ViewModel.ConfirmOverwriteAsync = value; }
    private void RemoveOutsideParent(object? sender, RoutedEventArgs args) => ViewModel.RemoveSelectedOutsideParent();
    private void CancelRigSettings(object? sender, RoutedEventArgs args) { if (!ViewModel.IsBusy) Close(null); }
    private async void ApplyRigSettings(object? sender, RoutedEventArgs args)
    {
        if (await ViewModel.CreateDraftAsync() is { } draft) Close(draft);
    }
}
