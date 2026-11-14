namespace Mmd.Modern.App;

/// <summary>Coalesces the producer to one queued or executing frame; restart invalidates stale callbacks.</summary>
public sealed class ViewportFrameDispatch(Action<Action> post, Action render)
{
    private int enabled, generation = 1, pendingGeneration;
    public void Start() { Interlocked.Increment(ref generation); Volatile.Write(ref enabled, 1); }
    public void Stop() { Volatile.Write(ref enabled, 0); Interlocked.Increment(ref generation); }
    public void Pulse()
    {
        if (Volatile.Read(ref enabled) == 0) return;
        int requestedGeneration = Volatile.Read(ref generation);
        if (Interlocked.CompareExchange(ref pendingGeneration, requestedGeneration, 0) != 0) return;
        try
        {
            post(() =>
            {
                // Keep ownership through recording/Present. A slow frame must not queue its
                // own successor and keep the dispatcher inside foreground jobs indefinitely.
                try
                {
                    if (Volatile.Read(ref enabled) != 0 && Volatile.Read(ref generation) == requestedGeneration) render();
                }
                finally { Interlocked.CompareExchange(ref pendingGeneration, 0, requestedGeneration); }
            });
        }
        catch { Interlocked.CompareExchange(ref pendingGeneration, 0, requestedGeneration); throw; }
    }
}
