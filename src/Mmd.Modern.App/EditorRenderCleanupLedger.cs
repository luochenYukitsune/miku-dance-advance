using System.Runtime.ExceptionServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Workspace-owned export cleanup. A failed cleanup stays owned and blocks the next export
/// before it creates another solver or renderer. Every boundary makes one bounded cleanup attempt.</summary>
internal sealed class EditorRenderCleanupLedger
{
    private readonly SemaphoreSlim exportGate = new(1, 1);
    private readonly object entriesGate = new();
    private readonly HashSet<Entry> entries = [];

    internal async ValueTask<ExportLease> EnterExportAsync(CancellationToken token)
    {
        if (!await exportGate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("An export or renderer cleanup is already active in this workspace.");
        try
        {
            await DrainCoreAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(this);
        }
        catch { exportGate.Release(); throw; }
    }

    internal void Retain(IEditorRenderInitializationCleanup cleanup) => RetainOwner(cleanup);

    internal IViewRenderer Track(IViewRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var entry = RetainOwner(renderer);
        return renderer is IViewRendererDeviceInfoProvider device
            ? new DeviceRenderer(renderer, entry, device) : new Renderer(renderer, entry);
    }

    private Entry RetainOwner(IAsyncDisposable owner)
    {
        var entry = new Entry(this, owner);
        lock (entriesGate) entries.Add(entry);
        return entry;
    }

    internal async ValueTask DrainAsync()
    {
        // Workspace.Dispose is synchronous on the UI owner. Waiting for a lease whose admission
        // continuation needs that UI could deadlock; retain ownership and let disposal be retried.
        if (!await exportGate.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("Finish the active export or renderer cleanup before disposing this workspace.");
        try { await DrainCoreAsync().ConfigureAwait(false); }
        finally { exportGate.Release(); }
    }

    private async ValueTask DrainCoreAsync()
    {
        Entry[] pending;
        lock (entriesGate) pending = entries.ToArray();
        List<Exception>? failures = null;
        foreach (var entry in pending)
        {
            try { await entry.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException("Renderer cleanup remains pending in this workspace.", failures);
    }

    internal sealed class ExportLease(EditorRenderCleanupLedger ledger) : IAsyncDisposable
    {
        private readonly object lifetime = new();
        private Task? release;
        public ValueTask DisposeAsync()
        {
            lock (lifetime) return new(release ??= ReleaseAsync());
        }
        private async Task ReleaseAsync()
        {
            try { await ledger.DrainCoreAsync().ConfigureAwait(false); }
            finally { ledger.exportGate.Release(); }
        }
    }

    private sealed class Entry(EditorRenderCleanupLedger ledger, IAsyncDisposable owner) : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool disposed;
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                await owner.DisposeAsync().ConfigureAwait(false);
                disposed = true;
                lock (ledger.entriesGate) ledger.entries.Remove(this);
            }
            finally { gate.Release(); }
        }
    }

    private class Renderer(IViewRenderer inner, Entry cleanup) : IViewRenderer,
        IViewSceneInputRequests, IViewSceneInputPreflight, IViewRenderingStylePreflight
    {
        public ViewRendererCapabilities Capabilities => inner.Capabilities;
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests
            => inner is IViewSceneInputRequests inputs ? inputs.SceneInputRequests : [];
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
            => inner.CaptureAsync(frame, cancellationToken);
        public void ValidateSceneInputs(PreviewFrame frame)
        { if (inner is IViewSceneInputPreflight preflight) preflight.ValidateSceneInputs(frame); }
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
        { if (inner is IViewRenderingStylePreflight preflight) preflight.ValidateRenderingStyles(scene, bindings); }
        public ValueTask DisposeAsync() => cleanup.DisposeAsync();
    }

    private sealed class DeviceRenderer(IViewRenderer inner, Entry cleanup, IViewRendererDeviceInfoProvider device)
        : Renderer(inner, cleanup), IViewRendererDeviceInfoProvider
    {
        public ViewRendererDeviceInfo DeviceInfo => device.DeviceInfo;
    }
}
