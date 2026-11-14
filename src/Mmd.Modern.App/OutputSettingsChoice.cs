using System.ComponentModel;

namespace Mmd.Modern.App;

/// <summary>A stable output option whose displayed label can change without replacing the selected item.</summary>
public sealed class OutputSettingsChoice(Func<string> label) : INotifyPropertyChanged
{
    public string Label => label();
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLabel() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}
