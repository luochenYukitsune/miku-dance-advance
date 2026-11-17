using Avalonia.Controls;

namespace Mmd.Modern.Demo;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => { if (DataContext is MainWindowViewModel vm) await vm.RenderAsync(); };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
