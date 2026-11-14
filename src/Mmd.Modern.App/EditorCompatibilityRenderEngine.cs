using System.Numerics;
using Avalonia.Threading;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>Internal ordinary-surface experiment only. Not a registered MME engine or startup choice.
/// The caller must establish absence of configured MME/EMM effects; render geometry cannot prove it.</summary>
internal sealed class EditorCompatibilityRenderEngine : IEditorRenderEngine
{
    private readonly EditorCompatibilityRuntimeSource runtime;
    private readonly D3D9SurfaceIsolation isolation;
    private readonly IEditorCompatibilityRendererFactory factory;
    private readonly Dictionary<Guid, ulong> nativeIds = [];
    private ulong nextNativeId = 1;
    private readonly Lazy<IEditorRenderBinding> empty;
    public string Id => "compatibility.d3d9.ordinary-surface-experimental";
    public IEditorProjectPersistence Persistence { get; }
    public IEditorRenderBinding EmptyBinding => empty.Value;

    internal EditorCompatibilityRenderEngine(EditorCompatibilityRuntimeSource runtime, D3D9SurfaceIsolation isolation)
        : this(runtime, isolation, new NativeFactory()) { }

    internal EditorCompatibilityRenderEngine(EditorCompatibilityRuntimeSource runtime, D3D9SurfaceIsolation isolation,
        IEditorCompatibilityRendererFactory factory)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(factory);
        if (isolation != D3D9SurfaceIsolation.NoConfiguredMmeOrEmm)
            throw new NotSupportedException("Unknown or configured MME/EMM effects cannot enter the isolated ordinary-surface engine.");
        this.runtime = runtime; this.isolation = isolation; this.factory = factory;
        Persistence = new EditorClassicProjectPersistence(this);
        empty = new(() => new Binding(this, new([]), [], EmptyFrame()));
    }

    public IEditorRenderBinding Bind(EditorProjectWorkspace workspace)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(workspace);
        if (!ReferenceEquals(workspace.RenderEngine, this)) throw new InvalidOperationException("The workspace belongs to another render engine.");
        if (workspace.MaterialBindings.Count != 0)
            throw new NotSupportedException("Modern material mappings are unsupported by the isolated ordinary-surface engine.");
        if (workspace.Session is not { } source) return EmptyBinding;
        Persistence.ValidateRenderContent(workspace);
        var objects = source.Objects.ToArray();
        var authored = source.CaptureAuthoredContent();
        var models = authored.Motions.ToDictionary(actor => actor.ObjectId, actor => actor.Model);
        var bindings = new List<D3D9SurfaceObjectBinding>(objects.Length);
        var sources = new List<(OriginalFrameScene Scene, SceneRenderingStyleBinding Style)>(objects.Length);
        foreach (var item in objects)
        {
            if (!models.TryGetValue(item.Id, out var model))
                throw new NotSupportedException("Every potential object requires matching model and bone metadata.");
            if (!nativeIds.TryGetValue(item.Id, out ulong nativeId))
            {
                if (nextNativeId == 0) throw new InvalidOperationException("Native object identities are exhausted.");
                nativeIds.Add(item.Id, nativeId = nextNativeId++);
            }
            bindings.Add(new(item.Id, nativeId, Path.GetFullPath(item.SourcePath), item.Name, model.Bones.Select(bone => bone.Name)));
            var input = SceneModelShadingPolicy.Apply(item.Source, source.GetModelPurpose(item.Id));
            // Preserve the source's own World and apply placement exactly once, including for
            // actors currently hidden by VMD. Core WithWorld validates and shares immutable streams.
            var placed = input.Surface.Snapshot.WithWorld(input.Surface.Snapshot.World * item.World);
            var potentialSource = new OriginalFrameScene(new(placed, input.Surface.Materials, input.Surface.Profile),
                input.Outline, input.OutlineOptions, input.Options, input.Materials, input.Lighting, input.EditorOverlay);
            sources.Add((potentialSource,
                new(item.Id, source.GetRenderingStyle(item.Id), item.Role, 0, item.Source.Materials.Count, true)));
        }
        var inputs = new D3D9SurfacePreflightInputs(bindings);
        // Validate even sources not present in the current combined geometry. No GI, shadows,
        // outlines or modern responses are silently removed to make this experiment render.
        foreach (var item in sources) inputs.ValidatePotentialSource(item.Scene, [item.Style], new());
        inputs.ValidateSceneStyles(EmptyFrame().Scene,
            [new(Guid.Empty, source.RenderingStyles.DefaultStyle, ModelRole.Unspecified, 0, 0, false)], new());
        if (source.Media.Count != 0)
            throw new NotSupportedException("Media tracks are not represented by the isolated ordinary-surface input.");
        if (source.PmmControls is { } controls && controls.ExportDocument().Accessories.Count != 0)
            throw new NotSupportedException("PMM accessories and effect helpers are unsupported by the isolated ordinary-surface engine.");
        if (authored.SceneMotion?.Motion.SelfShadowKeyframes.Any(key => key.Mode != 0) == true)
            throw new NotSupportedException("Animated self shadow is unsupported by the isolated ordinary-surface engine.");
        var snapshot = source.CaptureRenderSnapshot(inputs.SceneInputRequests);
        var frame = new PreviewFrame(1, 1, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        {
            ResolvedCamera = snapshot.ResolvedCamera, CameraParentTransform = snapshot.CameraParentTransform,
            MorphChannels = snapshot.MorphChannels, RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs
        };
        inputs.ValidateFrame(frame, new(1, 1), Vector4.One, isolation);
        return new Binding(this, inputs, sources.ToArray(), frame);
    }

    private static PreviewFrame EmptyFrame()
    {
        var camera = new PmmCameraPose(new(0, 10, 0), Vector3.Zero, -45, 30, true).CreateCamera();
        return new(1, 1, 0, new(new(SceneSnapshot.CreateEmpty(camera), [])), null, null, null) { ResolvedCamera = camera };
    }

    private sealed class Binding(EditorCompatibilityRenderEngine owner, D3D9SurfacePreflightInputs inputs,
        (OriginalFrameScene Scene, SceneRenderingStyleBinding Style)[] sources, PreviewFrame sample) : IEditorRenderBinding
    {
        public IEditorRenderEngine Engine => owner;
        private ViewRenderRequest Validate(ViewRenderRequest request, Vector4 background)
        {
            var frozen = inputs.ValidateRequest(request, background, owner.isolation);
            foreach (var item in sources) inputs.ValidatePotentialSource(item.Scene, [item.Style], frozen.EffectiveQuality);
            inputs.ValidateFrame(sample with { Width = request.Width, Height = request.Height }, frozen, background, owner.isolation);
            return frozen;
        }
        public ExportRequest PrepareExport(ExportRequest request)
        { ArgumentNullException.ThrowIfNull(request); request.Validate(); _ = Validate(request.Render, Vector4.One); return request; }
        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
        {
            RequireWorker();
            if (window == 0) throw new ArgumentException("A borrowed native HWND is required.", nameof(window));
            var frozen = Validate(request, Vector4.One);
            owner.runtime.ValidateFiles();
            var renderer = owner.factory.CreatePreviewAsync(owner.runtime, window, frozen, inputs, Vector4.One, owner.isolation)
                .ConfigureAwait(false).GetAwaiter().GetResult() ?? throw new InvalidOperationException("No compatibility preview renderer was created.");
            return new EditorD3D9SurfaceAdapters.Preview(renderer, inputs, frozen, owner.isolation);
        }
        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background)
        {
            RequireWorker();
            var frozen = Validate(request, background);
            owner.runtime.ValidateFiles();
            var renderer = owner.factory.CreateCaptureAsync(owner.runtime, frozen, inputs, background, owner.isolation)
                .ConfigureAwait(false).GetAwaiter().GetResult() ?? throw new InvalidOperationException("No compatibility capture renderer was created.");
            return new EditorD3D9SurfaceAdapters.Capture(renderer, inputs, frozen, background, owner.isolation);
        }
        private static void RequireWorker()
        {
            if (Dispatcher.UIThread.CheckAccess())
                throw new InvalidOperationException("Create compatibility GPU resources on the editor render/export worker, not the UI thread.");
        }
    }

    private sealed class NativeFactory : IEditorCompatibilityRendererFactory
    {
        public async Task<IRealtimeViewRenderer> CreatePreviewAsync(EditorCompatibilityRuntimeSource runtime, nint window,
            ViewRenderRequest request, D3D9SurfacePreflightInputs inputs, Vector4 background, D3D9SurfaceIsolation isolation)
        {
            var dependencies = runtime.LoadDependencies();
            try
            {
                var renderer = await ExperimentalD3D9SurfacePreviewRenderer.CreateAsync(request, runtime.NativeLibraryPath,
                    window, inputs.Bindings, background, isolation).ConfigureAwait(false);
                return new RuntimePreview(renderer, dependencies);
            }
            catch (D3D9SurfaceInitializationCleanupException error)
            { throw new EditorCompatibilityInitializationCleanupException(error, dependencies); }
            catch { dependencies.Dispose(); throw; }
        }
        public async Task<IViewRenderer> CreateCaptureAsync(EditorCompatibilityRuntimeSource runtime, ViewRenderRequest request,
            D3D9SurfacePreflightInputs inputs, Vector4 background, D3D9SurfaceIsolation isolation)
        {
            var dependencies = runtime.LoadDependencies();
            try
            {
                var renderer = await ExperimentalD3D9SurfaceViewRenderer.CreateAsync(request, runtime.NativeLibraryPath,
                    inputs.Bindings, background, isolation).ConfigureAwait(false);
                return new RuntimeCapture(renderer, dependencies);
            }
            catch (D3D9SurfaceInitializationCleanupException error)
            { throw new EditorCompatibilityInitializationCleanupException(error, dependencies); }
            catch { dependencies.Dispose(); throw; }
        }
    }

    private sealed class RuntimePreview(IRealtimeViewRenderer renderer, IDisposable dependencies) : IRealtimeViewRenderer
    {
        private readonly EditorCompatibilityRuntimeLifetime lifetime = new(renderer, dependencies);
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default) => renderer.PresentAsync(frame, cancellationToken);
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => renderer.ResizeAsync(width, height, cancellationToken);
        public ValueTask DisposeAsync() => lifetime.DisposeAsync();
    }
    private sealed class RuntimeCapture(IViewRenderer renderer, IDisposable dependencies) : IViewRenderer
    {
        private readonly EditorCompatibilityRuntimeLifetime lifetime = new(renderer, dependencies);
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default) => renderer.CaptureAsync(frame, cancellationToken);
        public ValueTask DisposeAsync() => lifetime.DisposeAsync();
    }
}

