using Avalonia;

namespace Mmd.Modern.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WindowsAppIdentity.Initialize();
        EditorCrashLog.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
