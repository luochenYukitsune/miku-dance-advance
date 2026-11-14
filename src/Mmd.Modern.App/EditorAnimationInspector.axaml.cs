using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Mmd.Modern.App;

public sealed partial class EditorAnimationInspector : UserControl
{
    public EditorAnimationInspector() => AvaloniaXamlLoader.Load(this);
}
