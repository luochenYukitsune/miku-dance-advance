using System.Numerics;
using Avalonia.Threading;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.FFmpeg;

namespace Mmd.Modern.App;

/// <summary>Classic production assembly over the existing MME host and public ViewOutput renderers.
/// Registration remains gated separately until the remaining native input and real-project checks pass.</summary>
internal sealed partial class EditorClassicRenderEngine : IEditorRenderEngine
{
    private readonly EditorCompatibilityRuntimeSource runtime;
    private readonly string cacheDirectory;
    private readonly IEditorClassicRendererFactory factory;
    private readonly EditorClassicToonResources? toonResources;
    private readonly Dictionary<Guid, ulong> nativeIds = [];
    private ulong nextNativeId = 1;
    private readonly Lazy<IEditorRenderBinding> empty;
    public string Id => "classic.d3d9.mme";
    public IEditorProjectPersistence Persistence { get; }
    public IEditorRenderBinding EmptyBinding => empty.Value;
    internal bool EffectsEnabled { get; set; } = true;
    // 十槽界面“恢复默认”只读取这里冻结的原版图片，不另外搜索路径。
    internal EditorClassicToonResources? ToonResources => toonResources;

    internal EditorClassicRenderEngine(EditorCompatibilityRuntimeSource runtime, string existingAbsoluteCacheDirectory,
        EditorClassicToonResources? toonResources = null)
        : this(runtime, existingAbsoluteCacheDirectory, new NativeFactory(), toonResources) { }

