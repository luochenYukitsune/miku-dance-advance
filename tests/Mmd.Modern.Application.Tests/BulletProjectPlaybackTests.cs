using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>Exercises real native worlds through the same injected factories used by project hosts.</summary>
public sealed class BulletProjectPlaybackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SceneWorldsFreezeIndependentlyAndSeekReplaysTheSameBulletTrajectory()
    {
        var model = Model(); var (layout, a, b) = Layout(model);
        var worlds = new List<BulletCharacterPhysicsBackend>();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(a.Id, model, Motion(2)), new(b.Id, model, Motion(30))],
            _ => { var world = new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null); worlds.Add(world); return world; },
            cancellationToken: Token);
        Assert.Equal(2, worlds.Count);
        Assert.All(session.Actors, actor => Assert.Contains("Bullet 2.75", actor.PhysicsBackend));
        session.Advance(1.0 / 30, Token); session.Advance(1.0 / 30, Token);
        var stopped = session.Frame.Surface.Snapshot.Vertices[0];
        Assert.True(stopped.Position.Y < 0);
        for (int i = 0; i < 6; i++) session.Advance(1.0 / 30, Token);
        var target = session.Frame.Surface.Snapshot.Vertices.ToArray();
        Assert.Equal(stopped, target[0]);
        Assert.True(target[3].Position.Y < stopped.Position.Y - .1f);
        Assert.Equal(2, session.Actors[0].LocalFrame);
        Assert.Equal(8, session.Actors[1].LocalFrame);
        Assert.Equal(20, target[3].Position.X);
        session.Seek(0, Token);
        Assert.Equal(Vector3.Zero, session.Frame.Surface.Snapshot.Vertices[0].Position);
        for (int i = 0; i < 8; i++) session.Advance(1.0 / 30, Token);
        Assert.Equal(target, session.Frame.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(Vector3.Zero, layout.Objects[0].Source.Surface.Snapshot.Vertices[0].Position);
        session.Dispose();
        Assert.All(worlds, world => Assert.Throws<ObjectDisposedException>(() => world.GetBodyWorldTransform(0)));
    }

    [Fact]
    public void CanceledRealBulletReplacementPreservesThePublishedFrameAndActiveWorld()
    {
        var model = Model(); var (layout, a, _) = Layout(model);
        var worlds = new List<BulletCharacterPhysicsBackend>();
        using var cancel = new CancellationTokenSource(); bool cancelCandidate = false;
        using var session = new SceneAnimationSession(layout.Objects, [new(a.Id, model, Motion(30))],
            _ => { var world = new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null); worlds.Add(world);
                if (cancelCandidate) cancel.Cancel(); return world; }, cancellationToken: Token);
        session.Advance(1.0 / 30, Token);
        var before = session.Frame; var actorStates = session.Actors;
        var oldPose = worlds[0].GetBodyWorldTransform(0); cancelCandidate = true;
        Assert.ThrowsAny<OperationCanceledException>(() => session.BindMotion(new(a.Id, model, Motion(60)), cancel.Token));
        Assert.Same(before, session.Frame); Assert.Same(actorStates, session.Actors); Assert.False(session.IsFaulted);
        Assert.Equal(oldPose, worlds[0].GetBodyWorldTransform(0));
        Assert.Throws<ObjectDisposedException>(() => worlds[1].GetBodyWorldTransform(0));
        session.Advance(1.0 / 30, Token);
        Assert.True(session.Frame.Surface.Snapshot.Vertices[0].Position.Y < before.Surface.Snapshot.Vertices[0].Position.Y);
    }

    [Fact]
    public void PmmShortActorKeepsItsBulletEndPoseWhileTheOtherActorContinues()
    {
        using var playback = Pmm(2, 30);
        playback.Seek(0);
        playback.Advance(1, 1.0 / 30); playback.Advance(2, 1.0 / 30);
        var stopped = playback.Frame.Surface.Snapshot.Vertices[0];
        Assert.True(stopped.Position.Y < 0);
        for (int frame = 3; frame <= 8; frame++) playback.Advance(frame, 1.0 / 30);
        Assert.Equal(stopped, playback.Frame.Surface.Snapshot.Vertices[0]);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[3].Position.Y < stopped.Position.Y - .1f);
        var target = playback.Frame.Surface.Snapshot.Vertices.ToArray();
        playback.Seek(0);
        Assert.Equal(Vector3.Zero, playback.Frame.Surface.Snapshot.Vertices[0].Position);
        for (int frame = 1; frame <= 8; frame++) playback.Advance(frame, 1.0 / 30);
        Assert.Equal(target, playback.Frame.Surface.Snapshot.Vertices.ToArray());
    }

    [Fact]
    public void PmmTickCrossingShortTailOnlySimulatesItsRemainingTime()
    {
        using var crossing = Pmm(1, 30); using var reference = Pmm(1, 30);
        crossing.Seek(0); reference.Seek(0);
        crossing.Advance(3, .1); reference.Advance(1, 1.0 / 30);
        Assert.Equal(reference.Frame.Surface.Snapshot.Vertices[0], crossing.Frame.Surface.Snapshot.Vertices[0]);
        Assert.True(crossing.Frame.Surface.Snapshot.Vertices[3].Position.Y < reference.Frame.Surface.Snapshot.Vertices[3].Position.Y);
    }

    [Fact]
    public void PmmFinishedActorRefreshesMorphOptionsWithoutAdvancingOrResettingBullet()
    {
        var worlds = new List<BulletCharacterPhysicsBackend>();
        using var playback = Pmm(1, 30, factory: (gravity, floor) =>
        { var world = new BulletCharacterPhysicsBackend(gravity, floor); worlds.Add(world); return world; }, morph: true);
        playback.Seek(0); playback.Advance(1, 1.0 / 30);
        Assert.Equal(2, playback.Frame.Surface.Snapshot.Vertices[0].Position.Z);
        var stopped = worlds[2].GetBodyWorldTransform(0);
        playback.Advance(2, 1.0 / 30, morphs: false);
        Assert.Equal(0, playback.Frame.Surface.Snapshot.Vertices[0].Position.Z);
        Assert.Equal(stopped, worlds[2].GetBodyWorldTransform(0));
        playback.Advance(3, 1.0 / 30, morphs: true);
        Assert.Equal(2, playback.Frame.Surface.Snapshot.Vertices[0].Position.Z);
        Assert.Equal(stopped, worlds[2].GetBodyWorldTransform(0));
        playback.Advance(3, 0, morphs: false);
        Assert.Equal(0, playback.Frame.Surface.Snapshot.Vertices[0].Position.Z);
        Assert.Equal(stopped, worlds[2].GetBodyWorldTransform(0));
        Assert.Equal(4, worlds.Count);
    }

    [Fact]
    public void PmmFinishedActorRefreshesOutsideParentIndependentOfTickSizeWithoutSteppingBullet()
    {
        var worlds = new List<BulletCharacterPhysicsBackend>();
        using var stepped = Pmm(1, 30, factory: (gravity, floor) =>
        { var world = new BulletCharacterPhysicsBackend(gravity, floor); worlds.Add(world); return world; }, outsideParent: true);
        using var crossing = Pmm(1, 30, outsideParent: true);
        stepped.Seek(0); crossing.Seek(0);
        stepped.Advance(1, 1.0 / 30);
        // Dependency ordering initializes the parent first, then this short child.
        var stopped = worlds[3].GetBodyWorldTransform(0);
        stepped.Advance(2, 1.0 / 30); stepped.Advance(3, 1.0 / 30);
        crossing.Advance(3, .1);
        Assert.InRange(MathF.Abs(stepped.Frame.Surface.Snapshot.Vertices[0].Position.X - 3), 0, 1e-4f);
        Assert.Equal(crossing.Frame.Surface.Snapshot.Vertices[0], stepped.Frame.Surface.Snapshot.Vertices[0]);
        Assert.Equal(stopped, worlds[3].GetBodyWorldTransform(0));
        Assert.Equal(4, worlds.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PmmFactoryReceivesProjectGravityAndGroundAndToggleRestartsAtTheRequestedPose(bool ground)
    {
        var worlds = new List<BulletCharacterPhysicsBackend>();
        using var playback = Pmm(30, 30, ground, (gravity, floor) =>
        {
            Assert.Equal(new(0, -98f, 0), gravity); Assert.Equal(ground ? 0f : (float?)null, floor);
            var world = new BulletCharacterPhysicsBackend(gravity, floor); worlds.Add(world); return world;
        });
        Assert.Equal(2, worlds.Count); Assert.True(playback.PhysicsEnabled);
        playback.Seek(0);
        Assert.Equal(4, worlds.Count); // Saved editor pose and registered timeline own separate controllers.
        Assert.All(worlds.Take(2), world => Assert.Throws<ObjectDisposedException>(() => world.GetBodyWorldTransform(0)));
        playback.Advance(1, 1.0 / 30);
        Assert.True(playback.PhysicsLastStepCount > 0);
        playback.SetPhysics(false);
        Assert.False(playback.PhysicsEnabled);
        Assert.Equal(Vector3.Zero, playback.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.All(worlds, world => Assert.Throws<ObjectDisposedException>(() => world.GetBodyWorldTransform(0)));
        playback.SetPhysics(true);
        Assert.Equal(6, worlds.Count); Assert.True(playback.PhysicsEnabled);
        Assert.Equal(Vector3.Zero, playback.Frame.Surface.Snapshot.Vertices[0].Position);
        playback.Dispose();
        Assert.All(worlds, world => Assert.Throws<ObjectDisposedException>(() => world.GetBodyWorldTransform(0)));
    }

    private static VmdMotion Motion(uint end) => new() { BoneKeyframes =
        [new() { BoneName = "root", Frame = 0 }, new() { BoneName = "root", Frame = end }] };

    private static (SceneCompositionEditor Layout, EditableSceneObject A, EditableSceneObject B) Layout(PmxModel model)
    {
        var layout = new SceneCompositionEditor(); var rest = Rest(model);
        var a = layout.Add("short", "short.pmx", ModelRole.Character, rest);
        var b = layout.Add("long", "long.pmx", ModelRole.Character, rest);
        layout.SetTransform(b.Id, new(20, 0, 0), Quaternion.Identity, 1);
        return (layout, a, layout.Objects[1]);
    }

    private static PmmProjectPlayback Pmm(int shortEnd, int longEnd, bool ground = false,
        Func<Vector3, float?, BulletCharacterPhysicsBackend>? factory = null, bool morph = false, bool outsideParent = false)
    {
        var model = Model(morph, outsideParent ? PmxRigidBodyOperation.DynamicAndBoneMerge : PmxRigidBodyOperation.Dynamic); var rest = Rest(model);
        PmmModel Actor(byte index, int end) => new()
        {
            Index = index, DrawOrderIndex = index, TransformOrderIndex = index, Visible = true, LastFrame = end,
            BoneNames = [new(0, "root", [])],
            InitialBoneKeyframes = [new() { NextIndex = 1, Rotation = Quaternion.Identity, Interpolation = BoneCurves() }],
            BoneKeyframes = [new() { RecordIndex = 1, ObjectIndex = 1, Frame = end, Rotation = Quaternion.Identity,
                Translation = outsideParent && index == 1 ? new(end, 0, 0) : Vector3.Zero, Interpolation = BoneCurves() }],
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
            MorphNames = morph ? [new(0, "smile", [])] : [],
            InitialMorphKeyframes = morph ? [new() { NextIndex = 1 }] : [],
            MorphKeyframes = morph ? [new() { RecordIndex = 1, ObjectIndex = 1, Frame = end, Weight = 1 }] : [],
            MorphStates = morph ? [new(0, 0)] : [],
            OutsideParentSubjectBoneIndices = outsideParent && index == 0 ? [-1] : [],
            OutsideParentStates = outsideParent && index == 0 ? [new(0, 0, 0, new(0, 1, 0))] : [],
            InitialModelKeyframe = new() { Visible = true, OutsideParents = outsideParent && index == 0 ? [new(0, 1, 0)] : [] }
        };
        var document = new PmmDocument
        {
            Models = [Actor(0, shortEnd), Actor(1, longEnd)], OutputWidth = 1920, OutputHeight = 1080, CameraFov = 30,
            Settings = new() { PhysicsSimulationMode = 1, PhysicsGroundEnabled = ground, CameraLookAtModelIndex = -1 },
            Gravity = new() { Acceleration = 9.8f, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Acceleration = 9.8f, Direction = -Vector3.UnitY } },
            Camera = new() { Position = new(0, 0, -10), Perspective = true,
                InitialKeyframe = new() { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                    Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
        };
        var prepared = document.Models.Select(source => new PreparedPmmModel(source, "synthetic.pmx", model,
            PmmMotionAdapter.Convert(source, model), PmmMotionAdapter.ConvertSavedPose(source, model))).ToList();
        // Match the existing PMM validation fixtures without widening the production import API.
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [document, "synthetic.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        return new(plan, document.Models.ToDictionary(actor => (int)actor.Index, _ => rest),
            (gravity, floor) => factory is null ? new BulletCharacterPhysicsBackend(gravity, floor) : factory(gravity, floor));
    }

    private static byte[] BoneCurves() => Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray();
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));

    private static PmxModel Model(bool morph = false, PmxRigidBodyOperation operation = PmxRigidBodyOperation.Dynamic) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "Bullet project fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
        Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
            SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = morph ? [new() { Name = "smile", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position,
            PositionOffsets = [new(0, new(0, 0, 2))] }] : [], DisplayFrames = [], Joints = [], SoftBodies = [],
        RigidBodies = [new() { Name = "sphere", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = new(0, 2, 0), Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = operation }],
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
    };
}
