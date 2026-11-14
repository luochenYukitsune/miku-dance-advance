using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

/// <summary>Neutral realtime render input. GPU resources and native presentation handles remain inside the adapter.</summary>
public sealed record PreviewFrame(int Width, int Height, double SourceFrame, OriginalFrameScene Scene,
    PmmCameraPose? Camera, PmmLightPose? Light, VmdSelfShadowPose? SelfShadow)
{
    /// <summary>Optional world-space camera already resolved by the scene owner; raw Camera remains editor metadata.</summary>
    public SceneCamera? ResolvedCamera { get; init; }
    public System.Numerics.Matrix4x4? CameraParentTransform { get; init; }
    public ViewTemporalFrameState TemporalState { get; init; } = new();
    /// <summary>Auxiliary actor-local channels. Scene.Surface.Materials contains authoritative evaluated
    /// render factors; this dictionary alone does not apply or override combined material bindings.</summary>
    public IReadOnlyDictionary<Guid, CharacterMorphChannels> MorphChannels { get; init; }
        = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, CharacterMorphChannels>(new Dictionary<Guid, CharacterMorphChannels>());
    /// <summary>Project-owned preset references and evaluated combined material ranges, including hidden objects.
    /// Legacy animation/PMM owners leave this list empty; preset resolution belongs to the render adapter.</summary>
    public IReadOnlyList<SceneRenderingStyleBinding> RenderingStyles { get; init; } = Array.Empty<SceneRenderingStyleBinding>();
    public IReadOnlyDictionary<Guid, SceneObjectRenderInputs> SceneInputs { get; init; } = SceneObjectRenderInputs.Empty;
    /// <summary>Optional detached source-local material tables from this exact sample, including hidden models.</summary>
    public IReadOnlyDictionary<Guid, SceneObjectMaterialInputs> ObjectMaterialInputs { get; init; } = SceneObjectMaterialInputs.Empty;
    public IReadOnlyList<SceneProjectAccessoryFrame> Accessories { get; init; } = Array.Empty<SceneProjectAccessoryFrame>();
    public IReadOnlyList<SceneProjectDrawItem> MixedDrawOrder { get; init; } = Array.Empty<SceneProjectDrawItem>();
    public int? AccessoryIndexAfterModels { get; init; }
    public IReadOnlyList<byte> UnpreparedAccessoryIndices { get; init; } = Array.Empty<byte>();
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, System.Numerics.Matrix4x4>> AccessoryParentBoneWorldMatrices { get; init; }
        = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, System.Numerics.Matrix4x4>>(
            new Dictionary<Guid, IReadOnlyDictionary<int, System.Numerics.Matrix4x4>>());
}

/// <summary>Present is a GPU path: ordinary preview must not implement present by CPU Capture.
/// Implementations own their native viewport binding and wait for GPU resources before resize/disposal.</summary>
public interface IRealtimeViewRenderer : IAsyncDisposable
{
    ViewRendererCapabilities Capabilities { get; }
    ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default);
}

/// <summary>An independently owned preview clock and renderer, serialized across present, resize, pause and close.
/// The host supplies elapsed time; offline output never uses this preview clock or physics world.</summary>
public sealed class PreviewSession : IAsyncDisposable
{
    private readonly ViewPlaybackOwner session;
    private readonly IRealtimeViewRenderer renderer;
    private readonly IReadOnlyList<SceneObjectRenderRequest> sceneInputRequests;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ViewRenderRequest request;
    private bool paused, disposed;
    private long historyGeneration, cameraCutKey;
    private PreviewSession(ViewPlaybackOwner session, IRealtimeViewRenderer renderer, ViewRenderRequest request,
        IReadOnlyList<SceneObjectRenderRequest> sceneInputRequests)
    { this.session = session; this.renderer = renderer; this.request = request; this.sceneInputRequests = sceneInputRequests; }

