using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Mmd.Modern.App;

public sealed partial class App : Avalonia.Application
{
    private readonly EditorStartupConfiguration? startupConfiguration;
    public App() { }
    internal App(EditorStartupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        startupConfiguration = configuration;
    }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = FrameViewportOptions.Parse(desktop.Args ?? []);
            if (options.Enabled) FrameViewportApplication.Start(desktop, options);
            else if ((desktop.Args ?? []).Contains("--legacy-preview"))
                desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            else EditorStartupApplication.Start(desktop, startupConfiguration);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
