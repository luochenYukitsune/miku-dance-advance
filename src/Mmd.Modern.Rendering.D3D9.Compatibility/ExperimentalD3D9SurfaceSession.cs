using System.Threading.Channels;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// Experimental ordinary-surface, object/subset FX, explicit scene FX or frozen EMM capture/preview.
/// Not an editor render engine or general MME backend. Path-only object/scene FX callers must hold the full
/// immutable resource tree until Dispose succeeds; EMM and WithAssets factories retain a declared dependency
/// lease through cleanup retries.
/// Owns one dedicated OS Thread and a bounded FIFO for the loaded DLL's single global session.
/// No time/physics advancement, shadow, outline or transparent background.
/// Dispose is mandatory. Await callers to apply backpressure rather than launching unbounded writers.
/// </summary>
public sealed class ExperimentalD3D9SurfaceSession : IDisposable, IAsyncDisposable
{
    private readonly Channel<WorkItem> queue;
    private readonly Thread owner;
    private readonly object disposalGate = new();
    private readonly ManualResetEventSlim cleanupRequested = new(false);
    private readonly D3D9OwnerWait ownerWait = new();
    private readonly TaskCompletionSource initialized = NewCompletion();
    private readonly bool preview;
    private readonly D3D9SurfaceScope scope;
    private readonly D3D9ObjectFxAssets.Lease? fxAssetsLease;
    private TaskCompletionSource? disposalAttempt;
    private bool closing, disposed;
    private ID3D9SurfaceNativeApi? api;
    private ulong handle;
    private NativeAbi abi;

    private ExperimentalD3D9SurfaceSession(Func<ID3D9SurfaceNativeApi> factory, D3D9SurfaceSessionOptions options,
        nint targetHwnd, CancellationToken initializationCancellation, D3D9ObjectFxConfiguration? objectFx = null,
        D3D9ObjectFxAssets.Lease? fxAssetsLease = null, D3D9SceneFxConfiguration? sceneFx = null,
        D3D9EmmSessionConfiguration? emm = null)
    {
        this.fxAssetsLease = fxAssetsLease;
        preview = targetHwnd != 0;
        scope = emm is not null ? D3D9SurfaceScope.ConfiguredEmm : sceneFx is not null ? D3D9SurfaceScope.ConfiguredSceneFx :
            objectFx is not null ? D3D9SurfaceScope.ConfiguredObjectFx : D3D9SurfaceScope.IsolatedSurface;
        queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        owner = new Thread(() => Run(factory, options, targetHwnd, initializationCancellation, objectFx, sceneFx, emm))
        {
            IsBackground = true, Name = "MDA experimental D3D9 surface"
        };
        try { owner.Start(); }
        catch
        {
            cleanupRequested.Dispose();
            ownerWait.Dispose();
            throw;
        }
    }

