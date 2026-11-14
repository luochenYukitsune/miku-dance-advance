using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mmd.Modern.Application;

/// <summary>Opt-in, local interaction recording. Writers never wait for a reader or perform file I/O.</summary>
public static class InteractionDiagnostics
{
    private static Session? active;
    private static Session? previous;
    private static long nextId, nextSessionId;

    public static bool IsEnabled => Volatile.Read(ref active) is not null;
    /// <summary>A unique recording epoch; zero while disabled. Input/snapshot tags must match this epoch.</summary>
    public static long SessionId => Volatile.Read(ref active)?.Id ?? 0;
    public static long NextId() => Interlocked.Increment(ref nextId);

    public static void Start(InteractionDiagnosticsOptions? options = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var session = new Session(options ?? new(), metadata);
        var old = Interlocked.Exchange(ref active, session);
        old?.Dispose();
        Volatile.Write(ref previous, old);
    }

    public static void Stop()
    {
        var old = Interlocked.Exchange(ref active, null);
        if (old is null) return;
        old.Dispose();
        Volatile.Write(ref previous, old);
    }

    public static void Record(string stage, long correlationId = 0, long relatedId = 0, long value = 0,
        string? detail = null, long elapsedTicks = 0, long allocatedBytes = 0)
    {
        var session = Volatile.Read(ref active);
        if (session is null) return;
        session.Record(stage, correlationId, relatedId, value, detail, elapsedTicks, allocatedBytes, -1);
    }

    /// <summary>Records detached work only in its originating session, including a concurrent Start/Stop.</summary>
    public static void RecordForSession(long expectedSessionId, string stage, long correlationId = 0,
        long relatedId = 0, long value = 0, string? detail = null, long elapsedTicks = 0, long allocatedBytes = 0)
    {
        var session = Volatile.Read(ref active);
        if (session is null || session.Id != expectedSessionId) return;
        session.Record(stage, correlationId, relatedId, value, detail, elapsedTicks, allocatedBytes, -1);
    }

    public static Scope Measure(string stage, long correlationId = 0, long relatedId = 0, string? detail = null)
    {
        var session = Volatile.Read(ref active);
        return session is null ? default : new(session, stage, correlationId, relatedId, detail);
    }

    public static InteractionDiagnosticsReport Snapshot()
        => (Volatile.Read(ref active) ?? Volatile.Read(ref previous))?.Snapshot() ?? InteractionDiagnosticsReport.Empty;

    /// <summary>Snapshot copying, JSON encoding and writing all run on a worker. No network destination is used.</summary>
    public static Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var session = Volatile.Read(ref active) ?? Volatile.Read(ref previous);
        return Task.Run(async () =>
        {
            var report = session?.Snapshot() ?? InteractionDiagnosticsReport.Empty;
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = false }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Session? session;
        private readonly string? stage, detail;
        private readonly long correlationId, relatedId, started, allocated, cpu;
        private readonly int thread;

        internal Scope(Session session, string stage, long correlationId, long relatedId, string? detail)
        {
            this.session = session; this.stage = stage; this.detail = detail;
            this.correlationId = correlationId; this.relatedId = relatedId;
            thread = NativeThreadId();
            started = Stopwatch.GetTimestamp(); allocated = GC.GetAllocatedBytesForCurrentThread();
            cpu = session.Options.SampleThreadCpu ? ThreadCpu100Nanoseconds() : -1;
        }

        public void Dispose()
        {
            if (session is null) return;
            // A scope is a synchronous phase, not an async lifetime. Never subtract allocation/CPU from another thread.
            bool sameThread = thread == NativeThreadId();
            long cpuNow = cpu >= 0 && sameThread ? ThreadCpu100Nanoseconds() : -1;
            session.Record(stage!, correlationId, relatedId, 0, detail, Stopwatch.GetTimestamp() - started,
                sameThread ? GC.GetAllocatedBytesForCurrentThread() - allocated : -1,
                cpuNow >= cpu && cpu >= 0 ? cpuNow - cpu : -1);
        }
    }

    internal sealed class Session : IDisposable
    {
        private readonly object gate = new();
        private readonly InteractionDiagnosticEvent[] ring;
        private readonly Queue<InteractionDiagnosticWindow> windows = new();
        private readonly IReadOnlyDictionary<string, string> metadata;
        private readonly Timer? timer;
        private readonly ExistingEventBridge? bridge;
        private readonly InteractionThreadSampler? threadSampler;
        private readonly long slowTicks, beforeTicks, afterTicks;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        private int count, head, disposed;
        private long sequence, overwritten, contended, slowTriggers, droppedWindows;
        private long pendingTrigger, pendingStart, pendingEnd;
        internal InteractionDiagnosticsOptions Options { get; }
        internal long Id { get; } = Interlocked.Increment(ref nextSessionId);

