using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class SurfaceViewRendererTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ViewRenderRequest Request = new(2, 2);
    private static readonly D3D9SurfaceIsolation Isolation = D3D9SurfaceIsolation.NoConfiguredMmeOrEmm;

    [Theory]
    [InlineData("gi")]
    [InlineData("aa")]
    [InlineData("ssaa")]
    [InlineData("spatial")]
    [InlineData("temporal")]
    [InlineData("diffusion")]
    [InlineData("background")]
    [InlineData("isolation")]
    public async Task UnsupportedRequestsFailBeforeCreatingTheCaptureSession(string unsupported)
    {
        var quality = unsupported switch
        {
            "gi" => new ViewQualityOptions(Gi: new(Mode: LightingMode.HardwareRayTracing)),
            "aa" => new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X),
            "ssaa" => new ViewQualityOptions(SpatialSamples: 2),
            "spatial" => new ViewQualityOptions(Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }),
            "temporal" => new ViewQualityOptions(TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr }),
            "diffusion" => new ViewQualityOptions(Diffusion: new(true)),
            _ => new ViewQualityOptions()
        };
        int creates = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => ExperimentalD3D9SurfaceViewRenderer.CreateForTestingAsync(
            Request with { Quality = quality }, [], unsupported == "background" ? Vector4.Zero : Vector4.One,
            unsupported == "isolation" ? default : Isolation, (_, _) =>
            { creates++; return ValueTask.FromResult<ID3D9SurfaceCaptureSession>(new FakeSession()); }, Token));
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task SceneRequestsFreezeBindingsAndSelectRawGeometryWithoutAnotherClock()
    {
        var objectId = Guid.NewGuid();
        var names = new[] { "root", "head" };
        var bindings = new List<D3D9SurfaceObjectBinding> { new(objectId, 7, "fixture.pmx", "fixture", names) };
        await using var renderer = await ExperimentalD3D9SurfaceViewRenderer.CreateForTestingAsync(Request, bindings,
            Vector4.One, Isolation, (_, _) => ValueTask.FromResult<ID3D9SurfaceCaptureSession>(new FakeSession()), Token);
        names[0] = "changed"; bindings.Clear();
        var input = Assert.Single(renderer.SceneInputRequests);
        Assert.Equal(objectId, input.ObjectId); Assert.True(input.IncludeSourceGeometry);
        Assert.Equal(new[] { 0, 1 }, input.BoneIndices);
    }

    [Fact]
    public async Task CapturePreservesHalfSourceFramesAndReturnsIndependentThreadFreeLeases()
    {
        var session = new FakeSession();
        await using var renderer = await Create(session);
        Assert.Equal("d3d9-isolated-surface-experimental", renderer.Capabilities.BackendId);
        Assert.True(renderer.Capabilities.CanCapture); Assert.False(renderer.Capabilities.CanPresent);
        var plan = new OfflineFramePlan(2, 2, new(60), 0, 1);
        using var first = await renderer.CaptureAsync(Frame(plan.GetSample(0)), Token);
        using var second = await renderer.CaptureAsync(Frame(plan.GetSample(1)), Token);
        Assert.Equal(new[] { 0d, .5d }, session.SourceFrames);
        Assert.Equal((byte)0, first.Pixels.Span[0]); Assert.Equal((byte)1, second.Pixels.Span[0]);
        Assert.Equal(16, second.Pixels.Length);
        await Task.Run(second.Dispose, Token);
        Assert.True(second.IsDisposed);
    }

    [Fact]
    public async Task InvalidFrameMetadataIsRejectedBeforeCaptureAdmission()
    {
        var session = new FakeSession();
        await using var renderer = await Create(session);
        var frame = Frame(new OfflineFramePlan(2, 2, new(30), 0, 0).GetSample(0));
        var mesh = new SceneSnapshot([new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero)],
            [0u, 1u, 2u], [new("test", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        frame = frame with { Scene = new(new(mesh, [new(Vector3.One)])) };
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.CaptureAsync(frame, Token).AsTask());
        Assert.Empty(session.SourceFrames);
    }

    [Fact]
    public async Task DisposeWaitsForAnAcceptedCaptureAndRejectsNewCalls()
    {
        var session = new FakeSession { Block = true };
        var renderer = await Create(session);
        var frame = Frame(new OfflineFramePlan(2, 2, new(30), 0, 0).GetSample(0));
        var capture = renderer.CaptureAsync(frame, Token).AsTask();
        await session.Entered.Task.WaitAsync(Token);
        var dispose = renderer.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted); Assert.Equal(0, session.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.CaptureAsync(frame, Token).AsTask());
        session.Unblock.TrySetResult();
        using var pixels = await capture;
        await dispose;
        Assert.Equal(1, session.Disposals);
    }

    [Fact]
    public async Task CancellationDoesNotDestroyAnInFlightCapture()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var session = new FakeSession { Block = true };
        var renderer = await Create(session);
        var capture = renderer.CaptureAsync(Frame(new OfflineFramePlan(2, 2, new(30), 0, 0).GetSample(0)), cancellation.Token).AsTask();
        await session.Entered.Task.WaitAsync(Token);
        cancellation.Cancel();
        var dispose = renderer.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted); Assert.Equal(0, session.Disposals);
        session.Unblock.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        await dispose;
        Assert.Equal(1, session.Disposals);
    }

    [Fact]
    public async Task FailedDisposalCanRetryWithoutReopeningCaptureAdmission()
    {
        var session = new FakeSession { FailFirstDisposal = true };
        var renderer = await Create(session);
        await Assert.ThrowsAsync<IOException>(() => renderer.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.CaptureAsync(
            Frame(new OfflineFramePlan(2, 2, new(30), 0, 0).GetSample(0)), Token).AsTask());
        await renderer.DisposeAsync();
        Assert.Equal(2, session.Disposals);
    }

    [Fact]
    public void InheritedQualityAndModernPresetAreRejectedWithoutChangingTheFrame()
    {
        var scene = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(), []),
            options: new(OriginalAlphaBlendMode.Normal, true, false, Antialiasing: FrameAntialiasing.Smaa1X));
        Assert.Throws<NotSupportedException>(() => D3D9SurfacePreflight.ValidateScene(scene,
            new(Antialiasing: FrameAntialiasing.Off)));
        Assert.Equal(FrameAntialiasing.Smaa1X, scene.Options!.Antialiasing);
        Assert.Throws<NotSupportedException>(() => D3D9SurfacePreflight.ValidateStyles(
            [new(Guid.NewGuid(), RenderingStyleId.Genshin, ModelRole.Character, 0, 0, false)]));
    }

    [Fact]
    public void DisabledOutlineInputsAreAcceptedWhileActiveOutlinesAreRejected()
    {
        SceneVertex[] vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero)];
        var snapshot = new SceneSnapshot(vertices, [0u, 1u, 2u],
            [new("test", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position,
            vertex.Normal, OriginalOutlineVertexMode.Expand, 1, 0)), [new(1, Vector4.One)]);
        var options = new OriginalOutlineOptions(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false);
        var surface = new OriginalStyleScene(snapshot, [new(Vector3.One)]);
        D3D9SurfacePreflight.ValidateScene(new(surface, outline, options), new());
        var character = new OriginalFrameScene(surface, outline, options, materials:
            [new OriginalFrameMaterialFlags(false, false, false) { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands }]);
        D3D9SurfacePreflight.ValidateScene(character, new());
        Assert.Throws<NotSupportedException>(() => D3D9SurfacePreflight.ValidateScene(
            new(surface, outline, options with { Eligible = true }), new()));
    }

    private static Task<ExperimentalD3D9SurfaceViewRenderer> Create(FakeSession session) =>
        ExperimentalD3D9SurfaceViewRenderer.CreateForTestingAsync(Request, [], Vector4.One, Isolation,
            (_, _) => ValueTask.FromResult<ID3D9SurfaceCaptureSession>(session), Token);

    private static OfflineFrame Frame(OfflineFrameSample sample) =>
        new(sample, 2, 2, new(new(SceneSnapshot.CreateEmpty(), [])), null, null, null);

    private sealed class FakeSession : ID3D9SurfaceCaptureSession
    {
        internal readonly List<double> SourceFrames = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Unblock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block, FailFirstDisposal;
        internal int Disposals;
        public async ValueTask<D3D9SurfacePixels> CaptureAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token)
        {
            SourceFrames.Add(snapshot.SourceFrame); Entered.TrySetResult();
            if (Block) await Unblock.Task; // Models an uninterruptible native call; caller observes cancellation afterward.
            var pixels = new byte[checked(snapshot.Width * snapshot.Height * 4)];
            pixels[0] = (byte)(SourceFrames.Count - 1);
            return new(snapshot.Width, snapshot.Height, snapshot.SourceFrame, pixels);
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (FailFirstDisposal && Disposals == 1) throw new IOException("simulated native cleanup failure");
            return ValueTask.CompletedTask;
        }
    }
}
