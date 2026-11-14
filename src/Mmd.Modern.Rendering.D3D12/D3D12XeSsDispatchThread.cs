using System.Collections.Concurrent;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Process-wide optional XeSS SDK lane. Intel's official XeSS-SR guide, Thread safety,
/// requires its API calls on the same thread as initialization and prohibits concurrent entry.
/// This lazy background worker is independent of view lifetime: preview/export keep separate
/// native contexts, while native initialization, execution and destruction all use this one lane.
/// D3D12 recording remains sequential; the caller waits while XeSS records on its command list.
/// Reference: pinned Intel XeSS SDK de0fb9c1, doc/xess_sr_developer_guide_english.md.</summary>
internal static class D3D12XeSsDispatchThread
{
    private static readonly Lazy<Worker> worker = new(() => new Worker());

    internal static void Invoke(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Invoke(() => { operation(); return true; });
    }

    internal static T Invoke<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var owner = worker.Value;
        // Nested bookkeeping on the dispatch thread must not block waiting for itself.
        if (Environment.CurrentManagedThreadId == owner.Thread.ManagedThreadId) return operation();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Queue.Add(() =>
        {
            try { completion.SetResult(operation()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private sealed class Worker
    {
        internal readonly BlockingCollection<Action> Queue = new(256);
        internal readonly Thread Thread;
        internal Worker()
        {
            Thread = new Thread(() =>
            {
                foreach (var operation in Queue.GetConsumingEnumerable()) operation();
            }) { IsBackground = true, Name = "MMD XeSS SDK" };
            Thread.Start();
        }
    }
}
