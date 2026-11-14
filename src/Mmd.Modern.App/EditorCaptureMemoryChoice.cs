using System.ComponentModel;

namespace Mmd.Modern.App;

/// <summary>Stable items keep the selected memory policy when their translated labels change.</summary>
public sealed class EditorCaptureMemoryChoice(string key) : INotifyPropertyChanged
{
    public string Label => EditorLocalization.Text(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    internal static IReadOnlyList<EditorCaptureMemoryChoice> Create() =>
        [new("自动"), new("Mda.Text.CaptureMemory.PreferSpeed"), new("Mda.Text.CaptureMemory.LowMemory")];
}
