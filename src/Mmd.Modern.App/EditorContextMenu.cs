using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Mmd.Modern.App;

internal static class EditorContextMenu
{
    public static MenuItem Item(string label, Action execute, bool enabled = true, string? gesture = null)
    {
        var item = CreateItem(label, gesture);
        item.IsEnabled = enabled;
        item.Click += (_, _) => execute();
        return item;
    }

    public static MenuItem Item(string label, ICommand command, string? gesture = null)
    {
        var item = CreateItem(label, gesture);
        item.Command = command;
        return item;
    }

    private static MenuItem CreateItem(string label, string? gesture) => new()
    {
        Header = EditorLocalization.Text(label),
        InputGesture = gesture is null ? null : KeyGesture.Parse(gesture)
    };

    // Programmatic Open bypasses Avalonia's Opening event. Normalize immediately before opening.
    internal static Control[] VisibleItems(IEnumerable<Control> items)
    {
        List<Control> result = [];
        Separator? pending = null;
        foreach (var item in items.Where(i => i.IsVisible))
        {
            if (item is Separator separator) { pending = separator; continue; }
            if (pending is not null && result.Count > 0) result.Add(pending);
            result.Add(item);
            pending = null;
        }
        return [.. result];
    }

    public static void Open(Control owner, Point? position, params Control[] items)
    {
        var visible = VisibleItems(items);
        if (visible.Length == 0 || TopLevel.GetTopLevel(owner) is null) return;
        var menu = new ContextMenu { ItemsSource = visible, Placement = PlacementMode.Bottom };
        if (position is { } point) menu.PlacementRect = new Rect(point, default(Size));
        Popup.SetTakesFocusFromNativeControl(menu, true);
        menu.Open(owner);
    }
}
