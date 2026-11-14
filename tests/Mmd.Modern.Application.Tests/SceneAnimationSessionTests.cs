using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneAnimationSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static PmxModel Model(bool physics = false, bool ik = false, bool morph = false, bool reversed = false) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "original-actor", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
        { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = reversed ? [new(0, 2, 1)] : [new(0, 1, 2)], Textures = [],
        Materials = [new() { Name = "toon", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 4,
            Ambient = new(.3f), DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = new(0, 0, 0, 1), EdgeSize = 1,
            TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = ik ? [Bone("root", Vector3.Zero), Bone("tip", Vector3.UnitX, 0), new() { Name = "goal", EnglishName = "",
            Position = Vector3.UnitY, ParentIndex = -1, DeformDepth = 1, Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 1,
            IkIterationCount = 8, IkLimit = MathF.PI, IkLinks = [new(0, false, Vector3.Zero, Vector3.Zero)] }] : [Bone("root", Vector3.Zero)],
        Morphs = morph ? [new() { Name = "smile", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position,
            PositionOffsets = [new(0, new(0, 0, 2))] }] : [], DisplayFrames = [], Joints = [], SoftBodies = [],
        RigidBodies = physics ? [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] : [],
    };
    private static PmxBone Bone(string name, Vector3 position, int parent = -1) => new()
    { Name = name, EnglishName = "", Position = position, ParentIndex = parent, DeformDepth = 0, Flags = PmxBoneFlags.None };
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static VmdMotion Motion(uint duration, Vector3 movement) => new() { BoneKeyframes =
        [new() { BoneName = "root", Frame = 0 }, new() { BoneName = "root", Frame = duration, Translation = movement }] };
    private static (SceneCompositionEditor Scene, EditableSceneObject A, EditableSceneObject B) Scene(PmxModel model, bool room = false)
    {
        var scene = new SceneCompositionEditor(); var rest = Rest(model);
        var a = scene.Add("one", "one.pmx", ModelRole.Character, rest);
        var b = scene.Add("two", "two.pmx", room ? ModelRole.Environment : ModelRole.Character, rest);
        scene.SetTransform(b.Id, new(10, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f), 2);
        return (scene, a, scene.Objects[1]);
    }
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("Unexpected physics allocation.");
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);

    [Fact]
    public void IndependentMotionAndStaticBackgroundPreserveOrderAndApplyWorldExactlyOnce()
    {
        var model = Model(); var (scene, a, b) = Scene(model);
        using var session = new SceneAnimationSession(scene.Objects,
            [new(a.Id, model, Motion(30, new(3, 0, 0))), new(b.Id, model, Motion(60, new(0, 6, 0)))], NoPhysics, cancellationToken: Token);
        session.Seek(60, Token);
        Near(new(3, 0, 0), session.Frame.Surface.Snapshot.Vertices[0].Position);
        Near(Vector3.Transform(new(0, 6, 0), b.World), session.Frame.Surface.Snapshot.Vertices[3].Position);
        Assert.Equal(60u, session.DurationFrames); Assert.True(session.Frame.Surface.Snapshot.HasDynamicVertices);
        Assert.Equal(30, session.Actors[0].LocalFrame); Assert.Equal(60, session.Actors[1].LocalFrame);
        Assert.Equal(a.Id, session.Actors[0].ObjectId); Assert.Equal(b.Id, session.Actors[1].ObjectId);
        Assert.Equal(Matrix4x4.Identity, session.Frame.Surface.Snapshot.World);
        for (int i = 0; i < 6; i++) Near(session.Frame.Surface.Snapshot.Vertices[i].Position, session.Frame.Outline!.Vertices[i].Position);
        session.ClearMotion(b.Id); var background = session.Frame.Surface.Snapshot.Vertices[3].Position;
        session.Seek(10, Token); Assert.Equal(background, session.Frame.Surface.Snapshot.Vertices[3].Position);
        Assert.False(session.Actors[1].HasMotion); Assert.Equal(Vector3.Zero, scene.Objects[0].Source.Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public void ZeroElapsedAndRepeatedPoseReusePublishedFrameAndSeekReplaysExactly()
    {
        var model = Model(); var (scene, a, _) = Scene(model, true);
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, new(3, 0, 0)))], NoPhysics, cancellationToken: Token);
        var before = session.Frame; Assert.Same(before, session.Advance(0, Token));
        var moved = session.Advance(.5, Token); Assert.Equal(15, session.CurrentFrame);
        session.Seek(0, Token); session.Advance(.5, Token);
        Assert.Equal(moved.Surface.Snapshot.Vertices, session.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(before.Surface.Snapshot.Vertices[3], session.Frame.Surface.Snapshot.Vertices[3]);
    }

    [Fact]
    public void IkSwitchChangesActualPoseThroughExistingController()
    {
        var model = Model(ik: true); var (scene, a, _) = Scene(model, true);
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, Vector3.Zero))], NoPhysics, cancellationToken: Token);
        Near(Vector3.UnitY, session.Frame.Surface.Snapshot.Vertices[1].Position);
        session.SetActorOptions(a.Id, false, true, true, Token);
        Near(Vector3.UnitX, session.Frame.Surface.Snapshot.Vertices[1].Position);
        Assert.False(session.Actors[0].IkEnabled);
    }

    [Fact]
    public void PhysicsDefaultsOnUsesSeparateWorldsAndShortActorStopsAtItsEnd()
    {
        var model = Model(physics: true); var (scene, a, b) = Scene(model);
        var backends = new List<FallingBackend>();
        using var session = new SceneAnimationSession(scene.Objects,
            [new(a.Id, model, Motion(10, Vector3.Zero)), new(b.Id, model, Motion(30, Vector3.Zero))],
            _ => { var backend = new FallingBackend(); backends.Add(backend); return backend; }, cancellationToken: Token);
        Assert.Equal(2, backends.Count); Assert.All(session.Actors, state => Assert.True(state.PhysicsEnabled));
        session.Advance(1.0 / 3, Token); int firstSteps = backends[0].Steps;
        var shortPose = session.Frame.Surface.Snapshot.Vertices[0]; session.Advance(1.0 / 3, Token);
        Assert.Equal(firstSteps, backends[0].Steps); Assert.True(backends[1].Steps > firstSteps);
        Assert.Equal(shortPose, session.Frame.Surface.Snapshot.Vertices[0]);
        var beforePause = session.Frame; session.Advance(0, Token); Assert.Same(beforePause, session.Frame);
        session.Seek(0, Token); Assert.All(backends, backend => Assert.Equal(0, backend.Steps));
        session.Advance(1.0 / 3, Token); Assert.Equal(shortPose, session.Frame.Surface.Snapshot.Vertices[0]);
        session.ClearMotion(a.Id); Assert.Equal(1, backends[0].DisposeCount); Assert.Equal(0, backends[1].DisposeCount);
        session.Dispose(); session.Dispose(); Assert.All(backends, backend => Assert.Equal(1, backend.DisposeCount));
    }

    [Fact]
    public void FailedOrCancelledMotionReplacementKeepsCurrentControllersAndPublishedFrame()
    {
        var model = Model(physics: true); var (scene, a, _) = Scene(model, true);
        var backends = new List<FallingBackend>(); bool fail = false;
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, Vector3.Zero))],
            _ => { var backend = new FallingBackend { FailInitialize = fail }; backends.Add(backend); return backend; }, cancellationToken: Token);
        session.Advance(.1, Token); var before = session.Frame; var states = session.Actors;
        fail = true;
        Assert.Throws<SceneActorAnimationException>(() => session.BindMotion(new(a.Id, model, Motion(60, Vector3.One)), Token));
        Assert.Same(before, session.Frame); Assert.Same(states, session.Actors); Assert.Equal(0, backends[0].DisposeCount);
        Assert.Equal(1, backends[1].DisposeCount); Assert.Equal(30u, session.DurationFrames); Assert.False(session.IsFaulted);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.BindMotion(new(a.Id, model, Motion(60, Vector3.One)), cancel.Token));
        Assert.Equal(2, backends.Count); Assert.Same(before, session.Frame);
        fail = false; session.BindMotion(new(a.Id, model, Motion(60, Vector3.One)), Token);
        Assert.Equal(1, backends[0].DisposeCount); Assert.Equal(60u, session.DurationFrames);
    }

    [Fact]
    public void PartialSolverFailureRequiresSeekAndNeverPublishesMixedFrame()
    {
        var model = Model(physics: true); var (scene, a, b) = Scene(model);
        var backends = new List<FallingBackend>();
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, Vector3.Zero)), new(b.Id, model, Motion(30, Vector3.Zero))],
            _ => { var backend = new FallingBackend(); backends.Add(backend); return backend; }, cancellationToken: Token);
        var before = session.Frame; var states = session.Actors; backends[1].FailStep = true;
        var error = Assert.Throws<SceneActorAnimationException>(() => session.Advance(.1, Token));
        Assert.Equal(b.Id, error.ObjectId); Assert.True(session.IsFaulted); Assert.True(backends[0].Steps > 0);
        Assert.Same(before, session.Frame); Assert.Same(states, session.Actors); Assert.Equal(0, session.CurrentFrame);
        Assert.Throws<InvalidOperationException>(() => session.Advance(.1, Token));
        backends[1].FailStep = false; session.Seek(0, Token); Assert.False(session.IsFaulted);
        session.Advance(.1, Token); Assert.Equal(3, session.CurrentFrame);
    }

    [Fact]
    public void RebindingAndOptionChangesPreserveFractionalMorphPose()
    {
        var model = Model(morph: true); var (scene, a, _) = Scene(model, true);
        var motion = new VmdMotion { BoneKeyframes = Motion(30, Vector3.Zero).BoneKeyframes,
            MorphKeyframes = [new() { MorphName = "smile", Frame = 0, Weight = 0 }, new() { MorphName = "smile", Frame = 1, Weight = 1 }] };
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, motion)], NoPhysics, cancellationToken: Token);
        session.Advance(1.0 / 60, Token); var expected = session.Frame.Surface.Snapshot.Vertices[0].Position;
        Near(new(0, 0, 1), expected);
        session.BindMotion(new(a.Id, model, motion), Token); Near(expected, session.Frame.Surface.Snapshot.Vertices[0].Position);
        session.SetActorOptions(a.Id, false, true, true, Token); Near(expected, session.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(.5, session.Actors[0].LocalFrame); Assert.Same(session.Frame, session.Advance(0, Token));
    }

    [Fact]
    public void RetiredBackendCleanupFailureCannotDestroyCommittedReplacementOrSkipOtherDisposals()
    {
        var model = Model(physics: true); var (scene, a, b) = Scene(model);
        var backends = new List<FallingBackend>();
        using var session = new SceneAnimationSession(scene.Objects,
            [new(a.Id, model, Motion(30, Vector3.Zero)), new(b.Id, model, Motion(30, Vector3.Zero))],
            _ => { var backend = new FallingBackend(); backends.Add(backend); return backend; }, cancellationToken: Token);
        backends[0].FailDispose = true;
        session.BindMotion(new(a.Id, model, Motion(60, Vector3.Zero)), Token);
        Assert.Equal(0, backends[2].DisposeCount); session.Advance(.1, Token); Assert.False(session.IsFaulted);
        backends[2].FailDispose = true; session.Dispose();
        Assert.All(backends, backend => Assert.Equal(1, backend.DisposeCount));
        Assert.Equal(2, session.CleanupDiagnostics.Count);
    }

    [Fact]
    public void CrossingShortTailTruncatesPhysicsAndStoppedActorDoesNotAdvance()
    {
        var model = Model(physics: true); var (scene, a, b) = Scene(model); var backends = new List<FallingBackend>();
        using var session = new SceneAnimationSession(scene.Objects,
            [new(a.Id, model, Motion(1, Vector3.Zero)), new(b.Id, model, Motion(30, Vector3.Zero))],
            _ => { var backend = new FallingBackend(); backends.Add(backend); return backend; }, cancellationToken: Token);
        // Long ticks retain the existing ten-substep preview budget; the short actor only receives its remaining 1/30s.
        session.Advance(.1, Token); Assert.Equal(4, backends[0].Steps); Assert.Equal(10, backends[1].Steps);
        var stopped = session.Frame.Surface.Snapshot.Vertices[0]; session.Advance(.1, Token);
        Assert.Equal(4, backends[0].Steps); Assert.Equal(stopped, session.Frame.Surface.Snapshot.Vertices[0]);
    }

    [Fact]
    public void ActivePhysicsWorldCannotBeReusedAndFailedConstructionReleasesAllCandidates()
    {
        var model = Model(physics: true); var (scene, a, b) = Scene(model); var shared = new FallingBackend();
        using (var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, Vector3.Zero))], _ => shared, cancellationToken: Token))
        {
            var before = session.Frame;
            Assert.Throws<SceneActorAnimationException>(() => session.BindMotion(new(b.Id, model, Motion(30, Vector3.Zero)), Token));
            Assert.Same(before, session.Frame); Assert.Equal(0, shared.DisposeCount); session.Advance(.1, Token);
        }
        Assert.Equal(1, shared.DisposeCount); var created = new List<FallingBackend>();
        Assert.Throws<SceneActorAnimationException>(() => new SceneAnimationSession(scene.Objects,
            [new(a.Id, model, Motion(30, Vector3.Zero)), new(b.Id, model, Motion(30, Vector3.Zero))],
            _ => { var backend = new FallingBackend { FailInitialize = created.Count == 1 }; created.Add(backend); return backend; }, cancellationToken: Token));
        Assert.Equal(2, created.Count); Assert.All(created, backend => Assert.Equal(1, backend.DisposeCount));
    }

    [Fact]
    public void CancellationDuringPreparationReleasesCandidateAndPreservesActiveWorld()
    {
        var model = Model(physics: true); var (scene, a, _) = Scene(model, true); var backends = new List<FallingBackend>();
        using var cancel = new CancellationTokenSource(); bool cancelInFactory = false;
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, Vector3.Zero))],
            _ => { var backend = new FallingBackend(); backends.Add(backend); if (cancelInFactory) cancel.Cancel(); return backend; }, cancellationToken: Token);
        var before = session.Frame; cancelInFactory = true;
        Assert.ThrowsAny<OperationCanceledException>(() => session.BindMotion(new(a.Id, model, Motion(60, Vector3.Zero)), cancel.Token));
        Assert.Same(before, session.Frame); Assert.Equal(0, backends[0].DisposeCount); Assert.Equal(1, backends[1].DisposeCount);
        session.Advance(.1, Token); Assert.False(session.IsFaulted);
    }

    [Fact]
    public void LayoutAndPublishedStateAreSnapshotsAndSameSizeDifferentTopologyIsRejected()
    {
        var model = Model(); var (scene, a, _) = Scene(model, true);
        var light = new NativeSceneLight(Guid.NewGuid(), "point", SceneLightKind.Point, new(1, 2, 3), Quaternion.Identity, Vector3.One);
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, new(3, 0, 0)))], NoPhysics,
            new([light]), Token);
        var before = session.Frame; var states = session.Actors;
        scene.SetTransform(a.Id, new(99, 0, 0), Quaternion.Identity, 1);
        Assert.Throws<NotSupportedException>(() => ((IList<SceneActorPlaybackState>)states)[0] = states[1]);
        Assert.Throws<SceneActorAnimationException>(() => session.BindMotion(new(a.Id, Model(reversed: true), Motion(30, Vector3.Zero)), Token));
        Assert.Same(before, session.Frame); session.Advance(.1, Token);
        Near(new(.3f, 0, 0), session.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(light, Assert.Single(session.Frame.Lighting!.Lights)); Assert.Equal(0, states[0].LocalFrame);
        Near(Vector3.Zero, before.Surface.Snapshot.Vertices[0].Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTimeDoesNotFaultOrMutatePublishedState(double value)
    {
        var model = Model(); var (scene, a, _) = Scene(model, true);
        using var session = new SceneAnimationSession(scene.Objects, [new(a.Id, model, Motion(30, Vector3.One))], NoPhysics, cancellationToken: Token);
        var before = session.Frame;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Advance(value, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Seek(value, Token));
        Assert.Same(before, session.Frame); Assert.False(session.IsFaulted);
    }

    internal sealed class FallingBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = []; public int Steps, DisposeCount; public bool FailInitialize, FailStep, FailDispose;
        public string Name => "deterministic fixture"; public IReadOnlyList<string> Diagnostics => Array.Empty<string>();
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input)
        { if (FailInitialize) throw new InvalidOperationException("fixture init failed"); worlds = input.ToArray(); Steps = 0; }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => worlds[index] = world;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void Step(float seconds)
        { if (FailStep) throw new InvalidOperationException("fixture step failed"); Steps++; for (int i = 0; i < worlds.Length; i++) worlds[i].Translation += new Vector3(0, -seconds, 0); }
        public void Dispose() { DisposeCount++; if (FailDispose) throw new InvalidOperationException("fixture cleanup failed"); }
    }
}
