using System.ComponentModel;

namespace Mmd.Modern.App;

/// <summary>Stable choice identity while its visible label follows the application language.</summary>
public sealed class EditorSurfaceOrderChoice(string key) : INotifyPropertyChanged
{
    public string Label => EditorLocalization.Text(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    internal static IReadOnlyList<EditorSurfaceOrderChoice> Create() =>
        [new("Mda.Text.SurfaceOrder.OpaqueFirst"), new("Mda.Text.SurfaceOrder.Original")];
}
