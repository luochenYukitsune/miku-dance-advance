using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SharedSoftBodyTests
{
    [Fact]
    public void ActualSharedBodiesAreConvertedBackToActorSpaceAndNotRebuiltFromBones()
    {
        var model = Model(true); var (layout, motion) = Fixture(model, 60);
        var placement = Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(8, 3, -2);
        layout.SetTransform(motion.ObjectId, new(8, 3, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f), 1);
        var rigid = new TrackingSharedBackend(); var soft = new RecordingSoft();
        using var session = Create(layout, [motion], () => rigid, _ => soft);
        session.Advance(1d / 30, TestContext.Current.CancellationToken);
        Assert.Equal(4, soft.Steps);
        Matrix4x4.Invert(placement, out var inverse);
        var expected = rigid.Bodies.Single(b => !b.Removed).Transform * inverse;
        Assert.InRange(Vector3.Distance(expected.Translation, Assert.Single(soft.Rigids).Translation), 0, 1e-5f);
        Assert.Equal(4, rigid.Steps);
    }

    [Fact]
    public void ReflectionHoldsSoftStateAndSeekResetsBothHistories()
    {
        var (layout, motion) = Fixture(Model(true), 60);
        var soft = new RecordingSoft();
        using var session = Create(layout, [motion], () => new TrackingSharedBackend(), _ => soft);
        int resets = soft.Resets;
        session.Advance(1d / 240, TestContext.Current.CancellationToken); Assert.Equal(0, soft.Steps); Assert.Equal(resets, soft.Resets);
        session.Advance(1d / 240, TestContext.Current.CancellationToken); Assert.Equal(1, soft.Steps); Assert.Equal(resets, soft.Resets);
        session.Seek(0, TestContext.Current.CancellationToken); Assert.Equal(resets + 1, soft.Resets);
        Assert.Equal(0, session.SharedPhysicsStepCount);
    }

    [Fact]
    public void PureClothStepsAtSharedBoundariesAndShortActorStopsWhileOtherContinues()
    {
        var (layout, shortMotion) = Fixture(Model(false), 1);
        var longModel = Model(false);
        var longActor = layout.Add("long", "long.pmx", ModelRole.Character, Rest(longModel));
        var longMotion = Motion(longActor.Id, longModel, 4);
        var shortSoft = new RecordingSoft(); var longSoft = new RecordingSoft();
        using var session = Create(layout, [shortMotion, longMotion], () => new TrackingSharedBackend(),
            item => item.Id == shortMotion.ObjectId ? shortSoft : longSoft);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); Assert.Equal(4, shortSoft.Steps);
        session.Advance(2d / 30, TestContext.Current.CancellationToken);
        Assert.Equal(4, shortSoft.Steps); Assert.Equal(12, longSoft.Steps);
        Assert.Equal(3, session.CurrentFrame);
    }

    [Fact]
    public void FailedSoftReplacementDisposesCandidateAndPreservesPublishedRunningScene()
    {
        var (layout, motion) = Fixture(Model(true), 60); var old = new RecordingSoft();
        var failed = new RecordingSoft { FailReset = true }; int created = 0;
        using var session = Create(layout, [motion], () => new TrackingSharedBackend(), _ => created++ == 0 ? old : failed);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); var before = session.Frame; int steps = old.Steps;
        Assert.Throws<SceneActorAnimationException>(() => session.BindMotion(motion with { IkEnabled = false }, TestContext.Current.CancellationToken));
        Assert.Same(before, session.Frame); Assert.False(session.IsFaulted);
        Assert.Equal(0, old.Disposals); Assert.Equal(1, failed.Disposals);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); Assert.True(old.Steps > steps);
    }

    [Fact]
    public void SoftStepFailureRequiresSeekAndCannotAdvanceRigidAgainUntilRecovery()
    {
        var (layout, motion) = Fixture(Model(true), 60); var soft = new RecordingSoft(); var rigid = new TrackingSharedBackend();
        using var session = Create(layout, [motion], () => rigid, _ => soft);
        soft.FailStep = true;
        Assert.ThrowsAny<Exception>(() => session.Advance(1d / 30, TestContext.Current.CancellationToken));
        Assert.True(session.IsFaulted); int steps = rigid.Steps;
        Assert.Throws<InvalidOperationException>(() => session.Advance(1d / 30, TestContext.Current.CancellationToken)); Assert.Equal(steps, rigid.Steps);
        soft.FailStep = false; session.Seek(0, TestContext.Current.CancellationToken); Assert.False(session.IsFaulted);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); Assert.True(soft.Steps > 0);
    }

    [Fact]
    public void ActualBulletSoftOnlyClothFallsInSharedSessionAndSeekRestoresRestPose()
    {
        var (layout, motion) = Fixture(Model(false), 60);
        using var session = Create(layout, [motion], () => new TrackingSharedBackend(), _ => new SoftBodyBulletBackend(new(0, -9.8f, 0), null));
        for (int i = 0; i < 15; i++) session.Advance(1d / 30, TestContext.Current.CancellationToken);
        Assert.True(session.Frame.Surface.Snapshot.Vertices[4].Position.Y < 2.99f);
        session.Seek(0, TestContext.Current.CancellationToken); Assert.Equal(3, session.Frame.Surface.Snapshot.Vertices[4].Position.Y);
    }

    [Fact]
    public void SuccessfulRebindBuildsFreshCandidateWorldAndDisposesOldSoftExactlyOnce()
    {
        var (layout, motion) = Fixture(Model(true), 60); var solvers = new List<RecordingSoft>();
        using var session = Create(layout, [motion], () => new TrackingSharedBackend(), _ => { var s = new RecordingSoft(); solvers.Add(s); return s; });
        session.Advance(1d / 30, TestContext.Current.CancellationToken); session.BindMotion(motion with { IkEnabled = false }, TestContext.Current.CancellationToken);
        Assert.Equal(2, solvers.Count); Assert.Equal(1, solvers[0].Disposals); Assert.Equal(0, solvers[1].Disposals);
        Assert.Equal(1, session.CurrentFrame); Assert.Equal(0, session.SharedPhysicsStepCount);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); Assert.Equal(4, solvers[1].Steps);
    }

    [Fact]
    public void ReusedSharedBackendRejectsBeforeResettingExistingWorld()
    {
        var (layout, motion) = Fixture(Model(true), 60); var rigid = new TrackingSharedBackend();
        var soft = new List<RecordingSoft>();
        using var session = Create(layout, [motion], () => rigid, _ => { var s = new RecordingSoft(); soft.Add(s); return s; });
        session.Advance(1d / 30, TestContext.Current.CancellationToken);
        var before = session.Frame; int initializations = rigid.Initializations;
        Assert.Throws<InvalidOperationException>(() => session.BindMotion(motion with { IkEnabled = false }, TestContext.Current.CancellationToken));
        Assert.Equal(initializations, rigid.Initializations); Assert.Same(before, session.Frame);
        Assert.Equal(0, soft[0].Disposals); Assert.Equal(1, soft[1].Disposals);
        session.Advance(1d / 30, TestContext.Current.CancellationToken); Assert.False(session.IsFaulted);
    }

    [Theory]
    [InlineData(1.5707964f, 0f)]
    [InlineData(0f, 1.5707964f)]
    public void ProjectSharedGravityIsConvertedFromWorldIntoRotatedActorCoordinates(float x, float z)
    {
        var (layout, motion) = Fixture(Model(true), 60);
        layout.SetTransform(motion.ObjectId, new(3, 5, -2), Quaternion.CreateFromYawPitchRoll(0, x, z), 1);
        var soft = new RecordingSoft();
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false, GravityX = 2, GravityY = -9, GravityZ = 3 } };
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException(),
            s => new TrackingSharedBackend { Gravity = s.Gravity }, (_, _) => soft);
        using var project = new SceneProjectSession(layout, new SceneLightingEditor(), [motion], provider, settings,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(soft.LastGravity);
        var actual = soft.LastGravity.GetValueOrDefault();
        Matrix4x4.Invert(layout.Objects[0].World, out var inverse);
        var expected = Vector3.TransformNormal(settings.Physics.Gravity, inverse);
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(settings.Physics.Gravity, Vector3.TransformNormal(actual, layout.Objects[0].World)), 0, 1e-5f);
    }

    [Fact]
    public void CustomSharedSoftFactoryGravityIsPreservedWithoutExplicitWorldGravityContract()
    {
        var (layout, motion) = Fixture(Model(true), 60); var soft = new RecordingSoft();
        using var session = Create(layout, [motion], () => new TrackingSharedBackend(), _ => soft);
        session.Advance(1d / 30, TestContext.Current.CancellationToken);
        Assert.Null(soft.LastGravity);
    }

    [Fact]
    public void DeclaredSharedGravityRejectsNonfiniteBeforeAllocatingAnyBackend()
    {
        var (layout, motion) = Fixture(Model(true), 60); int allocations = 0;
        Assert.Throws<ArgumentException>(() => new SceneAnimationSession(layout.Objects, [motion],
            _ => { allocations++; throw new InvalidOperationException(); },
            sharedPhysicsFactory: () => { allocations++; return new TrackingSharedBackend(); },
            softPhysicsFactory: _ => { allocations++; return new RecordingSoft(); },
            sharedWorldGravity: new Vector3(float.NaN, 0, 0), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, allocations);
    }

    [Fact]
    public void DeclaredSharedGravityRejectsScaledSoftPlacementBeforeAllocatingAnyBackend()
    {
        var (layout, motion) = Fixture(Model(false), 60); int allocations = 0;
        layout.SetTransform(motion.ObjectId, Vector3.Zero, Quaternion.Identity, 2);
        Assert.Throws<SceneActorAnimationException>(() => new SceneAnimationSession(layout.Objects, [motion],
            _ => { allocations++; throw new InvalidOperationException(); },
            sharedPhysicsFactory: () => { allocations++; return new TrackingSharedBackend(); },
            softPhysicsFactory: _ => { allocations++; return new RecordingSoft(); },
            sharedWorldGravity: new Vector3(0, -9.8f, 0), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, allocations);
    }

    [Theory]
    [InlineData(1f, false)]
    [InlineData(.25f, true)]
    public void FiniteWorldGravityRotationRejectsOnlyUnrepresentableLocalAcceleration(float fraction, bool accepted)
    {
        var (layout, motion) = Fixture(Model(false), 60);
        layout.SetTransform(motion.ObjectId, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4), 1);
        var gravity = new Vector3(float.MaxValue * fraction, float.MaxValue * fraction, 0);
        var soft = new RecordingSoft(); int allocations = 0;
        SceneAnimationSession CreateWithGravity() => new(layout.Objects, [motion], _ => throw new InvalidOperationException(),
            sharedPhysicsFactory: () => { allocations++; return new TrackingSharedBackend(); },
            softPhysicsFactory: _ => { allocations++; return soft; }, sharedWorldGravity: gravity,
            cancellationToken: TestContext.Current.CancellationToken);
        if (!accepted)
        {
            Assert.Throws<SceneActorAnimationException>(() => CreateWithGravity());
            Assert.Equal(0, allocations); Assert.Equal(0, soft.Disposals);
        }
        else
        {
            using var session = CreateWithGravity();
            Assert.Equal(2, allocations); Assert.NotNull(soft.LastGravity);
            var local = soft.LastGravity.GetValueOrDefault();
            Assert.True(float.IsFinite(local.X) && float.IsFinite(local.Y) && float.IsFinite(local.Z));
            // Normalize before comparing to avoid overflowing the test's own distance calculation.
            var expected = Vector3.TransformNormal(new Vector3(fraction, fraction, 0),
                Matrix4x4.CreateRotationZ(-MathF.PI / 4));
            Assert.InRange(Vector3.Distance(expected, local / float.MaxValue), 0, 1e-6f);
        }
    }

    private static SceneAnimationSession Create(SceneCompositionEditor layout, SceneActorMotion[] motions,
        Func<ICharacterPhysicsMutableBackend> rigid, Func<EditableSceneObject, ISoftBodyBackend> soft)
        => new(layout.Objects, motions, _ => throw new InvalidOperationException("Independent rigid factory must not run."),
            sharedPhysicsFactory: rigid, softPhysicsFactory: soft);
    private static (SceneCompositionEditor, SceneActorMotion) Fixture(PmxModel model, uint duration)
    { var layout = new SceneCompositionEditor(); var actor = layout.Add("cloth", "cloth.pmx", ModelRole.Character, Rest(model)); return (layout, Motion(actor.Id, model, duration)); }
    private static SceneActorMotion Motion(Guid id, PmxModel model, uint duration) => new(id, model, new()
    { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = duration }] });
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
    private static PmxModel Model(bool rigid)
    {
        var m = PmxSoftBodyTests.MaterialFixture();
        return new() { Header=m.Header, Name=m.Name, EnglishName=m.EnglishName, Comment=m.Comment, EnglishComment=m.EnglishComment,
            Vertices=m.Vertices,Faces=m.Faces,Textures=m.Textures,Materials=m.Materials,Bones=m.Bones,Morphs=m.Morphs,
            DisplayFrames=m.DisplayFrames,Joints=m.Joints,SoftBodies=m.SoftBodies,
            RigidBodies=rigid ? [new() { Name="offset body",EnglishName="",BoneIndex=0,Group=0,CollisionGroup=ushort.MaxValue,
                Shape=PmxRigidBodyShape.Sphere,ShapeSize=new(.1f),Translation=new(0,2,0),Rotation=Vector3.Zero,Mass=1,
                TranslationDamping=0,RotationDamping=0,Repulsion=0,Friction=.5f,Operation=PmxRigidBodyOperation.Dynamic }] : [] };
    }
    private sealed class RecordingSoft : ISoftBodyBackend
    {
        private SoftBodyDefinition[] definitions=[]; private Vector3[] targets=[];
        public Matrix4x4[] Rigids=[]; public int Steps, Resets, Disposals; public bool FailReset, FailStep;
        public Vector3? LastGravity;
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders) => this.definitions=definitions.ToArray();
        public void Reset(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Matrix4x4> rigids)
        { if(FailReset)throw new InvalidOperationException("injected reset"); Resets++; targets=positions.ToArray();Rigids=rigids.ToArray(); }
        public void Step(float seconds, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Matrix4x4> rigids)
        { if(FailStep)throw new InvalidOperationException("injected step"); Steps++;targets=positions.ToArray();Rigids=rigids.ToArray(); }
        public IReadOnlyList<SoftBodySample> Sample() => definitions.Select(d=>new SoftBodySample(d.VertexIndices.ToArray(),d.VertexIndices.Select(i=>targets[i]).ToArray(),new Vector3[d.VertexIndices.Count])).ToArray();
        public void SetGravity(Vector3 gravity) => LastGravity=gravity;
        public void Dispose()=>Disposals++;
    }
}

