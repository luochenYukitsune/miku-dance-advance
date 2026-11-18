using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRenderEngineRoutingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ModernCaptureRetainsOptionalDeviceIdentityAcrossTheEditorBinding()
    {
        // Metadata-only check: this must not create a D3D device while another export is active.
        var capture = typeof(EditorModernRenderEngine).GetNestedType("OutputRenderer",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(capture);
        Assert.True(typeof(IViewRendererDeviceInfoProvider).IsAssignableFrom(capture),
            "The editor capture wrapper must retain the selected renderer's device identity for resource sampling.");
    }

    [Fact]
    public async Task ProductionViewportCreationUsesTheSelectedEngineWithoutCreatingAnUnusedRenderer()
        => await session.Dispatch(async () =>
        {
            var engine = new TestEngine();
            using var viewport = new EditorViewportHost(() => (100, 80), engine);
            Assert.Equal(0, engine.PreviewCreations);
            var completed = new TaskCompletionSource<EditorViewportPresentationStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewport.FramePresented += value => completed.TrySetResult(value);
            viewport.Error += error => completed.TrySetException(error);
            viewport.RequestRender();
            var statistics = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(1, engine.PreviewCreations);
            Assert.Equal(1, statistics.PresentCount);
            Assert.Equal(100, engine.Request!.Width);
            Assert.Equal(80, engine.Request.Height);
            Assert.Equal(nint.Zero, engine.Window);
            Assert.Equal(viewport.Quality, engine.Request.Quality);
            viewport.IsSuspended = true;
            await viewport.SuspendAndReleaseResourcesAsync(Token);
            Assert.Equal(1, engine.Renderer.Disposals);
            return true;
        }, Token);

    [Fact]
    public async Task EditorUsesTheWorkspacesFixedEngineAndRejectsForeignRenderInputs()
        => await session.Dispatch(() =>
        {
            var engine = new TestEngine();
            var camera = new PmmCameraPose(new(0, 10, 0), Vector3.Zero, -45, 30, true).CreateCamera();
            using var shell = new EditorShellViewModel(camera, null, engine);
            var window = new EditorWindow(false) { DataContext = shell };
            var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
            try
            {
                Assert.Same(shell.Workspace.RenderEngine, viewport.RenderEngine);
                Assert.True(engine.Bindings > 0);
                Assert.Equal(0, engine.PreviewCreations);
                var other = new TestEngine("other-engine");
                Assert.Throws<InvalidOperationException>(() => EditorRenderEngineBinding.Require(engine, other.EmptyBinding));
                Assert.Throws<InvalidOperationException>(() => EditorRenderEngineBinding.Require(engine, new TestEngine(engine.Id).EmptyBinding));
                Assert.Throws<InvalidOperationException>(() => EditorRenderEngineBinding.Require(engine, null));
                Assert.Throws<InvalidOperationException>(() => viewport.SetMaterialBindings([]));
            }
            finally { window.DataContext = null; viewport.Dispose(); window.Close(); }
            return true;
        }, Token);

    [Fact]
    public async Task LegacyModernMappingEntryPointKeepsItsInputValidationWithoutCreatingGpuResources()
        => await session.Dispatch(() =>
        {
            using var viewport = new EditorViewportHost();
            Assert.Throws<ArgumentNullException>(() => viewport.SetMaterialBindings(null!));
            Assert.Throws<ArgumentException>(() => viewport.SetMaterialBindings([null!]));
            viewport.SetMaterialBindings([]);
            Assert.Null(viewport.Statistics);
            Assert.Same(EditorModernRenderEngine.Instance, viewport.RenderEngine);
            return true;
        }, Token);

    [Fact]
    public async Task ChangingFrozenInputsWhilePresentIsBlockedReleasesOldRendererAndReportsOnlyNewBinding()
        => await session.Dispatch(async () =>
        {
            var engine = new TestEngine();
            var old = new TestViewport { BlockPresent = true };
            var fresh = new TestViewport { CounterBase = 1000 };
            var before = new FrozenBinding(engine, old);
            var after = new FrozenBinding(engine, fresh);
            using var viewport = new EditorViewportHost(() => (100, 80), engine);
            var completed = new TaskCompletionSource<EditorViewportPresentationStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
            int callbacks = 0;
            viewport.FramePresented += value => { callbacks++; completed.TrySetResult(value); };
            viewport.Error += error => completed.TrySetException(error);
            try
            {
                viewport.SetRenderBinding(before);
                await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                viewport.SetRenderBinding(after);
                old.Release.TrySetResult();
                var statistics = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.Equal(1001, statistics.PresentCount);
                Assert.Equal(1, callbacks);
                Assert.Equal(1, old.Disposals);
                Assert.Equal(1, before.Creations);
                Assert.Equal(1, after.Creations);
                Assert.Equal(0, engine.PreviewCreations);
                viewport.IsSuspended = true;
                await viewport.SuspendAndReleaseResourcesAsync(Token);
                Assert.Equal(1, fresh.Disposals);
            }
            finally { old.Release.TrySetResult(); fresh.Release.TrySetResult(); }
            return true;
        }, Token);

    [Fact]
    public void WorkspaceEnablesDeferredVertexSkinningOnlyForModernRenderEngine()
    {
        var model = new PmxModel
        {
            Header = new()
            {
                Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 4, TextureIndexSize = 4, MaterialIndexSize = 4, BoneIndexSize = 4, MorphIndexSize = 4, RigidBodyIndexSize = 4
            },
            Name = "routing-actor", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices =
            [
                new() { Position = Vector3.Zero, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 },
                new() { Position = Vector3.UnitX, Normal = Vector3.UnitZ, Uv = Vector2.UnitX, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 },
                new() { Position = Vector3.UnitY, Normal = Vector3.UnitZ, Uv = Vector2.UnitY, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 },
            ],
            Faces = [new(0, 1, 2)],
            Textures = [],
            Materials =
            [
                new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1, Ambient = new(0.2f), DrawMode = PmxDrawModeFlags.None, EdgeColor = new(0, 0, 0, 1), EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }
            ],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate }],
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        string dir = Path.Combine(Path.GetTempPath(), "ws-defer-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string pmxPath = Path.Combine(dir, "actor.pmx");
            File.WriteAllBytes(pmxPath, PmxWriter.ToBytes(model));
            var source = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
                new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
            var asset = new SceneAnimationModelAsset(model, source);

            var animatedMotion = new VmdMotion
            {
                ModelName = model.Name,
                BoneKeyframes =
                [
                    new() { BoneName = "root", Frame = 0 },
                    new() { BoneName = "root", Frame = 10, Translation = new(1, 0, 0) },
                ],
            };
            using var modernWorkspace = new EditorProjectWorkspace();
            Guid modernActorId = modernWorkspace.AddModel(pmxPath, asset, new EditorModelImportOptions(new ModelPurpose(ModelRole.Character)));
            Assert.NotNull(modernWorkspace.Session);
            Assert.True(modernWorkspace.Session!.DeferVertexSkinningToRenderer);
            modernWorkspace.CommitMotion(modernActorId, animatedMotion);
            modernWorkspace.Session.Seek(5, Token);
            Assert.True(modernWorkspace.Session.Frame.Surface.Snapshot.HasDeferredDeformation);

            using var nonModernWorkspace = new EditorProjectWorkspace(new TestEngine("classic.non-modern"));
            Guid classicActorId = nonModernWorkspace.AddModel(pmxPath, asset, new EditorModelImportOptions(new ModelPurpose(ModelRole.Character)));
            Assert.NotNull(nonModernWorkspace.Session);
            Assert.False(nonModernWorkspace.Session!.DeferVertexSkinningToRenderer);
            nonModernWorkspace.CommitMotion(classicActorId, animatedMotion);
            nonModernWorkspace.Session.Seek(5, Token);
            Assert.False(nonModernWorkspace.Session.Frame.Surface.Snapshot.HasDeferredDeformation);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private sealed class FrozenBinding(IEditorRenderEngine owner, TestViewport renderer) : IEditorRenderBinding
    {
        public IEditorRenderEngine Engine => owner;
        public int Creations;
        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request) { Creations++; return renderer; }
        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background) => throw new InvalidOperationException("Unexpected capture.");
        public ExportRequest PrepareExport(ExportRequest request) => request;
    }

    private sealed class TestEngine(string id = "test.selected-engine") : IEditorRenderEngine, IEditorRenderBinding
    {
        public string Id => id;
        public IEditorProjectPersistence Persistence => new EditorModernProjectPersistence(this);
        public string EngineId => id;
        public IEditorRenderEngine Engine => this;
        public IEditorRenderBinding EmptyBinding => this;
        public int PreviewCreations, Bindings;
        public nint Window;
        public ViewRenderRequest? Request;
        public TestViewport Renderer { get; } = new();
        public IEditorRenderBinding Bind(EditorProjectWorkspace workspace) { Bindings++; return this; }
        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
        { PreviewCreations++; Window = window; Request = request; return Renderer; }
        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background) => throw new InvalidOperationException("Unexpected capture.");
        public ExportRequest PrepareExport(ExportRequest request) => request;
    }

    private sealed class TestViewport : IEditorViewportRenderer
    {
        public int Disposals, Presents;
        public bool BlockPresent;
        public int CounterBase;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string AdapterName => "Selected test backend";
        public bool IsWarp => false;
        public EditorViewportPresentationStatistics Statistics => new(CounterBase + Presents, 0, 0, 0, 100, 80, 2, null);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => [];
        public ViewRendererCapabilities Capabilities => new("test.selected-engine", 4096, 4096, false, true);
        public async ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Presents++; Entered.TrySetResult();
            if (BlockPresent) await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
