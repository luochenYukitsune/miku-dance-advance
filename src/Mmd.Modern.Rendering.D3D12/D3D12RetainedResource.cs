namespace Mmd.Modern.Rendering.D3D12;

/// <summary>One cache reference plus explicit frame leases. Cache replacement does not end an in-flight use.</summary>
internal sealed class D3D12RetainedResource(object identity, IDisposable value) : IDisposable
{
    internal object Identity { get; } = identity;
    internal IDisposable Value { get; } = value;
    internal long LastFrameId { get; set; } = -1;
    private int references = 1;
    private bool ownerDisposed;

    internal IDisposable Lease()
    {
        ObjectDisposedException.ThrowIf(ownerDisposed, this);
        Interlocked.Increment(ref references);
        return new FrameLease(this);
    }
    private void Release()
    {
        if (Interlocked.Decrement(ref references) == 0) Value.Dispose();
    }
    public void Dispose()
    {
        if (ownerDisposed) return;
        ownerDisposed = true;
        Release();
    }
    private sealed class FrameLease(D3D12RetainedResource owner) : IDisposable
    {
        private D3D12RetainedResource? retained = owner;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.Release();
    }
}
