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
    /// <summary>Raised on the UI thread once per distinct exception signature, with the written log path (or null).
    /// The editor window shows it so a failed operation is not silent; repeats are counted, not re-announced.</summary>
    internal static event Action<Exception, string?>? UiExceptionRecorded;

    /// <summary>Call once the Avalonia dispatcher exists (framework initialization).</summary>
    internal static void InstallUiThread()
    {
        if (Interlocked.Exchange(ref uiInstalled, 1) != 0) return;
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (!ShouldRecord("ui", args.Exception, out string? path)) return;
            try { UiExceptionRecorded?.Invoke(args.Exception, path); }
            catch (Exception notify) { System.Diagnostics.Trace.WriteLine("Crash notice failed: " + notify); }
        };
    }

    /// <summary>Most recent crash files kept; older ones are deleted when a new one is written.</summary>
    internal const int MaxFiles = 50;
    private static readonly object throttleGate = new();
    private static readonly Dictionary<string, int> seen = new(StringComparer.Ordinal);

    /// <summary>An exception on a per-frame path (playback timer, viewport refresh) used to write a new file every
    /// time. Only the first occurrence of each signature (source, type, throw site) is written; repeats are counted
    /// and the count is written at every power of two, so the log stays bounded but the frequency is visible.</summary>
    internal static bool ShouldRecord(string source, Exception error, out string? path, string? directory = null)
    {
        string signature = source + "|" + error.GetType().FullName + "|" + (error.TargetSite?.ToString() ?? error.Message);
        int count;
        lock (throttleGate)
        {
            seen.TryGetValue(signature, out count); count++;
            if (seen.Count < 1024 || seen.ContainsKey(signature)) seen[signature] = count;
        }
        path = null;
        if (count > 1 && (count & (count - 1)) != 0) return false;
        path = TryWrite(count == 1 ? source : $"{source}-repeat{count}", error, directory);
        return count == 1;
    }

    internal static void ResetThrottleForTests() { lock (throttleGate) seen.Clear(); }

    private static void Prune(string root)
    {
        try
        {
            var files = new DirectoryInfo(root).GetFiles("*.txt").OrderByDescending(file => file.Name, StringComparer.Ordinal).Skip(MaxFiles);
            foreach (var file in files) file.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
            Prune(root);
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
