using System.ComponentModel;
using System.Diagnostics;

namespace Mmd.Modern.Application;

/// <summary>Periodic process-thread observations, not an ETW Ready/Wait or context-switch trace.
/// Start and Dispose never enumerate or join threads on the caller. The owner must opt in.</summary>
public sealed class InteractionThreadSampler : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action<InteractionThreadSample> sample;
    private readonly Action<InteractionThreadSamplingBatch>? batch;
    private readonly InteractionThreadSamplerOptions options;
    private readonly Dictionary<int, Observation> previous = new();
    private readonly Queue<int> observedOrder = new();
    private readonly Task worker;
    private int disposed, cursor;
    private readonly record struct Observation(long Cpu, long CreatedUtcTicks, long Timestamp);

    private InteractionThreadSampler(Action<InteractionThreadSample> sample, Action<InteractionThreadSamplingBatch>? batch,
        InteractionThreadSamplerOptions options)
    {
        this.sample = sample; this.batch = batch; this.options = options;
        worker = Task.Run(RunAsync);
    }

    public static InteractionThreadSampler Start(Action<InteractionThreadSample> sample,
        Action<InteractionThreadSamplingBatch>? batch = null, InteractionThreadSamplerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        options ??= new();
        if (options.Interval < TimeSpan.FromMilliseconds(100) || options.Interval > TimeSpan.FromSeconds(10)
            || options.MaximumThreadsPerSample is < 1 or > 256 || options.MaximumCachedThreads is < 1 or > 2048
            || options.WorkBudget < TimeSpan.FromMilliseconds(1) || options.WorkBudget > TimeSpan.FromMilliseconds(25))
            throw new ArgumentOutOfRangeException(nameof(options));
        return new(sample, batch, options);
    }

    /// <summary>Await only off the UI path when a fully stopped sampler is required.</summary>
    public Task Completion => worker;

    private async Task RunAsync()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            using var timer = new PeriodicTimer(options.Interval);
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref disposed) != 0) break;
                Observe(process);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (Unavailable(error))
        {
            PublishBatch(new(Stopwatch.GetTimestamp(), 0, 0, 0, 1, 0, 0, 0, false,
                error.GetType().Name));
        }
        finally { cancellation.Dispose(); }
    }

    private void Observe(Process process)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            PublishBatch(new(Stopwatch.GetTimestamp(), 0, 0, 0, 1, 0, 0, 0, false, "unsupported-platform"));
            return;
        }
        long started = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        int total = 0, inspected = 0, published = 0, failed = 0, evicted = 0;
        bool budgetExceeded = false;
        string? errorName = null;
        try
        {
            // Process.Threads takes a platform process snapshot. That individual OS call cannot
            // be preempted by WorkBudget; its full cost is included in the batch measurement.
            process.Refresh();
            var threads = process.Threads;
            total = threads.Count;
            int maximum = Math.Min(total, options.MaximumThreadsPerSample);
            while (inspected < maximum && Volatile.Read(ref disposed) == 0)
            {
                if (Stopwatch.GetElapsedTime(started) >= options.WorkBudget) { budgetExceeded = true; break; }
                int index = cursor % total;
                cursor = (index + 1) % total;
                ++inspected;
                try
                {
                    using var thread = threads[index];
                    int id = thread.Id;
                    long cpu = thread.TotalProcessorTime.Ticks;
                    long created = thread.StartTime.ToUniversalTime().Ticks;
                    var state = thread.ThreadState;
                    ThreadWaitReason? wait = state == System.Diagnostics.ThreadState.Wait ? thread.WaitReason : null;
                    long now = Stopwatch.GetTimestamp();
                    bool hasPrevious = previous.TryGetValue(id, out var old) && old.CreatedUtcTicks == created && cpu >= old.Cpu;
                    if (!previous.ContainsKey(id))
                    {
                        if (previous.Count == options.MaximumCachedThreads)
                        { previous.Remove(observedOrder.Dequeue()); ++evicted; }
                        observedOrder.Enqueue(id);
                    }
                    previous[id] = new(cpu, created, now);
                    var entry = new InteractionThreadSample(now, id, cpu, hasPrevious ? cpu - old.Cpu : -1,
                        hasPrevious ? now - old.Timestamp : 0, state, wait);
                    if (Volatile.Read(ref disposed) == 0) { sample(entry); ++published; }
                }
                catch (Exception error) when (Unavailable(error)) { ++failed; }
            }
        }
        catch (Exception error) when (Unavailable(error)) { ++failed; errorName = error.GetType().Name; }
        PublishBatch(new(started, total, inspected, published, failed, evicted,
            Stopwatch.GetTimestamp() - started, GC.GetAllocatedBytesForCurrentThread() - allocated, budgetExceeded, errorName));
    }

    private void PublishBatch(InteractionThreadSamplingBatch value)
    {
        if (Volatile.Read(ref disposed) == 0) batch?.Invoke(value);
    }

    private static bool Unavailable(Exception error) => error is Win32Exception or InvalidOperationException
        or NotSupportedException or UnauthorizedAccessException;

    /// <summary>Nonblocking cancellation. A currently executing OS query may finish afterward.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { } // The worker may already have completed independently.
    }
}

public sealed record InteractionThreadSamplerOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(100);
    public int MaximumThreadsPerSample { get; init; } = 64;
    public int MaximumCachedThreads { get; init; } = 512;
    public TimeSpan WorkBudget { get; init; } = TimeSpan.FromMilliseconds(10);
}

/// <param name="CpuDelta100Nanoseconds">CPU since this thread's preceding observation, or -1 for a first/reused thread identity.</param>
/// <param name="ObservationIntervalTicks">Stopwatch ticks between observations, not time continuously spent in the reported state.</param>
public readonly record struct InteractionThreadSample(long Timestamp, int NativeThreadId, long CpuTotal100Nanoseconds,
    long CpuDelta100Nanoseconds, long ObservationIntervalTicks, System.Diagnostics.ThreadState State, ThreadWaitReason? WaitReason);

public readonly record struct InteractionThreadSamplingBatch(long Timestamp, int TotalThreads, int InspectedThreads,
    int PublishedThreads, int FailedThreads, int EvictedBaselines, long ElapsedTicks, long AllocatedBytes,
    bool BudgetExceeded, string? Error);
