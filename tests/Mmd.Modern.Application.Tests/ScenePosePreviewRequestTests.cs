using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>Detached request ownership and numerical orchestration contracts; no native/GPU dependency.</summary>
public sealed class ScenePosePreviewRequestTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CaptureDoesNotCreateOrStepSolversOrChangeTheLivePoseAndAuthoredMotion()
    {
        var live = new CountingFactory();
        using var source = Create(live, out var id);
        var frame = source.Frame; var authored = Assert.Single(source.CaptureAuthoredContent().Motions);
        var world = Assert.Single(live.Worlds); int steps = world.StepSeconds.Count;
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [id] = Motion(60) });
        Assert.True(request.IsCurrent(source)); Assert.Single(live.Worlds);
        Assert.Equal(steps, world.StepSeconds.Count); Assert.False(world.Disposed);
        Assert.Same(frame, source.Frame); Assert.Equal(7, source.CurrentFrame);
        Assert.Same(authored.Motion, Assert.Single(source.CaptureAuthoredContent().Motions).Motion);
        Assert.False(source.CanUndo); Assert.False(source.CanRedo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerUsesFreshOwnedSolversAndTheFullPolicyWithExactlyTheSynchronousPose(bool shared)
    {
        var live = new CountingFactory();
        using var source = Create(live, out var id, shared);
        var before = source.Frame;
        var motion = Motion(60);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [id] = motion });
        var inputs = new[] { new SceneObjectRenderRequest(id, [0]) };
        using var synchronous = source.CreatePosePreview(id, motion, Token);
        var expected = synchronous.CaptureRenderSnapshot(inputs);
        var worker = new CountingFactory();
        var actual = await Task.Run(() =>
        {
            using var detached = request.CreateSession(worker.Provider(), Token);
            var world = Assert.Single(worker.Worlds);
            Assert.Equal(Environment.CurrentManagedThreadId, world.CreatedThread);
            Assert.False(world.Disposed);
            return detached.CaptureRenderSnapshot(inputs);
        }, Token);
        var owned = Assert.Single(worker.Worlds);
        Assert.True(owned.Disposed); AssertSteps(owned, 120);
        Assert.NotSame(live.Worlds[0], owned);
        Assert.Equal(expected.SourceFrame, actual.SourceFrame);
        Assert.Equal(expected.Scene.Surface.Snapshot.Vertices.ToArray(), actual.Scene.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.Scene.Surface.Snapshot.Indices.ToArray(), actual.Scene.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(expected.Scene.Outline!.Vertices.ToArray(), actual.Scene.Outline!.Vertices.ToArray());
        Assert.Equal(expected.SceneInputs[id].BoneWorldMatrices[0], actual.SceneInputs[id].BoneWorldMatrices[0]);
        Assert.Equal(expected.RenderingStyles.ToArray(), actual.RenderingStyles.ToArray());
        Assert.Same(before, source.Frame); Assert.True(request.IsCurrent(source));
        AssertSteps(live.Worlds[0], 120); Assert.False(live.Worlds[0].Disposed);
        Assert.False(source.CanUndo); Assert.False(source.CanRedo);
    }

    [Fact]
    public void SameFrameSeekCommitUndoPlaybackAndDisposalEachInvalidateTheirCapturedRequest()
    {
        var live = new CountingFactory();
        using var source = Create(live, out var id);
        ScenePosePreviewRequest Capture() => source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [id] = Motion(60) });
        var seek = Capture(); Assert.True(seek.IsCurrent(source));
        source.Seek(source.CurrentFrame, Token); Assert.False(seek.IsCurrent(source));
        var commit = Capture(); Assert.True(commit.IsCurrent(source));
        source.Edit(edit => edit.Composition.SetTransform(id, Vector3.UnitX, Quaternion.Identity, 1), Token);
        Assert.False(commit.IsCurrent(source));
        var undo = Capture(); Assert.True(undo.IsCurrent(source));
        Assert.True(source.Undo(Token)); Assert.False(undo.IsCurrent(source)); Assert.False(seek.IsCurrent(source));
        var playback = Capture(); Assert.True(playback.IsCurrent(source));
        source.Advance(1d / 120, Token); Assert.False(playback.IsCurrent(source));
        var disposal = Capture(); Assert.True(disposal.IsCurrent(source));
        source.Dispose(); Assert.False(disposal.IsCurrent(source));
    }

    [Fact]
    public async Task CancelledInitializationReleasesItsCandidateAndRetainsTheLiveSolverPoseAndHistory()
    {
        var live = new CountingFactory();
        using var source = Create(live, out var id);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [id] = Motion(60) });
        var before = source.Frame; var world = Assert.Single(live.Worlds);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var rejected = new CountingFactory { AfterStep = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
        {
            using var unexpected = request.CreateSession(rejected.Provider(), cancellation.Token);
        }, Token));
        var candidate = Assert.Single(rejected.Worlds);
        Assert.True(candidate.Disposed); Assert.Single(candidate.StepSeconds);
        Assert.Same(before, source.Frame); Assert.Equal(7, source.CurrentFrame);
        AssertSteps(world, 120); Assert.False(world.Disposed); Assert.True(request.IsCurrent(source));
        Assert.False(source.CanUndo); Assert.False(source.CanRedo);
        var retry = new CountingFactory();
        using (var owner = request.CreateSession(retry.Provider(), Token))
        { AssertSteps(Assert.Single(retry.Worlds), 120); Assert.Equal(7, owner.CurrentFrame); }
        Assert.True(Assert.Single(retry.Worlds).Disposed);
    }

    [Fact]
    public async Task CapturedDraftRemainsDetachedAndEvaluatesAfterItsSourceHasBeenDisposed()
    {
        var live = new CountingFactory();
        using var source = Create(live, out var id);
        var keys = Motion(60).BoneKeyframes.ToArray();
        var mutable = new VmdMotion { BoneKeyframes = keys };
        using var synchronous = source.CreatePosePreview(id, mutable, Token);
        var expected = synchronous.Frame.Surface.Snapshot.Vertices.ToArray();
        var overrides = new Dictionary<Guid, VmdMotion> { [id] = mutable };
        var request = source.CapturePosePreviewRequest(overrides);
        keys[1] = new() { BoneName = "root", Frame = 30, Translation = new(900, 0, 0) }; overrides.Clear();
        source.Dispose(); Assert.False(request.IsCurrent(source));
        var worker = new CountingFactory();
        var actual = await Task.Run(() =>
        {
            using var owner = request.CreateSession(worker.Provider(), Token);
            return owner.CaptureRenderSnapshot();
        }, Token);
        Assert.Equal(expected, actual.Scene.Surface.Snapshot.Vertices.ToArray());
        AssertSteps(Assert.Single(worker.Worlds), 120); Assert.True(worker.Worlds[0].Disposed);
        Assert.True(live.Worlds[0].Disposed);
    }

    [Fact]
    public void AlreadyCancelledRequestDoesNotAskTheWorkerProviderForAnySolver()
    {
        var live = new CountingFactory();
        using var source = Create(live, out var id);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [id] = Motion(60) });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var worker = new CountingFactory(); var before = source.Frame;
        Assert.ThrowsAny<OperationCanceledException>(() => request.CreateSession(worker.Provider(), cancellation.Token));
        Assert.Empty(worker.Worlds); Assert.Same(before, source.Frame); Assert.True(request.IsCurrent(source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FaultedPlaybackPreservesItsFirstFailureForLaterPreviewAndRenderRequests(bool shared, bool cancel)
    {
        var factory = new CountingFactory();
        using var source = Create(factory, out _, shared);
        var before = source.Frame; double clock = source.CurrentFrame;
        var world = Assert.Single(factory.Worlds);
        using var cancellation = new CancellationTokenSource();
        var backendFailure = new InvalidOperationException("physics failed after mutating its state");
        world.AfterStep = cancel ? cancellation.Cancel : () => throw backendFailure;
        Exception failure = cancel
            ? Assert.ThrowsAny<OperationCanceledException>(() => source.Advance(1d / 30, cancellation.Token))
            : Assert.ThrowsAny<Exception>(() => source.Advance(1d / 30, Token));

        Assert.True(source.IsFaulted); Assert.Same(failure, source.FaultCause);
        Assert.Same(before, source.Frame); Assert.Equal(clock, source.CurrentFrame);
        int steps = world.StepSeconds.Count;
        var motions = new Dictionary<Guid, VmdMotion>();
        var previewFailure = Assert.Throws<InvalidOperationException>(() => source.CreatePosePreview(motions, Token));
        var requestFailure = Assert.Throws<InvalidOperationException>(() => source.CapturePosePreviewRequest(motions));
        var renderFailure = Assert.Throws<InvalidOperationException>(() => source.CaptureRenderSnapshot());
        var advanceFailure = Assert.Throws<InvalidOperationException>(() => source.Advance(1d / 30, Token));
        Assert.Same(failure, previewFailure.InnerException);
        Assert.Same(failure, requestFailure.InnerException);
        Assert.Same(failure, renderFailure.InnerException);
        Assert.Same(failure, advanceFailure.InnerException);
        Assert.Single(factory.Worlds); Assert.Equal(steps, world.StepSeconds.Count);
        Assert.Same(before, source.Frame); Assert.Equal(clock, source.CurrentFrame);

        // Recovery remains an explicit owner action, and must not keep a stale fault as its new cause.
        source.Seek(0, Token);
        Assert.False(source.IsFaulted); Assert.Null(source.FaultCause);
        Assert.NotNull(source.CapturePosePreviewRequest(motions));
        using var preview = source.CreatePosePreview(motions, Token);
        Assert.False(preview.IsFaulted);
    }

    private static SceneProjectSession Create(CountingFactory factory, out Guid id, bool shared = false)
    {
        var model = SharedPhysicsFixture.Model(); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", Path.GetFullPath("pose-request-fixture.pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model));
        id = actor.Id;
        return new(layout, new(), [new(actor.Id, model, Motion(30))], factory.Provider(),
            new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } }, initialFrame: 7, cancellationToken: Token);
    }

    private static VmdMotion Motion(float x) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = new(x, 0, 0) }]
    };

    private static void AssertSteps(CountingBackend world, int count)
    { Assert.Equal(count, world.StepSeconds.Count); Assert.All(world.StepSeconds, seconds => Assert.Equal(1f / 120, seconds)); }

    private sealed class CountingFactory
    {
        internal readonly List<CountingBackend> Worlds = [];
        internal Action? AfterStep;
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 120);
        private CountingBackend Create()
        { var world = new CountingBackend { AfterStep = AfterStep }; Worlds.Add(world); return world; }
    }

    private sealed class CountingBackend : ICharacterPhysicsMutableBackend
    {
        private readonly TrackingSharedBackend inner = new();
        internal readonly int CreatedThread = Environment.CurrentManagedThreadId;
        internal readonly List<float> StepSeconds = [];
        internal bool Disposed;
        internal Action? AfterStep;
        public string Name => "detached request counting backend";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => inner.RemoveBody(index);
        public void RemoveJoint(int index) => inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool state) => inner.SetBodyKinematic(index, state);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => inner.SetKinematicTarget(index, transform, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => inner.ApplyImpulse(index, linear, angular);
        public void Step(float seconds) { StepSeconds.Add(seconds); inner.Step(seconds); AfterStep?.Invoke(); }
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }
}
