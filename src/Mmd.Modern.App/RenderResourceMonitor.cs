using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mmd.Modern.App;

/// <summary>
/// Samples process resources off the UI thread, once per second by default. Each instance
/// belongs to one export. StopAsync drains an in-flight observation and closes native handles.
/// </summary>
public sealed class RenderResourceMonitor : IAsyncDisposable
{
    private readonly object lifecycleGate = new();
    private readonly object sampleGate = new();
    private readonly Func<RenderGpuMemorySnapshot?> gpuMemoryProvider;
    private readonly IRenderResourceSampleSource source;
    private readonly TimeProvider clock;
    private readonly TimeSpan interval;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool stopped;
    private bool disposed;
    private int sourceDisposed;
    private TimeSpan? previousCpuTime;
    private long previousCpuTimestamp;
    private ulong? peakWorkingSet;
    private ulong? peakVideoMemory;
    private long? peakAdapterLuid;
    private RenderResourceSnapshot? latestSnapshot;

    public RenderResourceMonitor(Func<RenderGpuMemorySnapshot?> gpuMemoryProvider, TimeSpan? interval = null)
        : this(gpuMemoryProvider, new ProcessRenderResourceSampleSource(), TimeProvider.System,
            interval ?? TimeSpan.FromSeconds(1)) { }

