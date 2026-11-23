using System.Collections.Concurrent;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Waits on fence completion events instead of polling with Thread.Sleep, whose wake-up follows
/// the platform timer (about 15.6 ms by default on Windows). Each concurrent waiter rents its own event,
/// so waiters on different threads never consume each other's signal. Dispose only after the fence drained.</summary>
internal sealed class D3D12FenceWaiter(ID3D12Fence fence) : IDisposable
{
    // Device removal sets the fence to UINT64_MAX and signals pending events; the bounded wait only
    // re-reads the fence as a safety net and never ends a wait early.
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentBag<AutoResetEvent> idle = [];
    private readonly List<AutoResetEvent> created = [];

    /// <summary>Returns once CompletedValue reaches <paramref name="value"/> (UINT64_MAX after device removal).
    /// Cancellation is observed while the fence is still pending.</summary>
    internal void Wait(ulong value, CancellationToken cancellationToken)
    {
        if (fence.CompletedValue >= value) return;
        var signal = Rent();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArmEvent(value, signal);
            while (fence.CompletedValue < value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool signaled = cancellationToken.CanBeCanceled
                    ? WaitHandle.WaitAny([signal, cancellationToken.WaitHandle], RecheckInterval) == 0
                    : signal.WaitOne(RecheckInterval);
                // A cancelled earlier wait can leave a registration on a pooled event; register again.
                if (signaled && fence.CompletedValue < value)
                    ArmEvent(value, signal);
            }
        }
        finally { idle.Add(signal); }
    }

    private void ArmEvent(ulong value, AutoResetEvent signal)
    {
        var result = fence.SetEventOnCompletion(value, signal.SafeWaitHandle.DangerousGetHandle());
        if (result.Failure && fence.CompletedValue != ulong.MaxValue) result.CheckError();
    }

    private AutoResetEvent Rent()
    {
        if (idle.TryTake(out var signal)) { signal.Reset(); return signal; }
        signal = new AutoResetEvent(false);
        lock (created) created.Add(signal);
        return signal;
    }

    public void Dispose()
    {
        lock (created) { foreach (var signal in created) signal.Dispose(); created.Clear(); }
    }
}
