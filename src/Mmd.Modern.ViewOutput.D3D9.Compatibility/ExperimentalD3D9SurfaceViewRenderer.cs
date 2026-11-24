using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>
/// Ordinary-surface, configured object/subset FX, scene-FX or frozen EMM capture experiment. Uses the existing evaluated project
/// snapshot and native owner lane; it creates no animation/physics clock and exposes no Present.
/// Unimplemented rendering features must not enter this adapter; this does not enable the production App factory.
/// </summary>
public sealed class ExperimentalD3D9SurfaceViewRenderer : IViewRenderer, IViewSceneInputRequests,
    IViewSceneInputPreflight, IViewRenderingStylePreflight
{
    private readonly ViewRenderRequest request;
    private readonly Vector4 background;
    private readonly D3D9SurfaceFrameEncoder encoder;
    private readonly ID3D9SurfaceCaptureSession session;
    private readonly Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly object lifetime = new();
    private bool closing, disposed;
    private Task? disposalAttempt;

    public ViewRendererCapabilities Capabilities { get; }
    public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => encoder.SceneInputRequests;

    private ExperimentalD3D9SurfaceViewRenderer(ViewRenderRequest request, Vector4 background,
        D3D9SurfaceFrameEncoder encoder, ID3D9SurfaceCaptureSession session,
        Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs = null)
    {
        this.request = request; this.background = background; this.encoder = encoder; this.session = session;
        this.mediaInputs = mediaInputs;
        Capabilities = new(encoder.Scope switch
        {
            D3D9SurfaceScope.ConfiguredObjectFx => "d3d9-object-fx-experimental",
            D3D9SurfaceScope.ConfiguredSceneFx => "d3d9-scene-fx-experimental",
            D3D9SurfaceScope.ConfiguredEmm => "d3d9-emm-experimental",
            _ => "d3d9-isolated-surface-experimental"
        }, 4096, 4096, true, false);
    }

    public static Task<ExperimentalD3D9SurfaceViewRenderer> CreateAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background,
        D3D9SurfaceIsolation isolation, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, bindings, background, isolation,
            async (options, _, cancellation) => new NativeCaptureSession(await ExperimentalD3D9SurfaceSession
                .CreateAsync(absoluteNativeDllPath, options, cancellation).ConfigureAwait(false)), token, classicPassInputs: classicPassInputs);
    }

    internal static Task<ExperimentalD3D9SurfaceViewRenderer> CreateForTestingAsync(ViewRenderRequest request,
        IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9SurfaceIsolation isolation,
        Func<D3D9SurfaceSessionOptions, CancellationToken, ValueTask<ID3D9SurfaceCaptureSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return CreateCoreAsync(request, bindings, background, isolation,
            (options, _, cancellation) => factory(options, cancellation), token, classicPassInputs: classicPassInputs);
    }

    /// <summary>
    /// Uses an explicit limited object/subset FX mapping and caller-declared complete dependency manifest.
    /// The native session retains its own asset lease through successful disposal, including cleanup retries;
    /// the caller may dispose the shared assets owner once no more renderers need to be created.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceViewRenderer> CreateObjectFxAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background,
        D3D9ObjectFxAssets assets, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, bindings, background, default,
            async (options, _, cancellation) => new NativeCaptureSession(await ExperimentalD3D9SurfaceSession
                .CreateObjectFxWithAssetsAsync(absoluteNativeDllPath, options, assets, cancellation).ConfigureAwait(false)),
            token, assets.Configuration, classicPassInputs: classicPassInputs);
    }

    internal static Task<ExperimentalD3D9SurfaceViewRenderer> CreateObjectFxForTestingAsync(ViewRenderRequest request,
        IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9ObjectFxConfiguration configuration,
        Func<D3D9SurfaceSessionOptions, CancellationToken, ValueTask<ID3D9SurfaceCaptureSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(factory);
        return CreateCoreAsync(request, bindings, background, default,
            (options, _, cancellation) => factory(options, cancellation), token, configuration, classicPassInputs: classicPassInputs);
    }

    /// <summary>
    /// Uses explicit scene FX and caller-declared complete dependency assets. The native session retains
    /// its asset lease until successful disposal, including failed-cleanup retries. This adapter submits
    /// one evaluated sample; the native scene host owns all offscreen and repeated drawing passes.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceViewRenderer> CreateSceneFxAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background,
        D3D9SceneFxAssets assets, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, bindings, background, default,
            async (options, _, cancellation) => new NativeCaptureSession(await ExperimentalD3D9SurfaceSession
                .CreateSceneFxWithAssetsAsync(absoluteNativeDllPath, options, assets, cancellation).ConfigureAwait(false)),
            token, sceneFx: assets.Configuration, classicPassInputs: classicPassInputs);
    }

    internal static Task<ExperimentalD3D9SurfaceViewRenderer> CreateSceneFxForTestingAsync(ViewRenderRequest request,
        IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9SceneFxConfiguration configuration,
        Func<D3D9SurfaceSessionOptions, CancellationToken, ValueTask<ID3D9SurfaceCaptureSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(factory);
        return CreateCoreAsync(request, bindings, background, default,
            (options, _, cancellation) => factory(options, cancellation), token, sceneFx: configuration, classicPassInputs: classicPassInputs);
    }

    /// <summary>
    /// Captures frozen EMM configuration through the same evaluated-frame path. Stable binding Guids and
    /// kinds must exactly match the import plan; native identities are copied from those bindings. The native
    /// session retains the frozen complete dependency lease through successful disposal and cleanup retries.
    /// </summary>
    public static Task<ExperimentalD3D9SurfaceViewRenderer> CreateEmmAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background,
        D3D9EmmRuntimeAssets assets, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, bindings, background, default,
            async (options, nativeObjectIds, cancellation) => new NativeCaptureSession(await ExperimentalD3D9SurfaceSession
                .CreateEmmWithAssetsAsync(absoluteNativeDllPath, options, assets, nativeObjectIds, cancellation).ConfigureAwait(false)),
            token, emm: assets.Configuration, classicPassInputs: classicPassInputs, mediaInputs: mediaInputs,
            screenCaptureMode: screenCaptureMode);
    }

    internal static Task<ExperimentalD3D9SurfaceViewRenderer> CreateEmmForTestingAsync(ViewRenderRequest request,
        IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9EmmRuntimeConfiguration configuration,
        Func<D3D9SurfaceSessionOptions, IReadOnlyDictionary<Guid, ulong>, CancellationToken, ValueTask<ID3D9SurfaceCaptureSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return CreateCoreAsync(request, bindings, background, default, factory, token, emm: configuration,
            classicPassInputs: classicPassInputs, mediaInputs: mediaInputs, screenCaptureMode: screenCaptureMode);
    }

    private static async Task<ExperimentalD3D9SurfaceViewRenderer> CreateCoreAsync(ViewRenderRequest request,
        IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9SurfaceIsolation isolation,
        Func<D3D9SurfaceSessionOptions, IReadOnlyDictionary<Guid, ulong>, CancellationToken, ValueTask<ID3D9SurfaceCaptureSession>> factory,
        CancellationToken token, D3D9ObjectFxConfiguration? objectFx = null, D3D9SceneFxConfiguration? sceneFx = null,
        D3D9EmmRuntimeConfiguration? emm = null, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        token.ThrowIfCancellationRequested();
        if (mediaInputs is not null && (emm is null || classicPassInputs is null))
            throw new NotSupportedException("Background decoding requires the Classic EMM original-pass path.");
        var (frozen, encoder) = D3D9SurfacePreflight.PrepareRenderer(request, background, isolation,
            bindings, objectFx, sceneFx, emm, classicPassInputs, screenCaptureMode);
        var session = await factory(new(frozen.Width, frozen.Height), encoder.NativeObjectIds, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The surface capture factory returned null.");
        return new(frozen, background, encoder, session, mediaInputs);
    }

    public async ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
    {
        ThrowIfClosing();
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            cancellationToken.ThrowIfCancellationRequested();
            // CPU-only validation and complete encoding precede every native queue admission.
            D3D9SurfacePreflight.ValidateFrame(frame, request, encoder.HasClassicPassInputs);
            var media = mediaInputs is null ? null : await mediaInputs(frame, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Classic media resolver returned null.");
            var snapshot = encoder.Encode(frame, request, background, media);
            cancellationToken.ThrowIfCancellationRequested();
            var order = request.EffectiveQuality.SurfaceOrder == SurfaceDrawOrder.Original
                ? D3D9SurfaceRenderOrder.Original : D3D9SurfaceRenderOrder.OpaqueFirst;
            var result = await session.CaptureAsync(snapshot, order, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Width != request.Width || result.Height != request.Height || result.SourceFrame != snapshot.SourceFrame)
                throw new InvalidDataException("Native surface capture dimensions or source sample differ from the encoded frame.");
            // Producer relinquishes this independent managed array. Disposal never invokes COM or the owner lane.
            return new(result.Width, result.Height, checked(result.Width * 4), result.Rgba);
        }
        finally { captureGate.Release(); }
    }

    public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
    {
        ThrowIfClosing();
        D3D9SurfacePreflight.ValidateScene(scene, request.EffectiveQuality, encoder.HasClassicPassInputs);
        D3D9SurfacePreflight.ValidateStyles(bindings);
    }

    public void ValidateSceneInputs(PreviewFrame frame)
    {
        ThrowIfClosing();
        var offline = D3D9SurfaceFrameInput.FromPreview(frame);
        D3D9SurfacePreflight.ValidateFrame(offline, request, encoder.HasClassicPassInputs);
        _ = encoder.Encode(offline, request, background);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            if (disposed) return ValueTask.CompletedTask;
            closing = true;
            // A failed native Destroy retains its owner; another Dispose retries its safe cleanup path.
            if (disposalAttempt is not { IsCompleted: false }) disposalAttempt = DisposeCoreAsync();
            return new(disposalAttempt);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            lock (lifetime) disposed = true;
        }
        finally { captureGate.Release(); }
    }

    private void ThrowIfClosing() { lock (lifetime) ObjectDisposedException.ThrowIf(closing, this); }

    private sealed class NativeCaptureSession(ExperimentalD3D9SurfaceSession owner) : ID3D9SurfaceCaptureSession
    {
        public async ValueTask<D3D9SurfacePixels> CaptureAsync(D3D9SurfaceSnapshot snapshot,
            D3D9SurfaceRenderOrder order, CancellationToken token)
        {
            var result = await owner.CaptureAsync(snapshot, order, token).ConfigureAwait(false);
            return new(result.Width, result.Height, result.SourceFrame, result.Rgba);
        }
        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }
}

/// <summary>CPU-test seam. Capture transfers its independent RGBA array; cleanup must await in-flight calls.</summary>
internal interface ID3D9SurfaceCaptureSession : IAsyncDisposable
{
    ValueTask<D3D9SurfacePixels> CaptureAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token);
}

internal sealed record D3D9SurfacePixels(int Width, int Height, double SourceFrame, byte[] Rgba);
