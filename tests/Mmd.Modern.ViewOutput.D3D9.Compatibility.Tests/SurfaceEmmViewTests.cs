using System.Numerics;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public void EmmPreflightRequiresExactlyTheFrozenInstanceGuidsAndKinds()
    {
        using var project = Project(out _);
        var bindings = FxBindings(project);
        var configuration = SurfaceEmmViewTests.Configuration(bindings);
        var inputs = new D3D9SurfacePreflightInputs(bindings.Reverse(), configuration);
        Assert.Equal(D3D9SurfaceScope.ConfiguredEmm, inputs.Scope);
        Assert.All(inputs.SceneInputRequests, value =>
        { Assert.True(value.IncludeSourceGeometry); Assert.True(value.IncludeMaterialInputs); });
        Assert.Throws<ArgumentException>(() => new D3D9SurfacePreflightInputs(bindings.Take(1), configuration));
        Assert.Throws<ArgumentException>(() => new D3D9SurfacePreflightInputs(bindings.Append(
            new(Guid.NewGuid(), 3, "extra.pmx", "extra")), configuration));
        Assert.Throws<ArgumentException>(() => new D3D9SurfacePreflightInputs([
            D3D9SurfaceObjectBinding.CreateAccessory(bindings[0].ObjectId, FirstId, bindings[0].SourcePath, "wrong-kind"), bindings[1]], configuration));
        Assert.Throws<ArgumentException>(() => new D3D9SurfacePreflightInputs([
            new(Guid.NewGuid(), FirstId, bindings[0].SourcePath, bindings[0].Name), bindings[1]], configuration));
        Assert.Throws<InvalidOperationException>(() => inputs.ValidateSceneFxRequest(Request, Vector4.One));
        Assert.Throws<InvalidOperationException>(() => inputs.ValidateRequest(Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => new D3D9SurfacePreflightInputs(bindings).ValidateEmmRequest(Request, Vector4.One));
        var sceneFx = new D3D9SurfacePreflightInputs(bindings, new D3D9SceneFxConfiguration([],
            new D3D9EffectResourceMap([new(@"D:\origin\scene.fx", @"D:\absent\scene.fx")])));
        var frame = CaptureEmm(project, new(bindings, configuration));
        Assert.Throws<InvalidOperationException>(() => sceneFx.ValidateEmmFrame(frame, Request, Vector4.One));
    }

    [Theory]
    [InlineData("effect", false)]
    [InlineData("show", false)]
    [InlineData("owner", false)]
    [InlineData("effect", true)]
    [InlineData("show", true)]
    [InlineData("owner", true)]
    public void EmmBoundsCheckEverySubsetSourceIncludingHiddenShowOnlyAndOwner(string source, bool hidden)
    {
        using var project = EmmAnimatedProject();
        if (hidden) project.Seek(1, TestContext.Current.CancellationToken);
        var bindings = FxBindings(project);
        string sections = source switch
        {
            "effect" => "[Effect]\nDefault=none\nPmd0[1]=none",
            "show" => "[Effect]\nDefault=none\nPmd0[1].show=false",
            _ => "[Effect]\nDefault=none\n[Effect@Target]\nOwner=Pmd0[1]\nPmd1=none"
        };
        var configuration = SurfaceEmmViewTests.Configuration(bindings, sections);
        var encoder = new D3D9SurfaceFrameEncoder(bindings, configuration);
        var preflight = new D3D9SurfacePreflightInputs(bindings, configuration);
        Assert.Throws<ArgumentException>(() => preflight.ValidateEmmFrame(CaptureEmm(project, encoder), Request, Vector4.One));
    }

    [Fact]
    public void EmmEncodingRetainsTheExistingEvaluatedClockAndRequiresRealMaterialInputs()
    {
        using var project = Project(out var ordinary);
        var bindings = FxBindings(project);
        var configuration = SurfaceEmmViewTests.Configuration(bindings, "[Effect]\nDefault=none\nPmd0[0].show=false\nPmd1=none");
        var encoder = new D3D9SurfaceFrameEncoder(bindings.Reverse(), configuration);
        var frame = CaptureEmm(project, encoder);
        var before = project.Frame; double clock = project.CurrentFrame;
        var snapshot = encoder.Encode(frame, Request, Vector4.One);
        Assert.Equal(D3D9SurfaceScope.ConfiguredEmm, snapshot.Scope);
        Assert.Equal(clock, snapshot.SourceFrame);
        Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
        Assert.Throws<NotSupportedException>(() => encoder.Encode(frame with
            { ObjectMaterialInputs = SceneObjectMaterialInputs.Empty }, Request, Vector4.One));
        Assert.Equal(D3D9SurfaceScope.IsolatedSurface, ordinary.Encode(Capture(project, ordinary), Request, Vector4.One).Scope);
    }

    private static OfflineFrame CaptureEmm(SceneProjectSession project, D3D9SurfaceFrameEncoder encoder)
    {
        var snapshot = project.CaptureRenderSnapshot(encoder.SceneInputRequests);
        return Capture(project, encoder) with { ObjectMaterialInputs = snapshot.ObjectMaterialInputs };
    }

    private static SceneProjectSession EmmAnimatedProject()
    {
        var model = Model();
        var source = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1, false));
        var layout = new SceneCompositionEditor();
        var first = layout.Add("animated", "animated.pmx", ModelRole.Unspecified, source);
        layout.Add("static", "static.pmx", ModelRole.Unspecified, Triangle("static", Vector3.UnitZ, Matrix4x4.Identity));
        var motion = new VmdMotion { IkKeyframes = [new() { Frame = 0, Visible = true }, new() { Frame = 1, Visible = false }] };
        return new(layout, new(), [new(first.Id, model, motion, PhysicsEnabled: false)], Provider());
    }
}

