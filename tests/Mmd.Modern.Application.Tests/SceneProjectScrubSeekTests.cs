using System.Collections.Concurrent;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneProjectScrubSeekTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "scrub-seek-" + Guid.NewGuid().ToString("N"));
    public SceneProjectScrubSeekTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ZeroSubstepsTryCaptureSeekRequestReturnsNull()
    {
        var f = Fixture();
        var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(0), cancellationToken: Token);
        Assert.Null(project.TryCaptureSeekRequest(15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedBackgroundSeekRunsFullSubstepsAndMatchesSynchronousSeekPose(bool shared)
    {
        const int substeps = 120;
        var texture = new SceneTexture("diffuse", 1, 1, [4, 5, 6, 255]);
        var f = Fixture(texture);
        var asyncFactory = new BackendFactory();
        var syncFactory = new BackendFactory();
        using var asyncProject = new SceneProjectSession(
            f.Layout, new(), f.Motions, asyncFactory.Provider(substeps), Settings(shared), cancellationToken: Token);
        using var syncProject = new SceneProjectSession(
            f.Layout, new(), f.Motions, syncFactory.Provider(substeps), Settings(shared), cancellationToken: Token);

        var initialWorld = Assert.Single(asyncFactory.Worlds);
        var liveFrameBeforePublish = asyncProject.Frame;

        var request = asyncProject.TryCaptureSeekRequest(15);
        Assert.NotNull(request);
        Assert.Equal(15, request.TargetFrame);
        Assert.True(request.IsCurrent(asyncProject));

        using var prepared = await Task.Run(() => request.Prepare(Token), Token);
        Assert.Equal(0, asyncProject.CurrentFrame);
        Assert.Same(liveFrameBeforePublish, asyncProject.Frame);
        Assert.Equal(2, asyncFactory.Worlds.Count);
        var candidateWorld = asyncFactory.Worlds.Last();
        Assert.Equal(substeps, candidateWorld.StepSeconds.Count);
        Assert.All(candidateWorld.StepSeconds, dt => Assert.Equal(1f / 120, dt));
        Assert.False(candidateWorld.Disposed);

        Assert.True(asyncProject.TryPublishSeek(prepared));
        Assert.False(asyncProject.TryPublishSeek(prepared));
        Assert.True(initialWorld.Disposed);
        Assert.False(candidateWorld.Disposed);

        syncProject.Seek(15, Token);
        AssertProjectEquivalent(syncProject, asyncProject, texture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealBulletBackgroundSeekMatchesSynchronousSeek(bool shared)
    {
        var f = Fixture();
        var provider = new ProjectSettingsBackendProvider(
            (settings, _) => new BulletCharacterPhysicsBackend(settings.Gravity, null),
            settings => new BulletCharacterPhysicsBackend(settings.Gravity, null),
            poseInitializationSubsteps: 120);
        using var asyncProject = new SceneProjectSession(f.Layout, new(), f.Motions, provider, Settings(shared), cancellationToken: Token);
        using var syncProject = new SceneProjectSession(f.Layout, new(), f.Motions, provider, Settings(shared), cancellationToken: Token);

        var request = asyncProject.TryCaptureSeekRequest(18);
        Assert.NotNull(request);
        using var prepared = await Task.Run(() => request.Prepare(Token), Token);
        Assert.True(asyncProject.TryPublishSeek(prepared));

        syncProject.Seek(18, Token);
        AssertProjectEquivalent(syncProject, asyncProject, expectedTexture: null);
    }

    [Theory]
    [MemberData(nameof(InvalidationCases))]
    public async Task StalePreparedSeekIsRejectedAndDisposedWithoutOverwritingNewState(string kind)
    {
        var f = Fixture();
        var factory = new BackendFactory();
        using var project = new SceneProjectSession(
            f.Layout, new(), f.Motions, factory.Provider(12), Settings(shared: false), cancellationToken: Token);

        // Prepare initial Undo/Redo history when testing undo/redo cases.
        project.Edit(edit => edit.Composition.SetTransform(f.Motions[0].ObjectId, new(1, 0, 0), Quaternion.Identity, 1), Token);
        if (kind == "redo")
            Assert.True(project.Undo(Token));

        var request = project.TryCaptureSeekRequest(20)!;
        using var prepared = await Task.Run(() => request.Prepare(Token), Token);
        var candidateWorld = factory.Worlds.Last();

        Action invalidate = kind switch
        {
            "edit" => () => project.Edit(edit => edit.Composition.SetTransform(f.Motions[0].ObjectId, new(9, 0, 0), Quaternion.Identity, 1), Token),
            "undo" => () => Assert.True(project.Undo(Token)),
            "redo" => () => Assert.True(project.Redo(Token)),
            "seek" => () => project.Seek(6, Token),
            "advance" => () => project.Advance(1d / 30, Token),
            "advance-preview" => () => project.AdvancePreview(1d / 30, Token),
            "edit-rendering-styles" => () => project.EditRenderingStyles(edit => edit.DefaultStyle = RenderingStyleId.Genshin, Token),
            "dispose" => project.Dispose,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        invalidate();

        if (kind != "dispose")
        {
            Assert.False(request.IsCurrent(project));
            var frameBeforeReject = project.Frame;
            double clockBeforeReject = project.CurrentFrame;
            Assert.False(project.TryPublishSeek(prepared));
            Assert.Same(frameBeforeReject, project.Frame);
            Assert.Equal(clockBeforeReject, project.CurrentFrame);
        }
        else
        {
            Assert.False(project.TryPublishSeek(prepared));
        }

        Assert.True(candidateWorld.Disposed);
    }

    public static IEnumerable<object[]> InvalidationCases() =>
    [
        ["edit"],
        ["undo"],
        ["redo"],
        ["seek"],
        ["advance"],
        ["advance-preview"],
        ["edit-rendering-styles"],
        ["dispose"]
    ];

    [Fact]
    public async Task CancelledPrepareDisposesPartialCandidateAndAliasedBackendThrows()
    {
        var f = Fixture();
        var factory = new BackendFactory();
        using var project = new SceneProjectSession(
            f.Layout, new(), f.Motions, factory.Provider(20), Settings(shared: false), cancellationToken: Token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        factory.ConfigureNext = world => world.OnStep = count =>
        {
            if (count == 4) cts.Cancel();
        };
        var request = project.TryCaptureSeekRequest(12)!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => request.Prepare(cts.Token), Token));
        Assert.Equal(2, factory.Worlds.Count);
        Assert.True(factory.Worlds.Last().Disposed);

        // Guard against any provider bug returning the active solver instance.
        var sharedAlias = new ProbeBackend();
        var aliasProvider = new ProjectSettingsBackendProvider((_, _) => sharedAlias, _ => sharedAlias, poseInitializationSubsteps: 3);
        using var aliasProject = new SceneProjectSession(f.Layout, new(), f.Motions, aliasProvider, Settings(shared: false), cancellationToken: Token);
        var badRequest = aliasProject.TryCaptureSeekRequest(10)!;
        using var badPrepared = await Task.Run(() => badRequest.Prepare(Token), Token);
        Assert.Throws<InvalidOperationException>(() => aliasProject.TryPublishSeek(badPrepared));
    }

    [Fact]
    public void RequiresPhysicsPreRollReflectsSubstepsPolicyAndAliveState()
    {
        var f = Fixture();
        var factory = new BackendFactory();
        using var noPreRoll = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(0), cancellationToken: Token);
        Assert.False(noPreRoll.RequiresPhysicsPreRoll);

        var withPreRoll = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(120), cancellationToken: Token);
        Assert.True(withPreRoll.RequiresPhysicsPreRoll);
        withPreRoll.Dispose();
        Assert.Throws<ObjectDisposedException>(() => withPreRoll.RequiresPhysicsPreRoll);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekWithoutPhysicsPreRollEvaluatesBonesAndMorphsSkipsPreRollAndConvergesAfterFullSeek(bool shared)
    {
        const int substeps = 120;
        var f = MorphAndBoneFixture();
        var scrubFactory = new BackendFactory();
        var noPreRollFactory = new BackendFactory();
        var fullFactory = new BackendFactory();
        using var scrubProject = new SceneProjectSession(
            f.Layout, new(), f.Motions, scrubFactory.Provider(substeps), Settings(shared), cancellationToken: Token);
        using var zeroSubstepProject = new SceneProjectSession(
            f.Layout, new(), f.Motions, noPreRollFactory.Provider(0), Settings(shared), cancellationToken: Token);
        using var syncProject = new SceneProjectSession(
            f.Layout, new(), f.Motions, fullFactory.Provider(substeps), Settings(shared), cancellationToken: Token);

        var initialWorld = Assert.Single(scrubFactory.Worlds);
        Assert.Equal(substeps, initialWorld.StepSeconds.Count);

        var previewFrame = scrubProject.SeekWithoutPhysicsPreRoll(15, Token);
        Assert.Same(previewFrame, scrubProject.Frame);
        Assert.Equal(15, scrubProject.CurrentFrame);
        Assert.Single(scrubFactory.Worlds);
        Assert.False(initialWorld.Disposed);
        Assert.Equal(substeps, initialWorld.StepSeconds.Count);

        // Bones (X=+6) and morphs (Z=+1 on vertex 0) match zero-substep Seek without 120 gravity steps.
        zeroSubstepProject.Seek(15, Token);
        AssertProjectEquivalent(zeroSubstepProject, scrubProject, expectedTexture: null);
        Assert.Equal(6f, scrubProject.Frame.Surface.Snapshot.Vertices[0].Position.X, precision: 4);
        Assert.Equal(1f, scrubProject.Frame.Surface.Snapshot.Vertices[0].Position.Z, precision: 4);

        syncProject.Seek(15, Token);
        Assert.NotEqual(syncProject.Frame.Surface.Snapshot.Vertices.ToArray(), scrubProject.Frame.Surface.Snapshot.Vertices.ToArray());

        // A subsequent full Seek warms the stabilized physics pose and converges bit-for-bit.
        scrubProject.Seek(15, Token);
        Assert.Equal(2, scrubFactory.Worlds.Count);
        Assert.True(initialWorld.Disposed);
        Assert.Equal(substeps, scrubFactory.Worlds.Last().StepSeconds.Count);
        AssertProjectEquivalent(syncProject, scrubProject, expectedTexture: null);
    }

    [Fact]
    public async Task SeekWithoutPhysicsPreRollChecksValidationCancellationAndInvalidatesStaleSeekRequests()
    {
        var f = Fixture();
        var factory = new BackendFactory();
        using var project = new SceneProjectSession(
            f.Layout, new(), f.Motions, factory.Provider(20), Settings(shared: false), cancellationToken: Token);

        Assert.Throws<ArgumentOutOfRangeException>(() => project.SeekWithoutPhysicsPreRoll(-1, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => project.SeekWithoutPhysicsPreRoll(1.5, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => project.SeekWithoutPhysicsPreRoll(double.NaN, Token));

        var requestBeforeCancelled = project.TryCaptureSeekRequest(10)!;
        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();
        var frameBeforeCancel = project.Frame;
        Assert.Throws<OperationCanceledException>(() => project.SeekWithoutPhysicsPreRoll(12, cancelledCts.Token));
        Assert.Equal(0, project.CurrentFrame);
        Assert.Same(frameBeforeCancel, project.Frame);
        Assert.True(requestBeforeCancelled.IsCurrent(project));

        // Performing SeekWithoutPhysicsPreRoll bumps stateEpoch and invalidates prior captured seek requests.
        using var prepared = await Task.Run(() => requestBeforeCancelled.Prepare(Token), Token);
        var staleWorld = factory.Worlds.Last();
        project.SeekWithoutPhysicsPreRoll(18, Token);
        Assert.False(requestBeforeCancelled.IsCurrent(project));
        Assert.False(project.TryPublishSeek(prepared));
        Assert.True(staleWorld.Disposed);
        Assert.Equal(18, project.CurrentFrame);

        Assert.Throws<InvalidOperationException>(() =>
            project.Edit(_ => project.SeekWithoutPhysicsPreRoll(5, Token), Token));

        project.Dispose();
        Assert.Throws<ObjectDisposedException>(() => project.SeekWithoutPhysicsPreRoll(0, Token));
    }

    private (SceneCompositionEditor Layout, SceneActorMotion[] Motions) MorphAndBoneFixture()
    {
        var baseModel = SharedPhysicsFixture.Model();
        var model = new PmxModel
        {
            Header = baseModel.Header, Name = baseModel.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Textures = baseModel.Textures, Faces = baseModel.Faces, Materials = baseModel.Materials,
            Bones = baseModel.Bones, DisplayFrames = baseModel.DisplayFrames,
            RigidBodies = baseModel.RigidBodies, Joints = baseModel.Joints, SoftBodies = baseModel.SoftBodies,
            Vertices = baseModel.Vertices,
            Morphs = [new() { Name = "push-z", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
                PositionOffsets = [new(0, new(0, 0, 2))] }]
        };
        string path = Path.Combine(directory, "morph-actor.pmx");
        File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var rest = SharedPhysicsFixture.Rest(model);
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("morph-actor", path, ModelRole.Character, rest);
        var motion = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "root" },
                new() { BoneName = "root", Frame = 30, Translation = new(12, 0, 0) }
            ],
            MorphKeyframes =
            [
                new() { MorphName = "push-z", Frame = 0, Weight = 0 },
                new() { MorphName = "push-z", Frame = 30, Weight = 1 }
            ]
        };
        var asset = SceneMotionAsset.FromMotion(motion);
        return (layout, [new(actor.Id, model, asset.Motion, Asset: asset)]);
    }

    private static void AssertProjectEquivalent(SceneProjectSession expected, SceneProjectSession actual, SceneTexture? expectedTexture)
    {
        Assert.Equal(expected.CurrentFrame, actual.CurrentFrame);
        Assert.Equal(Assert.Single(expected.Actors).LocalFrame, Assert.Single(actual.Actors).LocalFrame);
        Assert.Equal(expected.Frame.Surface.Snapshot.Vertices, actual.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(
            expected.CaptureRenderSnapshot().Scene.Surface.Snapshot.Vertices,
            actual.CaptureRenderSnapshot().Scene.Surface.Snapshot.Vertices);
        if (expectedTexture is not null)
            Assert.Same(expectedTexture, Assert.Single(actual.Frame.Surface.Snapshot.Textures));
    }

    private (SceneCompositionEditor Layout, SceneActorMotion[] Motions) Fixture(SceneTexture? texture = null)
    {
        var model = SharedPhysicsFixture.Model();
        string path = Path.Combine(directory, "actor.pmx");
        File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var rest = SharedPhysicsFixture.Rest(model);
        if (texture is not null)
        {
            var source = rest.Surface.Snapshot;
            var snapshot = new SceneSnapshot(source.Vertices, source.Indices,
                source.Materials.Select(m => m with { DiffuseTexture = 0 }), [texture],
                source.World, source.Camera, source.LightDirection, source.AdditionalUvs, source.RawRadialSeeds);
            rest = new(new(snapshot, rest.Surface.Materials, rest.Surface.Profile), rest.Outline, rest.OutlineOptions, materials: rest.Materials);
        }
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", path, ModelRole.Character, rest);
        var motion = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "root" },
                new() { BoneName = "root", Frame = 30, Translation = new(12, 0, 0) }
            ]
        };
        var asset = SceneMotionAsset.FromMotion(motion);
        return (layout, [new(actor.Id, model, asset.Motion, Asset: asset)]);
    }

    private static ProjectSettings Settings(bool shared) => new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } };

    private sealed class BackendFactory
    {
        internal readonly ConcurrentQueue<ProbeBackend> Worlds = new();
        internal Action<ProbeBackend>? ConfigureNext;
        internal ProjectSettingsBackendProvider Provider(int steps)
            => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: steps);
        private ProbeBackend Create()
        {
            var world = new ProbeBackend();
            var configure = Interlocked.Exchange(ref ConfigureNext, null);
            configure?.Invoke(world);
            Worlds.Enqueue(world);
            return world;
        }
    }

    private sealed class ProbeBackend : ICharacterPhysicsMutableBackend
    {
        private readonly TrackingSharedBackend inner = new();
        internal readonly List<float> StepSeconds = [];
        internal volatile bool Disposed;
        internal Action<int>? OnStep;
        public string Name => "scrub probe";
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
        public void Step(float seconds)
        {
            StepSeconds.Add(seconds);
            inner.Step(seconds);
            OnStep?.Invoke(StepSeconds.Count);
        }
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
