using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Mmd.Modern.App;

public partial class EditorModelBrowser : UserControl
{
    public EditorModelBrowser() => InitializeComponent();

    public event EventHandler? OpenModelPropertiesRequested;

    private void ModelListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Delete || args.KeyModifiers != KeyModifiers.None
            || DataContext is not EditorModelBrowserViewModel browser
            || browser.SelectedModel is not { } model) return;
        args.Handled = RemoveModel(browser, model);
    }

    private void ModelListDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is not EditorModelBrowserViewModel browser) return;
        if (args.Source is Visual source
            && (source.FindAncestorOfType<Button>() is not null
                || source.FindAncestorOfType<ScrollBar>() is not null))
            return;
        if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not EditorModelItem model
            || !browser.Models.Contains(model)) return;
        browser.SelectedModel = model;
        OpenModelPropertiesRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private static bool RemoveModel(EditorModelBrowserViewModel browser, EditorModelItem model)
    {
        if (!browser.Models.Contains(model)) return false;
        if (browser.RemoveModelCommand is not { } command || !command.CanExecute(model)) return false;
        command.Execute(model);
        return true;
    }

    private void BoneSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (DataContext is EditorModelBrowserViewModel browser && args.AddedItems.Count > 0
            && args.AddedItems[0] is EditorElementNode node)
            browser.SelectBoneNode(node);
    }

    private void MorphSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (DataContext is EditorModelBrowserViewModel browser && args.AddedItems.Count > 0
            && args.AddedItems[0] is EditorElementNode node)
            browser.SelectMorphNode(node);
    }
}
