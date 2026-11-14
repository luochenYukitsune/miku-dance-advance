using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterPhysicsPlaybackTests
{
    [Fact]
    public void ActualBepuFeedbackChangesTheSkinnedVertexAndAfterPhysicsIk()
    {
        RigBone[] bones = [new("link", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("physical", new(1, 1, 0), -1), new("goal", new(1, 1, 0), 2, AfterPhysics: true,
                Ik: new(1, 16, MathF.PI / 2, [new(0)]))];
        var animator = Animator(bones, [Vertex(Vector3.UnitX), Vertex(new(1, 1, 0))], [Binding(1), Binding(2)]);
        var definition = Definition(bones, [Body(2, PhysicsBodyMode.Dynamic, new(1, 1, 0))]);
        using var playback = new CharacterPhysicsPlayback(animator, definition, new BepuCharacterPhysicsBackend(new(0, -4, 0), null));
        var initial = playback.ResetAt(0);
        var result = initial;
        for (int i = 1; i <= 30; i++) result = playback.Advance(i / 4d, 1d / 120);
        Assert.True(result.BoneWorldMatrices[2].Translation.Y < initial.BoneWorldMatrices[2].Translation.Y - .1f);
        Assert.True(Vector3.Distance(initial.BoneWorldMatrices[1].Translation, result.BoneWorldMatrices[1].Translation) > .03f);
        Near(Vector3.Normalize(result.BoneWorldMatrices[3].Translation), result.BoneWorldMatrices[1].Translation);
        Near(result.BoneWorldMatrices[1].Translation, result.Vertices[0].Position);
        Near(result.BoneWorldMatrices[2].Translation, result.Vertices[1].Position);
        Assert.Contains(playback.Diagnostics, item => item.Contains("Bullet", StringComparison.Ordinal));
        Near(Vector3.UnitX, playback.ResetAt(0, new(EnableIk: false)).Vertices[0].Position);
        Near(Vector3.UnitX, playback.Advance(1, 1d / 30, new(EnableIk: false)).Vertices[0].Position);
    }

    [Fact]
    public void OneRenderAdvanceUsesTheSessionsFourFixedStepsAndTenStepCap()
    {
        var backend = new RecordingBackend();
        using var playback = Playback(backend);
        playback.ResetAt(0);
        playback.Advance(1, 1d / 30);
        Assert.Equal(4, playback.LastStepCount);
        Assert.Equal(4, backend.StepDurations.Count);
        Assert.All(backend.StepDurations, step => Assert.Equal((float)CharacterPhysicsSession.FixedStepSeconds, step));
        playback.Advance(31, 1);
        Assert.Equal(10, playback.LastStepCount);
        Assert.InRange(playback.DiscardedSeconds, .9166, .9167);
        playback.Advance(31, 0);
        Assert.Equal(0, playback.LastStepCount);
        Assert.Equal(14, backend.StepDurations.Count);
    }

    [Fact]
    public void ResetAtIsAnAbsoluteSeekAndReplayingAfterResetIsRepeatable()
    {
        RigBone[] bones = [new("root", new(0, 10, 0), -1)];
        var animator = Animator(bones, [Vertex(new(0, 10, 0))], [Binding(0)]);
        using var playback = new CharacterPhysicsPlayback(animator,
            Definition(bones, [Body(0, PhysicsBodyMode.Dynamic, new(0, 10, 0))]), new BepuCharacterPhysicsBackend(groundY: null));
        playback.ResetAt(0);
        CharacterSample first = null!;
        for (int i = 1; i <= 10; i++) first = playback.Advance(i, 1d / 30);
        playback.Advance(11, 1d / 240);
        var sought = playback.ResetAt(0);
        Near(new(0, 10, 0), sought.Vertices[0].Position);
        Assert.Equal(0, playback.LastStepCount);
        Assert.Equal(0, playback.DiscardedSeconds);
        Assert.Equal(0, playback.CurrentFrame);
        CharacterSample replay = null!;
        for (int i = 1; i <= 10; i++) replay = playback.Advance(i, 1d / 30);
        Near(first.Vertices[0].Position, replay.Vertices[0].Position);
        playback.ResetAt(0);
        playback.Advance(.125, 1d / 240);
        Assert.Equal(0, playback.LastStepCount);
    }

    [Fact]
    public void PausingByNotCallingOrByZeroElapsedDoesNotStepOrResetTheSolver()
    {
        var backend = new RecordingBackend();
        using var playback = Playback(backend);
        playback.ResetAt(0);
        var moving = playback.Advance(1, 1d / 30);
        int initializations = backend.InitializeCount;
        var paused = playback.Advance(1, 0);
        Assert.Equal(moving.Vertices, paused.Vertices);
        Assert.Equal(4, backend.StepDurations.Count);
        Assert.Equal(initializations, backend.InitializeCount);
        Assert.Equal(0, playback.LastStepCount);
    }

    [Fact]
    public void BackwardFrameRequiresAnExplicitResetRatherThanImplicitReplay()
    {
        var backend = new RecordingBackend();
        using var playback = Playback(backend);
        playback.ResetAt(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Advance(9, 1d / 30));
        Assert.Equal(10, playback.CurrentFrame);
        Assert.Equal(2, backend.InitializeCount);
        Assert.Empty(backend.StepDurations);
        Assert.False(playback.IsFaulted);
        playback.ResetAt(9);
        Assert.Equal(9, playback.CurrentFrame);
    }

    [Fact]
    public void InvalidPublicInputsLeaveTheValidSessionAndFractionalTimeUntouched()
    {
        var backend = new RecordingBackend();
        using var playback = Playback(backend);
        playback.ResetAt(0);
        playback.Advance(.125, 1d / 240);
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.ResetAt(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Advance(double.PositiveInfinity, 1d / 30));
        foreach (double elapsed in new[] { -.01, double.NaN, double.PositiveInfinity, 86401d })
            Assert.Throws<ArgumentOutOfRangeException>(() => playback.Advance(1, elapsed));
        Assert.Equal(2, backend.InitializeCount);
        Assert.False(playback.IsFaulted);
        playback.Advance(.25, 1d / 240);
        Assert.Single(backend.StepDurations);
    }

    [Fact]
    public void MismatchedAnimationBoneCountIsRejectedBeforeResettingBackend()
    {
        var backend = new RecordingBackend();
        var animator = Animator([new("root", Vector3.Zero, -1)]);
        using var playback = new CharacterPhysicsPlayback(animator,
            new([new(-1, Matrix4x4.Identity), new(0, Matrix4x4.Identity)], [], []), backend);
        Assert.Throws<ArgumentException>(() => playback.ResetAt(0));
        Assert.Equal(1, backend.InitializeCount);
        Assert.False(playback.IsFaulted);
        Assert.Null(playback.CurrentFrame);
    }

    [Fact]
    public void DynamicZeroMassStillReflectsAndLastBodyWinsForDuplicateBindings()
    {
        RigBone[] bones = [new("root", Vector3.Zero, -1)];
        var animator = Animator(bones, [Vertex(Vector3.Zero)], [Binding(0)],
            new([new("root", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                new(30, new(30, 0, 0), Quaternion.Identity, BoneCurves.Linear)])]));
        using (var zeroMass = new CharacterPhysicsPlayback(animator, Definition(bones, [Body(0, PhysicsBodyMode.Dynamic, Vector3.Zero) with { Mass = 0 }]), new RecordingBackend()))
        {
            zeroMass.ResetAt(0);
            Near(Vector3.Zero, zeroMass.Advance(30, 1d / 120).Vertices[0].Position);
        }
        using var duplicate = new CharacterPhysicsPlayback(animator, Definition(bones,
            [Body(0, PhysicsBodyMode.Dynamic, Vector3.Zero), Body(0, PhysicsBodyMode.Dynamic, Vector3.Zero)]), new RecordingBackend());
        duplicate.ResetAt(0);
        Near(new(2, 0, 0), duplicate.Advance(1, 1d / 120).Vertices[0].Position);
    }

    [Fact]
    public void UnboundDynamicBodiesDoNotReflectOntoTheRootBone()
    {
        var backend = new RecordingBackend();
        RigBone[] bones = [new("root", new(3, 0, 0), -1)];
        using var playback = new CharacterPhysicsPlayback(Animator(bones, [Vertex(new(3, 0, 0))], [Binding(0)]),
            Definition(bones, [Body(-1, PhysicsBodyMode.Dynamic, Vector3.Zero)]), backend);
        playback.ResetAt(0);
        Near(new(3, 0, 0), playback.Advance(1, 1d / 120).Vertices[0].Position);
    }

    [Fact]
    public void AlignedModeImportsPhysicalRotationAndKeepsAnimatedTranslation()
    {
        RigBone[] bones = [new("root", Vector3.Zero, -1)];
        var animator = Animator(bones, [Vertex(Vector3.UnitX)], [Binding(0)],
            new([new("root", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                new(30, new(3, 0, 0), Quaternion.Identity, BoneCurves.Linear)])]));
        using var playback = new CharacterPhysicsPlayback(animator,
            Definition(bones, [Body(0, PhysicsBodyMode.DynamicAligned, Vector3.Zero)]),
            new RecordingBackend { RotationPerStep = MathF.PI / 2 });
        playback.ResetAt(0);
        var sample = playback.Advance(30, 1d / 120);
        Near(new(3, 0, 0), sample.BoneWorldMatrices[0].Translation);
        Near(new(3, 1, 0), sample.Vertices[0].Position);
    }

    [Fact]
    public void AdvanceBeforeTheFirstExplicitResetIsRejected()
    {
        using var playback = Playback(new RecordingBackend());
        Assert.Throws<InvalidOperationException>(() => playback.Advance(0, 1d / 30));
        Assert.False(playback.IsFaulted);
    }

    [Fact]
    public void AFailedAdvanceRequiresExplicitSuccessfulResetToRecover()
    {
        var backend = new RecordingBackend();
        using var playback = Playback(backend);
        playback.ResetAt(0);
        backend.FailStep = true;
        Assert.Throws<InvalidOperationException>(() => playback.Advance(1, 1d / 30));
        Assert.True(playback.IsFaulted);
        Assert.Equal(0, playback.CurrentFrame);
        backend.FailStep = false;
        Assert.Throws<InvalidOperationException>(() => playback.Advance(1, 1d / 30));
        Near(Vector3.Zero, playback.ResetAt(1).Vertices[0].Position);
        Assert.False(playback.IsFaulted);
        Near(new(1, 0, 0), playback.Advance(2, 1d / 120).Vertices[0].Position);
    }

    [Fact]
    public void AFailedResetDoesNotReturnAnAnimationFallbackAndCanBeRetried()
    {
        var backend = new RecordingBackend();
        using var playback = Playback(backend);
        playback.ResetAt(0);
        backend.FailInitialize = true;
        Assert.Throws<InvalidOperationException>(() => playback.ResetAt(10));
        Assert.True(playback.IsFaulted);
        Assert.Equal(0, playback.CurrentFrame);
        backend.FailInitialize = false;
        playback.ResetAt(10);
        Assert.False(playback.IsFaulted);
        Assert.Equal(10, playback.CurrentFrame);
    }

    [Fact]
    public void InvalidPhysicsOutputFailsInsteadOfReturningACorruptSkeleton()
    {
        var backend = new RecordingBackend();
        using var playback = Playback(backend);
        playback.ResetAt(0);
        backend.CorruptOutput = true;
        Assert.Throws<ArgumentException>(() => playback.Advance(1, 1d / 120));
        Assert.True(playback.IsFaulted);
    }

    [Fact]
    public void DisposalOwnsBackendAndRejectsFurtherWork()
    {
        var backend = new RecordingBackend();
        var playback = Playback(backend);
        playback.ResetAt(0);
        playback.Dispose();
        playback.Dispose();
        Assert.Equal(1, backend.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => playback.ResetAt(0));
        Assert.Throws<ObjectDisposedException>(() => playback.Advance(0, 0));
    }

    [Fact]
    public void FailedInitializationReleasesTheOwnedBackend()
    {
        var backend = new RecordingBackend { FailInitialize = true };
        Assert.Throws<InvalidOperationException>(() => Playback(backend));
        Assert.Equal(1, backend.DisposeCount);
    }

    private static CharacterPhysicsPlayback Playback(RecordingBackend backend)
    {
        RigBone[] bones = [new("root", Vector3.Zero, -1)];
        return new(Animator(bones, [Vertex(Vector3.Zero)], [Binding(0)]),
            Definition(bones, [Body(0, PhysicsBodyMode.Dynamic, Vector3.Zero)]), backend);
    }

    private static CharacterAnimator Animator(RigBone[] bones, SceneVertex[]? vertices = null, SkinBinding[]? bindings = null, MotionClip? clip = null)
        => new(new(bones, vertices ?? [], bindings ?? []), clip ?? new([]));
    private static CharacterPhysicsDefinition Definition(RigBone[] bones, PhysicsBodyDefinition[] bodies)
        => new(bones.Select(bone => new PhysicsBoneDefinition(bone.ParentIndex, Matrix4x4.CreateTranslation(bone.RestPosition))), bodies, []);
    private static PhysicsBodyDefinition Body(int bone, PhysicsBodyMode mode, Vector3 position)
        => new("body", bone, PhysicsShapeKind.Sphere, new(.1f), position, Vector3.Zero, 1, 0, 0, 0, .5f, 0, 0, mode);
    private static SceneVertex Vertex(Vector3 position) => new(position, Vector3.UnitZ, Vector2.Zero);
    private static SkinBinding Binding(int bone) => new(SkinningKind.Bdef1, bone, -1, -1, -1, new(1, 0, 0, 0));
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);

    private sealed class RecordingBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] transforms = [];
        private CharacterPhysicsDefinition definition = null!;
        public int InitializeCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool FailInitialize { get; set; }
        public bool FailStep { get; set; }
        public bool CorruptOutput { get; set; }
        public float RotationPerStep { get; set; }
        public List<float> StepDurations { get; } = [];
        public string Name => "recording";
        public IReadOnlyList<string> Diagnostics => ["recording diagnostic"];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms)
        {
            InitializeCount++;
            if (FailInitialize) throw new InvalidOperationException("Initialize failed.");
            this.definition = definition;
            transforms = bodyWorldTransforms.ToArray();
        }
        public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds) => transforms[bodyIndex] = worldTransform;
        public Matrix4x4 GetBodyWorldTransform(int bodyIndex) => CorruptOutput ? Matrix4x4.CreateTranslation(float.NaN, 0, 0) : transforms[bodyIndex];
        public void Step(float stepSeconds)
        {
            if (FailStep) throw new InvalidOperationException("Step failed.");
            StepDurations.Add(stepSeconds);
            for (int i = 0; i < transforms.Length; i++)
                if (definition.Bodies[i].Mode != PhysicsBodyMode.Kinematic && definition.Bodies[i].Mass > 0)
                {
                    transforms[i] = Matrix4x4.CreateRotationZ(RotationPerStep) * transforms[i];
                    transforms[i].Translation += new Vector3(i + 1, 0, 0);
                }
        }
        public void Dispose() => DisposeCount++;
    }
}
