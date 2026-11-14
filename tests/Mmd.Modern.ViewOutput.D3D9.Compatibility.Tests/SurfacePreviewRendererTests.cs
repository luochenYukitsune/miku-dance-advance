using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class SurfacePreviewRendererTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ViewRenderRequest Request = new(2, 2);
    private const D3D9SurfaceIsolation Isolation = D3D9SurfaceIsolation.NoConfiguredMmeOrEmm;

    [Fact]
    public async Task DefaultEmptyViewportGuidesArePresentedSeparatelyAndNeverEnterTheScenePackets()
    {
        var session = new FakeSession();
        await using var renderer = await Create(session);
        var frame = Frame(0);
        var overlay = new ViewportOverlaySnapshot([
            new(new(.1f, .4f), new(.9f, .4f), Vector4.One, DeviceDepth: new(.5f, .8f)),
            new(new(.5f), new(.5f), new(1, 0, 0, 1), Shape: ViewportOverlayShape.Circle)]);
        var original = frame.Scene;
        frame = frame with { Scene = new(original.Surface, original.Outline, original.OutlineOptions,
            original.Options, original.Materials, original.Lighting, overlay) };
        // Capture's strict scene contract remains unchanged; preview owns a separate overlay pass.
        Assert.Throws<NotSupportedException>(() => D3D9SurfacePreflight.ValidateScene(frame.Scene, Request.EffectiveQuality));
        renderer.ValidateRenderingStyles(frame.Scene, []);
        renderer.ValidateSceneInputs(frame);
        await renderer.PresentAsync(frame, Token);
        Assert.Equal(2, Assert.Single(session.Snapshots).PreviewOverlayPrimitiveCount);
        Assert.Same(overlay, frame.Scene.EditorOverlay);
        Assert.Equal(0, session.Captures);
        await renderer.PresentAsync(Frame(0), Token);
        Assert.Equal(0, session.Snapshots[1].PreviewOverlayPrimitiveCount);
        Assert.Equal(session.Snapshots[0].FrameByteCount, session.Snapshots[1].FrameByteCount);
        Assert.Equal(session.Snapshots[0].VisualByteCount, session.Snapshots[1].VisualByteCount);
    }

    [Theory]
    [InlineData("hwnd")]
    [InlineData("bindings")]
    [InlineData("quality")]
    [InlineData("isolation")]
    [InlineData("background")]
    [InlineData("cancel")]
    public async Task InvalidCreationInputsAreRejectedBeforeNativeCreation(string invalid)
    {
        int creates = 0;
        var binding = new D3D9SurfaceObjectBinding(Guid.NewGuid(), 1, "fixture.pmx", "fixture");
        await Assert.ThrowsAnyAsync<Exception>(() => ExperimentalD3D9SurfacePreviewRenderer.CreateForTestingAsync(
            invalid == "quality" ? Request with { Quality = new(SpatialSamples: 2) } : Request,
            invalid == "hwnd" ? 0 : 1,
            invalid == "bindings" ? [binding, binding] : [],
            invalid == "background" ? Vector4.Zero : Vector4.One,
            invalid == "isolation" ? default : Isolation,
            (_, _) => { creates++; return ValueTask.FromResult<ID3D9SurfacePreviewSession>(new FakeSession()); },
            invalid == "cancel" ? new CancellationToken(true) : Token));
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task PresentationUsesDirectPresentAndPreservesEveryFractionalSourceFrame()
    {
        var session = new FakeSession();
        await using var renderer = await Create(session);
        Assert.True(renderer.Capabilities.CanPresent); Assert.False(renderer.Capabilities.CanCapture);
        Assert.False(typeof(IViewRenderer).IsAssignableFrom(renderer.GetType()));
        foreach (double source in new[] { 0, .5, .1, double.Epsilon, 123.45678901234567 })
        {
            var frame = Frame(source);
            renderer.ValidateSceneInputs(frame);
            await renderer.PresentAsync(frame, Token);
        }
        Assert.Equal(new[] { 0, .5, .1, double.Epsilon, 123.45678901234567 }, session.Snapshots.Select(value => value.SourceFrame));
        Assert.All(session.Orders, value => Assert.Equal(D3D9SurfaceRenderOrder.OpaqueFirst, value));
        Assert.Equal(0, session.Captures);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("dimensions")]
    [InlineData("inputs")]
    [InlineData("shadow")]
    [InlineData("style")]
    public async Task InvalidFramesAreRejectedBeforeNativeQueueAdmission(string invalid)
    {
        var session = new FakeSession();
        await using var renderer = await Create(session);
        var frame = Frame(.5);
        frame = invalid switch
        {
            "source" => frame with { SourceFrame = double.NaN },
            "dimensions" => frame with { Width = 3 },
            "inputs" => frame with { RenderingStyles = [new(Guid.NewGuid(), RenderingStyleId.Mmd, ModelRole.Unspecified, 0, 0, false)] },
            "shadow" => frame with { SelfShadow = new(1, 10) },
            _ => frame with { RenderingStyles = [new(Guid.NewGuid(), RenderingStyleId.Genshin, ModelRole.Character, 0, 0, false)] }
        };
        Assert.ThrowsAny<Exception>(() => renderer.ValidateSceneInputs(frame));
        await Assert.ThrowsAnyAsync<Exception>(() => renderer.PresentAsync(frame, Token).AsTask());
        Assert.Empty(session.Snapshots); Assert.Equal(0, session.Captures);
    }

    [Fact]
    public async Task ResizeWaitsForPresentThenUpdatesDimensionsBeforeAnotherAdmission()
    {
        var session = new FakeSession { BlockPresent = true };
        await using var renderer = await Create(session);
        var present = renderer.PresentAsync(Frame(.5), Token).AsTask();
        await session.Entered.Task.WaitAsync(Token);
        var resize = renderer.ResizeAsync(3, 4, Token).AsTask();
        Assert.False(resize.IsCompleted); Assert.Empty(session.Resizes);
        session.Unblock.TrySetResult();
        await present; await resize;
        Assert.Equal(new[] { (3, 4) }, session.Resizes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.PresentAsync(Frame(.75), Token).AsTask());
        await renderer.PresentAsync(Frame(.75) with { Width = 3, Height = 4 }, Token);
        Assert.Equal(new[] { .5, .75 }, session.Snapshots.Select(value => value.SourceFrame));
    }

    [Fact]
    public async Task ResizeCancellationAfterNativeAdmissionKeepsManagedAndNativeDimensionsTogether()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var session = new FakeSession { OnResize = cancellation.Cancel };
        await using var renderer = await Create(session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.ResizeAsync(3, 4, cancellation.Token).AsTask());
        Assert.Equal(new[] { (3, 4) }, session.Resizes);
        await renderer.PresentAsync(Frame(.5) with { Width = 3, Height = 4 }, Token);
        Assert.Equal(3, Assert.Single(session.Snapshots).Width);
    }

    [Fact]
    public async Task FailedResizeLeavesPreviousDimensionsAndInvalidResizeNeverEntersNative()
    {
        var session = new FakeSession { FailResize = true };
        await using var renderer = await Create(session);
        await Assert.ThrowsAnyAsync<Exception>(() => renderer.ResizeAsync(0, 4, Token).AsTask());
        Assert.Empty(session.Resizes);
        await Assert.ThrowsAsync<IOException>(() => renderer.ResizeAsync(3, 4, Token).AsTask());
        await renderer.PresentAsync(Frame(.5), Token);
        Assert.Equal(2, Assert.Single(session.Snapshots).Width);
    }

    [Fact]
    public async Task CancellationAndCloseDrainAcceptedPresentAndRejectNewCalls()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var session = new FakeSession { BlockPresent = true };
        var renderer = await Create(session);
        var present = renderer.PresentAsync(Frame(.5), cancellation.Token).AsTask();
        await session.Entered.Task.WaitAsync(Token);
        cancellation.Cancel();
        var dispose = renderer.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted); Assert.Equal(0, session.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.PresentAsync(Frame(1), Token).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.ResizeAsync(3, 4, Token).AsTask());
        Assert.Throws<ObjectDisposedException>(() => renderer.ValidateSceneInputs(Frame(1)));
        Assert.Throws<ObjectDisposedException>(() => renderer.ValidateRenderingStyles(Frame(1).Scene, []));
        session.Unblock.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => present);
        await dispose;
        Assert.Single(session.Snapshots); Assert.Equal(1, session.Disposals); Assert.Equal(0, session.Captures);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotAdmitAnotherPresent()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var session = new FakeSession { BlockPresent = true };
        await using var renderer = await Create(session);
        var present = renderer.PresentAsync(Frame(.5), Token).AsTask();
        await session.Entered.Task.WaitAsync(Token);
        var canceled = renderer.PresentAsync(Frame(1), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        session.Unblock.TrySetResult(); await present;
        Assert.Single(session.Snapshots);
    }

    [Fact]
    public async Task FailedDestroyCanRetryWithoutReopeningPresentOrResize()
    {
        var session = new FakeSession { FailFirstDisposal = true };
        var renderer = await Create(session);
        await Assert.ThrowsAsync<IOException>(() => renderer.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.PresentAsync(Frame(.5), Token).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.ResizeAsync(3, 4, Token).AsTask());
        await renderer.DisposeAsync(); await renderer.DisposeAsync();
        Assert.Equal(2, session.Disposals);
    }

    [Fact]
    public async Task NativeMetadataMustDescribeTheSameFrameAndDimensions()
    {
        var session = new FakeSession { WrongMetadata = true };
        await using var renderer = await Create(session);
        await Assert.ThrowsAsync<InvalidDataException>(() => renderer.PresentAsync(Frame(.5), Token).AsTask());
    }

    private static Task<ExperimentalD3D9SurfacePreviewRenderer> Create(FakeSession session) =>
        ExperimentalD3D9SurfacePreviewRenderer.CreateForTestingAsync(Request, 1, [], Vector4.One, Isolation,
            (_, _) => ValueTask.FromResult<ID3D9SurfacePreviewSession>(session), Token);

    private static PreviewFrame Frame(double source) => new(2, 2, source,
        new(new(SceneSnapshot.CreateEmpty(), [])), null, null, null);

    // Implementing both seams makes any accidental RGBA capture visible to the test.
    private sealed class FakeSession : ID3D9SurfacePreviewSession, ID3D9SurfaceCaptureSession
    {
        internal readonly List<D3D9SurfaceSnapshot> Snapshots = [];
        internal readonly List<D3D9SurfaceRenderOrder> Orders = [];
        internal readonly List<(int Width, int Height)> Resizes = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Unblock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockPresent, FailFirstDisposal, WrongMetadata, FailResize;
        internal Action? OnResize;
        internal int Disposals, Captures;
        public async ValueTask<D3D9SurfacePresented> PresentAsync(D3D9SurfaceSnapshot snapshot,
            D3D9SurfaceRenderOrder order, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Snapshots.Add(snapshot); Orders.Add(order); Entered.TrySetResult();
            if (BlockPresent) await Unblock.Task;
            return new(snapshot.Width, snapshot.Height, WrongMetadata ? snapshot.SourceFrame + 1 : snapshot.SourceFrame);
        }
        public ValueTask ResizeAsync(int width, int height, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Resizes.Add((width, height));
            if (FailResize) throw new IOException("simulated native resize failure");
            OnResize?.Invoke(); return ValueTask.CompletedTask;
        }
        public ValueTask<D3D9SurfacePixels> CaptureAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token)
        { Captures++; throw new InvalidOperationException("Preview must never perform RGBA capture."); }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (FailFirstDisposal && Disposals == 1) throw new IOException("simulated native cleanup failure");
            return ValueTask.CompletedTask;
        }
    }
}

