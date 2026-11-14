using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ProjectStyleTransportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "view-project-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ProjectStyleTransportTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ProjectPreviewCarriesCurrentSnapshotAndKeepsPriorSelectionsDetached()
    {
        var project = CreateProject(); var renderer = new RecordingPreviewRenderer();
        await using var preview = await PreviewSession.CreateProjectAsync(new(20, 10),
            _ => ValueTask.FromResult(project), (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.1, Token);
        var first = Assert.Single(renderer.Frames); var snapshot = project.CaptureRenderSnapshot();
        Assert.Equal(3, first.SourceFrame, 8); Assert.Same(snapshot.Scene, first.Scene);
        Assert.Equal(snapshot.Camera, first.Camera); Assert.Equal(snapshot.Light, first.Light);
        Assert.Equal(snapshot.SelfShadow, first.SelfShadow); Assert.Equal(snapshot.ResolvedCamera, first.ResolvedCamera);
        Assert.NotNull(first.Camera); Assert.NotNull(first.Light); Assert.NotNull(first.SelfShadow);
        Assert.Equal(snapshot.MorphChannels.Keys, first.MorphChannels.Keys);
        Assert.Equal(snapshot.RenderingStyles.ToArray(), first.RenderingStyles.ToArray());
        Assert.Equal(new[] { "studio.default", "studio.object" }, first.RenderingStyles.Select(binding => binding.Preset.StableId));
        Assert.Equal(new[] { ModelRole.Character, ModelRole.Environment }, first.RenderingStyles.Select(binding => binding.Role));
        Assert.Equal(new[] { 0, 1 }, first.RenderingStyles.Select(binding => binding.MaterialStart));
        Assert.All(first.RenderingStyles, binding => { Assert.True(binding.Visible); Assert.Equal(1, binding.MaterialCount); });

        project.SetDefaultRenderingStyle(new("studio.changed"), Token);
        await preview.SetPausedAsync(true, Token); await preview.PresentAsync(.5, Token);
        Assert.Equal(3, project.CurrentFrame, 8);
        Assert.Equal("studio.changed", renderer.Frames[1].RenderingStyles[0].Preset.StableId);
        Assert.Equal("studio.default", first.RenderingStyles[0].Preset.StableId);
        await preview.DisposeAsync();
        Assert.Equal(1, renderer.Disposals); Assert.Throws<ObjectDisposedException>(() => project.Frame);
    }

    [Fact]
    public async Task ProjectExportUsesExactExistingSchedulerAndAnIndependentPreviewClock()
    {
        var previewProject = CreateProject(); var previewRenderer = new RecordingPreviewRenderer();
        await using var preview = await PreviewSession.CreateProjectAsync(new(20, 10),
            _ => ValueTask.FromResult(previewProject), (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(previewRenderer), Token);
        await preview.PresentAsync(.1, Token);
        var exportProject = CreateProject(); var renderer = new RecordingRenderer(); var sink = new TestSink();
        renderer.Capturing = frame =>
        {
            var snapshot = exportProject.CaptureRenderSnapshot();
            Assert.Same(snapshot.Scene, frame.Scene); Assert.Equal(snapshot.Camera, frame.Camera);
            Assert.Equal(snapshot.Light, frame.Light); Assert.Equal(snapshot.SelfShadow, frame.SelfShadow);
            Assert.Equal(snapshot.ResolvedCamera, frame.ResolvedCamera);
            Assert.Equal(snapshot.RenderingStyles.ToArray(), frame.RenderingStyles.ToArray());
            Assert.Equal(snapshot.MorphChannels.Keys, frame.MorphChannels.Keys);
        };
        var coordinator = ExportCoordinator.ForProject(_ => ValueTask.FromResult(exportProject),
            (_, _) => ValueTask.FromResult<IViewRenderer>(renderer));
        var result = await coordinator.RunAsync(new(new(2, 2, new(60000, 1001), 0, 30)), sink, cancellationToken: Token);
        Assert.Equal(60, result.CompletedFrames); Assert.Equal(118, result.SimulationTicks);
        Assert.Equal(new OfflineFrameTime(1, 2), renderer.Frames[1].Sample.SampledSourceFrame);
        Assert.Equal(new OfflineFrameTime(1001, 60000), sink.Samples[1].PresentationTime);
        Assert.Equal(3, previewProject.CurrentFrame, 8); Assert.Equal(0, previewRenderer.Disposals);
        Assert.Throws<ObjectDisposedException>(() => exportProject.Frame);
        Assert.Equal(60, renderer.Releases); Assert.Equal(1, renderer.Disposals); Assert.True(sink.Completed);
    }

    [Fact]
    public async Task ProjectFactoriesDisposeAcquiredOwnersWhenRendererCreationFails()
    {
        var previewProject = CreateProject();
        await Assert.ThrowsAsync<IOException>(async () => await PreviewSession.CreateProjectAsync(new(20, 10),
            _ => ValueTask.FromResult(previewProject), (_, _) => throw new IOException("preview renderer"), Token));
        Assert.Throws<ObjectDisposedException>(() => previewProject.Frame);
        var exportProject = CreateProject(); var sink = new TestSink();
        var coordinator = ExportCoordinator.ForProject(_ => ValueTask.FromResult(exportProject),
            (_, _) => throw new IOException("export renderer"));
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.Throws<ObjectDisposedException>(() => exportProject.Frame);
        Assert.True(sink.Aborted); Assert.True(sink.Disposed);
    }

    [Fact]
    public async Task CancelledProjectExportReleasesLeaseAndOwnedProject()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var project = CreateProject(); var renderer = new RecordingRenderer();
        var sink = new TestSink { Writing = (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; } };
        var coordinator = ExportCoordinator.ForProject(_ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IViewRenderer>(renderer));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 30)), sink, cancellationToken: cancel.Token));
        Assert.Single(renderer.Frames); Assert.Equal(1, renderer.Releases); Assert.Equal(1, renderer.Disposals);
        Assert.Equal(ExportTaskState.Cancelled, coordinator.State); Assert.True(sink.Aborted); Assert.False(sink.AbortTokenWasCancelled);
        Assert.Throws<ObjectDisposedException>(() => project.Frame);
    }

    [Fact]
    public async Task RejectedStylePreflightDoesNotAdvancePreviewClockAndCanBeRetriedAfterConfirmation()
    {
        var project = CreateProject(); var renderer = new RecordingPreviewRenderer { RejectStyles = true };
        await using var preview = await PreviewSession.CreateProjectAsync(new(20, 10), _ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await preview.PresentAsync(.1, Token));
        Assert.Equal(0, project.CurrentFrame); Assert.Empty(renderer.Frames); Assert.Equal(1, renderer.Preflights);
        renderer.RejectStyles = false;
        await preview.PresentAsync(.1, Token);
        Assert.Equal(3, project.CurrentFrame, 8); Assert.Single(renderer.Frames); Assert.Equal(2, renderer.Preflights);
    }

    [Fact]
    public async Task PreviewPreflightReceivesHiddenObjectReferencesBeforeAdvancing()
    {
        var project = CreateProject(hideSecond: true);
        var renderer = new RecordingPreviewRenderer { RejectStyles = true, Validating = bindings =>
        {
            Assert.Equal(2, bindings.Count);
            Assert.True(bindings[0].Visible); Assert.False(bindings[1].Visible); Assert.Equal(0, bindings[1].MaterialCount);
            Assert.Equal("studio.object", bindings[1].Preset.StableId);
        } };
        await using var preview = await PreviewSession.CreateProjectAsync(new(20, 10), _ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await preview.PresentAsync(.1, Token));
        Assert.Equal(0, project.CurrentFrame); Assert.Empty(renderer.Frames);
    }

    [Fact]
    public async Task RejectedStylePreflightDoesNotSeekExportOrBeginSinkAndCleansOwnedResources()
    {
        var project = CreateProject(); project.Seek(10, Token);
        var renderer = new RecordingRenderer { RejectStyles = true };
        renderer.Disposing = () => Assert.Equal(10, project.CurrentFrame);
        var sink = new TestSink();
        var coordinator = ExportCoordinator.ForProject(_ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IViewRenderer>(renderer));
        await Assert.ThrowsAsync<NotSupportedException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.False(sink.Begun); Assert.True(sink.Aborted); Assert.True(sink.Disposed);
        Assert.Empty(renderer.Frames); Assert.Equal(1, renderer.Preflights); Assert.Equal(1, renderer.Disposals);
        Assert.Throws<ObjectDisposedException>(() => project.Frame);
    }

    [Fact]
    public async Task PmaPreviewRetainsExtractedResourcesUntilRendererCloses()
    {
        string path = SavePma(); var loaded = LoadPma(path); string cache = loaded.CacheDirectory;
        var renderer = new RecordingPreviewRenderer { Disposing = () => Assert.True(Directory.Exists(cache)) };
        var preview = await PreviewSession.CreatePmaAsync(new(20, 10), _ => ValueTask.FromResult(loaded),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.1, Token);
        Assert.True(Directory.Exists(cache)); Assert.All(loaded.Models, model => Assert.True(File.Exists(Path.Combine(cache, model.EditedModelPath))));
        Assert.Equal(loaded.Session.CaptureRenderSnapshot().RenderingStyles.ToArray(), Assert.Single(renderer.Frames).RenderingStyles.ToArray());
        await preview.DisposeAsync(); await preview.DisposeAsync();
        Assert.Equal(1, renderer.Disposals); Assert.False(Directory.Exists(cache));
        Assert.Throws<ObjectDisposedException>(() => loaded.Session.Frame);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("capability")]
    [InlineData("cancellation")]
    public async Task PmaPreviewFailureDisposesSessionAndExtraction(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var loaded = LoadPma(SavePma()); string cache = loaded.CacheDirectory;
        var renderer = new RecordingPreviewRenderer { CanPresent = failure != "capability" };
        async Task Create() => await PreviewSession.CreatePmaAsync(new(20, 10),
            _ => { if (failure == "cancellation") cancel.Cancel(); return ValueTask.FromResult(loaded); },
            (_, _) => failure == "factory" ? throw new IOException("renderer") : ValueTask.FromResult<IRealtimeViewRenderer>(renderer), cancel.Token);
        if (failure == "factory") await Assert.ThrowsAsync<IOException>(Create);
        else if (failure == "capability") await Assert.ThrowsAsync<NotSupportedException>(Create);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(Create);
        Assert.False(Directory.Exists(cache)); Assert.Throws<ObjectDisposedException>(() => loaded.Session.Frame);
        Assert.Equal(failure == "factory" ? 0 : 1, renderer.Disposals);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("factory")]
    [InlineData("preflight")]
    [InlineData("capture")]
    [InlineData("cancellation")]
    public async Task PmaExportRetainsResourcesThroughCaptureAndCleansEveryOutcome(string outcome)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var loaded = LoadPma(SavePma()); string cache = loaded.CacheDirectory;
        var renderer = new RecordingRenderer
        {
            RejectStyles = outcome == "preflight",
            Capturing = frame =>
            {
                Assert.True(Directory.Exists(cache)); Assert.NotEmpty(frame.RenderingStyles);
                if (outcome == "capture") throw new IOException("capture");
            },
            Disposing = () => Assert.True(Directory.Exists(cache))
        };
        var sink = new TestSink { Writing = (_, token) =>
        {
            Assert.True(Directory.Exists(cache));
            if (outcome == "cancellation") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            return ValueTask.CompletedTask;
        } };
        var coordinator = ExportCoordinator.ForPma(_ => ValueTask.FromResult(loaded),
            (_, _) => outcome == "factory" ? throw new IOException("renderer") : ValueTask.FromResult<IViewRenderer>(renderer));
        var run = coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: cancel.Token);
        if (outcome == "complete") { await run; Assert.True(sink.Completed); }
        else if (outcome is "capture" or "factory") await Assert.ThrowsAsync<IOException>(() => run);
        else if (outcome == "preflight") await Assert.ThrowsAsync<NotSupportedException>(() => run);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(Directory.Exists(cache)); Assert.Throws<ObjectDisposedException>(() => loaded.Session.Frame);
        Assert.Equal(outcome == "factory" ? 0 : 1, renderer.Disposals);
        Assert.Equal(outcome is "capture" or "factory" or "preflight" ? 0 : 1, renderer.Releases);
        if (outcome is "factory" or "preflight") Assert.False(sink.Begun);
        Assert.True(sink.Disposed); Assert.Equal(outcome != "complete", sink.Aborted);
    }

    [Fact]
    public async Task LegacySceneOwnersCarryEmptyStyleBindings()
    {
        var fixture = new OutputFixture(); var renderer = new RecordingPreviewRenderer();
        await using var preview = await PreviewSession.CreateAsync(new(20, 10), fixture.CreateSession,
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.1, Token); Assert.Empty(Assert.Single(renderer.Frames).RenderingStyles);
        var exportFixture = new OutputFixture(); var capture = new RecordingRenderer();
        await new ExportCoordinator(exportFixture.CreateSession, (_, _) => ValueTask.FromResult<IViewRenderer>(capture))
            .RunAsync(new(new(2, 2, new(30), 0, 0)), new TestSink(), cancellationToken: Token);
        Assert.Empty(Assert.Single(capture.Frames).RenderingStyles);
    }

    [Fact]
    public async Task LegacyPmmOwnersCarryEmptyStyleBindingsAndTheirResolvedCamera()
    {
        var playback = CreatePmm(); var renderer = new RecordingPreviewRenderer();
        await using var preview = await PreviewSession.CreatePmmAsync(new(20, 10), _ => ValueTask.FromResult(playback),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.1, Token);
        var frame = Assert.Single(renderer.Frames);
        Assert.Empty(frame.RenderingStyles); Assert.Equal(playback.CameraPose, frame.Camera); Assert.Equal(playback.Camera, frame.ResolvedCamera);
        var exportPlayback = CreatePmm(); var capture = new RecordingRenderer();
        await ExportCoordinator.ForPmm(_ => ValueTask.FromResult(exportPlayback), (_, _) => ValueTask.FromResult<IViewRenderer>(capture))
            .RunAsync(new(new(2, 2, new(30), 0, 0)), new TestSink(), cancellationToken: Token);
        Assert.Empty(Assert.Single(capture.Frames).RenderingStyles);
    }

    [Fact]
    public async Task NewFactoriesRejectNullArgumentsAndNullFactoryResults()
    {
        Func<CancellationToken, ValueTask<SceneProjectSession>> projectFactory = _ => ValueTask.FromResult<SceneProjectSession>(null!);
        Func<CancellationToken, ValueTask<PmaLoadedProject>> pmaFactory = _ => ValueTask.FromResult<PmaLoadedProject>(null!);
        Func<ViewRenderRequest, CancellationToken, ValueTask<IRealtimeViewRenderer>> previewFactory = (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(new RecordingPreviewRenderer());
        Func<ViewRenderRequest, CancellationToken, ValueTask<IViewRenderer>> rendererFactory = (_, _) => ValueTask.FromResult<IViewRenderer>(new RecordingRenderer());
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await PreviewSession.CreateProjectAsync(new(2, 2), null!, previewFactory, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await PreviewSession.CreatePmaAsync(new(2, 2), null!, previewFactory, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await PreviewSession.CreateProjectAsync(new(2, 2), projectFactory, null!, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await PreviewSession.CreatePmaAsync(new(2, 2), pmaFactory, null!, Token));
        Assert.Throws<ArgumentNullException>(() => ExportCoordinator.ForProject(null!, rendererFactory));
        Assert.Throws<ArgumentNullException>(() => ExportCoordinator.ForPma(null!, rendererFactory));
        Assert.Throws<ArgumentNullException>(() => ExportCoordinator.ForProject(projectFactory, null!));
        Assert.Throws<ArgumentNullException>(() => ExportCoordinator.ForPma(pmaFactory, null!));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await PreviewSession.CreateProjectAsync(new(2, 2), projectFactory, previewFactory, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await PreviewSession.CreatePmaAsync(new(2, 2), pmaFactory, previewFactory, Token));
        var projectSink = new TestSink(); var pmaSink = new TestSink();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportCoordinator.ForProject(projectFactory, rendererFactory)
            .RunAsync(new(new(2, 2, new(30), 0, 0)), projectSink, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportCoordinator.ForPma(pmaFactory, rendererFactory)
            .RunAsync(new(new(2, 2, new(30), 0, 0)), pmaSink, cancellationToken: Token));
        Assert.True(projectSink.Disposed); Assert.True(pmaSink.Disposed);
    }

    private SceneProjectSession CreateProject(bool hideSecond = false)
    {
        var model = Model(); string source = Path.Combine(directory, "actor.pmx");
        File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var layout = new SceneCompositionEditor();
        var first = layout.Add("character", source, ModelRole.Character, Rest(model));
        var second = layout.Add("environment", source, ModelRole.Environment, Rest(model));
        var motion = SceneMotionAsset.FromMotion(new()
        { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitX }] });
        var environmentMotion = SceneMotionAsset.FromMotion(new()
        { BoneKeyframes = motion.Motion.BoneKeyframes, IkKeyframes = [new() { Visible = !hideSecond }] });
        var sceneMotion = SceneMotionAsset.FromMotion(new()
        {
            CameraKeyframes = [new() { Distance = -30, Interest = new(1, 2, 3), ViewAngle = 35 }],
            LightKeyframes = [new() { Color = new(.2f, .4f, .6f), Direction = new(1, -2, 3) }],
            SelfShadowKeyframes = [new() { Mode = 2, Distance = .002f }]
        }, "scene.vmd");
        var project = new SceneProjectSession(layout, new(),
            [new(first.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion), new(second.Id, model, environmentMotion.Motion, PhysicsEnabled: false, Asset: environmentMotion)],
            new NoPhysics(), sceneMotion: sceneMotion, cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("studio.default"), Token);
        project.SetModelRenderingStyle(second.Id, new RenderingPresetReference("studio.object"), Token);
        return project;
    }

    private string SavePma()
    {
        using var project = CreateProject(); string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pma");
        PmaProjectStore.Save(path, project, new() { CacheDirectory = Path.Combine(directory, "save-cache") }, Token);
        return path;
    }
    private PmmProjectPlayback CreatePmm()
    {
        var model = Model(); string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var document = new PmmDocument
        {
            Models = [new() { Index = 0, Visible = true, LastFrame = 30, Name = new(0, "actor", []), Path = new(0, source, []),
                InitialModelKeyframe = new() { Visible = true } }],
            OutputWidth = 1280, OutputHeight = 720, CameraFov = 30,
            Settings = new() { PhysicsSimulationMode = 1, CameraLookAtModelIndex = -1 },
            Camera = new() { Position = new(0, 0, -10), Perspective = true,
                InitialKeyframe = new() { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                    Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
        };
        var plan = PmmProjectImport.Prepare(document, Path.Combine(directory, "source.pmm"), cancellationToken: Token);
        Assert.True(plan.CanCommit);
        return new(plan, new Dictionary<int, OriginalFrameScene> { [0] = Rest(model) },
            (_, _) => throw new InvalidOperationException("No physics bodies in PMM fixture."));
    }
    private PmaLoadedProject LoadPma(string path) => PmaProjectStore.Load(path, Path.Combine(directory, "load-cache"),
        (source, token) => { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(source); var model = PmxReader.Read(input); return new(model, Rest(model)); },
        new NoPhysics(), cancellationToken: Token);
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static PmxModel Model() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "test", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
        Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
            SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = [], DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = [],
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
    };
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException("No rigid bodies in fixture.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException("No shared physics in fixture.");
    }
    private sealed class RecordingPreviewRenderer : IRealtimeViewRenderer, IViewRenderingStylePreflight
    {
        public bool CanPresent = true;
        public ViewRendererCapabilities Capabilities => new("test-preview", 4096, 4096, false, CanPresent);
        public List<PreviewFrame> Frames { get; } = [];
        public int Disposals;
        public int Preflights;
        public bool RejectStyles;
        public Action<IReadOnlyList<SceneRenderingStyleBinding>>? Validating;
        public Action? Disposing;
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Frames.Add(frame); return ValueTask.CompletedTask; }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; Disposing?.Invoke(); return ValueTask.CompletedTask; }
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
        { Preflights++; Validating?.Invoke(bindings); if (RejectStyles) throw new NotSupportedException("Confirm a replacement for an unavailable preset before rendering."); }
    }
    private sealed class RecordingRenderer : IViewRenderer, IViewRenderingStylePreflight
    {
        public ViewRendererCapabilities Capabilities => new("test-capture", 4096, 4096, true, false);
        public List<OfflineFrame> Frames { get; } = [];
        public int Releases, Disposals;
        public int Preflights;
        public bool RejectStyles;
        public Action<OfflineFrame>? Capturing;
        public Action? Disposing;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Frames.Add(frame); Capturing?.Invoke(frame);
            return ValueTask.FromResult(new FrameBufferLease(frame.Width, frame.Height, frame.Width * 4,
                new byte[frame.Width * frame.Height * 4], () => Releases++));
        }
        public ValueTask DisposeAsync() { Disposals++; Disposing?.Invoke(); return ValueTask.CompletedTask; }
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
        { Preflights++; if (RejectStyles) throw new NotSupportedException("Confirm a replacement for an unavailable preset before rendering."); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
