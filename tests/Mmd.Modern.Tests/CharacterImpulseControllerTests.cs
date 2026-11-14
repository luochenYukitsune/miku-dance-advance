using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterImpulseControllerTests
{
    [Fact]
    public void FractionalPhysicsSampleCannotReuseItsEarlierIntegerFrameAsASeekCache()
    {
        using var controller = Controller(); var backend = new RecordingBackend();
        controller.EnablePhysicsAt(backend, 0, enableMorphs: false);
        int initialized = backend.InitializeCalls;
        controller.Advance(.5, 1d / 60, enableMorphs: false);
        Assert.Equal(initialized, backend.InitializeCalls);
        controller.SampleAt(0, enableMorphs: false);
        Assert.Equal(initialized + 1, backend.InitializeCalls);
    }

    [Fact]
    public void RuntimeGravityReachesBackendWithoutResettingTheCurrentPoseAndSurvivesSeek()
    {
        using var controller = Controller();
        Assert.Throws<InvalidOperationException>(() => controller.SetGravity(Vector3.UnitX));
        var backend = new RecordingBackend();
        controller.EnablePhysics(backend, 0);
        controller.Advance(1, 1d / 30);
        var worlds = controller.BoneWorldMatrices.ToArray();
        int impulseCalls = backend.ImpulseCalls;
        controller.SetGravity(new(3, -4, 5));
        Assert.Equal(new Vector3(3, -4, 5), backend.Gravity);
        Assert.Equal(worlds, controller.BoneWorldMatrices);
        Assert.Equal(impulseCalls, backend.ImpulseCalls);
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetGravity(new(float.NaN)));
        Assert.Equal(new Vector3(3, -4, 5), backend.Gravity);
        controller.ResetPhysics(0);
        Assert.Equal(new Vector3(3, -4, 5), backend.Gravity);
    }

    [Fact]
    public void ImpulseOnlyVmdBindsAndPhysicsOffExposesImmutableUnappliedSample()
    {
        using var controller = Controller();
        controller.SampleAt(3.5);
        Assert.True(controller.SupportsMorphs);
        Assert.Equal(1, controller.MatchedMorphTrackCount);
        Assert.Equal(30u, controller.DurationFrames);
        var snapshot = controller.MorphChannels;
        var impulse = Assert.Single(snapshot.Impulses);
        Assert.Equal(new Vector3(1, 0, 0), impulse.TranslationVelocity);
        Assert.Throws<NotSupportedException>(() => ((IList<PmxRigidBodyImpulse>)snapshot.Impulses)[0] = default);
        controller.SampleAt(4, enableMorphs: false);
        Assert.Empty(controller.MorphChannels.Impulses);
        Assert.Single(snapshot.Impulses);
    }

    [Fact]
    public void ControllerExecutesCurrentWeightsPerStepButNotAtResetPauseOrMorphDisable()
    {
        using var controller = Controller();
        var backend = new RecordingBackend();
        controller.EnablePhysics(backend, 0);
        Assert.Equal(0, backend.ImpulseCalls);
        controller.Advance(1, 1d / 30);
        Assert.Equal(4, backend.ImpulseCalls);
        Assert.Equal(new Vector3(4, 0, 0), backend.Total);
        controller.Advance(1, 0);
        Assert.Equal(4, backend.ImpulseCalls);
        controller.SampleAt(10);
        Assert.Equal(0, backend.ImpulseCalls);
        controller.Advance(11, 1d / 30, enableMorphs: false);
        Assert.Equal(0, backend.ImpulseCalls);
        controller.Advance(12, 1d / 30);
        Assert.Equal(4, backend.ImpulseCalls);
    }

    private static CharacterAnimationController Controller()
    {
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var m = PmxReader.Read(input);
        var model = new PmxModel
        {
            Header = m.Header, Name = "impulse", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = m.Vertices.Select(v => new PmxVertex { Position = v.Position, Normal = v.Normal, Uv = v.Uv,
                AdditionalUvs = v.AdditionalUvs, WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0,-1,-1,-1],
                BoneWeights = [1,0,0,0], EdgeScale = v.EdgeScale }).ToArray(), Faces = m.Faces, Textures = m.Textures,
            Materials = m.Materials, Bones = m.Bones, DisplayFrames = [], SoftBodies = [], Joints = [],
            Morphs = [new() { Name = "push", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Impulse,
                ImpulseOffsets = [new(0, false, new(2, 0, 0), Vector3.Zero)] }],
            RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = m.Bones[0].Position,
                Rotation = Vector3.Zero, Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0,
                Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }]
        };
        var motion = new VmdMotion { MorphKeyframes = [new() { MorphName = "push", Frame = 0, Weight = .5f },
            new() { MorphName = "push", Frame = 30, Weight = .5f }] };
        return new(model, motion, PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));
    }

    private sealed class RecordingBackend : ICharacterPhysicsBackend, ICharacterImpulseBackend, ICharacterGravityBackend
    {
        private Matrix4x4[] worlds = [];
        public int ImpulseCalls;
        public Vector3 Total;
        public Vector3 Gravity;
        public int InitializeCalls;
        public void SetGravity(Vector3 gravity) => Gravity = gravity;
        public string Name => "impulse recording";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { worlds = transforms.ToArray(); ImpulseCalls = 0; Total = Vector3.Zero; InitializeCalls++; }
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) { ImpulseCalls++; Total += linear; }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => worlds[index] = transform;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void Step(float seconds) { }
        public void Dispose() { }
    }
}
