using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>
/// Ordinary-surface, configured object/subset FX, scene-FX or frozen EMM preview experiment. Presents the existing evaluated project
/// snapshot directly to a borrowed HWND on the native owner lane, without capturing RGBA or owning a clock.
/// Unsupported rendering features must not enter this adapter; this does not enable the production App factory.
/// </summary>
public sealed class ExperimentalD3D9SurfacePreviewRenderer : IRealtimeViewRenderer, IViewSceneInputRequests,
    IViewSceneInputPreflight, IViewRenderingStylePreflight
{
    private ViewRenderRequest request;
    private readonly Vector4 background;
    private readonly D3D9SurfaceFrameEncoder encoder;
    private readonly ID3D9SurfacePreviewSession session;
    private readonly Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifetime = new();
    private bool closing, disposed;
    private Task? disposalAttempt;

    public ViewRendererCapabilities Capabilities { get; }
    public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => encoder.SceneInputRequests;

    private ExperimentalD3D9SurfacePreviewRenderer(ViewRenderRequest request, Vector4 background,
        D3D9SurfaceFrameEncoder encoder, ID3D9SurfacePreviewSession session,
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
        }, 4096, 4096, false, true);
    }

    /// <summary>The caller owns the target HWND and must keep it alive until disposal completes.</summary>
    public static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings,
        Vector4 background, D3D9SurfaceIsolation isolation, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, targetHwnd, bindings, background, isolation,
            async (options, _, cancellation) => new NativePreviewSession(await ExperimentalD3D9SurfaceSession
                .CreatePreviewAsync(absoluteNativeDllPath, targetHwnd, options, cancellation).ConfigureAwait(false)), token, classicPassInputs: classicPassInputs);
    }

    internal static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateForTestingAsync(ViewRenderRequest request,
        nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9SurfaceIsolation isolation,
        Func<D3D9SurfaceSessionOptions, CancellationToken, ValueTask<ID3D9SurfacePreviewSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return CreateCoreAsync(request, targetHwnd, bindings, background, isolation,
            (options, _, cancellation) => factory(options, cancellation), token, classicPassInputs: classicPassInputs);
    }

    /// <summary>
    /// Presents explicit limited object/subset FX. The native owner holds the declared dependency leases until
    /// successful destruction and module unload; keep the exact borrowed HWND alive for that same lifetime.
    /// </summary>
    public static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateObjectFxAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings,
        Vector4 background, D3D9ObjectFxAssets assets, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, targetHwnd, bindings, background, default,
            async (options, _, cancellation) => new NativePreviewSession(await ExperimentalD3D9SurfaceSession
                .CreateObjectFxPreviewWithAssetsAsync(absoluteNativeDllPath, targetHwnd, options, assets, cancellation).ConfigureAwait(false)),
            token, assets.Configuration, classicPassInputs: classicPassInputs);
    }

    internal static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateObjectFxForTestingAsync(ViewRenderRequest request,
        nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9ObjectFxConfiguration configuration,
        Func<D3D9SurfaceSessionOptions, CancellationToken, ValueTask<ID3D9SurfacePreviewSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(factory);
        return CreateCoreAsync(request, targetHwnd, bindings, background, default,
            (options, _, cancellation) => factory(options, cancellation), token, configuration, classicPassInputs: classicPassInputs);
    }

    /// <summary>
    /// Presents explicitly configured scene FX. The native owner retains its dependency leases through
    /// successful destruction; the caller keeps the borrowed HWND alive for that same lifetime. Native
    /// scene drawing repeats one evaluated snapshot without advancing another animation or physics clock.
    /// </summary>
    public static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateSceneFxAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings,
        Vector4 background, D3D9SceneFxAssets assets, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, targetHwnd, bindings, background, default,
            async (options, _, cancellation) => new NativePreviewSession(await ExperimentalD3D9SurfaceSession
                .CreateSceneFxPreviewWithAssetsAsync(absoluteNativeDllPath, targetHwnd, options, assets, cancellation).ConfigureAwait(false)),
            token, sceneFx: assets.Configuration, classicPassInputs: classicPassInputs);
    }

    internal static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateSceneFxForTestingAsync(ViewRenderRequest request,
        nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9SceneFxConfiguration configuration,
        Func<D3D9SurfaceSessionOptions, CancellationToken, ValueTask<ID3D9SurfacePreviewSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(factory);
        return CreateCoreAsync(request, targetHwnd, bindings, background, default,
            (options, _, cancellation) => factory(options, cancellation), token, sceneFx: configuration, classicPassInputs: classicPassInputs);
    }

    /// <summary>
    /// Presents frozen EMM configuration using the same evaluated sample and its exact stable binding map.
    /// The native session retains all dependency leases until destruction succeeds; keep the borrowed HWND
    /// alive for that lifetime. Repeated native passes never advance another animation or physics clock.
    /// </summary>
    public static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateEmmAsync(ViewRenderRequest request,
        string absoluteNativeDllPath, nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings,
        Vector4 background, D3D9EmmRuntimeAssets assets, CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteNativeDllPath);
        if (!Path.IsPathFullyQualified(absoluteNativeDllPath))
            throw new ArgumentException("An explicit absolute native DLL path is required.", nameof(absoluteNativeDllPath));
        return CreateCoreAsync(request, targetHwnd, bindings, background, default,
            async (options, nativeObjectIds, cancellation) => new NativePreviewSession(await ExperimentalD3D9SurfaceSession
                .CreateEmmPreviewWithAssetsAsync(absoluteNativeDllPath, targetHwnd, options, assets, nativeObjectIds, cancellation).ConfigureAwait(false)),
            token, emm: assets.Configuration, classicPassInputs: classicPassInputs, mediaInputs: mediaInputs,
            screenCaptureMode: screenCaptureMode);
    }

    internal static Task<ExperimentalD3D9SurfacePreviewRenderer> CreateEmmForTestingAsync(ViewRenderRequest request,
        nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9EmmRuntimeConfiguration configuration,
        Func<D3D9SurfaceSessionOptions, IReadOnlyDictionary<Guid, ulong>, CancellationToken, ValueTask<ID3D9SurfacePreviewSession>> factory,
        CancellationToken token = default, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return CreateCoreAsync(request, targetHwnd, bindings, background, default, factory, token, emm: configuration,
            classicPassInputs: classicPassInputs, mediaInputs: mediaInputs, screenCaptureMode: screenCaptureMode);
    }

    private static async Task<ExperimentalD3D9SurfacePreviewRenderer> CreateCoreAsync(ViewRenderRequest request,
        nint targetHwnd, IEnumerable<D3D9SurfaceObjectBinding> bindings, Vector4 background, D3D9SurfaceIsolation isolation,
        Func<D3D9SurfaceSessionOptions, IReadOnlyDictionary<Guid, ulong>, CancellationToken, ValueTask<ID3D9SurfacePreviewSession>> factory,
        CancellationToken token, D3D9ObjectFxConfiguration? objectFx = null, D3D9SceneFxConfiguration? sceneFx = null,
        D3D9EmmRuntimeConfiguration? emm = null, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? mediaInputs = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        token.ThrowIfCancellationRequested();
        if (mediaInputs is not null && (emm is null || classicPassInputs is null))
            throw new NotSupportedException("Background decoding requires the Classic EMM original-pass path.");
        if (targetHwnd == 0) throw new ArgumentException("An existing target HWND is required.", nameof(targetHwnd));
        var frozen = emm is not null ? D3D9SurfacePreflight.FreezeEmmRequest(request, background)
            : sceneFx is not null ? D3D9SurfacePreflight.FreezeSceneFxRequest(request, background)
            : objectFx is not null ? D3D9SurfacePreflight.FreezeObjectFxRequest(request, background)
            : D3D9SurfacePreflight.FreezeRequest(request, background, isolation);
        // Freeze and validate every binding before loading a native DLL or creating its device.
        var encoder = emm is not null ? new D3D9SurfaceFrameEncoder(bindings, emm, screenCaptureMode)
            : sceneFx is not null ? new D3D9SurfaceFrameEncoder(bindings, sceneFx)
            : objectFx is not null ? new D3D9SurfaceFrameEncoder(bindings, objectFx) : new D3D9SurfaceFrameEncoder(bindings);
        encoder.UseClassicPassInputs(classicPassInputs);
        var session = await factory(new(frozen.Width, frozen.Height), encoder.NativeObjectIds, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The surface preview factory returned null.");
        return new(frozen, background, encoder, session, mediaInputs);
    }

    public async ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
    {
        _ = RequireRequest();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = RequireRequest();
            cancellationToken.ThrowIfCancellationRequested();
            // Complete CPU-only preflight and same-scope encoding before native queue admission.
            var overlays = D3D9SurfaceEditorOverlay.Freeze(frame, encoder.HasClassicPassInputs);
            var offline = D3D9SurfaceFrameInput.FromPreview(D3D9SurfaceEditorOverlay.FrameWithoutOverlay(frame));
            D3D9SurfacePreflight.ValidateFrame(offline, current, encoder.HasClassicPassInputs);
            var media = mediaInputs is null ? null : await mediaInputs(offline, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Classic media resolver returned null.");
            var snapshot = encoder.Encode(offline, current, background, media).WithPreviewOverlay(overlays);
            cancellationToken.ThrowIfCancellationRequested();
            var order = current.EffectiveQuality.SurfaceOrder == SurfaceDrawOrder.Original
                ? D3D9SurfaceRenderOrder.Original : D3D9SurfaceRenderOrder.OpaqueFirst;
            var result = await session.PresentAsync(snapshot, order, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Width != current.Width || result.Height != current.Height || result.SourceFrame != snapshot.SourceFrame)
                throw new InvalidDataException("Native surface presentation dimensions or source sample differ from the encoded frame.");
        }
        finally { gate.Release(); }
    }

    public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
    {
        var current = RequireRequest();
        D3D9SurfacePreflight.ValidateScene(D3D9SurfaceEditorOverlay.SceneWithoutOverlay(scene), current.EffectiveQuality, encoder.HasClassicPassInputs);
        D3D9SurfacePreflight.ValidateStyles(bindings);
    }

    public void ValidateSceneInputs(PreviewFrame frame)
    {
        var current = RequireRequest();
        _ = D3D9SurfaceEditorOverlay.Freeze(frame, encoder.HasClassicPassInputs);
        var offline = D3D9SurfaceFrameInput.FromPreview(D3D9SurfaceEditorOverlay.FrameWithoutOverlay(frame));
        D3D9SurfacePreflight.ValidateFrame(offline, current, encoder.HasClassicPassInputs);
        _ = encoder.Encode(offline, current, background);
    }

    /// <summary>Serializes with presentation and drains native resize before publishing the new dimensions.</summary>
    public async ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        _ = RequireRequest();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = RequireRequest();
            cancellationToken.ThrowIfCancellationRequested();
            var resized = current with { Width = width, Height = height };
            var next = encoder.Scope switch
            {
                D3D9SurfaceScope.ConfiguredEmm => D3D9SurfacePreflight.FreezeEmmRequest(resized, background),
                D3D9SurfaceScope.ConfiguredSceneFx => D3D9SurfacePreflight.FreezeSceneFxRequest(resized, background),
                D3D9SurfaceScope.ConfiguredObjectFx => D3D9SurfacePreflight.FreezeObjectFxRequest(resized, background),
                D3D9SurfaceScope.IsolatedSurface => D3D9SurfacePreflight.FreezeRequest(resized, background,
                    D3D9SurfaceIsolation.NoConfiguredMmeOrEmm),
                _ => throw new InvalidOperationException("Unsupported surface scope.")
            };
            // Once admitted, finish bookkeeping even when cancellation arrives during native Reset.
            // Otherwise native may already have resized while the adapter still accepts the old extent.
            await session.ResizeAsync(width, height, CancellationToken.None).ConfigureAwait(false);
            lock (lifetime) request = next;
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            if (disposed) return ValueTask.CompletedTask;
            closing = true;
            // Failed native Destroy keeps the owner alive; a later Dispose retries cleanup.
            if (disposalAttempt is not { IsCompleted: false }) disposalAttempt = DisposeCoreAsync();
            return new(disposalAttempt);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            lock (lifetime) disposed = true;
        }
        finally { gate.Release(); }
    }

    private ViewRenderRequest RequireRequest()
    {
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            return request;
        }
    }

    private sealed class NativePreviewSession(ExperimentalD3D9SurfaceSession owner) : ID3D9SurfacePreviewSession
    {
        public async ValueTask<D3D9SurfacePresented> PresentAsync(D3D9SurfaceSnapshot snapshot,
            D3D9SurfaceRenderOrder order, CancellationToken token)
        {
            var result = await owner.PresentAsync(snapshot, order, token).ConfigureAwait(false);
            return new(result.Width, result.Height, result.SourceFrame);
        }
        public async ValueTask ResizeAsync(int width, int height, CancellationToken token)
            => await owner.ResizeAsync(width, height, token).ConfigureAwait(false);
        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }
}

/// <summary>CPU-test seam with direct presentation metadata and no RGBA capture operation.</summary>
internal interface ID3D9SurfacePreviewSession : IAsyncDisposable
{
    ValueTask<D3D9SurfacePresented> PresentAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token);
    ValueTask ResizeAsync(int width, int height, CancellationToken token);
}

internal sealed record D3D9SurfacePresented(int Width, int Height, double SourceFrame);
