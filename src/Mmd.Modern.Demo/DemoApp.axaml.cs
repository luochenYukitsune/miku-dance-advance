using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Mmd.Modern.Demo;

/// <summary>
/// Developer frame viewer / engine demo host. It lives outside the shipped editor so the
/// editor assembly does not carry these tools; it reuses the editor's real rendering stack
/// but never its startup path.
/// </summary>
public sealed partial class DemoApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            // The original Veldrid PMD preview, kept as a developer reference outside the editor.
            if (args.Contains("--legacy-preview"))
                desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            else
            {
                var options = FrameViewportOptions.Parse(args);
                if (!options.Enabled)
                    throw new ArgumentException("The demo host expects --frame-viewer or --legacy-preview.");
                FrameViewportApplication.Start(desktop, options);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
