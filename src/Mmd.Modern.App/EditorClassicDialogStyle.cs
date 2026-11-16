using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Mmd.Modern.App;

internal static class EditorClassicDialogStyle
{
    internal static void Apply(Window owner, Window dialog, params ListBox[] lists)
    {
        bool dark = owner.ActualThemeVariant != ThemeVariant.Light;
        IBrush Brush(string key, string fallback) => owner.TryFindResource(key, out var value) && value is IBrush brush
            ? brush : new SolidColorBrush(Color.Parse(fallback));
        var selected = Brush("EditorSelectedSurface", dark ? "#182633" : "#E3ECF3");
        foreach (var list in lists)
        {
            list.Background = Brush("EditorPanelSurface", dark ? "#13171C" : "#FFFFFF");
            list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(":selected"))
            { Setters = { new Setter(ListBoxItem.BackgroundProperty, selected) } });
            list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
            { Setters = { new Setter(ContentPresenter.BackgroundProperty, selected) } });
        }
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>())
        { Setters = { new Setter(Button.MinHeightProperty, 32d), new Setter(Button.PaddingProperty, new Thickness(12, 6)) } });
    }
}
