using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;
namespace Mmd.Modern.Application.Tests;
public sealed class ControllerSoftBodyTests
{
    [Fact]
    public void ControllerAdvancesActualClothAndUpdatesMatchingOutlineThenSeekResets()
    {
        var model = PmxSoftBodyTests.MaterialFixture();
        var surface = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var outline = PmxOriginalOutlineAdapter.FromRestPose(model);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }] };
        using var controller = new CharacterAnimationController(model, motion, surface, outline);
        controller.EnableSoftBodies(new SoftBodyBulletBackend(new(0, -9.8f, 0), null), 0);
        var initial = controller.SampleAt(0);
        for (int i = 1; i <= 30; i++) controller.Advance(i, 1d / 30);
        var moved = controller.Advance(31, 1d / 30);
        Assert.True(moved.Surface.Snapshot.Vertices[4].Position.Y < initial.Surface.Snapshot.Vertices[4].Position.Y - .01f);
        Assert.Equal(initial.Surface.Snapshot.Vertices[6].Position, moved.Surface.Snapshot.Vertices[6].Position);
        Assert.Equal(moved.Surface.Snapshot.Vertices[4].Position, moved.Outline.Vertices[4].Position);
        var reset = controller.ResetPhysics(0);
        Assert.Equal(initial.Surface.Snapshot.Vertices, reset.Surface.Snapshot.Vertices);
        controller.DisableSoftBodies(); Assert.False(controller.SoftBodiesEnabled);
    }
    [Fact]
    public void SessionSoftOnlyActorUsesFactoryAndRejectsMissingFactory()
    {
        var model = PmxSoftBodyTests.MaterialFixture();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        var layout = new SceneCompositionEditor(); var actor = layout.Add("cloth", "cloth.pmx", ModelRole.Character, rest);
        var motion = new SceneActorMotion(actor.Id, model, new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }] });
        ICharacterPhysicsBackend NoRigid(EditableSceneObject _) => throw new InvalidOperationException("No rigid world expected.");
        Assert.Throws<SceneActorAnimationException>(() => new SceneAnimationSession(layout.Objects, [motion], NoRigid));
        int created = 0;
        using var session = new SceneAnimationSession(layout.Objects, [motion], NoRigid, softPhysicsFactory: _ =>
        { created++; return new SoftBodyBulletBackend(new(0, -9.8f, 0), null); });
        Assert.True(session.Actors[0].SupportsPhysics); Assert.True(session.Actors[0].PhysicsEnabled);
        Assert.NotNull(session.Actors[0].PhysicsBackend);
        for (int i = 0; i < 30; i++) session.Advance(1d / 30, TestContext.Current.CancellationToken);
        Assert.Equal(1, created); Assert.True(session.Frame.Surface.Snapshot.Vertices[4].Position.Y < 2.99f);
        session.Seek(0, TestContext.Current.CancellationToken); Assert.Equal(3, session.Frame.Surface.Snapshot.Vertices[4].Position.Y);
    }

    [Fact]
    public void EnablingSoftBodiesPreservesCurrentRigidPoseAndUsesMatchingColliderWorlds()
    {
        var model = RigidCloth();
        using var controller = Controller(model);
        var rigid = new SceneAnimationSessionTests.FallingBackend();
        controller.EnablePhysics(rigid, 0);
        var before = controller.Advance(7.5, .25);
        var worlds = controller.BoneWorldMatrices.ToArray();
        var soft = new RecordingSoftBackend();
        controller.EnableSoftBodies(soft, 7.5);
        var after = controller.SampleAt(7.5);
        Assert.Equal(before.Surface.Snapshot.Vertices, after.Surface.Snapshot.Vertices);
        Assert.Equal(worlds, controller.BoneWorldMatrices);
        Assert.Equal(before.Surface.Snapshot.Vertices.ToArray().Select(v => v.Position), soft.Targets);
        Assert.Equal(rigid.GetBodyWorldTransform(0), Assert.Single(soft.Rigids));
        Assert.Contains("recording soft diagnostic", controller.Diagnostics);
    }

    [Fact]
    public void SoftEnableAtDifferentRigidFrameRejectsWithoutChangingEitherSolver()
    {
        using var controller = Controller(RigidCloth());
        var rigid = new SceneAnimationSessionTests.FallingBackend();
        controller.EnablePhysics(rigid, 0);
        var before = controller.Advance(3, .1); int steps = rigid.Steps;
        var soft = new RecordingSoftBackend();
        Assert.Throws<InvalidOperationException>(() => controller.EnableSoftBodies(soft, 0));
        Assert.True(soft.Disposed); Assert.Equal(steps, rigid.Steps);
        Assert.Same(before.Surface, controller.SampleAt(3).Surface);
        Assert.False(controller.SoftBodiesEnabled);
    }

    [Fact]
    public void EnablingRigidWithActiveSoftRejectsWithoutResettingTheSoftHistory()
    {
        using var controller = Controller(RigidCloth());
        var soft = new RecordingSoftBackend(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(3, .1); int resets = soft.ResetCount;
        var rigid = new SceneAnimationSessionTests.FallingBackend();
        Assert.Throws<NotSupportedException>(() => controller.EnablePhysicsAt(rigid, 3));
        Assert.Equal(1, rigid.DisposeCount); Assert.Equal(resets, soft.ResetCount);
        Assert.Same(before.Surface, controller.SampleAt(3).Surface);
        Assert.True(controller.SoftBodiesEnabled); Assert.True(controller.PhysicsEnabled);
    }

    [Fact]
    public void RigidLongDeltaKeepsItsBoundedCatchUpContract()
    {
        using var controller = Controller(RigidCloth(), 600);
        var rigid = new SceneAnimationSessionTests.FallingBackend(); controller.EnablePhysics(rigid, 0);
        controller.Advance(330, 11);
        Assert.Equal(CharacterPhysicsSession.MaximumSubsteps, rigid.Steps);
        Assert.True(controller.PhysicsDiscardedSeconds > 10);
    }

    [Fact]
    public void SessionRejectsUnsupportedSoftDeltaBeforeMutatingOrFaulting()
    {
        var model = PmxSoftBodyTests.MaterialFixture();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        var layout = new SceneCompositionEditor(); var actor = layout.Add("cloth", "cloth.pmx", ModelRole.Character, rest);
        var motion = new SceneActorMotion(actor.Id, model, new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 600 }] });
        var soft = new RecordingSoftBackend();
        using var session = new SceneAnimationSession(layout.Objects, [motion], _ => throw new InvalidOperationException(), softPhysicsFactory: _ => soft);
        var before = session.Frame; int resets = soft.ResetCount;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Advance(11, TestContext.Current.CancellationToken));
        Assert.Same(before, session.Frame); Assert.False(session.IsFaulted); Assert.Equal(0, session.CurrentFrame);
        Assert.Equal(resets, soft.ResetCount); Assert.Equal(0, soft.StepCount);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); Assert.True(soft.StepCount > 0);
    }

    [Fact]
    public void ReusedSoftBackendIsRejectedBeforeTheExistingActorCanBeResetOrDisposed()
    {
        var model = PmxSoftBodyTests.MaterialFixture();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        var layout = new SceneCompositionEditor(); var actor = layout.Add("cloth", "cloth.pmx", ModelRole.Character, rest);
        var motion = new SceneActorMotion(actor.Id, model, new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }] });
        var soft = new RecordingSoftBackend();
        using var session = new SceneAnimationSession(layout.Objects, [motion], _ => throw new InvalidOperationException(), softPhysicsFactory: _ => soft);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); var before = session.Frame; int resets = soft.ResetCount;
        Assert.Throws<SceneActorAnimationException>(() => session.BindMotion(motion with { IkEnabled = false }, TestContext.Current.CancellationToken));
        Assert.Same(before, session.Frame); Assert.False(soft.Disposed); Assert.Equal(resets, soft.ResetCount);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); Assert.False(session.IsFaulted);
    }

    [Fact]
    public void MovingFrozenSoftAttachmentPreservesFinalDeformationWithoutResettingHistory()
    {
        using var controller = Controller(PmxSoftBodyTests.MaterialFixture());
        controller.SetOutsideParents(new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.Identity });
        var soft = new RecordingSoftBackend(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(60, .1); int resets = soft.ResetCount;
        var after = controller.RefreshFrozenOutsideParents(
            new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.CreateTranslation(1, 0, 0) });
        Assert.Equal(resets, soft.ResetCount);
        Assert.Equal(before.Surface.Snapshot.Vertices[4].Position + Vector3.UnitX, after.Surface.Snapshot.Vertices[4].Position);
        Assert.Same(after.Surface, controller.SampleAt(60).Surface);
    }

    private static CharacterAnimationController Controller(PmxModel model, uint duration = 60) => new(model,
        new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = duration }] },
        PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));

    private static PmxModel RigidCloth()
    {
        var m = PmxSoftBodyTests.MaterialFixture();
        return new() { Header = m.Header, Name = m.Name, EnglishName = m.EnglishName, Comment = m.Comment, EnglishComment = m.EnglishComment,
            Vertices = m.Vertices, Faces = m.Faces, Textures = m.Textures, Materials = m.Materials, Bones = m.Bones,
            Morphs = m.Morphs, DisplayFrames = m.DisplayFrames, Joints = m.Joints, SoftBodies = m.SoftBodies,
            RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.1f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] };
    }

    private sealed class RecordingSoftBackend : ISoftBodyBackend
    {
        private int[] ids = [];
        public Vector3[] Targets = []; public Matrix4x4[] Rigids = []; public int ResetCount, StepCount; public bool Disposed;
        public IReadOnlyList<string> Diagnostics => ["recording soft diagnostic"];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders)
            => ids = definitions.SelectMany(d => d.VertexIndices).ToArray();
        public void Reset(ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids)
        { Targets = targets.ToArray(); Rigids = rigids.ToArray(); ResetCount++; }
        public void Step(float seconds, ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids)
        { StepCount++; foreach (int id in ids) Targets[id] -= new Vector3(0, seconds, 0); }
        public IReadOnlyList<SoftBodySample> Sample() => [new(ids, ids.Select(id => Targets[id]).ToArray(), ids.Select(_ => Vector3.UnitZ).ToArray())];
        public void SetGravity(Vector3 gravity) { }
        public void Dispose() => Disposed = true;
    }
}
