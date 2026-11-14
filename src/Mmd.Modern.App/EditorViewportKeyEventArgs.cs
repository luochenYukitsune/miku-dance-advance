using Avalonia.Input;

namespace Mmd.Modern.App;

/// <summary>A key pressed while the native editor viewport owns keyboard focus.</summary>
public sealed class EditorViewportKeyEventArgs(Key key, KeyModifiers modifiers) : EventArgs
{
    public Key Key { get; } = key;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool Handled { get; set; }
}