public sealed class SurfaceEmmViewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ViewRenderRequest Request = new(2, 2, Quality: new(SurfaceOrder: SurfaceDrawOrder.Original));

    internal static D3D9EmmRuntimeConfiguration Configuration(IEnumerable<D3D9SurfaceObjectBinding> bindings,
        string sections = "[Effect]\nDefault=none")
    {
        var objects = bindings.ToArray();
        var records = objects.Select((value, index) => $"{(value.Kind == PmmDrawableKind.Model ? "Pmd" : "Acs")}{index}={value.SourcePath}");
        var document = EmmReader.Read(Encoding.UTF8.GetBytes($"[Info]\nVersion=3\n[Object]\n{string.Join('\n', records)}\n{sections}\n"));
        var plan = EmmImportPlan.Create(document, @"D:\scene\mapping.emm", objects.Select((value, index) =>
            new EmmObjectBinding(value.ObjectId, value.Kind == PmmDrawableKind.Model, value.SourcePath, index)), @"D:\scene");
        return D3D9EmmRuntimeConfiguration.Create(plan, objects.Select(value =>
            new D3D9EmmRuntimeRootSeed(value.ObjectId, value.Kind == PmmDrawableKind.Model)), new D3D9EffectResourceMap([]));
    }

    [Fact]
    public async Task EmmCaptureAndResizePresentSubmitOneFractionalSampleInDedicatedScope()
    {
        var capture = new CaptureSession(); var preview = new PreviewSession();
        await using var output = await CreateCapture(capture);
        await using var viewport = await CreatePreview(preview);
        Assert.Equal("d3d9-emm-experimental", output.Capabilities.BackendId);
        Assert.Equal(output.Capabilities.BackendId, viewport.Capabilities.BackendId);
        var frame = Frame(7.5);
        output.ValidateSceneInputs(frame); viewport.ValidateSceneInputs(frame);
        using var pixels = await output.CaptureAsync(D3D9SurfaceFrameInput.FromPreview(frame), Token);
        await viewport.PresentAsync(frame, Token);
        await viewport.ResizeAsync(3, 4, Token);
        await viewport.PresentAsync(frame with { Width = 3, Height = 4 }, Token);
        Assert.Equal(new[] { (3, 4) }, preview.Resizes);
        Assert.Equal((3, 4), (preview.Snapshots[1].Width, preview.Snapshots[1].Height));
        Assert.All(capture.Snapshots.Concat(preview.Snapshots), snapshot =>
        { Assert.Equal(D3D9SurfaceScope.ConfiguredEmm, snapshot.Scope); Assert.Equal(7.5, snapshot.SourceFrame); });
        Assert.All(capture.Orders.Concat(preview.Orders), order => Assert.Equal(D3D9SurfaceRenderOrder.Original, order));
    }

    [Fact]
    public async Task EmmFactoryGetsTheCopied64BitGuidMapBeforeCallerMutation()
    {
        var first = new D3D9SurfaceObjectBinding(Guid.NewGuid(), 0xfedcba9876543210, "same.pmx", "first");
        var second = new D3D9SurfaceObjectBinding(Guid.NewGuid(), 0x1234567887654321, "same.pmx", "second");
        var configuration = Configuration([first, second]);
        var bindings = new List<D3D9SurfaceObjectBinding> { second, first };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyDictionary<Guid, ulong>? frozen = null;
        var creating = ExperimentalD3D9SurfaceViewRenderer.CreateEmmForTestingAsync(Request, bindings, Vector4.One,
            configuration, async (_, ids, _) =>
            { frozen = ids; entered.TrySetResult(); await release.Task; return new CaptureSession(); }, Token);
        await entered.Task.WaitAsync(Token);
        bindings.Clear(); release.TrySetResult();
        await using var output = await creating;
        Assert.NotNull(frozen);
        Assert.Equal(first.NativeId, frozen[first.ObjectId]); Assert.Equal(second.NativeId, frozen[second.ObjectId]);
        Assert.Equal(2, frozen.Count); Assert.Equal(2, output.SceneInputRequests.Count);
    }

    [Theory]
    [InlineData(false, "identity")]
    [InlineData(true, "identity")]
    [InlineData(false, "quality")]
    [InlineData(true, "quality")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task EmmCreationRejectsInvalidInputBeforeNativeFactory(bool preview, string invalid)
    {
        var binding = new D3D9SurfaceObjectBinding(Guid.NewGuid(), 42, "source.pmx", "actor");
        var configuration = Configuration([binding]); int creates = 0;
        var request = invalid == "quality" ? Request with { Quality = new(SpatialSamples: 2) } : Request;
        var token = invalid == "cancel" ? new CancellationToken(true) : Token;
        D3D9SurfaceObjectBinding[] bindings = invalid == "identity" ? [] : [binding];
        Func<Task> create = preview
            ? () => ExperimentalD3D9SurfacePreviewRenderer.CreateEmmForTestingAsync(request, 1, bindings, Vector4.One,
                configuration, (_, _, _) => { creates++; return ValueTask.FromResult<ID3D9SurfacePreviewSession>(new PreviewSession()); }, token)
            : () => ExperimentalD3D9SurfaceViewRenderer.CreateEmmForTestingAsync(request, bindings, Vector4.One,
                configuration, (_, _, _) => { creates++; return ValueTask.FromResult<ID3D9SurfaceCaptureSession>(new CaptureSession()); }, token);
        if (invalid == "identity") await Assert.ThrowsAsync<ArgumentException>(create);
        else if (invalid == "quality") await Assert.ThrowsAsync<NotSupportedException>(create);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(create);
        Assert.Equal(0, creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicEmmFactoryValidatesLiveGuidsBeforeOpeningTheNativeDll(bool preview)
    {
        var binding = new D3D9SurfaceObjectBinding(Guid.NewGuid(), 42, "source.pmx", "actor");
        using var assets = D3D9EmmRuntimeAssets.Open(Configuration([binding]));
        Func<Task> create = preview
            ? () => ExperimentalD3D9SurfacePreviewRenderer.CreateEmmAsync(Request, @"D:\absent\not-loaded.dll", 1,
                [], Vector4.One, assets, Token)
            : () => ExperimentalD3D9SurfaceViewRenderer.CreateEmmAsync(Request, @"D:\absent\not-loaded.dll",
                [], Vector4.One, assets, Token);
        await Assert.ThrowsAsync<ArgumentException>(create);
    }

    [Fact]
    public async Task EmmResizeFailureKeepsTheOldExtentAndCleanupRetriesAfterCaptureDrains()
    {
        var preview = new PreviewSession { FailFirstResize = true, FailFirstDispose = true };
        var viewport = await CreatePreview(preview);
        await Assert.ThrowsAsync<IOException>(() => viewport.ResizeAsync(3, 4, Token).AsTask());
        await viewport.PresentAsync(Frame(.5), Token);
        Assert.Equal((2, 2), (Assert.Single(preview.Snapshots).Width, preview.Snapshots[0].Height));
        await Assert.ThrowsAsync<IOException>(() => viewport.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => viewport.PresentAsync(Frame(.5), Token).AsTask());
        await viewport.DisposeAsync(); Assert.Equal(2, preview.Disposals);

        var capture = new CaptureSession { Block = true, FailFirstDispose = true };
        var output = await CreateCapture(capture);
        var pending = output.CaptureAsync(D3D9SurfaceFrameInput.FromPreview(Frame(.5)), Token).AsTask();
        await capture.Entered.Task.WaitAsync(Token);
        var disposal = output.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); Assert.Equal(0, capture.Disposals);
        capture.Release.TrySetResult(); using var pixels = await pending;
        await Assert.ThrowsAsync<IOException>(() => disposal);
        await output.DisposeAsync(); Assert.Equal(2, capture.Disposals);
    }

    private static PreviewFrame Frame(double source) => new(2, 2, source, new(new(SceneSnapshot.CreateEmpty(), [])), null, null, null);
    private static Task<ExperimentalD3D9SurfaceViewRenderer> CreateCapture(CaptureSession session) =>
        ExperimentalD3D9SurfaceViewRenderer.CreateEmmForTestingAsync(Request, [], Vector4.One, Configuration([]),
            (_, _, _) => ValueTask.FromResult<ID3D9SurfaceCaptureSession>(session), Token);
    private static Task<ExperimentalD3D9SurfacePreviewRenderer> CreatePreview(PreviewSession session) =>
        ExperimentalD3D9SurfacePreviewRenderer.CreateEmmForTestingAsync(Request, 1, [], Vector4.One, Configuration([]),
            (_, _, _) => ValueTask.FromResult<ID3D9SurfacePreviewSession>(session), Token);

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
        internal bool FailFirstResize, FailFirstDispose;
        internal int Disposals;
        public ValueTask<D3D9SurfacePresented> PresentAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token)
        { Snapshots.Add(snapshot); Orders.Add(order); return ValueTask.FromResult(new D3D9SurfacePresented(snapshot.Width, snapshot.Height, snapshot.SourceFrame)); }
        public ValueTask ResizeAsync(int width, int height, CancellationToken token)
        {
            Resizes.Add((width, height));
            if (Resizes.Count == 1 && FailFirstResize) throw new IOException("simulated resize failure");
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            if (++Disposals == 1 && FailFirstDispose) throw new IOException("simulated cleanup failure");
            return ValueTask.CompletedTask;
        }
    }
}
