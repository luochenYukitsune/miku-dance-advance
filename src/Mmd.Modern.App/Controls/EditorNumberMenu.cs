using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Mmd.Modern.App.Controls;

public enum EditorNumberDraftField { None, MorphWeight, CameraFov, MainLightIntensity }

public static class EditorNumberMenu
{
    public static readonly AttachedProperty<EditorNumberDraftField> DraftProperty =
        AvaloniaProperty.RegisterAttached<Control, EditorNumberDraftField>("Draft", typeof(EditorNumberMenu));
    public static EditorNumberDraftField GetDraft(Control control) => control.GetValue(DraftProperty);
    public static void SetDraft(Control control, EditorNumberDraftField value) => control.SetValue(DraftProperty, value);

    private static EditorNumberDraft? CaptureDraft(Control owner)
    {
        if (GetDraft(owner) == EditorNumberDraftField.None || TopLevel.GetTopLevel(owner) is not EditorWindow { DataContext: EditorShellViewModel shell }) return null;
        if (!shell.HasProject || shell.IsBusy || shell.IsPlaying) return EditorNumberDraft.Unavailable;
        return (GetDraft(owner), owner.DataContext) switch
        {
            (EditorNumberDraftField.MorphWeight, EditorAnimationViewModel animation) when animation.SelectedMorphIndex is { } index =>
                animation.CaptureMorphMenu(shell.Workspace, index, () => animation.SelectedMorphIndex == index),
            (EditorNumberDraftField.MorphWeight, EditorMorphDockGroup group) when group.Selected is { } choice =>
                shell.Animation.CaptureMorphMenu(shell.Workspace, choice.Index, () => group.CanEdit && group.Selected == choice),
            (EditorNumberDraftField.CameraFov, EditorSceneInspectorViewModel scene) => scene.CaptureFovMenu(shell.Workspace),
            (EditorNumberDraftField.MainLightIntensity, EditorSceneInspectorViewModel scene) => scene.CaptureIntensityMenu(shell.Workspace),
            _ => EditorNumberDraft.Unavailable
        };
    }

    internal static void Attach(Control owner, Func<decimal?> read, Func<decimal?> defaultValue,
        Action<decimal> write, Func<bool>? canEdit = null)
    {
        owner.AddHandler(Control.ContextRequestedEvent, (_, args) =>
        {
            args.Handled = true;
            var position = args.TryGetPosition(owner, out var point) ? point : (Avalonia.Point?)null;
            var window = TopLevel.GetTopLevel(owner);
            var clipboard = window?.Clipboard;
            var target = owner.DataContext;
            var history = CaptureDraft(owner);
            bool Editable() => owner.IsEffectivelyEnabled && owner.IsEffectivelyVisible && window is not null
                && ReferenceEquals(TopLevel.GetTopLevel(owner), window) && ReferenceEquals(owner.DataContext, target)
                && (canEdit?.Invoke() ?? true) && (history?.CanWrite() ?? true);
            void Commit(decimal value)
            {
                if (!Editable()) return;
                if (history is null) write(value);
                else history.Write(() => write(value));
            }
            var current = read();
            var resetValue = defaultValue();
            var reset = EditorContextMenu.Item("Mda.Text.Menu.ResetValue", () =>
            {
                if (defaultValue() is { } value) Commit(value);
            }, Editable() && resetValue.HasValue && resetValue != current);
            reset.IsVisible = resetValue.HasValue;
            var copy = EditorContextMenu.Item("Mda.Text.Menu.CopyNumber", async () =>
            {
                if (clipboard is not null && current.HasValue)
                    await CopyAsync(clipboard, current.Value);
            }, clipboard is not null && current.HasValue);
            decimal? pasted = null;
            var paste = EditorContextMenu.Item("Mda.Text.Menu.PasteNumber", () =>
            {
                if (pasted.HasValue) Commit(pasted.Value);
            }, false);
            EditorContextMenu.Open(owner, position, reset, new Separator(), copy, paste);
            if (clipboard is not null && Editable()) _ = ReadPasteValueAsync();

            async Task ReadPasteValueAsync()
            {
                try
                {
                    pasted = Parse(await clipboard!.TryGetTextAsync());
                    paste.IsEnabled = pasted.HasValue && Editable();
                }
                catch (Exception error) when (ClipboardUnavailable(error)) { }
            }
        }, RoutingStrategies.Tunnel);
    }

    internal static decimal? Parse(string? text)
    {
        const NumberStyles style = NumberStyles.Float;
        return decimal.TryParse(text, style, CultureInfo.InvariantCulture, out var value)
            || decimal.TryParse(text, style, CultureInfo.CurrentCulture, out value) ? value : null;
    }

    private static async Task CopyAsync(IClipboard clipboard, decimal value)
    {
        try { await clipboard.SetTextAsync(value.ToString("G29", CultureInfo.InvariantCulture)); }
        catch (Exception error) when (ClipboardUnavailable(error)) { }
    }

    private static bool ClipboardUnavailable(Exception error) =>
        error is ExternalException or InvalidOperationException or NotSupportedException or IOException;
}
