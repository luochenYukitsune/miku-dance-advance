using System.Numerics;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorCompatibilityRenderEngineTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnknownOrConfiguredEffectsCannotConstructAnOrdinarySurfaceEngine(int isolation)
    {
        using var files = new RuntimeFiles();
        Assert.Throws<NotSupportedException>(() => new EditorCompatibilityRenderEngine(files.Source,
            (D3D9SurfaceIsolation)isolation, new RecordingFactory()));
    }

    [Fact]
    public void RuntimeSourcesRequireExactAbsoluteFilesAndRetainTheirIdentity()
    {
        using var files = new RuntimeFiles();
        Assert.Throws<ArgumentException>(() => new EditorCompatibilityRuntimeSource("relative.dll", files.D3dx, files.Compiler));
        Assert.Throws<ArgumentException>(() => new EditorCompatibilityRuntimeSource(files.Native, files.Compiler, files.D3dx));
        Assert.Equal(Path.GetFullPath(files.Native), files.Source.NativeLibraryPath);
        File.AppendAllText(files.D3dx, "changed");
        Assert.Throws<InvalidDataException>(files.Source.ValidateFiles);
    }

    [Fact]
    public async Task RuntimeLeaseSurvivesFailedDestroyAndIsReleasedOnlyAfterSuccessfulRetry()
    {
        var owner = new RetryOwner();
        var runtime = new CountingLease();
        var lifetime = new EditorCompatibilityRuntimeLifetime(owner, runtime);
        await Assert.ThrowsAsync<IOException>(() => lifetime.DisposeAsync().AsTask());
        Assert.Equal(0, runtime.Disposals);
        await lifetime.DisposeAsync();
        await lifetime.DisposeAsync();
        Assert.Equal(2, owner.Disposals);
        Assert.Equal(1, runtime.Disposals);
    }

    [Fact]
    public async Task FrozenBindingRoutesBothIndependentOutputsAfterTheWorkspaceHasBeenDisposed()
        => await session.Dispatch(async () =>
        {
            using var files = new RuntimeFiles();
            var factory = new RecordingFactory();
            var engine = new EditorCompatibilityRenderEngine(files.Source, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm, factory);
            var workspace = new EditorProjectWorkspace(engine);
            string path = Path.Combine(files.Directory, "fixture.pmx");
            var asset = PlainAsset(path);
            Guid id = workspace.AddModel(path, asset, ModelRole.Character);
            var live = workspace.Session!;
            double before = live.CurrentFrame;
            var binding = workspace.CaptureRenderBinding();
            Assert.Equal(before, live.CurrentFrame);
            workspace.Dispose();

            var request = new ViewRenderRequest(4, 3);
            await Task.Run(async () =>
            {
                await using var preview = binding.CreatePreview(123, request);
                await using var capture = binding.CreateCapture(request, Vector4.One);
                var prepared = new ExportRequest(new(4, 3, new(60), 0, 0));
                Assert.Same(prepared, binding.PrepareExport(prepared));
                Assert.Equal(1, factory.PreviewCreates);
                Assert.Equal(1, factory.CaptureCreates);
                Assert.Equal((nint)123, factory.Window);
                Assert.Equal(files.Source, factory.Runtime);
                var frozen = Assert.Single(factory.Bindings!);
                Assert.Equal(id, frozen.ObjectId);
                Assert.Equal("fixture", frozen.Name);
                Assert.Equal(Path.GetFullPath(path), frozen.SourcePath);
                Assert.Equal(new[] { "root" }, frozen.BoneNames);
                Assert.Equal(new[] { 0 }, Assert.Single(preview.SceneInputRequests).BoneIndices);
                Assert.True(Assert.Single(preview.SceneInputRequests).IncludeSourceGeometry);
                Assert.Contains("unavailable", preview.AdapterName, StringComparison.Ordinal);
                Assert.Null(preview.Statistics.Frame);
            }, Token);
            Assert.Equal(1, factory.Preview.Disposals);
            Assert.Equal(1, factory.Capture.Disposals);
            return true;
        }, Token);

    [Theory]
    [InlineData("quality")]
    [InlineData("alpha")]
    [InlineData("runtime")]
    public async Task InvalidCreationInputIsRejectedBeforeEitherRendererFactory(string invalid)
        => await session.Dispatch(async () =>
        {
            using var files = new RuntimeFiles();
            var factory = new RecordingFactory();
            var engine = new EditorCompatibilityRenderEngine(files.Source, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm, factory);
            var binding = engine.EmptyBinding;
            var request = new ViewRenderRequest(4, 3, invalid == "quality" ? new(SpatialSamples: 2) : null);
            if (invalid == "runtime") File.AppendAllText(files.Native, "changed");
            await Task.Run(() =>
            {
                if (invalid != "alpha") Assert.ThrowsAny<Exception>(() => binding.CreatePreview(123, request));
                Assert.ThrowsAny<Exception>(() => binding.CreateCapture(request, invalid == "alpha" ? Vector4.Zero : Vector4.One));
            }, Token);
            Assert.Equal(0, factory.PreviewCreates);
            Assert.Equal(0, factory.CaptureCreates);
            return true;
        }, Token);

    [Fact]
    public async Task BindingRejectsUnsupportedPotentialObjectSourcesAndModernMappingsWithoutCreatingGpu()
        => await session.Dispatch(() =>
        {
            using var files = new RuntimeFiles();
            var factory = new RecordingFactory();
            var engine = new EditorCompatibilityRenderEngine(files.Source, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm, factory);
            using var workspace = new EditorProjectWorkspace(engine);
            string path = Path.Combine(files.Directory, "fixture.pmx");
            workspace.AddModel(path, PlainAsset(path), ModelRole.Character);
            workspace.MaterialBindings.Add(Guid.NewGuid(), new("modern", "Modern", new string('0', 64), []));
            Assert.Throws<NotSupportedException>(() => workspace.CaptureRenderBinding());
            workspace.MaterialBindings.Clear();
            // A second object's authored outline is rejected even when a current-frame-only
            // renderer could otherwise omit that object. Every potential source is inspected.
            workspace.AddModel(Path.Combine(files.Directory, "outlined.pmx"),
                EditorProjectWorkspace.CreateModelAsset(MorphApplicationTests.Model([]), path, Token), ModelRole.Prop);
            Assert.Throws<NotSupportedException>(() => workspace.CaptureRenderBinding());
            Assert.Equal(0, factory.PreviewCreates + factory.CaptureCreates);
            return true;
        }, Token);

    [Fact]
    public async Task BindingRejectsAnUnsupportedStyleBeforeCreatingEitherOutput()
        => await session.Dispatch(() =>
        {
            using var files = new RuntimeFiles();
            var factory = new RecordingFactory();
            var engine = new EditorCompatibilityRenderEngine(files.Source, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm, factory);
            using var workspace = new EditorProjectWorkspace(engine);
            string path = Path.Combine(files.Directory, "fixture.pmx");
            workspace.AddModel(path, PlainAsset(path), ModelRole.Character);
            workspace.Session!.SetDefaultRenderingStyle(RenderingStyleId.Genshin, Token);
            Assert.Throws<NotSupportedException>(() => workspace.CaptureRenderBinding());
            Assert.Equal(0, factory.PreviewCreates + factory.CaptureCreates);
            return true;
        }, Token);

    [Theory]
    [InlineData(OriginalSphereMode.Multiply, true)]
    [InlineData(OriginalSphereMode.Add, true)]
    [InlineData(OriginalSphereMode.Multiply, false)]
    [InlineData(OriginalSphereMode.Add, false)]
    [InlineData(OriginalSphereMode.None, true)]
    public async Task HiddenPotentialSourcesValidatePlacementBeforeAnyGpuFactory(OriginalSphereMode sphere, bool placed)
        => await session.Dispatch(() =>
        {
            using var files = new RuntimeFiles();
            var factory = new RecordingFactory();
            var engine = new EditorCompatibilityRenderEngine(files.Source, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm, factory);
            using var workspace = new EditorProjectWorkspace(engine);
            string path = Path.Combine(files.Directory, "hidden.pmx");
            var asset = SphereAsset(path, sphere);
            Guid id = workspace.AddModel(path, asset, ModelRole.Character);
            if (placed) workspace.Edit(edit => edit.Composition.SetTransform(id, new(3, 0, 0), Quaternion.Identity, 1));
            workspace.CommitMotion(id, new Mmd.Modern.Formats.VmdMotion
            {
                ModelName = asset.Model.Name,
                IkKeyframes = [new() { Frame = 0, Visible = false }, new() { Frame = 1, Visible = true }]
            });
            var live = workspace.Session!;
            Assert.False(Assert.Single(live.Actors).Visible);
            double before = live.CurrentFrame;
            if (placed && sphere is OriginalSphereMode.Multiply or OriginalSphereMode.Add)
                Assert.Throws<NotSupportedException>(() => workspace.CaptureRenderBinding());
            else
                Assert.Same(engine, workspace.CaptureRenderBinding().Engine);
            Assert.Equal(before, live.CurrentFrame);
            Assert.False(Assert.Single(live.Actors).Visible);
            Assert.Equal(0, factory.PreviewCreates + factory.CaptureCreates);
            return true;
        }, Token);

    [Fact]
    public async Task NativeCreationCannotBlockTheUiThread()
        => await session.Dispatch(() =>
        {
            using var files = new RuntimeFiles();
            var factory = new RecordingFactory();
            var engine = new EditorCompatibilityRenderEngine(files.Source, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm, factory);
            Assert.Throws<InvalidOperationException>(() => engine.EmptyBinding.CreatePreview(123, new(4, 3)));
            Assert.Throws<InvalidOperationException>(() => engine.EmptyBinding.CreateCapture(new(4, 3), Vector4.One));
            Assert.Equal(0, factory.PreviewCreates + factory.CaptureCreates);
            return true;
        }, Token);

    private static SceneAnimationModelAsset PlainAsset(string path)
    {
        var asset = EditorProjectWorkspace.CreateModelAsset(MorphApplicationTests.Model([]), path, Token);
        var source = asset.Source;
        return asset with { Source = new(source.Surface, source.Outline, source.OutlineOptions! with { Eligible = false },
            source.Options, source.Materials, source.Lighting, source.EditorOverlay) };
    }

    private static SceneAnimationModelAsset SphereAsset(string path, OriginalSphereMode mode)
    {
        var asset = PlainAsset(path);
        if (mode == OriginalSphereMode.None) return asset;
        var source = asset.Source;
        var snapshot = source.Surface.Snapshot;
        var textured = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, snapshot.Materials,
            [new SceneTexture("sphere", 1, 1, [255, 255, 255, 255])], world: snapshot.World,
            camera: snapshot.Camera, lightDirection: snapshot.LightDirection,
            additionalUvs: snapshot.AdditionalUvs, rawRadialSeeds: snapshot.RawRadialSeeds);
        return asset with { Source = new(new(textured, source.Surface.Materials.Select(value => value with
            { SphereTexture = 0, SphereMode = mode }), source.Surface.Profile), source.Outline,
            source.OutlineOptions, source.Options, source.Materials, source.Lighting, source.EditorOverlay) };
    }

    private sealed class RecordingFactory : IEditorCompatibilityRendererFactory
    {
        internal int PreviewCreates, CaptureCreates;
        internal nint Window;
        internal EditorCompatibilityRuntimeSource? Runtime;
        internal IReadOnlyList<D3D9SurfaceObjectBinding>? Bindings;
        internal PreviewRenderer Preview { get; } = new();
        internal CaptureRenderer Capture { get; } = new();
        public Task<IRealtimeViewRenderer> CreatePreviewAsync(EditorCompatibilityRuntimeSource runtime, nint window,
            ViewRenderRequest request, D3D9SurfacePreflightInputs inputs, Vector4 background, D3D9SurfaceIsolation isolation)
        { PreviewCreates++; Runtime = runtime; Window = window; Bindings = inputs.Bindings; return Task.FromResult<IRealtimeViewRenderer>(Preview); }
        public Task<IViewRenderer> CreateCaptureAsync(EditorCompatibilityRuntimeSource runtime, ViewRenderRequest request,
            D3D9SurfacePreflightInputs inputs, Vector4 background, D3D9SurfaceIsolation isolation)
        { CaptureCreates++; Runtime = runtime; Bindings = inputs.Bindings; return Task.FromResult<IViewRenderer>(Capture); }
    }

    private sealed class PreviewRenderer : IRealtimeViewRenderer
    {
        internal int Disposals;
        public ViewRendererCapabilities Capabilities => new("d3d9-isolated-surface-experimental", 4096, 4096, false, true);
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class CaptureRenderer : IViewRenderer
    {
        internal int Disposals;
        public ViewRendererCapabilities Capabilities => new("d3d9-isolated-surface-experimental", 4096, 4096, true, false);
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new FrameBufferLease(frame.Width, frame.Height, frame.Width * 4, new byte[frame.Width * frame.Height * 4]));
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class RetryOwner : IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync()
        {
            if (++Disposals == 1) throw new IOException("injected Destroy failure");
            return ValueTask.CompletedTask;
        }
    }
    private sealed class CountingLease : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    private sealed class RuntimeFiles : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "mda-compat-engine-" + Guid.NewGuid().ToString("N"));
        internal string Native => Path.Combine(Directory, "Mmd.Modern.D3D9.Compatibility.dll");
        internal string D3dx => Path.Combine(Directory, "d3dx9_43.dll");
        internal string Compiler => Path.Combine(Directory, "d3dcompiler_43.dll");
        internal EditorCompatibilityRuntimeSource Source { get; }
        internal RuntimeFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Native, "fake native"); File.WriteAllText(D3dx, "fake d3dx"); File.WriteAllText(Compiler, "fake compiler");
            Source = new(Native, D3dx, Compiler);
        }
        public void Dispose()
        {
            string absolute = Path.GetFullPath(Directory);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("mda-compat-engine-", StringComparison.Ordinal)
                || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected compatibility engine fixture cleanup path.");
            System.IO.Directory.Delete(absolute, recursive: true);
        }
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
