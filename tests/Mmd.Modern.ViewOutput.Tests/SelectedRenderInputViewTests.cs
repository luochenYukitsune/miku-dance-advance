using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class SelectedRenderInputViewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProjectPreviewForwardsSelectedInputsWithItsGeometryAndCapturesTheDeclarationOnlyOnce()
    {
        var project = Project(); var id = project.Objects[0].Id;
        var declarations = new List<SceneObjectRenderRequest> { new(id, [0], [1]) };
        var renderer = new PreviewRenderer(declarations);
        await using var preview = await PreviewSession.CreateProjectAsync(new(20, 10), _ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        declarations.Clear(); // Renderer list mutation cannot alter the view's detached request list.
        await preview.PresentAsync(.1, Token); var first = Assert.Single(renderer.Frames);
        var expected = project.CaptureRenderSnapshot([new(id, [0], [1])]);
        Assert.Same(expected.Scene, first.Scene); Assert.Equal(3, first.SourceFrame, 8);
        Assert.Equal(expected.SceneInputs[id].BoneWorldMatrices[0], first.SceneInputs[id].BoneWorldMatrices[0]);
        Assert.Equal(expected.SceneInputs[id].TextureIndices[1], first.SceneInputs[id].TextureIndices[1]);
        Assert.Single(first.SceneInputs[id].BoneWorldMatrices); Assert.Single(first.SceneInputs[id].TextureIndices);
        var firstMatrix = first.SceneInputs[id].BoneWorldMatrices[0];
        await preview.PresentAsync(.1, Token); var second = renderer.Frames[1];
        Assert.Equal(6, second.SourceFrame, 8); Assert.NotEqual(firstMatrix, second.SceneInputs[id].BoneWorldMatrices[0]);
        Assert.Equal(firstMatrix, first.SceneInputs[id].BoneWorldMatrices[0]); Assert.Equal(1, renderer.RequestReads);
    }

    [Fact]
    public async Task ProjectExportTransmitsEachExactFractionalSampleAndRetainsTheCapturedRequests()
    {
        var project = Project(); var id = project.Objects[0].Id;
        var declarations = new List<SceneObjectRenderRequest> { new(id, [0], [1]) }; var renderer = new CaptureRenderer(declarations);
        renderer.Capturing = frame =>
        {
            declarations.Clear(); var snapshot = project.CaptureRenderSnapshot([new(id, [0], [1])]);
            Assert.Same(snapshot.Scene, frame.Scene); Assert.Equal(snapshot.SourceFrame, frame.Sample.SampledSourceFrame.ToDouble());
            Assert.Equal(snapshot.SceneInputs[id].BoneWorldMatrices[0], frame.SceneInputs[id].BoneWorldMatrices[0]);
            Assert.Equal(snapshot.SceneInputs[id].TextureIndices[1], frame.SceneInputs[id].TextureIndices[1]);
        };
        var sink = new TestSink();
        var result = await ExportCoordinator.ForProject(_ => ValueTask.FromResult(project), (_, _) => ValueTask.FromResult<IViewRenderer>(renderer))
            .RunAsync(new(new(2, 2, new(120), 0, 1)), sink, cancellationToken: Token);
        Assert.Equal(5, result.CompletedFrames); Assert.Equal(4, result.SimulationTicks);
        Assert.Equal(1, renderer.RequestReads); Assert.Equal(5, renderer.Releases); Assert.True(sink.Completed);
        Assert.NotEqual(renderer.Frames[0].SceneInputs[id].BoneWorldMatrices[0], renderer.Frames[^1].SceneInputs[id].BoneWorldMatrices[0]);
        Assert.Throws<ObjectDisposedException>(() => project.Frame);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("bone")]
    [InlineData("texture")]
    [InlineData("static")]
    public async Task PreviewInputRejectionDoesNotAdvanceOrSubmit(string invalid)
    {
        var project = Project(motion: invalid != "static"); var id = project.Objects[0].Id;
        var renderer = new PreviewRenderer([InvalidRequest(id, invalid)]);
        await using var preview = await PreviewSession.CreateProjectAsync(new(20, 10), _ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        var frame = project.Frame;
        if (invalid == "static")
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await preview.PresentAsync(.1, Token));
        else if (invalid == "unknown")
            await Assert.ThrowsAsync<ArgumentException>(async () => await preview.PresentAsync(.1, Token));
        else
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await preview.PresentAsync(.1, Token));
        Assert.Equal(0, project.CurrentFrame); Assert.Same(frame, project.Frame); Assert.Empty(renderer.Frames); Assert.False(project.IsFaulted);
    }

    [Fact]
    public async Task ExportInputRejectionPrecedesSinkBeginAndSeek()
    {
        var project = Project(); var id = project.Objects[0].Id; project.Seek(4, Token);
        var renderer = new CaptureRenderer([new(id, [10])]) { Disposing = () => Assert.Equal(4, project.CurrentFrame) };
        var sink = new TestSink();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExportCoordinator.ForProject(_ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IViewRenderer>(renderer)).RunAsync(new(new(2, 2, new(30), 0, 1)), sink, cancellationToken: Token));
        Assert.False(sink.Begun); Assert.True(sink.Aborted); Assert.Empty(renderer.Frames); Assert.Equal(1, renderer.Disposals);
    }

    [Fact]
    public async Task CaptureFailureThenRetryAtTheSameSampleKeepsDetachedInputsAndGeometryTogether()
    {
        var project = Project(); var id = project.Objects[0].Id; var renderer = new PreviewRenderer([new(id, [0], [0])]) { FailPresent = true };
        await using var preview = await PreviewSession.CreateProjectAsync(new(20, 10), _ => ValueTask.FromResult(project),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await Assert.ThrowsAsync<IOException>(async () => await preview.PresentAsync(0, Token));
        var failed = Assert.Single(renderer.Frames); Assert.Equal(0, project.CurrentFrame);
        renderer.FailPresent = false; await preview.PresentAsync(0, Token); var retried = renderer.Frames[1];
        Assert.Same(failed.Scene, retried.Scene); Assert.Equal(failed.SourceFrame, retried.SourceFrame);
        Assert.Equal(failed.SceneInputs[id].BoneWorldMatrices[0], retried.SceneInputs[id].BoneWorldMatrices[0]);
        Assert.Equal(failed.SceneInputs[id].TextureIndices[0], retried.SceneInputs[id].TextureIndices[0]);
    }

    [Fact]
    public async Task LegacySceneOwnerRejectsRequestsBeforePreviewAdvanceAndBeforeExportBegin()
    {
        var fixture = new OutputFixture(); var renderer = new PreviewRenderer([new(Guid.NewGuid(), [0])]);
        await using var preview = await PreviewSession.CreateAsync(new(20, 10), fixture.CreateSession,
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await preview.PresentAsync(.1, Token));
        Assert.Equal(0, fixture.Session!.CurrentFrame); Assert.Empty(renderer.Frames);
        var exportFixture = new OutputFixture(); var capture = new CaptureRenderer([new(Guid.NewGuid(), [0])]); var sink = new TestSink();
        await Assert.ThrowsAsync<NotSupportedException>(() => new ExportCoordinator(exportFixture.CreateSession,
            (_, _) => ValueTask.FromResult<IViewRenderer>(capture)).RunAsync(new(new(2, 2, new(30), 0, 1)), sink, cancellationToken: Token));
        Assert.False(sink.Begun); Assert.Empty(capture.Frames); Assert.True(sink.Aborted);
    }

    [Fact]
    public async Task LegacyPmmOwnerRejectsRequestsInsteadOfFabricatingProjectIdentities()
    {
        string directory = Path.Combine(Path.GetTempPath(), "selected-pmm-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var playback = Pmm(directory); var renderer = new PreviewRenderer([new(Guid.NewGuid(), [0])]);
            await using var preview = await PreviewSession.CreatePmmAsync(new(20, 10), _ => ValueTask.FromResult(playback),
                (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
            await Assert.ThrowsAsync<NotSupportedException>(async () => await preview.PresentAsync(.1, Token));
            Assert.Equal(0, playback.CurrentFrame); Assert.Empty(renderer.Frames);
            var exportPlayback = Pmm(directory); var capture = new CaptureRenderer([new(Guid.NewGuid(), [0])]); var sink = new TestSink();
            await Assert.ThrowsAsync<NotSupportedException>(() => ExportCoordinator.ForPmm(_ => ValueTask.FromResult(exportPlayback),
                (_, _) => ValueTask.FromResult<IViewRenderer>(capture)).RunAsync(new(new(2, 2, new(30), 0, 1)), sink, cancellationToken: Token));
            Assert.False(sink.Begun); Assert.Empty(capture.Frames); Assert.True(sink.Aborted);
        }
        finally { RemoveTemp(directory); }
    }

    [Fact]
    public async Task PmaOwnersForwardSelectedInputsAndKeepExtractionAliveThroughRendererCleanup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "selected-pma-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(Model()));
            string path = Path.Combine(directory, "actor.pma");
            using (var project = Project(sourcePath: source, textures: false))
                PmaProjectStore.Save(path, project, new() { CacheDirectory = Path.Combine(directory, "save-cache") }, Token);
            PmaLoadedProject Load() => PmaProjectStore.Load(path, Path.Combine(directory, "load-cache"), (file, token) =>
            {
                token.ThrowIfCancellationRequested(); using var input = File.OpenRead(file); var model = PmxReader.Read(input);
                return new(model, Rest(model, []));
            }, new NoPhysics(), cancellationToken: Token);
            var loadedPreview = Load(); var id = loadedPreview.Session.Objects[0].Id; string previewCache = loadedPreview.CacheDirectory;
            var renderer = new PreviewRenderer([new(id, [0])]) { Disposing = () => Assert.True(Directory.Exists(previewCache)) };
            var preview = await PreviewSession.CreatePmaAsync(new(20, 10), _ => ValueTask.FromResult(loadedPreview),
                (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
            await preview.PresentAsync(.1, Token); Assert.Single(Assert.Single(renderer.Frames).SceneInputs[id].BoneWorldMatrices);
            await preview.DisposeAsync(); Assert.False(Directory.Exists(previewCache));
            var loadedExport = Load(); id = loadedExport.Session.Objects[0].Id; string exportCache = loadedExport.CacheDirectory;
            var capture = new CaptureRenderer([new(id, [0])]) { Disposing = () => Assert.True(Directory.Exists(exportCache)) };
            await ExportCoordinator.ForPma(_ => ValueTask.FromResult(loadedExport), (_, _) => ValueTask.FromResult<IViewRenderer>(capture))
                .RunAsync(new(new(2, 2, new(30), 0, 1)), new TestSink(), cancellationToken: Token);
            Assert.Equal(2, capture.Frames.Count); Assert.All(capture.Frames, frame => Assert.Single(frame.SceneInputs[id].BoneWorldMatrices));
            Assert.False(Directory.Exists(exportCache));
        }
        finally { RemoveTemp(directory); }
    }

    [Fact]
    public async Task DuplicateProviderIdentitiesFailDuringAcquisitionAndCleanUpTheProject()
    {
        var project = Project(); var request = new SceneObjectRenderRequest(project.Objects[0].Id, [0]);
        var renderer = new PreviewRenderer([request, request]);
        await Assert.ThrowsAsync<ArgumentException>(async () => await PreviewSession.CreateProjectAsync(new(20, 10),
            _ => ValueTask.FromResult(project), (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token));
        Assert.Equal(1, renderer.Disposals); Assert.Throws<ObjectDisposedException>(() => project.Frame);
    }

    private static SceneObjectRenderRequest InvalidRequest(Guid id, string invalid) => invalid switch
    {
        "unknown" => new(Guid.NewGuid(), [0]), "bone" => new(id, [1]), "texture" => new(id, localTextureIndices: [2]),
        "static" => new(id, [0]), _ => throw new ArgumentOutOfRangeException(nameof(invalid))
    };
    private static SceneProjectSession Project(bool motion = true, string sourcePath = "actor.pmx", bool textures = true)
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        SceneTexture[] maps = textures ? [new("a", 1, 1, [30, 60, 90, 255]), new("b", 1, 1, [90, 60, 30, 255])] : [];
        var actor = layout.Add("actor", sourcePath, ModelRole.Character, Rest(model, maps));
        var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" },
            new() { BoneName = "root", Frame = 30, Translation = new(3, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f) }] });
        return new(layout, new(), motion ? [new(actor.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset)] : [], new NoPhysics(), cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model, SceneTexture[] textures)
    {
        var surface = PmxOriginalStyleAdapter.FromRestPose(model).Scene; var basis = surface.Snapshot;
        var snapshot = new SceneSnapshot(basis.Vertices, basis.Indices, basis.Materials, textures, basis.World, basis.Camera, basis.LightDirection);
        return new(new(snapshot, surface.Materials, surface.Profile), PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    }
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
    private static PmmProjectPlayback Pmm(string directory)
    {
        var model = Model(); string source = Path.Combine(directory, "pmm-actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var document = new PmmDocument
        {
            Models = [new() { Index = 0, Visible = true, LastFrame = 30, Name = new(0, "actor", []), Path = new(0, source, []), InitialModelKeyframe = new() { Visible = true } }],
            OutputWidth = 1280, OutputHeight = 720, CameraFov = 30, Settings = new() { PhysicsSimulationMode = 1, CameraLookAtModelIndex = -1 },
            Camera = new() { Position = new(0, 0, -10), Perspective = true, InitialKeyframe = new() { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
        };
        var plan = PmmProjectImport.Prepare(document, Path.Combine(directory, "source.pmm"), cancellationToken: Token);
        return new(plan, new Dictionary<int, OriginalFrameScene> { [0] = Rest(model, []) }, (_, _) => throw new InvalidOperationException());
    }
    private static void RemoveTemp(string directory)
    {
        string resolved = Path.GetFullPath(directory); string temp = Path.GetFullPath(Path.GetTempPath());
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException("No physics in fixture.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException("No shared physics in fixture.");
    }
    private sealed class PreviewRenderer(IReadOnlyList<SceneObjectRenderRequest> requests) : IRealtimeViewRenderer, IViewSceneInputRequests
    {
        public ViewRendererCapabilities Capabilities => new("cpu-selected-inputs", 4096, 4096, false, true);
        public int RequestReads, Disposals;
        public bool FailPresent;
        public Action? Disposing;
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get { RequestReads++; return requests; } }
        public List<PreviewFrame> Frames { get; } = [];
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Frames.Add(frame); if (FailPresent) throw new IOException("present fixture"); return ValueTask.CompletedTask; }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; Disposing?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class CaptureRenderer(IReadOnlyList<SceneObjectRenderRequest> requests) : IViewRenderer, IViewSceneInputRequests
    {
        public ViewRendererCapabilities Capabilities => new("cpu-selected-inputs", 4096, 4096, true, false);
        public int RequestReads, Releases, Disposals;
        public Action<OfflineFrame>? Capturing;
        public Action? Disposing;
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get { RequestReads++; return requests; } }
        public List<OfflineFrame> Frames { get; } = [];
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Frames.Add(frame); Capturing?.Invoke(frame);
            return ValueTask.FromResult(new FrameBufferLease(frame.Width, frame.Height, frame.Width * 4, new byte[frame.Width * frame.Height * 4], () => Releases++));
        }
        public ValueTask DisposeAsync() { Disposals++; Disposing?.Invoke(); return ValueTask.CompletedTask; }
    }
}
