using Avalonia.Controls;

namespace Mmd.Modern.App;

public sealed partial class EditorSceneInspector : UserControl
{
    public EditorSceneInspector() => InitializeComponent();

    private void ResizeCameraFields(object? sender, SizeChangedEventArgs args)
    {
        bool stack = args.NewSize.Width < 440;
        Grid.SetColumn(CameraLensFields, stack ? 0 : 1);
        Grid.SetRow(CameraLensFields, stack ? 1 : 0);
        Grid.SetColumnSpan(CameraTransformFields, stack ? 2 : 1);
        Grid.SetColumnSpan(CameraLensFields, stack ? 2 : 1);
        CameraPoseGrid.RowSpacing = stack ? 6 : 0;
    }
}
