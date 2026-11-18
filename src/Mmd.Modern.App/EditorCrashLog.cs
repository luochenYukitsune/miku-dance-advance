using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
            // Mark observed first: it must happen even when the write is throttled or fails.
            args.SetObserved();
            // Many tasks failing at the same site raise one event each; throttle by the inner exception's type and throw
            // site so one fault cannot fill the retained logs. The first write still records the whole aggregate.
            Exception key = args.Exception.InnerExceptions.Count == 1 ? args.Exception.InnerExceptions[0] : args.Exception;
            ShouldRecord("unobserved-task", key, out string? _, write: (source, directory) => TryWrite(source, args.Exception, directory));
        };
    }

    private static int uiInstalled;
    /// <summary>Raised on the UI thread for every UI exception, with its signature and the log path written for it (or
    /// null when this occurrence was throttled). Each window decides whether it has already shown that signature, so a
    /// fault that happened before a window could display it is still shown once the window is ready.</summary>
    internal static event Action<Exception, string, string?>? UiExceptionRecorded;

    /// <summary>Call once the Avalonia dispatcher exists (framework initialization).</summary>
    internal static void InstallUiThread()
    {
        if (Interlocked.Exchange(ref uiInstalled, 1) != 0) return;
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            args.Handled = true;
            string signature = Signature("ui", args.Exception);
            ShouldRecord("ui", args.Exception, out string? path);
            try { UiExceptionRecorded?.Invoke(args.Exception, signature, path); }
            catch (Exception notify) { SafeTrace("Crash notice failed: " + Describe(notify)); }
        };
    }

    /// <summary>Most recent crash files kept; older ones are deleted when a new one is written.</summary>
    internal const int MaxFiles = 50;
    private static readonly object throttleGate = new();
    private static readonly Dictionary<string, int> seen = new(StringComparer.Ordinal);
    internal const int MaxSignatures = 1024;
    private const string OverflowSignature = "<overflow>";

    /// <summary>An exception on a per-frame path (playback timer, viewport refresh) used to write a new file every
    /// time. Only the first occurrence of each signature (source, type, throw site) is written; repeats are counted
    /// and the count is written at every power of two, so the log stays bounded but the frequency is visible.</summary>
    /// <returns>True when this is the first occurrence of the signature (or of the shared overflow bucket).</returns>
    internal static bool ShouldRecord(string source, Exception error, out string? path, string? directory = null,
        Func<string, string?, string?>? write = null)
    {
        string signature = Signature(source, error);
        int count;
        lock (throttleGate)
        {
            // Past the table bound, new signatures share one overflow counter so they are still throttled.
            if (!seen.ContainsKey(signature) && seen.Count >= MaxSignatures) signature = OverflowSignature;
            seen.TryGetValue(signature, out count); count++;
            seen[signature] = count;
        }
        path = null;
        if (count > 1 && (count & (count - 1)) != 0) return false;
        string name = count == 1 ? source : $"{source}-repeat{count}";
        path = write is null ? TryWrite(name, error, directory) : write(name, directory);
        return count == 1;
    }

    /// <summary>Source, exception type and throw site. TargetSite.ToString() omits the declaring type, so Location
    /// includes it; A.Refresh() and B.Refresh() stay distinct.</summary>
    internal static string Signature(string source, Exception error) => source + "|" + error.GetType().FullName + "|" + Location(error);

    /// <summary>Signatures one window has already shown. Bounded and recency-ordered, independent of the log's overflow
    /// counter, so two new faults after the log table filled are both shown once.</summary>
    internal sealed class NoticeFilter(int capacity = 256)
    {
        private readonly LinkedList<string> order = new();
        private readonly Dictionary<string, LinkedListNode<string>> shown = new(StringComparer.Ordinal);
        /// <summary>Call only once the notice was actually delivered.</summary>
        internal bool MarkShown(string signature)
        {
            if (shown.TryGetValue(signature, out var node)) { order.Remove(node); order.AddFirst(node); return false; }
            shown.Add(signature, order.AddFirst(signature));
            if (shown.Count > capacity) { shown.Remove(order.Last!.Value); order.RemoveLast(); }
            return true;
        }
        /// <summary>A hit also counts as recent use, so a fault that keeps recurring is not evicted by a burst of new
        /// ones and shown again.</summary>
        internal bool WasShown(string signature)
        {
            if (!shown.TryGetValue(signature, out var node)) return false;
            order.Remove(node); order.AddFirst(node);
            return true;
        }
    }

    /// <summary>Throw-site identity for throttling. Never throws: a DynamicMethod's MetadataToken (and other
    /// metadata) can throw, and an exception from this handler would skip logging and notifying the original one.</summary>
    internal static string Location(Exception error)
    {
        try
        {
            var site = error.TargetSite;
            if (site is null) return error.Message;
            string method = site.ToString() ?? site.Name;
            string type = site.DeclaringType?.AssemblyQualifiedName ?? "<dynamic>";
            try { return $"{type}::{method}#{site.MetadataToken}"; }
            catch (InvalidOperationException) { return $"{type}::{method}"; }
        }
        // The final fallback touches no exception member: a custom Message (or TargetSite) getter may itself throw,
        // and anything thrown here escapes the handler before the exception is logged or reported.
        catch (Exception) { return UnknownLocation; }
    }
    internal const string UnknownLocation = "<unknown site>";

    /// <summary>Exception text for trace output that never throws, even when ToString/Message are overridden badly.</summary>
    private static string Describe(Exception error)
    {
        try { return error.ToString(); }
        catch (Exception) { return error.GetType().FullName ?? "Exception"; }
    }

    internal static void ResetThrottleForTests() { lock (throttleGate) seen.Clear(); }

    /// <summary>Names this class writes: yyyyMMdd-HHmmss-fff-source-xxxxxxxx.txt. Other files in the folder are never
    /// counted or deleted.</summary>
    private static readonly Regex CrashLogName = new(@"^[0-9]{8}-[0-9]{6}-[0-9]{3}-.+-[0-9a-f]{8}\.txt$", RegexOptions.CultureInvariant);

    /// <summary>Keeps MaxFiles crash logs: first the ones this process wrote most recently (up to MaxFiles, newest
    /// write first), then the newest others by name. Name order is only a hint (the clock can move back, another
    /// process may write names that sort later), so a path this class returned is never removed by a later prune
    /// until MaxFiles newer logs were written, whichever thread wrote them and however their names sort.</summary>
    private static void Prune(string root)
    {
        try
        {
            var files = new DirectoryInfo(root).GetFiles("*.txt").Where(file => CrashLogName.IsMatch(file.Name)).ToList();
            var present = files.Select(file => file.FullName).ToHashSet(StringComparer.Ordinal);
            var kept = recentWrites.Where(present.Contains).Take(MaxFiles).ToHashSet(StringComparer.Ordinal);
            var others = files.Where(file => !kept.Contains(file.FullName))
                .OrderByDescending(file => file.Name, StringComparer.Ordinal).Skip(MaxFiles - kept.Count);
            foreach (var file in others) file.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void SafeTrace(string message)
    {
        try { System.Diagnostics.Trace.WriteLine(message); }
        catch (Exception) { }
    }

    /// <summary>Serializes write + prune inside this process and guards <see cref="recentWrites"/>.</summary>
    private static readonly object writeGate = new();
    /// <summary>Full paths this process wrote, newest first, at most MaxFiles. Prune keeps these before any file chosen by
    /// name, so a returned path survives the prunes of writers that run after it (on any thread) until MaxFiles newer
    /// logs exist; a caller can show it without a lease.</summary>
    private static readonly LinkedList<string> recentWrites = new();

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
            path = Path.GetFullPath(path);
            lock (writeGate)
            {
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                recentWrites.AddFirst(path);
                while (recentWrites.Count > MaxFiles) recentWrites.RemoveLast();
                Prune(root);
            }
            // A throwing TraceListener must not turn a completed write into a failure.
            SafeTrace("Unhandled " + source + " exception recorded at " + path + ": " + Describe(error));
            return path;
        }
        catch (Exception logging)
        {
            SafeTrace("Unhandled " + source + " exception (log write failed: " + Describe(logging) + "): " + Describe(error));
            return null;
        }
    }
}