        internal Session(InteractionDiagnosticsOptions options, IReadOnlyDictionary<string, string>? suppliedMetadata)
        {
            if (options.Capacity is < 16 or > 131_072) throw new ArgumentOutOfRangeException(nameof(options.Capacity));
            if (options.MaximumSlowWindows is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(options.MaximumSlowWindows));
            if (options.SlowThreshold < TimeSpan.Zero || options.BeforeSlowWindow < TimeSpan.Zero || options.AfterSlowWindow < TimeSpan.Zero
                || options.BeforeSlowWindow > TimeSpan.FromMinutes(1) || options.AfterSlowWindow > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(options));
            Options = options;
            ring = new InteractionDiagnosticEvent[options.Capacity];
            slowTicks = ToTicks(options.SlowThreshold); beforeTicks = ToTicks(options.BeforeSlowWindow); afterTicks = ToTicks(options.AfterSlowWindow);
            var values = new Dictionary<string, string>
            {
                ["processId"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["processorCount"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["os"] = RuntimeInformation.OSDescription,
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["applicationAssemblyVersion"] = typeof(InteractionDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown"
            };
            if (suppliedMetadata is not null)
                foreach (var item in suppliedMetadata.Take(64)) values[Limit(item.Key, 128)!] = Limit(item.Value, 4096)!;
            values["diagnosticsSessionId"] = Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (options.SampleThreads) values["threadSampling"] = "periodic 100ms ProcessThread observations; not ETW Ready/Wait/context-switch tracing; at most 64 queries per tick with 10ms soft work budget; OS snapshot/query calls cannot be preempted";
            metadata = values;
            if (options.CaptureSlowWindows) timer = new Timer(_ => CompleteWindow(false), null, 100, 100);
            if (options.BridgePoseEventSources) bridge = new ExistingEventBridge(this);
            if (options.SampleThreads) threadSampler = InteractionThreadSampler.Start(RecordThreadSample, RecordThreadBatch);
        }

        private void RecordThreadSample(InteractionThreadSample sample)
            => Record("thread.sample", 0, sample.NativeThreadId, sample.CpuDelta100Nanoseconds,
                $"periodic-nonETW; state={sample.State}; wait={sample.WaitReason?.ToString() ?? "n/a"}; intervalTicks={sample.ObservationIntervalTicks}; cpuTotal100ns={sample.CpuTotal100Nanoseconds}; sampledAt={sample.Timestamp}",
                0, 0, sample.CpuDelta100Nanoseconds);

        private void RecordThreadBatch(InteractionThreadSamplingBatch batch)
            => Record("thread.sample-batch", 0, 0, batch.PublishedThreads,
                $"periodic-nonETW; total={batch.TotalThreads}; inspected={batch.InspectedThreads}; failed={batch.FailedThreads}; evicted={batch.EvictedBaselines}; budgetExceeded={batch.BudgetExceeded}; error={batch.Error ?? "none"}",
                batch.ElapsedTicks, batch.AllocatedBytes, -1);

        internal void Record(string stage, long correlationId, long relatedId, long value, string? detail,
            long elapsedTicks, long allocatedBytes, long threadCpu100Nanoseconds)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (!Monitor.TryEnter(gate)) { Interlocked.Increment(ref contended); return; }
            try
            {
                if (disposed != 0) return;
                long now = Stopwatch.GetTimestamp();
                bool slow = elapsedTicks >= slowTicks;
                ring[head] = new(++sequence, now, NativeThreadId(), Environment.CurrentManagedThreadId,
                    Limit(stage, 128)!, correlationId, relatedId, value, Limit(detail, 512), elapsedTicks, allocatedBytes,
                    threadCpu100Nanoseconds, slow);
                head = (head + 1) % ring.Length;
                if (count < ring.Length) ++count; else ++overwritten;
                if (slow)
                {
                    ++slowTriggers;
                    if (Options.CaptureSlowWindows && pendingTrigger == 0)
                    {
                        pendingTrigger = now;
                        pendingStart = Math.Max(started, now - elapsedTicks - beforeTicks);
                        pendingEnd = now + afterTicks;
                    }
                }
            }
            finally { Monitor.Exit(gate); }
        }

        internal InteractionDiagnosticsReport Snapshot()
        {
            lock (gate)
            {
                return new(startedUtc, started, Stopwatch.Frequency, Options, new Dictionary<string, string>(metadata),
                    CopyEvents(), overwritten, Interlocked.Read(ref contended), slowTriggers, droppedWindows,
                    windows.ToArray(), Volatile.Read(ref disposed) == 0);
            }
        }

        private InteractionDiagnosticEvent[] CopyEvents()
        {
            var result = new InteractionDiagnosticEvent[count];
            int first = (head - count + ring.Length) % ring.Length;
            for (int i = 0; i < count; ++i) result[i] = ring[(first + i) % ring.Length];
            return result;
        }

        private void CompleteWindow(bool stopping)
        {
            // Background copying is bounded. A writer contending with it is dropped and counted, never blocked.
            lock (gate)
            {
                if (pendingTrigger == 0 || (!stopping && Stopwatch.GetTimestamp() < pendingEnd)) return;
                var all = CopyEvents();
                var selected = all.Where(item => item.Timestamp >= pendingStart && item.Timestamp <= pendingEnd).ToArray();
                bool beforeTruncated = overwritten > 0 && (all.Length == 0 || all[0].Timestamp > pendingStart);
                bool afterTruncated = stopping && Stopwatch.GetTimestamp() < pendingEnd;
                if (windows.Count == Options.MaximumSlowWindows) { windows.Dequeue(); ++droppedWindows; }
                windows.Enqueue(new(pendingTrigger, pendingStart, pendingEnd, beforeTruncated, afterTruncated, selected));
                pendingTrigger = 0;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            threadSampler?.Dispose(); timer?.Dispose(); bridge?.Dispose(); CompleteWindow(true);
        }
    }

    private sealed class ExistingEventBridge : EventListener
    {
        private readonly Session session;
        internal ExistingEventBridge(Session session)
        {
            this.session = session;
            foreach (var source in EventSource.GetSources()) EnableKnown(source);
        }
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            // EventListener's base constructor can call this before our fields are initialized.
            if (session is not null) EnableKnown(eventSource);
        }
        private void EnableKnown(EventSource source)
        {
            if (source.Name is "MDA-PosePreview" or "MDA-PoseEvaluation") EnableEvents(source, EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var payload = eventData.Payload;
            if (payload is null || payload.Count < 3) return;
            string stage = payload[0]?.ToString() ?? eventData.EventName ?? "phase";
            bool evaluation = eventData.EventSource.Name == "MDA-PoseEvaluation";
            int ticksIndex = evaluation ? 2 : 1, bytesIndex = evaluation ? 3 : 2;
            long ticks = payload.Count > ticksIndex && payload[ticksIndex] is long t ? t : 0;
            long bytes = payload.Count > bytesIndex && payload[bytesIndex] is long b ? b : 0;
            session.Record(stage, 0, 0, evaluation && payload.Count > 4 && payload[4] is int c ? c : 1,
                evaluation ? payload[1]?.ToString() : eventData.EventSource.Name, ticks, bytes, -1);
        }
    }

    private static string? Limit(string? value, int maximum) => value is { Length: var n } && n > maximum ? value[..maximum] : value;
    private static long ToTicks(TimeSpan duration) => (long)(duration.TotalSeconds * Stopwatch.Frequency);
    private static int NativeThreadId() => OperatingSystem.IsWindows() ? unchecked((int)GetCurrentThreadId()) : Environment.CurrentManagedThreadId;
    private static long ThreadCpu100Nanoseconds()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? kernel + user : -1;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
}

public sealed record InteractionDiagnosticsOptions
{
    public int Capacity { get; init; } = 16_384;
    public bool SampleThreadCpu { get; init; }
    public bool SampleThreads { get; init; }
    public bool BridgePoseEventSources { get; init; }
    public bool CaptureSlowWindows { get; init; } = true;
    public int MaximumSlowWindows { get; init; } = 4;
    public TimeSpan SlowThreshold { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan BeforeSlowWindow { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan AfterSlowWindow { get; init; } = TimeSpan.FromSeconds(1);
}

/// <param name="ThreadCpu100Nanoseconds">GetThreadTimes scope delta, or target-thread periodic CPU delta for thread.sample (target ID in RelatedId), or -1 when disabled/unavailable; not a scheduling wait classification.</param>
public readonly record struct InteractionDiagnosticEvent(long Sequence, long Timestamp, int NativeThreadId, int ManagedThreadId,
    string Stage, long CorrelationId, long RelatedId, long Value, string? Detail, long ElapsedTicks,
    long AllocatedBytes, long ThreadCpu100Nanoseconds, bool Slow);

public sealed record InteractionDiagnosticWindow(long TriggerTimestamp, long RequestedStart, long RequestedEnd,
    bool BeforeTruncated, bool AfterTruncated, InteractionDiagnosticEvent[] Events);

public sealed record InteractionDiagnosticsReport(DateTimeOffset StartedUtc, long StartedTimestamp, long TimestampFrequency,
    InteractionDiagnosticsOptions Options, IReadOnlyDictionary<string, string> Metadata, InteractionDiagnosticEvent[] Events,
    long OverwrittenEvents, long ContendedEvents, long SlowTriggers, long DroppedSlowWindows,
    InteractionDiagnosticWindow[] SlowWindows, bool Recording)
{
    internal static InteractionDiagnosticsReport Empty { get; } = new(default, 0, Stopwatch.Frequency, new(),
        new Dictionary<string, string>(), [], 0, 0, 0, 0, [], false);
}