    internal EditorClassicRenderEngine(EditorCompatibilityRuntimeSource runtime, string existingAbsoluteCacheDirectory,
        IEditorClassicRendererFactory factory, EditorClassicToonResources? toonResources = null)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingAbsoluteCacheDirectory);
        if (!Path.IsPathFullyQualified(existingAbsoluteCacheDirectory))
            throw new ArgumentException("An explicit absolute Classic runtime cache directory is required.", nameof(existingAbsoluteCacheDirectory));
        this.runtime = runtime; this.factory = factory; this.toonResources = toonResources;
        cacheDirectory = Path.GetFullPath(existingAbsoluteCacheDirectory);
        Persistence = new EditorMmeProjectPersistence(this,
            toonResources is null ? null : toonResources.LoadCommon,
            toonResources is null ? null : toonResources.LoadDefault, toonResources);
        // This isolated zero-object path is only for the editor's no-session state. A typed document,
        // including configured postprocessing over empty geometry, always takes its EMM path below.
        empty = new(() => new Binding(this, null, new([]), [], EmptyFrame()));
    }

    internal SceneAnimationModelAsset PrepareModelAsset(SceneAnimationModelAsset asset, string path, CancellationToken token)
        => EditorClassicModelAssets.RetainToonTextures(asset, path, token,
            toonResources is null ? null : toonResources.LoadCommon,
            toonResources is null ? null : toonResources.LoadDefault);

    internal EditorClassicToonPackage PrepareToons(SceneProjectEdit edit, EditorClassicToonPackage? previous, CancellationToken token)
        => EditorClassicToonPackage.Capture(edit, toonResources, previous, token);

    internal EditorClassicDocumentState CaptureDocument(SceneProjectSession session, D3D9ClassicEffectsPreparation effects,
        D3D9ClassicAccessoryAssetsState? accessories = null, CancellationToken token = default)
        => EditorClassicDocumentState.Capture(session, effects, accessories).WithToonResources(session, toonResources, token);

    // Import composition only. The planner, native selection rules and collector are existing
    // completed components; this entry supplies the selected engine's pinned CPU runtime.
    internal D3D9ClassicEffectsPreparation PrepareEffects(EmmImportPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); runtime.ValidateFiles();
        using var inspector = new D3D9NativeEffectDependencyInspector(runtime.NativeLibraryPath,
            runtime.D3dxLibraryPath, runtime.CompilerLibraryPath);
        var result = D3D9ClassicEffectsPreparation.Capture(plan,
            request => inspector.DiscoverImplicitRoot(request.OriginAbsoluteObjectPath, request.DefaultEffectAbsolutePath),
            inspector, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (result.Preparation is { } prepared) return prepared;
        var details = result.Diagnostics.Where(value => value.Status != D3D9EffectDependencyStatus.Complete)
            .Select(value => $"[{value.Code}] {value.TopFxOriginAbsolutePath ?? value.CandidateOriginAbsolutePath}\n{value.Message}")
            .Distinct().Take(8);
        var error = new InvalidDataException(EditorLocalization.Text("Mda.Text.Classic.Import.EffectsFailed")
            + Environment.NewLine + string.Join(Environment.NewLine, details));
        error.Data["ClassicEffectDiagnostics"] = result.Diagnostics;
        throw error;
    }

    public IEditorRenderBinding Bind(EditorProjectWorkspace workspace)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(workspace);
        if (!ReferenceEquals(workspace.RenderEngine, this))
            throw new InvalidOperationException("The workspace belongs to another render engine.");
        if (workspace.MaterialBindings.Count != 0)
            throw new NotSupportedException("Modern material mappings are unsupported by the Classic render engine.");
        if (workspace.Session is not { } source) return EmptyBinding;
        var state = workspace.ProjectMetadata as EditorClassicDocumentState ?? throw new NotSupportedException(
            "Classic rendering requires its prepared document metadata and frozen effect assignments.");
        var effects = state.RequireEffects(source);
        if (!EffectsEnabled) effects = effects.WithoutEffects();
        var authored = source.CaptureAuthoredContent();
        var models = authored.Motions.ToDictionary(value => value.ObjectId, value => value.Model);
        var bindings = new List<D3D9SurfaceObjectBinding>(source.Objects.Count + source.Accessories.Count);
        var sources = new List<(OriginalFrameScene Scene, SceneRenderingStyleBinding Style)>();
        foreach (var item in source.Objects)
        {
            if (!models.TryGetValue(item.Id, out var model))
                throw new NotSupportedException("Every Classic model requires its exact authored model and bone metadata.");
            bindings.Add(new(item.Id, NativeId(item.Id), Path.GetFullPath(item.SourcePath), item.Name,
                model.Bones.Select(bone => bone.Name)));
            // Classic consumes original source flags. Model purpose is modern rendering policy.
            var input = item.Source;
            var placed = input.Surface.Snapshot.WithWorld(input.Surface.Snapshot.World * item.World);
            sources.Add((new(new(placed, input.Surface.Materials, input.Surface.Profile), input.Outline,
                    input.OutlineOptions, input.Options, input.Materials, input.Lighting, input.EditorOverlay),
                new(item.Id, source.GetRenderingStyle(item.Id), item.Role, 0, item.Source.Materials.Count, true)));
        }
        foreach (var item in source.Accessories)
        {
            if (state.AccessoryAssets is null
                || !state.AccessoryAssets.AccessoryTextures.TryGetValue(item.ObjectId, out var textures))
                throw new NotSupportedException("Every Classic accessory requires its complete prepared ordinary and sphere texture map.");
            bindings.Add(D3D9SurfaceObjectBinding.CreateAccessory(item.ObjectId, NativeId(item.ObjectId),
                Path.GetFullPath(item.Asset.Path), Path.GetFileName(item.Asset.Path), textures));
            sources.Add((item.Asset.Source,
                new(item.ObjectId, RenderingPresetReference.BuiltIn(RenderingStyleId.Mmd), ModelRole.Unspecified,
                    0, item.Asset.Source.Materials.Count, true)));
        }

        // Identity rows are the resource map's explicit lexical-only form. These captured provenance
        // identities are used solely by CPU preflight; they are never opened or passed to a GPU session.
        // Actual private paths and asset leases are created independently on each renderer worker.
        var origins = effects.Tree?.DependencyGraph?.Nodes.Select(value => value.OriginAbsolutePath)
            ?? Enumerable.Empty<string>();
        if (effects.Tree is not null && effects.Tree.DependencyGraph is null)
            throw new NotSupportedException("Classic execution requires its captured dependency graph.");
        var metadataMap = new D3D9EffectResourceMap(origins.Select(path => new D3D9EffectResourceBinding(path, path)));
        var configuration = D3D9EmmRuntimeConfiguration.Create(effects.FrozenEffects.ImportPlan,
            effects.FrozenEffects.RootSeeds, metadataMap);
        var passInputs = EditorClassicPassInputs.Capture(source, models);
        var mediaSources = EditorClassicMediaInputs.Capture(source, out var screenCaptureMode);
        var inputs = new D3D9SurfacePreflightInputs(bindings, configuration, passInputs.Resolve, screenCaptureMode);
        foreach (var item in sources) inputs.ValidatePotentialSource(item.Scene, [item.Style], new());
        inputs.ValidateSceneStyles(EmptyFrame().Scene,
            [new(Guid.Empty, source.RenderingStyles.DefaultStyle, ModelRole.Unspecified, 0, 0, false)], new());
        var snapshot = source.CaptureRenderSnapshot(inputs.SceneInputRequests);
        var frame = new PreviewFrame(1, 1, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        {
            ResolvedCamera = snapshot.ResolvedCamera, CameraParentTransform = snapshot.CameraParentTransform,
            MorphChannels = snapshot.MorphChannels, RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs,
            ObjectMaterialInputs = snapshot.ObjectMaterialInputs, Accessories = snapshot.Accessories,
            MixedDrawOrder = snapshot.MixedDrawOrder, AccessoryIndexAfterModels = snapshot.AccessoryIndexAfterModels,
            UnpreparedAccessoryIndices = snapshot.UnpreparedAccessoryIndices,
            AccessoryParentBoneWorldMatrices = snapshot.AccessoryParentBoneWorldMatrices
        };
        inputs.ValidateEmmFrame(frame, new(1, 1, new(SurfaceOrder: SurfaceDrawOrder.Original)), Vector4.One);
        return new Binding(this, effects, inputs, sources.ToArray(), frame, passInputs.Resolve, mediaSources, screenCaptureMode);
    }

    private ulong NativeId(Guid id)
    {
        if (!nativeIds.TryGetValue(id, out ulong value))
        {
            if (nextNativeId == 0) throw new InvalidOperationException("Native object identities are exhausted.");
            nativeIds.Add(id, value = nextNativeId++);
        }
        return value;
    }

    private static PreviewFrame EmptyFrame()
    {
        var camera = new PmmCameraPose(new(0, 10, 0), Vector3.Zero, -45, 30, true).CreateCamera();
        return new(1, 1, 0, new(new(SceneSnapshot.CreateEmpty(camera), [])), null, null, null) { ResolvedCamera = camera };
    }

    private sealed class Binding(EditorClassicRenderEngine owner, D3D9ClassicEffectsPreparation? effects,
        D3D9SurfacePreflightInputs inputs, (OriginalFrameScene Scene, SceneRenderingStyleBinding Style)[] sources,
        PreviewFrame sample, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        IReadOnlyList<D3D9ClassicMediaSource>? mediaSources = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null) : IEditorRenderBinding
    {
        private const D3D9SurfaceIsolation EmptyIsolation = D3D9SurfaceIsolation.NoConfiguredMmeOrEmm;
        public IEditorRenderEngine Engine => owner;
        private ViewRenderRequest Validate(ViewRenderRequest request, Vector4 background)
        {
            var frozen = EditorD3D9SurfaceAdapters.ValidateRequest(inputs, request, background, EmptyIsolation);
            foreach (var item in sources) inputs.ValidatePotentialSource(item.Scene, [item.Style], frozen.EffectiveQuality);
            EditorD3D9SurfaceAdapters.ValidateFrame(inputs, sample with { Width = request.Width, Height = request.Height },
                frozen, background, EmptyIsolation);
            return frozen;
        }
        public ExportRequest PrepareExport(ExportRequest request)
        { ArgumentNullException.ThrowIfNull(request); request.Validate(); _ = Validate(request.Render, Vector4.One); return request; }
        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
        {
            RequireWorker();
            if (window == 0) throw new ArgumentException("A borrowed native HWND is required.", nameof(window));
            var frozen = Validate(request, Vector4.One);
            var renderer = owner.factory.CreatePreviewAsync(owner.runtime, owner.cacheDirectory, effects,
                window, frozen, inputs, Vector4.One, classicPassInputs, mediaSources, screenCaptureMode).ConfigureAwait(false).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("No Classic preview renderer was created.");
            return new EditorD3D9SurfaceAdapters.Preview(renderer, inputs, frozen, EmptyIsolation);
        }
        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background)
        {
            RequireWorker();
            var frozen = Validate(request, background);
            var renderer = owner.factory.CreateCaptureAsync(owner.runtime, owner.cacheDirectory, effects,
                frozen, inputs, background, classicPassInputs, mediaSources, screenCaptureMode).ConfigureAwait(false).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("No Classic capture renderer was created.");
            return new EditorD3D9SurfaceAdapters.Capture(renderer, inputs, frozen, background, EmptyIsolation);
        }
        private static void RequireWorker()
        {
            if (Dispatcher.UIThread.CheckAccess())
                throw new InvalidOperationException("Create Classic runtime and GPU resources on the editor render/export worker, not the UI thread.");
        }
    }

    private sealed class NativeFactory : IEditorClassicRendererFactory
    {
        public Task<IRealtimeViewRenderer> CreatePreviewAsync(EditorCompatibilityRuntimeSource runtime, string cacheDirectory,
            D3D9ClassicEffectsPreparation? effects, nint window, ViewRenderRequest request,
            D3D9SurfacePreflightInputs inputs, Vector4 background,
            Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
            IReadOnlyList<D3D9ClassicMediaSource>? mediaSources = null,
            D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
            => CreateAsync(runtime, cacheDirectory, effects, mediaSources, screenCaptureMode, async (assets, media) => assets is null
                ? await ExperimentalD3D9SurfacePreviewRenderer.CreateAsync(request, runtime.NativeLibraryPath,
                    window, inputs.Bindings, background, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm,
                    classicPassInputs: classicPassInputs).ConfigureAwait(false)
                : await ExperimentalD3D9SurfacePreviewRenderer.CreateEmmAsync(request, runtime.NativeLibraryPath,
                    window, inputs.Bindings, background, assets, classicPassInputs: classicPassInputs,
                    mediaInputs: ResolveMediaInputs(media, screenCaptureMode), screenCaptureMode: screenCaptureMode).ConfigureAwait(false),
                (renderer, lifetime) => (IRealtimeViewRenderer)new RuntimePreview(renderer, lifetime));

        public Task<IViewRenderer> CreateCaptureAsync(EditorCompatibilityRuntimeSource runtime, string cacheDirectory,
            D3D9ClassicEffectsPreparation? effects, ViewRenderRequest request, D3D9SurfacePreflightInputs inputs, Vector4 background,
            Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
            IReadOnlyList<D3D9ClassicMediaSource>? mediaSources = null,
            D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
            => CreateAsync(runtime, cacheDirectory, effects, mediaSources, screenCaptureMode, async (assets, media) => assets is null
                ? await ExperimentalD3D9SurfaceViewRenderer.CreateAsync(request, runtime.NativeLibraryPath,
                    inputs.Bindings, background, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm,
                    classicPassInputs: classicPassInputs).ConfigureAwait(false)
                : await ExperimentalD3D9SurfaceViewRenderer.CreateEmmAsync(request, runtime.NativeLibraryPath,
                    inputs.Bindings, background, assets, classicPassInputs: classicPassInputs,
                    mediaInputs: ResolveMediaInputs(media, screenCaptureMode), screenCaptureMode: screenCaptureMode).ConfigureAwait(false),
                (renderer, lifetime) => (IViewRenderer)new RuntimeCapture(renderer, lifetime));

        private static Func<OfflineFrame, CancellationToken, ValueTask<D3D9ClassicMediaInputs>>? ResolveMediaInputs(
            D3D9ClassicMediaDecoder? media, D3D9ClassicScreenCaptureMode? screenCaptureMode)
        {
            if (media is not null) return media.DecodeAsync;
            if (screenCaptureMode is null) return null;
            return (frame, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new D3D9ClassicMediaInputs(frame, [], screenCaptureMode));
            };
        }

        private static async Task<TResult> CreateAsync<TRenderer, TResult>(EditorCompatibilityRuntimeSource runtime,
            string cacheDirectory, D3D9ClassicEffectsPreparation? effects,
            IReadOnlyList<D3D9ClassicMediaSource>? mediaSources,
            D3D9ClassicScreenCaptureMode? screenCaptureMode,
            Func<D3D9EmmRuntimeAssets?, D3D9ClassicMediaDecoder?, Task<TRenderer>> create,
            Func<TRenderer, EditorCompatibilityRuntimeLifetime, TResult> wrap)
            where TRenderer : IAsyncDisposable
        {
            if (effects is null && (mediaSources is { Count: > 0 } || screenCaptureMode is not null))
                throw new NotSupportedException("Classic backgrounds require their prepared EMM document path.");
            var dependencies = new EditorCompatibilityDependencyLifetime();
            D3D9ClassicMediaDecoder? media = null;
            IAsyncDisposable? owner = null;
            try
            {
                dependencies.Add(runtime.LoadDependencies());
                D3D9EmmRuntimeAssets? assets = null;
                if (effects is not null)
                {
                    using var inspector = new D3D9NativeEffectDependencyInspector(runtime.NativeLibraryPath,
                        runtime.D3dxLibraryPath, runtime.CompilerLibraryPath);
                    var prepared = effects.PrepareRuntime(cacheDirectory, inspector);
                    dependencies.Add(prepared);
                    assets = D3D9EmmRuntimeAssets.Open(prepared.Configuration);
                    dependencies.Add(assets);
                }
                if (mediaSources?.Any(value => value.Track.Enabled) == true)
                {
                    // Resolve only the already bundled pair. These worker-owned sessions open
                    // frozen track bytes and never borrow the workspace or original media files.
                    media = await D3D9ClassicMediaDecoder.OpenAsync(
                        new FfmpegSceneVideoDecoder(EditorExportService.ResolveBundledMediaTools()),
                        mediaSources, cacheDirectory, screenCaptureMode: screenCaptureMode).ConfigureAwait(false);
                    owner = media;
                }
                var renderer = await create(assets, media).ConfigureAwait(false);
                owner = new RuntimeOwner(renderer, media);
                return wrap(renderer, new EditorCompatibilityRuntimeLifetime(owner, dependencies));
            }
            catch (D3D9SurfaceInitializationCleanupException error)
            {
                // Native initialization already retained its own failed destruction. Keep the
                // decoder and all frozen effects/modules alive until that retry has succeeded.
                throw new EditorCompatibilityInitializationCleanupException(error,
                    new RuntimeOwner(error, media), dependencies);
            }
            catch (Exception error)
            {
                // The decoder can fail before returning its owner. Its public initialization
                // contract exposes any failed cleanup for transfer to the editor's retry ledger.
                owner ??= error.Data["ClassicMediaCleanupOwner"] as IAsyncDisposable;
                if (owner is not null)
                {
                    try { await owner.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup)
                    { throw new EditorCompatibilityInitializationCleanupException(new AggregateException(error, cleanup),
                        owner, dependencies); }
                }
                try { dependencies.Dispose(); }
                catch (Exception cleanup)
                { throw new EditorCompatibilityInitializationCleanupException(new AggregateException(error, cleanup),
                    EditorCompatibilityCompletedOwner.Instance, dependencies); }
                throw;
            }
        }
    }

    /// <summary>Destroy the native consumer before its decoder. Each completed step stays
    /// completed when a later cleanup fails; effects and runtime modules remain held outside.</summary>
    private sealed class RuntimeOwner(IAsyncDisposable renderer, IAsyncDisposable? media) : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool rendererDisposed, mediaDisposed;
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!rendererDisposed)
                { await renderer.DisposeAsync().ConfigureAwait(false); rendererDisposed = true; }
                if (!mediaDisposed)
                {
                    if (media is not null) await media.DisposeAsync().ConfigureAwait(false);
                    mediaDisposed = true;
                }
            }
            finally { gate.Release(); }
        }
    }

    private sealed class RuntimePreview(IRealtimeViewRenderer renderer, EditorCompatibilityRuntimeLifetime lifetime) : IRealtimeViewRenderer
    {
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default) => renderer.PresentAsync(frame, cancellationToken);
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => renderer.ResizeAsync(width, height, cancellationToken);
        public ValueTask DisposeAsync() => lifetime.DisposeAsync();
    }
    private sealed class RuntimeCapture(IViewRenderer renderer, EditorCompatibilityRuntimeLifetime lifetime) : IViewRenderer
    {
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default) => renderer.CaptureAsync(frame, cancellationToken);
        public ValueTask DisposeAsync() => lifetime.DisposeAsync();
    }
}

internal interface IEditorClassicRendererFactory
{
    Task<IRealtimeViewRenderer> CreatePreviewAsync(EditorCompatibilityRuntimeSource runtime, string cacheDirectory,
        D3D9ClassicEffectsPreparation? effects, nint window, ViewRenderRequest request,
        D3D9SurfacePreflightInputs inputs, Vector4 background,
        Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        IReadOnlyList<D3D9ClassicMediaSource>? mediaSources = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null);
    Task<IViewRenderer> CreateCaptureAsync(EditorCompatibilityRuntimeSource runtime, string cacheDirectory,
        D3D9ClassicEffectsPreparation? effects, ViewRenderRequest request, D3D9SurfacePreflightInputs inputs, Vector4 background,
        Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null,
        IReadOnlyList<D3D9ClassicMediaSource>? mediaSources = null,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null);
}