    internal RenderResourceMonitor(Func<RenderGpuMemorySnapshot?> gpuMemoryProvider,
        IRenderResourceSampleSource source, TimeProvider clock, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(gpuMemoryProvider);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));
        this.gpuMemoryProvider = gpuMemoryProvider;
        this.source = source;
        this.clock = clock;
        this.interval = interval;
    }

    /// <summary>Raised on the sampling worker. Subscribers must marshal UI changes themselves.</summary>
    public event Action<RenderResourceSnapshot>? SnapshotAvailable;
    public RenderResourceSnapshot? LatestSnapshot => Volatile.Read(ref latestSnapshot);
    public Task Completion { get { lock (lifecycleGate) return worker ?? Task.CompletedTask; } }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is not null || stopped)
                throw new InvalidOperationException("A resource monitor can only be started once per export.");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = cancellation.Token;
            worker = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        Task? completion;
        lock (lifecycleGate)
        {
            stopped = true;
            cancellation?.Cancel();
            completion = worker;
        }
        if (completion is not null) await completion.ConfigureAwait(false);
        DisposeSource();
    }

    public async ValueTask DisposeAsync()
    {
        lock (lifecycleGate) disposed = true;
        await StopAsync().ConfigureAwait(false);
        lock (lifecycleGate)
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var snapshot = CollectSnapshot();
                if (token.IsCancellationRequested) break;
                Volatile.Write(ref latestSnapshot, snapshot);
                // A diagnostics subscriber cannot fail the export or prevent native cleanup.
                foreach (var subscriber in SnapshotAvailable?.GetInvocationList() ?? [])
                {
                    if (token.IsCancellationRequested) break;
                    try { ((Action<RenderResourceSnapshot>)subscriber)(snapshot); }
                    catch (Exception) { }
                }
                await Task.Delay(interval, clock, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { DisposeSource(); }
    }

    internal RenderResourceSnapshot CollectSnapshot()
    {
        lock (sampleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref sourceDisposed) != 0, this);
            double? cpuPercent = null;
            ulong? workingSet = null;
            string? cpuReason = null, memoryReason = null;
            try
            {
                var cpuTime = source.ReadProcessCpuTime();
                var timestamp = clock.GetTimestamp();
                if (previousCpuTime is { } previous)
                {
                    double wallSeconds = clock.GetElapsedTime(previousCpuTimestamp, timestamp).TotalSeconds;
                    double cpuSeconds = (cpuTime - previous).TotalSeconds;
                    int processors = source.LogicalProcessorCount;
                    if (wallSeconds > 0 && cpuSeconds >= 0 && processors > 0)
                        cpuPercent = Math.Clamp(cpuSeconds / wallSeconds / processors * 100, 0, 100);
                    else cpuReason = "The process CPU interval or logical processor count is invalid.";
                }
                else cpuReason = "Two process CPU samples are required.";
                previousCpuTime = cpuTime;
                previousCpuTimestamp = timestamp;
            }
            catch (Exception error)
            {
                previousCpuTime = null;
                cpuReason = Failure("Process CPU time", error);
            }
            try
            {
                workingSet = source.ReadProcessWorkingSetBytes();
                peakWorkingSet = Maximum(peakWorkingSet, workingSet);
            }
            catch (Exception error) { memoryReason = Failure("Process working set", error); }

            RenderGpuMemorySnapshot? gpuMemory = null;
            string? videoReason = null;
            try { gpuMemory = gpuMemoryProvider(); }
            catch (Exception error) { videoReason = Failure("Renderer memory snapshot", error); }
            long? luid = gpuMemory?.AdapterLuid;
            ulong? usage = luid.HasValue ? gpuMemory?.ProcessLocalVideoMemoryUsageBytes : null;
            ulong? budget = luid.HasValue ? gpuMemory?.LocalVideoMemoryBudgetBytes : null;
            if (!luid.HasValue)
                videoReason ??= gpuMemory?.UnavailableReason ?? "The selected renderer's adapter is not available.";
            else if (!usage.HasValue || !budget.HasValue)
                videoReason = gpuMemory?.UnavailableReason ?? "DXGI local process memory is unavailable.";
            if (luid.HasValue)
            {
                if (peakAdapterLuid != luid) { peakAdapterLuid = luid; peakVideoMemory = null; }
                peakVideoMemory = Maximum(peakVideoMemory, usage);
            }

            var gpu = new RenderGpuUtilizationSample(null, "The selected renderer's adapter is not available.");
            if (luid is { } actualLuid)
            {
                try { gpu = source.ReadGpuUtilization(actualLuid); }
                catch (Exception error) { gpu = new(null, Failure("GPU engine counters", error)); }
            }
            if (gpu.Percent is { } value && (!double.IsFinite(value) || value is < 0 or > 100))
                gpu = new(null, "GPU engine counters returned an invalid percentage.");

            return new RenderResourceSnapshot
            {
                SampledAtUtc = clock.GetUtcNow(),
                ProcessCpuPercent = cpuPercent,
                ProcessWorkingSetBytes = workingSet,
                ProcessPeakWorkingSetBytes = peakWorkingSet,
                AdapterName = gpuMemory?.AdapterName,
                AdapterLuid = luid,
                GpuUtilizationPercent = gpu.Percent,
                ProcessLocalVideoMemoryUsageBytes = usage,
                LocalVideoMemoryBudgetBytes = budget,
                PeakProcessLocalVideoMemoryUsageBytes = peakVideoMemory,
                VideoMemorySampledAtUtc = gpuMemory?.SampledAtUtc,
                CpuUnavailableReason = cpuReason,
                MemoryUnavailableReason = memoryReason,
                GpuUnavailableReason = gpu.Percent.HasValue ? null : gpu.UnavailableReason ?? "GPU engine counters are unavailable.",
                VideoMemoryUnavailableReason = videoReason
            };
        }
    }

    private void DisposeSource()
    {
        lock (sampleGate)
        {
            if (Interlocked.Exchange(ref sourceDisposed, 1) != 0) return;
            source.Dispose();
        }
    }

    private static ulong? Maximum(ulong? previous, ulong? current)
        => current.HasValue ? previous.HasValue ? Math.Max(previous.Value, current.Value) : current : previous;

    private static string Failure(string operation, Exception error)
        => $"{operation} is unavailable ({error.GetType().Name}).";
}

internal readonly record struct RenderGpuUtilizationSample(double? Percent, string? UnavailableReason = null);

internal interface IRenderResourceSampleSource : IDisposable
{
    int LogicalProcessorCount { get; }
    TimeSpan ReadProcessCpuTime();
    ulong ReadProcessWorkingSetBytes();
    RenderGpuUtilizationSample ReadGpuUtilization(long adapterLuid);
}

internal sealed class ProcessRenderResourceSampleSource : IRenderResourceSampleSource
{
    private Process? process;
    private readonly PdhProcessGpuSampler gpu = new(Environment.ProcessId);
    public int LogicalProcessorCount => Environment.ProcessorCount;
    private Process CurrentProcess => process ??= Process.GetCurrentProcess();
    public TimeSpan ReadProcessCpuTime() => CurrentProcess.TotalProcessorTime;
    public ulong ReadProcessWorkingSetBytes()
    {
        CurrentProcess.Refresh();
        return checked((ulong)CurrentProcess.WorkingSet64);
    }
    public RenderGpuUtilizationSample ReadGpuUtilization(long adapterLuid) => gpu.Sample(adapterLuid);
    public void Dispose() { gpu.Dispose(); process?.Dispose(); process = null; }
}