    public static async ValueTask<PreviewSession> CreateAsync(ViewRenderRequest request,
        Func<CancellationToken, ValueTask<SceneAnimationSession>> sessionFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IRealtimeViewRenderer>> rendererFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        return await CreateCoreAsync(request, async token => ViewPlaybackOwner.From(await sessionFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The preview session factory returned null.")), rendererFactory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates an independently owned PMM preview, including its resolved bone-parented camera.</summary>
    public static ValueTask<PreviewSession> CreatePmmAsync(ViewRenderRequest request,
        Func<CancellationToken, ValueTask<PmmProjectPlayback>> playbackFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IRealtimeViewRenderer>> rendererFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playbackFactory);
        return CreateCoreAsync(request, async token => ViewPlaybackOwner.From(await playbackFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The PMM preview factory returned null.")), rendererFactory, cancellationToken);
    }

    /// <summary>The factory must return a newly owned project, never another view's live project or clock.</summary>
    public static ValueTask<PreviewSession> CreateProjectAsync(ViewRenderRequest request,
        Func<CancellationToken, ValueTask<SceneProjectSession>> projectFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IRealtimeViewRenderer>> rendererFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectFactory);
        return CreateCoreAsync(request, async token => ViewPlaybackOwner.From(await projectFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The project preview factory returned null.")), rendererFactory, cancellationToken);
    }

    /// <summary>Owns the newly loaded PMA session and retains its extracted resources until preview closes.</summary>
    public static ValueTask<PreviewSession> CreatePmaAsync(ViewRenderRequest request,
        Func<CancellationToken, ValueTask<PmaLoadedProject>> projectFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IRealtimeViewRenderer>> rendererFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectFactory);
        return CreateCoreAsync(request, async token => ViewPlaybackOwner.From(await projectFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The PMA preview factory returned null.")), rendererFactory, cancellationToken);
    }

    private static async ValueTask<PreviewSession> CreateCoreAsync(ViewRenderRequest request,
        Func<CancellationToken, ValueTask<ViewPlaybackOwner>> sessionFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IRealtimeViewRenderer>> rendererFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(sessionFactory); ArgumentNullException.ThrowIfNull(rendererFactory);
        request.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var session = await sessionFactory(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The preview session factory returned null.");
        IRealtimeViewRenderer? renderer = null;
        try
        {
            renderer = await rendererFactory(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The preview renderer factory returned null.");
            renderer.Capabilities.Validate(request, capture: false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(session, renderer, request, ViewSceneInputRequests.Capture(renderer));
        }
        catch (Exception original)
        {
            var errors = new List<Exception>();
            if (renderer is not null) { try { await renderer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); } }
            try { session.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0) original.Data["ViewOutputCleanupErrors"] = errors.AsReadOnly();
            throw;
        }
    }

    public async ValueTask PresentAsync(double elapsedSeconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAlive();
            if (renderer is IViewRenderingStylePreflight || renderer is IViewSceneInputPreflight || sceneInputRequests.Count != 0)
            {
                var current = session.Snapshot(request.Width, request.Height, sceneInputRequests);
                if (renderer is IViewRenderingStylePreflight preflight)
                    preflight.ValidateRenderingStyles(current.Scene, current.RenderingStyles);
                if (renderer is IViewSceneInputPreflight inputPreflight) inputPreflight.ValidateSceneInputs(current);
            }
            if (!paused) session.Advance(elapsedSeconds, cancellationToken);
            var frame = session.Snapshot(request.Width, request.Height, sceneInputRequests) with
            {
                TemporalState = new()
                {
                    FrameTimeMilliseconds = elapsedSeconds >= float.Epsilon && elapsedSeconds <= float.MaxValue / 1000d
                        ? (float)(elapsedSeconds * 1000) : null,
                    HistoryGeneration = historyGeneration, CameraCutKey = cameraCutKey
                }
            };
            await renderer.PresentAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async ValueTask SetPausedAsync(bool value, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { RequireAlive(); paused = value; } finally { gate.Release(); }
    }

    /// <summary>Call after a host seek/restart or a discontinuous camera edit. Continuous orbit motion keeps history.</summary>
    public async ValueTask InvalidateTemporalHistoryAsync(bool cameraCut = false, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAlive();
            if (cameraCut) cameraCutKey = unchecked(cameraCutKey + 1);
            else historyGeneration = unchecked(historyGeneration + 1);
        }
        finally { gate.Release(); }
    }

    public async ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAlive(); var next = request with { Width = width, Height = height };
            renderer.Capabilities.Validate(next, capture: false);
            await renderer.ResizeAsync(width, height, cancellationToken).ConfigureAwait(false);
            request = next;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return; disposed = true;
            Exception? failure = null;
            try { await renderer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
            try { session.Dispose(); }
            catch (Exception error) { if (failure is null) failure = error; else failure.Data["ViewOutputCleanupErrors"] = new[] { error }; }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { gate.Release(); }
    }

    private void RequireAlive() => ObjectDisposedException.ThrowIf(disposed, this);
}
