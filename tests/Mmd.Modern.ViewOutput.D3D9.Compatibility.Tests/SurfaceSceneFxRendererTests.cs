using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class SurfaceSceneFxRendererTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ViewRenderRequest Request = new(2, 2, Quality: new(SurfaceOrder: SurfaceDrawOrder.Original));
    private static D3D9SceneFxConfiguration Configuration(IEnumerable<D3D9ObjectFxAssignment>? rows = null) =>
        new(rows ?? [], new D3D9EffectResourceMap([new(@"D:\origin\scene.fx", @"D:\absent\scene.fx")]));

    [Fact]
    public async Task CaptureAndResizedPresentUseSceneScopeAndTheSameFractionalSample()
    {
        var capture = new CaptureSession(); var preview = new PreviewSession();
        await using var output = await CreateCapture(capture);
        await using var viewport = await CreatePreview(preview);
        Assert.Equal("d3d9-scene-fx-experimental", output.Capabilities.BackendId);
        Assert.Equal(output.Capabilities.BackendId, viewport.Capabilities.BackendId);
        var frame = Frame(.5);
        output.ValidateSceneInputs(frame); viewport.ValidateSceneInputs(frame);
        using var pixels = await output.CaptureAsync(D3D9SurfaceFrameInput.FromPreview(frame), Token);
        await viewport.PresentAsync(frame, Token);
        await viewport.ResizeAsync(3, 4, Token);
        await viewport.PresentAsync(frame with { Width = 3, Height = 4 }, Token);
        foreach (var snapshot in capture.Snapshots.Concat(preview.Snapshots))
        {
            Assert.Equal(D3D9SurfaceScope.ConfiguredSceneFx, snapshot.Scope);
            Assert.Equal(.5, snapshot.SourceFrame);
        }
        Assert.Equal((3, 4), (preview.Snapshots[1].Width, preview.Snapshots[1].Height));
        Assert.Equal(new[] { (3, 4) }, preview.Resizes);
        Assert.All(capture.Orders.Concat(preview.Orders), order => Assert.Equal(D3D9SurfaceRenderOrder.Original, order));
        Assert.True(output.Capabilities.CanCapture); Assert.False(output.Capabilities.CanPresent);
        Assert.True(viewport.Capabilities.CanPresent); Assert.False(viewport.Capabilities.CanCapture);
    }

    [Theory]
    [InlineData(false, "identity")]
    [InlineData(true, "identity")]
    [InlineData(false, "quality")]
    [InlineData(true, "quality")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task SceneInputsFailBeforeEitherNativeFactory(bool preview, string invalid)
    {
        int creates = 0;
        var config = Configuration(invalid == "identity" ? [new(42, -1, D3D9ObjectFxAssignmentMode.Standard)] : []);
        var request = invalid == "quality" ? Request with { Quality = new(SpatialSamples: 2) } : Request;
        var token = invalid == "cancel" ? new CancellationToken(true) : Token;
        Func<Task> create = preview
            ? () => ExperimentalD3D9SurfacePreviewRenderer.CreateSceneFxForTestingAsync(request, 1, [], Vector4.One, config,
                (_, _) => { creates++; return ValueTask.FromResult<ID3D9SurfacePreviewSession>(new PreviewSession()); }, token)
            : () => ExperimentalD3D9SurfaceViewRenderer.CreateSceneFxForTestingAsync(request, [], Vector4.One, config,
                (_, _) => { creates++; return ValueTask.FromResult<ID3D9SurfaceCaptureSession>(new CaptureSession()); }, token);
        if (invalid == "identity") await Assert.ThrowsAsync<ArgumentException>(create);
        else if (invalid == "quality") await Assert.ThrowsAsync<NotSupportedException>(create);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(create);
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task SceneBindingsFreezeBeforeTheAsynchronousNativeFactoryRuns()
    {
        var binding = new D3D9SurfaceObjectBinding(Guid.NewGuid(), 42, "source.pmx", "actor");
        var bindings = new List<D3D9SurfaceObjectBinding> { binding };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating = ExperimentalD3D9SurfaceViewRenderer.CreateSceneFxForTestingAsync(Request, bindings, Vector4.One,
            Configuration([new(42, -1, D3D9ObjectFxAssignmentMode.Standard)]), async (_, _) =>
            { entered.TrySetResult(); await release.Task; return new CaptureSession(); }, Token);
        await entered.Task.WaitAsync(Token);
        bindings.Clear();
        release.TrySetResult();
        await using var renderer = await creating;
        Assert.Equal(binding.ObjectId, Assert.Single(renderer.SceneInputRequests).ObjectId);
    }

    [Fact]
    public async Task SceneCaptureDrainsAcceptedWorkAndRetriesFailedCleanup()
    {
        var session = new CaptureSession { Block = true, FailFirstDispose = true };
        var renderer = await CreateCapture(session);
        var capture = renderer.CaptureAsync(D3D9SurfaceFrameInput.FromPreview(Frame(.5)), Token).AsTask();
        await session.Entered.Task.WaitAsync(Token);
        var disposal = renderer.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); Assert.Equal(0, session.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.CaptureAsync(D3D9SurfaceFrameInput.FromPreview(Frame(1)), Token).AsTask());
        session.Release.TrySetResult();
        using var pixels = await capture;
        await Assert.ThrowsAsync<IOException>(() => disposal);
        await renderer.DisposeAsync();
        Assert.Equal(2, session.Disposals);
    }

    [Fact]
    public async Task ScenePreviewRejectsUnimplementedShadowBeforeAdmissionAndRetriesCleanup()
    {
        var session = new PreviewSession { FailFirstDispose = true };
        var renderer = await CreatePreview(session);
        await Assert.ThrowsAsync<NotSupportedException>(() => renderer.PresentAsync(Frame(.5) with { SelfShadow = new(1, 10) }, Token).AsTask());
        Assert.Empty(session.Snapshots);
        await Assert.ThrowsAsync<IOException>(() => renderer.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.ResizeAsync(3, 4, Token).AsTask());
        await renderer.DisposeAsync();
        Assert.Equal(2, session.Disposals);
    }

    private static PreviewFrame Frame(double source) => new(2, 2, source, new(new(SceneSnapshot.CreateEmpty(), [])), null, null, null);
    private static Task<ExperimentalD3D9SurfaceViewRenderer> CreateCapture(CaptureSession session) =>
        ExperimentalD3D9SurfaceViewRenderer.CreateSceneFxForTestingAsync(Request, [], Vector4.One,
            Configuration(), (_, _) => ValueTask.FromResult<ID3D9SurfaceCaptureSession>(session), Token);
    private static Task<ExperimentalD3D9SurfacePreviewRenderer> CreatePreview(PreviewSession session) =>
        ExperimentalD3D9SurfacePreviewRenderer.CreateSceneFxForTestingAsync(Request, 1, [], Vector4.One,
            Configuration(), (_, _) => ValueTask.FromResult<ID3D9SurfacePreviewSession>(session), Token);

    private sealed class CaptureSession : ID3D9SurfaceCaptureSession
    {
        internal readonly List<D3D9SurfaceSnapshot> Snapshots = [];
        internal readonly List<D3D9SurfaceRenderOrder> Orders = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block, FailFirstDispose;
        internal int Disposals;
        public async ValueTask<D3D9SurfacePixels> CaptureAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token)
        {
            Snapshots.Add(snapshot); Orders.Add(order); Entered.TrySetResult();
            if (Block) await Release.Task;
            return new(snapshot.Width, snapshot.Height, snapshot.SourceFrame, new byte[snapshot.Width * snapshot.Height * 4]);
        }
        public ValueTask DisposeAsync()
        {
            if (++Disposals == 1 && FailFirstDispose) throw new IOException("simulated cleanup failure");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreviewSession : ID3D9SurfacePreviewSession
    {
        internal readonly List<D3D9SurfaceSnapshot> Snapshots = [];
        internal readonly List<D3D9SurfaceRenderOrder> Orders = [];
        internal readonly List<(int, int)> Resizes = [];
        internal bool FailFirstDispose;
        internal int Disposals;
        public ValueTask<D3D9SurfacePresented> PresentAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token)
        { Snapshots.Add(snapshot); Orders.Add(order); return ValueTask.FromResult(new D3D9SurfacePresented(snapshot.Width, snapshot.Height, snapshot.SourceFrame)); }
        public ValueTask ResizeAsync(int width, int height, CancellationToken token)
        { Resizes.Add((width, height)); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            if (++Disposals == 1 && FailFirstDispose) throw new IOException("simulated cleanup failure");
            return ValueTask.CompletedTask;
        }
    }
}
