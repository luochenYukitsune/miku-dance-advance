using System.Numerics;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(EditorFrameAgeDiagnosticsCollection.Name)]
public sealed class EditorViewportLatestCaptureTests : IAsyncDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Host = typeof(EditorViewportHost);
    private HeadlessUnitTestSession? session;
    private HeadlessUnitTestSession Headless => session ??= HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));

    [Fact]
    public async Task ActiveWorkCoalescesRequestsWithoutPostingCaptureOrKeepingOldPending() => await Headless.Dispatch(() =>
    {
        var viewport = CreateBusy();
        try
        {
            Set(viewport, "pending", Work(18)); // B was queued before the new request C.
            viewport.RequestRender();
            viewport.RequestRender();
            Assert.False((bool)Get(viewport, "capturePosted")!);
            Assert.Null(Get(viewport, "pending"));
            Assert.True((long)Get(viewport, "demandSequence")! > (long)Get(viewport, "capturedDemandSequence")!);
            Assert.Null(Get(viewport, "renderer"));
        }
        finally { Clean(viewport); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CompletionWakesLatestDemandOnceAndNeverWaitsForUi() => await Headless.Dispatch(() =>
    {
        var viewport = CreateBusy();
        try
        {
            viewport.RequestRender(); viewport.RequestRender();
            Assert.False((bool)Get(viewport, "capturePosted")!);
            // Simulate Present returning on a worker. The dispatcher is deliberately not pumped.
            Task.Run(() => Invoke(viewport, "CompleteActiveWork")).GetAwaiter().GetResult();
            Assert.True((bool)Get(viewport, "capturePosted")!);
            Assert.False((bool)Get(viewport, "workerRunning")!);
            Assert.Null(Get(viewport, "pending"));
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OldEpochPostCannotClearNewEpochPost() => await Headless.Dispatch(() =>
    {
        var viewport = CreateIdle();
        try
        {
            viewport.RequestRender();
            long oldRevision = (long)Get(viewport, "revision")!;
            long oldToken = (long)Get(viewport, "capturePostToken")!;
            viewport.RestartRenderer();
            Assert.True((bool)Get(viewport, "capturePosted")!);
            long newToken = (long)Get(viewport, "capturePostToken")!;
            Assert.NotEqual(oldToken, newToken);
            Invoke(viewport, "CapturePendingFrame", oldRevision, oldToken);
            Assert.True((bool)Get(viewport, "capturePosted")!);
            Assert.Equal(newToken, Get(viewport, "capturePostToken"));
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RestartDuringActiveWorkIsWokenWhenOldWorkCompletes() => await Headless.Dispatch(() =>
    {
        var viewport = CreateBusy();
        try
        {
            viewport.RestartRenderer();
            Assert.False((bool)Get(viewport, "capturePosted")!);
            Task.Run(() => Invoke(viewport, "CompleteActiveWork")).GetAwaiter().GetResult();
            Assert.True((bool)Get(viewport, "capturePosted")!);
            Assert.True((long)Get(viewport, "demandSequence")! > (long)Get(viewport, "capturedDemandSequence")!);
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SuspendInvalidatesDemandAndCompletionCannotWakeIt() => await Headless.Dispatch(() =>
    {
        var viewport = CreateBusy();
        try
        {
            viewport.RequestRender();
            viewport.IsSuspended = true;
            Task.Run(() => Invoke(viewport, "CompleteActiveWork")).GetAwaiter().GetResult();
            Assert.False((bool)Get(viewport, "capturePosted")!);
            Assert.Null(Get(viewport, "pending"));
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SupersededQueuedWorkDoesNotAdvanceAdmission() => await Headless.Dispatch(() =>
    {
        var viewport = CreateBusy();
        try
        {
            var work = Work(18, poseInputPending: true);
            Invoke(viewport, "QueueWork", work);
            var admission = Get(viewport, "poseFrameAdmission")!;
            var last = admission.GetType().GetField("last", Hidden)!;
            Assert.Null(last.GetValue(admission)); // queued B was never submitted
            viewport.RequestRender();
            Assert.Null(Get(viewport, "pending"));
            Assert.Null(last.GetValue(admission));
            Set(viewport, "capturedDemandSequence", Get(viewport, "demandSequence")); // latest capture consumed C
            Invoke(viewport, "QueueWork", work); // same pose remains eligible at dequeue
            Assert.NotNull(Invoke(viewport, "TakePendingWork"));
            Assert.NotNull(last.GetValue(admission));
            var candidate = work.GetType().GetProperty("PoseCandidate")!.GetValue(work)!;
            var admit = admission.GetType().GetMethod("TryAdmit", Hidden)!;
            Assert.False((bool)admit.Invoke(admission, [candidate])!); // This exact pose really meets suppression conditions.
        }
        finally { Clean(viewport); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CaptureReenteringSuspendCannotQueueOrDequeueWork() => await Headless.Dispatch(() =>
    {
        var viewport = CreateBusy();
        try
        {
            viewport.IsSuspended = true;
            Invoke(viewport, "QueueWork", Work(18));
            Assert.Null(Get(viewport, "pending"));
            Assert.Null(Invoke(viewport, "TakePendingWork"));
            viewport.IsSuspended = false;
            Assert.True((long)Get(viewport, "demandSequence")! > (long)Get(viewport, "capturedDemandSequence")!);
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SharedGateDefersRealProjectCaptureUntilLatestPreviewDemand() => await Headless.Dispatch(() =>
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
            materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var actors = Enumerable.Range(0, 2).Select(i => layout.Add("actor" + i, "actor" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 16, Translation = new(16, 0, 0) }] };
        using var project = new SceneProjectSession(layout, new(), actors.Select(a => new SceneActorMotion(a.Id, model, motion, PhysicsEnabled: false)).ToArray(),
            new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException()),
            pmmControls: Controls(actors, model), cancellationToken: TestContext.Current.CancellationToken);
        var animation = typeof(SceneProjectSession).GetField("animation", Hidden)!.GetValue(project)!;
        var entries = (Array)animation.GetType().GetField("entries", Hidden)!.GetValue(animation)!;
        var entry = entries.GetValue(1)!;
        var leaf = (CharacterAnimationController)entry.GetType().GetProperty("Controller")!.GetValue(entry)!;
        var original = leaf.BoneWorldMatrices;
        var viewport = CreateIdle();
        try
        {
            viewport.RequestRender();
            long revision = (long)Get(viewport, "revision")!, staleToken = (long)Get(viewport, "capturePostToken")!;
            Set(viewport, "workerRunning", true);
            int captures = 0; SceneProjectRenderSnapshot? snapshot = null;
            Action capture = () => { captures++; snapshot = project.CaptureRenderSnapshot([new(actors[1].Id, [0], includeSourceGeometry: true)]); };
            for (int i = 0; i < 3; i++) { project.AdvancePreview(1.0 / 30); viewport.RequestRender(); }
            Invoke(viewport, "RunCapture", revision, staleToken, capture);
            Assert.Equal(0, captures); Assert.Same(original, leaf.BoneWorldMatrices);
            Task.Run(() => Invoke(viewport, "CompleteActiveWork")).GetAwaiter().GetResult();
            long latestToken = (long)Get(viewport, "capturePostToken")!;
            Invoke(viewport, "RunCapture", revision, latestToken, capture);
            Assert.Equal(1, captures); Assert.NotNull(snapshot);
            Assert.Equal(project.CurrentFrame, snapshot.SourceFrame); Assert.Equal(3d, snapshot.SourceFrame);
            Assert.NotSame(original, leaf.BoneWorldMatrices);
            Assert.NotEqual(Matrix4x4.Identity, snapshot.SceneInputs[actors[1].Id].BoneWorldMatrices[0]);
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OldCaptureThrowAfterRestartCannotPoisonNewEpoch() => await Headless.Dispatch(() =>
    {
        var viewport = CreateIdle(); int errors = 0;
        try
        {
            viewport.Error += _ => errors++;
            viewport.RequestRender();
            long revision = (long)Get(viewport, "revision")!, token = (long)Get(viewport, "capturePostToken")!;
            Invoke(viewport, "RunCapture", revision, token, (Action)(() => { viewport.RestartRenderer(); throw new InvalidOperationException("old capture"); }));
            Assert.False(viewport.HasRendererError); Assert.True(viewport.IsPreparingFrame); Assert.Equal(0, errors);
            Assert.True((bool)Get(viewport, "capturePosted")!);
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureThrowAfterSuspendDoesNotFailResumedPreview(bool resumeInsideCapture) => await Headless.Dispatch(() =>
    {
        var viewport = CreateIdle(); int errors = 0;
        try
        {
            viewport.Error += _ => errors++;
            viewport.PrepareNextFrame();
            long revision = (long)Get(viewport, "revision")!, token = (long)Get(viewport, "capturePostToken")!;
            Invoke(viewport, "RunCapture", revision, token, (Action)(() =>
            {
                viewport.IsSuspended = true;
                if (resumeInsideCapture) viewport.IsSuspended = false;
                throw new InvalidOperationException("suspended capture");
            }));
            Assert.False(viewport.HasRendererError); Assert.True(viewport.IsPreparingFrame); Assert.Equal(0, errors);
            viewport.IsSuspended = false;
            Assert.True((bool)Get(viewport, "capturePosted")!);
        }
        finally { Clean(viewport); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    private static PmxModel Model() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0, VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "actor", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2)], Textures = [],
        Materials = [new() { Name = "toon", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 4, Ambient = new(.3f), DrawMode = PmxDrawModeFlags.None, EdgeSize = 1, SphereMode = PmxSphereMode.None, EdgeColor = new(0, 0, 0, 1), TextureIndex = -1, SphereTextureIndex = -1, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }], Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [], RigidBodies = []
    };
    private static ScenePmmControlState Controls(EditableSceneObject[] actors, PmxModel model)
    {
        PmmString S(string text) => new(0, text, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(x => x).ToArray();
        var models = actors.Select((actor, i) => new PmmModel { Index = (byte)i, Name = S(actor.Name), Path = S("actor" + i + ".pmx"), BoneNames = [S("root")],
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 16,
            InitialBoneKeyframes = [new() { ObjectIndex = 0, Rotation = Quaternion.Identity, Interpolation = linear }],
            InitialModelKeyframe = new() { Visible = true }, BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)] }).ToArray();
        var document = new PmmDocument { AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30, Models = models,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } } };
        return ScenePmmControlState.Create(document, actors.Select((actor, i) => new ScenePmmActorSource(actor.Id, (byte)i, model)));
    }

    private static EditorViewportHost CreateIdle()
    {
        var viewport = new EditorViewportHost();
        Set(viewport, "childWindow", (nint)1); // Sentinel only; no native child window is created.
        Set(viewport, "accepting", true);
        Set(viewport, "revision", 7L);
        return viewport;
    }
    private static EditorViewportHost CreateBusy()
    {
        var viewport = CreateIdle(); Set(viewport, "workerRunning", true); return viewport;
    }
    private static object Work(double source, bool poseInputPending = false)
    {
        var camera = new SceneCamera(new(0, 1, -3), Vector3.Zero, Vector3.UnitY, 4, .1f, 100);
        var frame = new PreviewFrame(100, 80, source, new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(camera), [])), null, null, null) { ResolvedCamera = camera };
        var quality = new ViewQualityOptions(); var bindings = EditorModernRenderEngine.Instance.EmptyBinding;
        var candidate = new EditorPoseFrameCandidate(0, 100, 80, 7, quality, bindings, frame, 0, new object(), null, null, poseInputPending, false, poseInputPending);
        var type = Host.GetNestedType("RenderWork", BindingFlags.NonPublic)!;
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(c => c.GetParameters().Length == 14).Invoke(
            [(nint)0, 100, 80, 7L, quality, bindings, frame, 0L, candidate, 0L, 0L, 0L, 0L, null]);
    }
    private static object? Get(EditorViewportHost viewport, string name)
    { var field = Host.GetField(name, Hidden); Assert.NotNull(field); return field.GetValue(viewport); }
    private static void Set(EditorViewportHost viewport, string name, object? value)
    { var field = Host.GetField(name, Hidden); Assert.NotNull(field); field.SetValue(viewport, value); }
    private static object? Invoke(EditorViewportHost viewport, string name, params object?[] args)
    { var method = Host.GetMethod(name, Hidden); Assert.NotNull(method); return method.Invoke(viewport, args); }
    private static void Clean(EditorViewportHost viewport)
    { Set(viewport, "childWindow", nint.Zero); viewport.Dispose(); }
    public async ValueTask DisposeAsync() { if (session is { } current) await Task.Run(current.Dispose).ConfigureAwait(false); }
}
