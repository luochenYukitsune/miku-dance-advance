using Avalonia.Controls;

namespace Mmd.Modern.App;

public partial class EditorLightInspector
{
    private bool selectingLightKind;
    private void CommitLightKind(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshing || committing || selectingLightKind || recoveryRestoreDepth != 0 || !modernParameters
            || sender is not ComboBox { DataContext: EditorNativeLightItem item, SelectedIndex: >= 0 } choice
            || !ReferenceEquals(item, ViewModel?.SelectedLight)
            || source.Lights.FirstOrDefault(light => light.Id == item.Id) is not { } committed
            || (int)committed.Kind == choice.SelectedIndex) return;
        selectingLightKind = true;
        try
        {
            if (EditStartingRequested?.Invoke() == false) { choice.SelectedIndex = item.KindIndex; return; }
            item.KindIndex = choice.SelectedIndex;
            TryCommit();
        }
        finally { selectingLightKind = false; }
    }
}