/// <summary>Runtime modules remain loaded until the actual native owner has been destroyed.
/// A failed Destroy is retryable and must never release its dependencies.</summary>
internal sealed class EditorCompatibilityRuntimeLifetime(IAsyncDisposable owner, IDisposable dependencies) : IAsyncDisposable
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
            dependencies.Dispose();
            disposed = true;
        }
        finally { gate.Release(); }
    }
}

/// <summary>Retains both the native exception's retryable owner and its explicit runtime modules.</summary>
internal sealed class EditorCompatibilityInitializationCleanupException : Exception, IDisposable, IEditorRenderInitializationCleanup
{
    private readonly EditorCompatibilityRuntimeLifetime lifetime;
    internal EditorCompatibilityInitializationCleanupException(D3D9SurfaceInitializationCleanupException error, IDisposable dependencies)
        : this(error, error, dependencies) { }
    internal EditorCompatibilityInitializationCleanupException(Exception error, IAsyncDisposable owner, IDisposable dependencies)
        : base("Compatibility initialization cleanup failed. Dispose this exception to retry owner cleanup before releasing its runtimes.", error)
        => lifetime = new(owner, dependencies);
    public void Dispose() => DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => lifetime.DisposeAsync();
}

internal interface IEditorCompatibilityRendererFactory
{
    Task<IRealtimeViewRenderer> CreatePreviewAsync(EditorCompatibilityRuntimeSource runtime, nint window,
        ViewRenderRequest request, D3D9SurfacePreflightInputs inputs, Vector4 background, D3D9SurfaceIsolation isolation);
    Task<IViewRenderer> CreateCaptureAsync(EditorCompatibilityRuntimeSource runtime, ViewRenderRequest request,
        D3D9SurfacePreflightInputs inputs, Vector4 background, D3D9SurfaceIsolation isolation);
}