    /// <summary>Loads only the explicit absolute DLL path; creation/query/device calls stay on the owner Thread.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateAsync(string absoluteDllPath, D3D9SurfaceSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        return CreateCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, 0, cancellationToken);
    }

    /// <summary>
    /// Creates a session with an immutable preview mode and borrowed target HWND. The caller must keep this exact
    /// window alive until Dispose succeeds, including admitted work and any failed-Destroy cleanup retries.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreatePreviewAsync(string absoluteDllPath, nint targetHwnd,
        D3D9SurfaceSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, targetHwnd, cancellationToken);
    }

    /// <summary>
    /// Creates immutable limited object/subset FX capture mode. The caller MUST freeze and hold the complete
    /// include/resource tree until Dispose succeeds; configuration copies rows and paths, not file assets.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxAsync(string absoluteDllPath,
        D3D9SurfaceSessionOptions options, D3D9ObjectFxConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        return CreateCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, 0, cancellationToken, configuration);
    }

    /// <summary>
    /// Creates immutable limited object/subset FX preview mode. Keep the exact borrowed HWND and the complete
    /// frozen FX include/resource tree alive until Dispose succeeds, including any failed cleanup retries.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxPreviewAsync(string absoluteDllPath, nint targetHwnd,
        D3D9SurfaceSessionOptions options, D3D9ObjectFxConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, targetHwnd, cancellationToken, configuration);
    }

    /// <summary>
    /// Creates limited object/subset FX capture while retaining the declared complete dependency tree.
    /// Disposing assets closes new acquisition; this session retains its own lease until Destroy and module
    /// unload both succeed, including initialization-cleanup exception retries. No dependency discovery occurs.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxWithAssetsAsync(string absoluteDllPath,
        D3D9SurfaceSessionOptions options, D3D9ObjectFxAssets assets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        return CreateWithAssetsCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, 0, assets, cancellationToken);
    }

    /// <summary>Creates limited object/subset FX preview with an owned asset lease and a borrowed HWND kept alive by the caller.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxPreviewWithAssetsAsync(string absoluteDllPath, nint targetHwnd,
        D3D9SurfaceSessionOptions options, D3D9ObjectFxAssets assets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateWithAssetsCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, targetHwnd, assets, cancellationToken);
    }

    /// <summary>
    /// Creates explicit scene FX capture mode using one frozen sample for all native render turns. The caller
    /// must retain the complete immutable dependency tree until disposal succeeds, including cleanup retries.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxAsync(string absoluteDllPath,
        D3D9SurfaceSessionOptions options, D3D9SceneFxConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        return CreateCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, 0, cancellationToken, sceneFx: configuration);
    }

    /// <summary>Creates explicit scene FX preview. Keep the borrowed HWND and complete FX tree alive through successful disposal.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxPreviewAsync(string absoluteDllPath, nint targetHwnd,
        D3D9SurfaceSessionOptions options, D3D9SceneFxConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, targetHwnd, cancellationToken, sceneFx: configuration);
    }

    /// <summary>Creates explicit scene FX capture with an owned declared dependency lease. No dependency discovery occurs.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxWithAssetsAsync(string absoluteDllPath,
        D3D9SurfaceSessionOptions options, D3D9SceneFxAssets assets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        return CreateWithAssetsCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, 0, assets, cancellationToken);
    }

    /// <summary>Creates explicit scene FX preview with a retained asset lease and a borrowed HWND kept alive by the caller.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxPreviewWithAssetsAsync(string absoluteDllPath, nint targetHwnd,
        D3D9SurfaceSessionOptions options, D3D9SceneFxAssets assets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateWithAssetsCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, targetHwnd, assets, cancellationToken);
    }

    /// <summary>
    /// Creates a distinct frozen EMM capture session and leases every materialized resource in its map.
    /// Supply exactly one nonzero unique native ID for each configuration object, matching the submitted
    /// FRM1 identities. IDs are copied before asynchronous initialization. The map must already contain
    /// every required include/texture dependency; original metadata paths are never opened or discovered.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateEmmAsync(string absoluteDllPath,
        D3D9SurfaceSessionOptions options, D3D9EmmRuntimeConfiguration configuration,
        IReadOnlyDictionary<Guid, ulong> nativeObjectIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        var selected = new D3D9EmmSessionConfiguration(configuration, nativeObjectIds);
        return CreateEmmCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, 0, selected, cancellationToken);
    }

    /// <summary>Creates frozen EMM preview with a retained resource lease and an exact borrowed HWND kept alive through disposal.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateEmmPreviewAsync(string absoluteDllPath, nint targetHwnd,
        D3D9SurfaceSessionOptions options, D3D9EmmRuntimeConfiguration configuration,
        IReadOnlyDictionary<Guid, ulong> nativeObjectIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        var selected = new D3D9EmmSessionConfiguration(configuration, nativeObjectIds);
        return CreateEmmCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, targetHwnd, selected, cancellationToken);
    }

    /// <summary>Creates EMM capture using a caller-opened complete resource lease, retained through cleanup retries.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateEmmWithAssetsAsync(string absoluteDllPath,
        D3D9SurfaceSessionOptions options, D3D9EmmRuntimeAssets assets, IReadOnlyDictionary<Guid, ulong> nativeObjectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        return CreateEmmWithAssetsCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, 0, assets, nativeObjectIds, cancellationToken);
    }

    /// <summary>Creates EMM preview using a retained resource lease and an exact borrowed HWND kept alive through disposal.</summary>
    public static Task<ExperimentalD3D9SurfaceSession> CreateEmmPreviewWithAssetsAsync(string absoluteDllPath, nint targetHwnd,
        D3D9SurfaceSessionOptions options, D3D9EmmRuntimeAssets assets, IReadOnlyDictionary<Guid, ulong> nativeObjectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeSurfaceApi.ValidateAbsolutePath(absoluteDllPath);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateEmmWithAssetsCoreAsync(() => NativeSurfaceApi.Load(absoluteDllPath), options, targetHwnd, assets, nativeObjectIds, cancellationToken);
    }

    private static Task<ExperimentalD3D9SurfaceSession> CreateEmmCoreAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, nint targetHwnd, D3D9EmmSessionConfiguration selected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var assets = D3D9EmmRuntimeAssets.Open(selected.Configuration);
        var lease = assets.Acquire();
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken, fxAssetsLease: lease, emm: selected);
    }

    private static Task<ExperimentalD3D9SurfaceSession> CreateEmmWithAssetsCoreAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, nint targetHwnd, D3D9EmmRuntimeAssets assets,
        IReadOnlyDictionary<Guid, ulong> nativeObjectIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var selected = new D3D9EmmSessionConfiguration(assets.Configuration, nativeObjectIds);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = assets.Acquire();
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken, fxAssetsLease: lease, emm: selected);
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, CancellationToken cancellationToken) => CreateCoreAsync(factory, options, 0, cancellationToken);

    internal static Task<ExperimentalD3D9SurfaceSession> CreatePreviewForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        nint targetHwnd, D3D9SurfaceSessionOptions options, CancellationToken cancellationToken)
    {
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken);
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, D3D9ObjectFxConfiguration configuration, CancellationToken cancellationToken) =>
        CreateCoreAsync(factory, options, 0, cancellationToken, configuration);

    internal static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxPreviewForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        nint targetHwnd, D3D9SurfaceSessionOptions options, D3D9ObjectFxConfiguration configuration, CancellationToken cancellationToken)
    {
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken, configuration);
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxWithAssetsForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, D3D9ObjectFxAssets assets, CancellationToken cancellationToken) =>
        CreateWithAssetsCoreAsync(factory, options, 0, assets, cancellationToken);

    internal static Task<ExperimentalD3D9SurfaceSession> CreateObjectFxPreviewWithAssetsForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        nint targetHwnd, D3D9SurfaceSessionOptions options, D3D9ObjectFxAssets assets, CancellationToken cancellationToken)
    {
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateWithAssetsCoreAsync(factory, options, targetHwnd, assets, cancellationToken);
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, D3D9SceneFxConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return CreateCoreAsync(factory, options, 0, cancellationToken, sceneFx: configuration);
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxPreviewForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        nint targetHwnd, D3D9SurfaceSessionOptions options, D3D9SceneFxConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken, sceneFx: configuration);
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxWithAssetsForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, D3D9SceneFxAssets assets, CancellationToken cancellationToken) =>
        CreateWithAssetsCoreAsync(factory, options, 0, assets, cancellationToken);

    internal static Task<ExperimentalD3D9SurfaceSession> CreateSceneFxPreviewWithAssetsForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        nint targetHwnd, D3D9SurfaceSessionOptions options, D3D9SceneFxAssets assets, CancellationToken cancellationToken)
    {
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateWithAssetsCoreAsync(factory, options, targetHwnd, assets, cancellationToken);
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateEmmForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, D3D9EmmRuntimeConfiguration configuration,
        IReadOnlyDictionary<Guid, ulong> nativeObjectIds, CancellationToken cancellationToken) =>
        CreateCoreAsync(factory, options, 0, cancellationToken, emm: new(configuration, nativeObjectIds));

    internal static Task<ExperimentalD3D9SurfaceSession> CreateEmmPreviewForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        nint targetHwnd, D3D9SurfaceSessionOptions options, D3D9EmmRuntimeConfiguration configuration,
        IReadOnlyDictionary<Guid, ulong> nativeObjectIds, CancellationToken cancellationToken)
    {
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken, emm: new(configuration, nativeObjectIds));
    }

    internal static Task<ExperimentalD3D9SurfaceSession> CreateEmmWithAssetsForTestingAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, D3D9EmmRuntimeAssets assets, IReadOnlyDictionary<Guid, ulong> nativeObjectIds,
        CancellationToken cancellationToken) =>
        CreateEmmWithAssetsCoreAsync(factory, options, 0, assets, nativeObjectIds, cancellationToken);

    private static Task<ExperimentalD3D9SurfaceSession> CreateWithAssetsCoreAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, nint targetHwnd, D3D9ObjectFxAssets assets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = assets.Acquire();
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken, assets.Configuration, lease);
    }

    private static Task<ExperimentalD3D9SurfaceSession> CreateWithAssetsCoreAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, nint targetHwnd, D3D9SceneFxAssets assets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = assets.Acquire();
        return CreateCoreAsync(factory, options, targetHwnd, cancellationToken, fxAssetsLease: lease, sceneFx: assets.Configuration);
    }

    private static async Task<ExperimentalD3D9SurfaceSession> CreateCoreAsync(Func<ID3D9SurfaceNativeApi> factory,
        D3D9SurfaceSessionOptions options, nint targetHwnd, CancellationToken cancellationToken, D3D9ObjectFxConfiguration? objectFx = null,
        D3D9ObjectFxAssets.Lease? fxAssetsLease = null, D3D9SceneFxConfiguration? sceneFx = null,
        D3D9EmmSessionConfiguration? emm = null)
    {
        ExperimentalD3D9SurfaceSession session;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = new ExperimentalD3D9SurfaceSession(factory, options, targetHwnd, cancellationToken, objectFx, fxAssetsLease, sceneFx, emm);
        }
        catch { fxAssetsLease?.Dispose(); throw; }
        try
        {
            await session.initialized.Task.ConfigureAwait(false);
            return session;
        }
        catch (Exception initializationError)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError) { throw new D3D9SurfaceInitializationCleanupException(initializationError, cleanupError, session); }
            throw;
        }
    }

    /// <summary>
    /// Cancellation is checked before admission, before the native call, and after it returns.
    /// In-flight native calls are never interrupted or unloaded. An error from native takes precedence over cancellation.
    /// </summary>
    public Task<D3D9SurfaceCapture> CaptureAsync(D3D9SurfaceSnapshot snapshot,
        D3D9SurfaceRenderOrder renderOrder = D3D9SurfaceRenderOrder.Original, CancellationToken cancellationToken = default)
    {
        if (preview) throw new InvalidOperationException("A preview session cannot capture CPU RGBA.");
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PreviewOverlayPrimitiveCount != 0)
            throw new NotSupportedException("Editor overlays are preview-only and cannot enter a capture session.");
        ValidateScope(snapshot);
        if (renderOrder is not (D3D9SurfaceRenderOrder.Original or D3D9SurfaceRenderOrder.OpaqueFirst))
            throw new ArgumentOutOfRangeException(nameof(renderOrder));
        return Submit(() =>
        {
            ValidateExtent(snapshot.Width, snapshot.Height);
            if ((abi.SupportedFeatures & snapshot.RequiredFeatures) != snapshot.RequiredFeatures)
                throw new NotSupportedException($"Native surface does not support the snapshot's required features 0x{snapshot.RequiredFeatures:X}.");
            return api!.Capture(handle, snapshot, renderOrder);
        }, cancellationToken);
    }

    /// <summary>Submits the frozen packets directly to native Present; cancellation and FIFO rules match CaptureAsync.</summary>
    public Task<D3D9SurfacePresentation> PresentAsync(D3D9SurfaceSnapshot snapshot,
        D3D9SurfaceRenderOrder renderOrder = D3D9SurfaceRenderOrder.Original, CancellationToken cancellationToken = default)
    {
        if (!preview) throw new InvalidOperationException("A capture session cannot present to a borrowed window.");
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateScope(snapshot);
        if (renderOrder is not (D3D9SurfaceRenderOrder.Original or D3D9SurfaceRenderOrder.OpaqueFirst))
            throw new ArgumentOutOfRangeException(nameof(renderOrder));
        return Submit(() =>
        {
            ValidateExtent(snapshot.Width, snapshot.Height);
            var requiredFeatures = snapshot.RequiredFeatures | 128UL;
            if ((abi.SupportedFeatures & requiredFeatures) != requiredFeatures)
                throw new NotSupportedException($"Native surface does not support the snapshot's required preview features 0x{requiredFeatures:X}.");
            return api!.Present(handle, snapshot, renderOrder);
        }, cancellationToken);
    }

    public Task ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        D3D9SurfaceSessionOptions.ValidateExtent(width, height);
        return Submit(() =>
        {
            ValidateExtent(width, height);
            api!.Resize(handle, width, height);
            return true;
        }, cancellationToken);
    }

    private async Task<T> Submit<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        RejectOwnerReentry();
        cancellationToken.ThrowIfCancellationRequested();
        lock (disposalGate) ObjectDisposedException.ThrowIf(closing, this);
        var work = new WorkItem<T>(operation, cancellationToken);
        try { await queue.Writer.WriteAsync(work, cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(ExperimentalD3D9SurfaceSession)); }
        ownerWait.Signal();
        return await work.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        RejectOwnerReentry();
        BeginDispose().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Stops admission, drains accepted calls and destroys/unloads on the owner Thread.
    /// If Destroy fails, the task faults while the DLL/owner remain alive; call Dispose again to retry safely.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        RejectOwnerReentry();
        return new(BeginDispose());
    }

    private Task BeginDispose()
    {
        lock (disposalGate)
        {
            if (disposed) return Task.CompletedTask;
            if (disposalAttempt is { Task.IsCompleted: false }) return disposalAttempt.Task;
            disposalAttempt = NewCompletion();
            closing = true;
            queue.Writer.TryComplete();
            cleanupRequested.Set();
            ownerWait.Signal();
            return disposalAttempt.Task;
        }
    }

    private void RejectOwnerReentry()
    {
        // No native callback is exposed, but keep an accidental future callback from self-deadlocking.
        if (Environment.CurrentManagedThreadId == owner.ManagedThreadId)
            throw new InvalidOperationException("Cannot synchronously reenter or dispose the native surface owner Thread.");
    }

    private void Run(Func<ID3D9SurfaceNativeApi> factory, D3D9SurfaceSessionOptions options, nint targetHwnd,
        CancellationToken cancellationToken, D3D9ObjectFxConfiguration? objectFx, D3D9SceneFxConfiguration? sceneFx,
        D3D9EmmSessionConfiguration? emm)
    {
        Exception? startupError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            api = factory();
            cancellationToken.ThrowIfCancellationRequested();
            abi = api.QueryAbi();
            abi.Validate();
            ValidateExtent(options.Width, options.Height);
            if (preview && ((abi.SupportedFeatures & 128UL) == 0 || !api.SupportsPreview))
                throw new NotSupportedException("Native surface preview requires feature 128 and both optional CreatePreview and Present exports.");
            if (sceneFx is not null && ((abi.SupportedFeatures & 1792UL) != 1792UL || !api.SupportsSceneFx))
                throw new NotSupportedException("Native scene FX requires features 256/512/1024 and the optional ConfigureSceneFxV1 export.");
            if (emm is not null && ((abi.SupportedFeatures & 5888UL) != 5888UL || !api.SupportsEmm))
                throw new NotSupportedException("Native EMM requires features 256/512/1024/4096 and the optional ConfigureEmmV1 export.");
            if (objectFx is { Resources: not null } &&
                ((abi.SupportedFeatures & 768UL) != 768UL || !api.SupportsObjectFxResources))
                throw new NotSupportedException("Packaged object FX requires features 256/512 and the optional ConfigureObjectFxV2 export.");
            if (objectFx is { Resources: null } && ((abi.SupportedFeatures & 256UL) == 0 || !api.SupportsObjectFx))
                throw new NotSupportedException("Native object FX requires feature 256 and the optional ConfigureObjectFx export.");
            cancellationToken.ThrowIfCancellationRequested();
            handle = preview ? api.CreatePreview(targetHwnd, options) : api.Create(options);
            if (handle == 0) throw new InvalidDataException("Native surface create returned a zero handle.");
            cancellationToken.ThrowIfCancellationRequested();
            if (objectFx is not null) api.ConfigureObjectFx(handle, objectFx);
            if (sceneFx is not null) api.ConfigureSceneFx(handle, sceneFx);
            if (emm is not null) api.ConfigureEmm(handle, emm);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) { startupError = error; }

        if (startupError is not null)
        {
            // Factory observes the failure, requests disposal, and waits for it before returning the error.
            lock (disposalGate) { closing = true; queue.Writer.TryComplete(); }
            initialized.TrySetException(startupError);
            WaitForCleanupRequest();
        }
        else
        {
            initialized.TrySetResult();
            try
            {
                for (;;)
                {
                    ownerWait.PumpMessages();
                    if (queue.Reader.TryRead(out var work)) { work.Execute(); continue; }
                    if (queue.Reader.Completion.IsCompleted)
                    {
                        queue.Reader.Completion.GetAwaiter().GetResult();
                        break;
                    }
                    ownerWait.Wait();
                }
            }
            catch (Exception laneError)
            {
                // A WorkItem catches its own operation errors; this is an unexpected queue failure.
                lock (disposalGate) { closing = true; queue.Writer.TryComplete(laneError); }
                while (queue.Reader.TryRead(out var work)) work.Fail(laneError);
            }
        }

        for (;;)
        {
            TaskCompletionSource attempt;
            lock (disposalGate)
            {
                disposalAttempt ??= NewCompletion();
                attempt = disposalAttempt;
                cleanupRequested.Reset();
            }
            try
            {
                if (handle != 0) { api!.Destroy(handle); handle = 0; }
                api?.Dispose();
                api = null;
                fxAssetsLease?.Dispose();
                lock (disposalGate)
                {
                    closing = true; disposed = true;
                    cleanupRequested.Dispose();
                    ownerWait.Dispose();
                }
                attempt.TrySetResult();
                return;
            }
            catch (Exception cleanupError)
            {
                // Keep both the native module and this exact OS Thread available for an explicit retry.
                attempt.TrySetException(cleanupError);
                WaitForCleanupRequest();
            }
        }
    }

    private void WaitForCleanupRequest()
    {
        while (!cleanupRequested.IsSet)
        {
            ownerWait.PumpMessages();
            if (!cleanupRequested.IsSet) ownerWait.Wait();
        }
    }

    private void ValidateExtent(int width, int height)
    {
        D3D9SurfaceSessionOptions.ValidateExtent(width, height);
        if (width > abi.MaxWidth || height > abi.MaxHeight)
            throw new NotSupportedException($"Native surface extent is limited to {abi.MaxWidth}x{abi.MaxHeight}.");
    }

    private void ValidateScope(D3D9SurfaceSnapshot snapshot)
    {
        if (snapshot.Scope != scope)
            throw new InvalidOperationException("Snapshot scope does not match the session's immutable FX configuration.");
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private abstract class WorkItem
    {
        internal abstract void Execute();
        internal abstract void Fail(Exception error);
    }

    private sealed class WorkItem<T>(Func<T> operation, CancellationToken cancellationToken) : WorkItem
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<T> Task => completion.Task;
        internal override void Execute()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = operation();
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        internal override void Fail(Exception error) => completion.TrySetException(error);
    }
}

/// <summary>
/// Startup failed and native cleanup also failed. The closed owner is retained here so cleanup remains retryable.
/// Retry disposal of this exception until it succeeds; the original two errors are preserved in InnerException.
/// </summary>
public sealed class D3D9SurfaceInitializationCleanupException : Exception, IDisposable, IAsyncDisposable
{
    private readonly ExperimentalD3D9SurfaceSession cleanupOwner;
    internal D3D9SurfaceInitializationCleanupException(Exception initializationError, Exception cleanupError,
        ExperimentalD3D9SurfaceSession cleanupOwner)
        : base("D3D9 surface initialization and cleanup failed. Dispose this exception to retry cleanup on its retained owner Thread.",
            new AggregateException(initializationError, cleanupError)) => this.cleanupOwner = cleanupOwner;

    public void Dispose() => cleanupOwner.Dispose();
    public ValueTask DisposeAsync() => cleanupOwner.DisposeAsync();
}
