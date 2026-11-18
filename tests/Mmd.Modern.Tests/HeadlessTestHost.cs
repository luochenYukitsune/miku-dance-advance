using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;

namespace Mmd.Modern.Tests;

internal static class HeadlessTestHost
{
    // Avalonia 11.3.22 StartNew publishes a session before its worker task is assigned.
    // Construct the pinned session with its real, unstarted worker, then start the worker.
    // Dispatch, application isolation and Dispose remain Avalonia's implementations.
    // Upstream fix: https://github.com/AvaloniaUI/Avalonia/pull/22222
    internal static HeadlessUnitTestSession Start(Func<AppBuilder> build,
        AvaloniaTestIsolationLevel isolation = AvaloniaTestIsolationLevel.PerTest)
    {
        var builder = build();
        var shutdown = new CancellationTokenSource();
        var queue = new BlockingCollection<(Action Action, ExecutionContext? Context)>();
        var worker = new Task(() =>
        {
            try
            {
                foreach (var (action, context) in queue.GetConsumingEnumerable(shutdown.Token))
                {
                    if (context is null) action();
                    else ExecutionContext.Run(context, static state => ((Action)state!)(), action);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        });
        var session = CreateSession(builder, shutdown, queue, worker, isolation == AvaloniaTestIsolationLevel.PerTest);
        worker.Start(TaskScheduler.Default);
        return session;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern HeadlessUnitTestSession CreateSession(AppBuilder builder, CancellationTokenSource shutdown,
        BlockingCollection<(Action, ExecutionContext?)> queue, Task worker, bool isolated);
}
