using System.Globalization;
using System.Text;
using Avalonia.Threading;

namespace Mmd.Modern.App;

/// <summary>
/// Last-chance exception handling. The editor has many async void UI handlers; without a hook an
/// exception escaping any of them terminated the process with no record. UI-thread exceptions are
/// written to <c>Logs/Crash</c> and marked handled so the open workspace survives; exceptions on
/// other threads cannot be recovered, but are still recorded before the runtime terminates.
/// </summary>
internal static class EditorCrashLog
{
    private static int installed;

    internal static string Directory => Path.Combine(EditorUserDataPaths.Current.RootDirectory, "Logs", "Crash");

    internal static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) TryWrite("domain", error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryWrite("unobserved-task", args.Exception);
            args.SetObserved();
        };
    }

    private static int uiInstalled;

    /// <summary>Call once the Avalonia dispatcher exists (framework initialization).</summary>
    internal static void InstallUiThread()
    {
        if (Interlocked.Exchange(ref uiInstalled, 1) != 0) return;
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            TryWrite("ui", args.Exception);
            args.Handled = true;
        };
    }

    internal static string? TryWrite(string source, Exception error, string? directory = null)
    {
        try
        {
            string root = directory ?? Directory;
            System.IO.Directory.CreateDirectory(root);
            string path = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + source + "-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            var text = new StringBuilder();
            text.AppendLine("Source: " + source);
            text.AppendLine("UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            text.Append(ExportDiagnosticException.Readable(error));
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            System.Diagnostics.Trace.WriteLine("Unhandled " + source + " exception recorded at " + path + ": " + error);
            return path;
        }
        catch (Exception logging)
        {
            System.Diagnostics.Trace.WriteLine("Unhandled " + source + " exception (log write failed: " + logging.Message + "): " + error);
            return null;
        }
    }
}
