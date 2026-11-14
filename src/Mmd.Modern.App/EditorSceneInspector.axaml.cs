using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Mmd.Modern.App;

public sealed partial class EditorSceneInspector : UserControl
{
    public EditorSceneInspector() => AvaloniaXamlLoader.Load(this);
}