/// <summary>PDH is optional; missing driver counters and the warm-up interval remain unavailable.</summary>
internal sealed class PdhProcessGpuSampler(int processId) : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private const uint ValidData = 0;
    private const uint NewData = 1;
    private const int MaximumBufferBytes = 16 * 1024 * 1024;
    private nint query;
    private nint counter;
    private long? adapterLuid;
    private bool primed;

    public RenderGpuUtilizationSample Sample(long actualAdapterLuid)
    {
        if (!OperatingSystem.IsWindows())
            return new(null, "Windows GPU engine performance counters are unavailable on this platform.");
        if (adapterLuid != actualAdapterLuid)
        {
            Dispose();
            adapterLuid = actualAdapterLuid;
        }
        if (query == 0)
        {
            uint status = PdhOpenQueryW(null, 0, out query);
            if (status != 0) { Dispose(); return Unavailable("PdhOpenQuery", status); }
            // English counter names work with localized Windows installations. Restrict the
            // wildcard to this PID before formatting; the selected LUID is checked below.
            status = PdhAddEnglishCounterW(query,
                $@"\GPU Engine(pid_{processId}_*)\Utilization Percentage", 0, out counter);
            if (status != 0) { Dispose(); return Unavailable("GPU Engine counter", status); }
        }
        uint collectStatus = PdhCollectQueryData(query);
        if (collectStatus != 0) { Dispose(); return Unavailable("PdhCollectQueryData", collectStatus); }
        if (!primed)
        {
            primed = true;
            return new(null, "Two GPU engine counter samples are required.");
        }
        // PDH_FMT_COUNTERVALUE carries its own CStatus. A successful array call alone does
        // not make every instance valid. Probe a fresh size again if the instance set changes.
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            uint bytes = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out _, 0);
            if (status != MoreData) return Unavailable("GPU engine array size", status);
            if (bytes == 0 || bytes > MaximumBufferBytes)
                return new(null, "GPU engine counter buffer size is invalid.");
            nint buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out uint count, buffer);
                if (status == MoreData) continue;
                if (status != 0) return Unavailable("GPU engine array", status);
                int itemSize = Marshal.SizeOf<PdhFormattedCounterItem>();
                if ((ulong)count * (ulong)itemSize > bytes)
                    return new(null, "GPU engine counter item count is invalid.");
                var samples = new List<RenderGpuEngineCounterSample>(checked((int)count));
                for (int i = 0; i < count; ++i)
                {
                    var item = Marshal.PtrToStructure<PdhFormattedCounterItem>(buffer + i * itemSize);
                    string? instance = Marshal.PtrToStringUni(item.Name);
                    if (instance is not null) samples.Add(new(instance, item.Value.Value, item.Value.Status));
                }
                return SelectBusiestEngine(samples, processId, actualAdapterLuid);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new(null, "GPU engine counter instances changed while sampling.");
    }

    internal static bool MatchesInstance(string instance, int pid, long luid)
    {
        ulong bits = unchecked((ulong)luid);
        string prefix = string.Create(CultureInfo.InvariantCulture,
            $"pid_{pid}_luid_0x{bits >> 32:x8}_0x{bits & uint.MaxValue:x8}_");
        return instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static RenderGpuUtilizationSample SelectBusiestEngine(
        IEnumerable<RenderGpuEngineCounterSample> samples, int pid, long luid)
    {
        double? maximum = null;
        foreach (var sample in samples)
        {
            if (sample.Status is not (ValidData or NewData) || !MatchesInstance(sample.Instance, pid, luid)) continue;
            if (!double.IsFinite(sample.Percent) || sample.Percent is < 0 or > 100) continue;
            maximum = maximum.HasValue ? Math.Max(maximum.Value, sample.Percent) : sample.Percent;
        }
        return maximum.HasValue ? new(maximum) : new(null,
            "No valid GPU engine counter matches this process and the selected adapter.");
    }

    private static RenderGpuUtilizationSample Unavailable(string operation, uint status)
        => new(null, $"{operation} is unavailable (PDH 0x{status:X8}).");

    public void Dispose()
    {
        if (query != 0) PdhCloseQuery(query);
        query = 0;
        counter = 0;
        primed = false;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterItem
    {
        public nint Name;
        public PdhFormattedCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize,
        out uint itemCount, nint items);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}

internal readonly record struct RenderGpuEngineCounterSample(string Instance, double Percent, uint Status = 0);
