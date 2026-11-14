using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRenderEngineExportTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)] // Successful export.
    [InlineData(1)] // Capture failure.
    [InlineData(2)] // Cancellation during capture.
    public async Task PublicExportUsesSelectedEngineWithoutModernPreprocessingAndKeepsLiveSession(int outcome)
        => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-engine-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var engine = new RecordingEngine(outcome == 1);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            using var workspace = new EditorProjectWorkspace(engine);
            string modelPath = Path.Combine(directory, "fixture.pmx");
            var model = MorphApplicationTests.Model([]);
            var id = workspace.AddModel(modelPath, EditorProjectWorkspace.CreateModelAsset(model, modelPath, Token), ModelRole.Character);
            workspace.CommitMotion(id, new VmdMotion
            {
                ModelName = model.Name,
                BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 6, Translation = new(6, 0, 0) }]
            });
            var live = workspace.Session!;
            live.Seek(2, Token);
            var liveFrame = live.Frame;
            var liveMotion = workspace.Motions.Single().Motion;
            bool dirty = workspace.IsDirty, undo = workspace.CanUndo, redo = workspace.CanRedo;
            // Modern rejects this unused object identity before constructing a GPU. A selected
            // engine owns its own validation and must never run that modern-only resolver.
            workspace.MaterialBindings.Add(Guid.NewGuid(), new("test.foreign-engine", "Foreign engine",
                new string('0', 64), []));
            Assert.Contains(live.Frame.Materials, value => value.GiToonResponseOverride == RayGiToonResponse.PreservePaletteBands);

            var quality = new ViewQualityOptions(Gi: new RayGiOptions(LightingMode.HardwareRayTracing,
                Strength: 4, SamplesPerPixel: 16, Composition: RayGiComposition.Additive), SpatialSamples: 32);
            var request = new ExportRequest(new OfflineFramePlan(4, 3, new(30), 6, 6), quality);
            var preset = new ViewOutputPresetDefinition("test.selected-engine", "Selected engine",
                new(16, 12), new(4, 3, new(30), quality));
            string output = Path.Combine(directory, "frame.png");
            var background = new Vector4(.2f, .3f, .4f, 0);
            var draft = new OutputSettingsDraft(preset, request, OutputMediaKind.PngImage, output, true, background, null);
            engine.OnCapture = frame =>
            {
                Assert.Same(live, workspace.Session);
                Assert.Equal(2, live.CurrentFrame);
                Assert.Same(liveFrame, live.Frame);
                Assert.Equal(6, frame.Sample.SampledSourceFrame.ToDouble());
                Assert.NotEqual(live.Frame.Surface.Snapshot.Vertices[0].Position, frame.Scene.Surface.Snapshot.Vertices[0].Position);
                if (outcome == 2) cancellation.Cancel();
            };

            if (outcome == 1)
            {
                var failure = await Assert.ThrowsAsync<IOException>(() => EditorExportService.ExportAsync(workspace, draft, null, null, token: cancellation.Token));
                Assert.Equal("selected engine capture failure", failure.Message);
                Assert.False(File.Exists(output));
            }
            else if (outcome == 2)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditorExportService.ExportAsync(workspace, draft, null, null, token: cancellation.Token));
                Assert.False(File.Exists(output));
            }
            else
            {
                var result = await EditorExportService.ExportAsync(workspace, draft, null, null, token: cancellation.Token);
                Assert.Equal(1, result.CompletedFrames);
                Assert.Equal(engine.Id, result.BackendId);
                Assert.Equal(RecordingRenderer.ExpectedPixels(4, 3), ReadPng(File.ReadAllBytes(output), 4, 3));
            }

            Assert.Equal(1, engine.BindCount);
            Assert.Equal(1, engine.Binding.PrepareCount);
            Assert.Same(request, engine.Binding.PreparedInput);
            Assert.Equal(RayGiComposition.Additive, engine.Binding.PreparedInput!.Quality!.EffectiveGi.Composition);
            Assert.Equal(engine.Binding.PreparedOutput!.Render, engine.Binding.CaptureRequest);
            Assert.Equal(background, engine.Binding.Background);
            Assert.Equal(1, engine.Binding.Renderer.Captures);
            Assert.Equal(outcome == 0 ? 1 : 0, engine.Binding.Renderer.Releases);
            Assert.Equal(1, engine.Binding.Renderer.Disposals);
            Assert.Same(request, draft.Request);
            Assert.Equal(32, draft.Request.Quality!.SpatialSamples);
            Assert.Equal(RayGiComposition.Additive, draft.Request.Quality.EffectiveGi.Composition);
            Assert.Same(live, workspace.Session);
            Assert.Same(liveFrame, live.Frame);
            Assert.Equal(2, live.CurrentFrame);
            Assert.Same(liveMotion, workspace.Motions.Single().Motion);
            Assert.Equal((dirty, undo, redo), (workspace.IsDirty, workspace.CanUndo, workspace.CanRedo));
            live.Seek(3, Token); // Export cleanup must not dispose the editable session.
            Assert.Equal(3, live.CurrentFrame);
        }
        finally
        {
            string cleanup = Path.GetFullPath(directory);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!cleanup.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(cleanup).StartsWith("mda-engine-export-", StringComparison.Ordinal)
                || (File.GetAttributes(cleanup) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected engine export fixture cleanup path.");
            Directory.Delete(cleanup, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    private sealed class RecordingEngine : IEditorRenderEngine
    {
        internal RecordingEngine(bool failCapture) => Binding = new(this, failCapture);
        public string Id => "test.selected-engine";
        public IEditorProjectPersistence Persistence => new EditorModernProjectPersistence(this);
        internal RecordingBinding Binding { get; }
        internal int BindCount;
        internal Action<OfflineFrame>? OnCapture { set => Binding.Renderer.OnCapture = value; }
        public IEditorRenderBinding EmptyBinding => throw new InvalidOperationException("Export must bind the project.");
        public IEditorRenderBinding Bind(EditorProjectWorkspace workspace)
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            Assert.Same(this, workspace.RenderEngine);
            Assert.NotEmpty(workspace.MaterialBindings);
            BindCount++;
            return Binding;
        }
    }

    private sealed class RecordingBinding(RecordingEngine owner, bool failCapture) : IEditorRenderBinding
    {
        public IEditorRenderEngine Engine => owner;
        internal RecordingRenderer Renderer { get; } = new(failCapture);
        internal int PrepareCount;
        internal ExportRequest? PreparedInput, PreparedOutput;
        internal ViewRenderRequest? CaptureRequest;
        internal Vector4 Background;
        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
            => throw new InvalidOperationException("Export cannot borrow a preview renderer.");
        public ExportRequest PrepareExport(ExportRequest request)
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            PrepareCount++; PreparedInput = request;
            return PreparedOutput = request with { Quality = request.Quality! with { SpatialSamples = 2 } };
        }
        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background)
        {
            CaptureRequest = request; Background = background;
            Assert.Equal(2, request.EffectiveQuality.SpatialSamples);
            return Renderer;
        }
    }

    private sealed class RecordingRenderer(bool failCapture) : IViewRenderer
    {
        public ViewRendererCapabilities Capabilities => new("test.selected-engine", 4096, 4096, true, false);
        internal Action<OfflineFrame>? OnCapture;
        internal int Captures, Releases, Disposals;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Captures++; OnCapture?.Invoke(frame);
            cancellationToken.ThrowIfCancellationRequested();
            if (failCapture) throw new IOException("selected engine capture failure");
            return ValueTask.FromResult(new FrameBufferLease(frame.Width, frame.Height, frame.Width * 4,
                ExpectedPixels(frame.Width, frame.Height), () => Releases++));
        }
        internal static byte[] ExpectedPixels(int width, int height)
            => Enumerable.Range(0, width * height).SelectMany(i => new byte[] { (byte)(i + 1), 47, 93, 127 }).ToArray();
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private static byte[] ReadPng(byte[] png, int width, int height)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        using var compressed = new MemoryStream();
        for (int p = 8; p < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            string type = Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IHDR")
            {
                Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p + 8)));
                Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p + 12)));
            }
            if (type == "IDAT") compressed.Write(png.AsSpan(p + 8, length));
            p += length + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream(); zlib.CopyTo(raw);
        byte[] rows = raw.ToArray(), pixels = new byte[width * height * 4];
        Assert.Equal(height * (width * 4 + 1), rows.Length);
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, rows[y * (width * 4 + 1)]);
            rows.AsSpan(y * (width * 4 + 1) + 1, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }
        return pixels;
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