public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public async Task PreviewForwardsSameEvaluatedUvSceneInputsWithoutAdvancingTheProject()
    {
        using var project = UvProject([UvStream(1, 10)], out var encoder);
        var bindings = project.Objects.Select((item, index) => new D3D9SurfaceObjectBinding(item.Id,
            (ulong)index + 1, item.SourcePath, item.Name)).ToArray();
        var native = new SameSamplePreviewSession();
        await using var renderer = await ExperimentalD3D9SurfacePreviewRenderer.CreateForTestingAsync(Request, 1,
            bindings, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm,
            (_, _) => ValueTask.FromResult<ID3D9SurfacePreviewSession>(native), TestContext.Current.CancellationToken);
        Assert.Equal(encoder.SceneInputRequests.Select(value => value.ObjectId), renderer.SceneInputRequests.Select(value => value.ObjectId));
        Assert.All(renderer.SceneInputRequests, value => Assert.True(value.IncludeSourceGeometry));
        project.Seek(2, TestContext.Current.CancellationToken);
        var snapshot = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        var frame = new PreviewFrame(Request.Width, Request.Height, project.CurrentFrame, snapshot.Scene,
            snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        {
            RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs,
            MorphChannels = snapshot.MorphChannels, ResolvedCamera = snapshot.ResolvedCamera,
            CameraParentTransform = snapshot.CameraParentTransform
        };
        var before = project.Frame; double clock = project.CurrentFrame;
        var offline = D3D9SurfaceFrameInput.FromPreview(frame);
        Assert.Same(frame.Scene, offline.Scene); Assert.Same(frame.SceneInputs, offline.SceneInputs);
        Assert.Same(frame.RenderingStyles, offline.RenderingStyles); Assert.Same(frame.MorphChannels, offline.MorphChannels);
        Assert.Equal(frame.CameraParentTransform, offline.CameraParentTransform); Assert.Equal(frame.ResolvedCamera, offline.ResolvedCamera);
        renderer.ValidateSceneInputs(frame); await renderer.PresentAsync(frame, TestContext.Current.CancellationToken);
        Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
        var forwarded = Assert.Single(native.Snapshots);
        var expected = encoder.Encode(offline, Request, Vector4.One);
        Assert.Equal(2, forwarded.SourceFrame); Assert.Equal(expected.FrameByteCount, forwarded.FrameByteCount);
        Assert.Equal(expected.VisualByteCount, forwarded.VisualByteCount);
        var bad = frame with { SceneInputs = SceneObjectRenderInputs.Empty };
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.PresentAsync(bad, TestContext.Current.CancellationToken).AsTask());
        Assert.Single(native.Snapshots); Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
    }

    private sealed class SameSamplePreviewSession : ID3D9SurfacePreviewSession
    {
        internal readonly List<D3D9SurfaceSnapshot> Snapshots = [];
        public ValueTask<D3D9SurfacePresented> PresentAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token)
        { Snapshots.Add(snapshot); return ValueTask.FromResult(new D3D9SurfacePresented(snapshot.Width, snapshot.Height, snapshot.SourceFrame)); }
        public ValueTask ResizeAsync(int width, int height, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
