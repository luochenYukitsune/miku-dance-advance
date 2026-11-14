using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Mmd.Modern.App;

public partial class EditorModelBrowser : UserControl
{
    public EditorModelBrowser() => InitializeComponent();

    private void ModelListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Delete || args.KeyModifiers != KeyModifiers.None
            || DataContext is not EditorModelBrowserViewModel browser
            || browser.SelectedModel is not { } model) return;
        args.Handled = RemoveModel(browser, model);
    }

    private void ReplaceModelFromContext(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not EditorModelBrowserViewModel browser
            || sender is not MenuItem { CommandParameter: EditorModelItem model }
            || !browser.Models.Contains(model)) return;
        browser.SelectedModel = model;
        if (browser.ReplaceModelCommand is not { } command || !command.CanExecute(model)) return;
        command.Execute(model); args.Handled = true;
    }

    private void RemoveModelFromContext(object? sender, RoutedEventArgs args)
    {
        if (DataContext is EditorModelBrowserViewModel browser
            && sender is MenuItem { CommandParameter: EditorModelItem model })
            args.Handled = RemoveModel(browser, model);
    }

    private static bool RemoveModel(EditorModelBrowserViewModel browser, EditorModelItem model)
    {
        if (!browser.Models.Contains(model)) return false;
        // A context menu belongs to its clicked row, which need not be the selected row.
        browser.SelectedModel = model;
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
