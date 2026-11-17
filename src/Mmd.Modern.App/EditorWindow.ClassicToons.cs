using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private sealed record ToonModel(Guid Id, string Name) { public override string ToString() => Name; }
    private sealed record ToonMaterial(int Index, string Label) { public override string ToString() => Label; }

    // 原版 Toon01–10 并列界面；作用对象默认是浏览器里当前选中的模型。
    private async void OpenClassicToonSlots(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Workspace.RenderEngine is not EditorClassicRenderEngine
            || shell.Workspace.Session is null || classicEffectsDialogOpen) return;
        classicEffectsDialogOpen = true; shell.StopPlayback();
        try { await new EditorClassicToonSlotsWindow(this, shell.Workspace, shell.Browser.SelectedModel?.ObjectId).ShowDialog(this); }
        catch (Exception error) { LogClassicEffect(error.ToString()); await ShowErrorAsync(error); }
        finally { classicEffectsDialogOpen = false; }
    }

    private async void OpenClassicToons(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Workspace.RenderEngine is not EditorClassicRenderEngine
            || shell.Workspace.Session is not { } session || classicEffectsDialogOpen) return;
        classicEffectsDialogOpen = true; shell.StopPlayback();
        var dialog = new Window { Title = MmeText("Toon"), Width = 620, Height = 490, MinWidth = 500, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            RequestedThemeVariant = ActualThemeVariant, FontFamily = FontFamily, FontSize = 14,
            UseLayoutRounding = true };
        EditorDialogTheme.Apply(dialog, this);
        var models = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = session.Objects.Select(value => new ToonModel(value.Id, value.Name)).ToArray() };
        var materials = new ListBox();
        var hint = new TextBlock { Text = MmeText("ToonHelp"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = new TextBlock { FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var replace = new Button { Content = MmeText("ToonReplace"), MinWidth = 110, Padding = new(12, 6) };
        var close = new Button { Content = MmeText("Close"), IsCancel = true, MinWidth = 88 };
        bool preparing = false;
        dialog.Closing += (_, e) => { if (preparing) e.Cancel = true; };
        close.Click += (_, _) => dialog.Close();
        models.SelectionChanged += (_, _) =>
        {
            if (models.SelectedItem is not ToonModel model) { materials.ItemsSource = null; return; }
            var pmx = session.CaptureAuthoredContent().Motions.Single(value => value.ObjectId == model.Id).Model;
            materials.ItemsSource = pmx.Materials.Select((value, index) => new ToonMaterial(index,
                $"{index}  {value.Name}  ·  " + (value.ToonMode == PmxToonMode.Common
                    ? $"Toon{value.ToonTextureIndex + 1:00}"
                    : value.ToonTextureIndex >= 0 ? pmx.Textures[value.ToonTextureIndex] : MmeText("DefaultToon")))).ToArray();
            materials.SelectedIndex = pmx.Materials.Count > 0 ? 0 : -1;
        };
        replace.Click += async (_, _) =>
        {
            if (models.SelectedItem is not ToonModel model || materials.SelectedItem is not ToonMaterial material) return;
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = MmeText("ToonReplace"),
                    FileTypeFilter = [new(MmeText("Images")) { Patterns = ["*.bmp", "*.png", "*.jpg", "*.jpeg", "*.tga", "*.dds"] }] });
                if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
                preparing = true; models.IsEnabled = materials.IsEnabled = replace.IsEnabled = close.IsEnabled = false;
                status.Text = MmeText("Preparing");
                await shell.Workspace.ApplyClassicToonAsync(model.Id, [material.Index], path);
                status.Text = MmeText("ToonApplied");
            }
            catch (Exception error) { status.Text = error.Message; LogClassicEffect(error.ToString()); }
            finally { preparing = false; models.IsEnabled = materials.IsEnabled = replace.IsEnabled = close.IsEnabled = true; }
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10, Children = { replace, close } };
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 12 };
        root.Children.Add(hint); root.Children.Add(models); Grid.SetRow(models, 1);
        root.Children.Add(materials); Grid.SetRow(materials, 2); root.Children.Add(status); Grid.SetRow(status, 3);
        root.Children.Add(footer); Grid.SetRow(footer, 4); dialog.Content = root;
        models.SelectedIndex = session.Objects.Count > 0 ? 0 : -1;
        try { await dialog.ShowDialog(this); } finally { classicEffectsDialogOpen = false; }
    }
}
