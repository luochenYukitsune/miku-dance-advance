using System.Numerics;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ControllerRigidBackendIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActiveBackendAliasRejectsBeforeResetOrErrorPathDisposal(bool withSoft, bool invalidFrame)
    {
        using var controller = Controller(); var active = new ProbeRigid();
        controller.EnablePhysicsAt(active, 0);
        var soft = new ProbeSoft(); if (withSoft) controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(3, .1); int initializations = active.Initializations, softResets = soft.Resets;
        Assert.Throws<InvalidOperationException>(() => controller.EnablePhysicsAt(active, invalidFrame ? double.NaN : 3));
        Assert.Equal(initializations, active.Initializations); Assert.Equal(0, active.Disposals);
        Assert.Equal(softResets, soft.Resets); Assert.Equal(0, soft.Batches);
        Assert.Same(before.Surface, controller.SampleAt(3).Surface);
        int steps = active.Steps; controller.Advance(4, 1d / 30); Assert.True(active.Steps > steps);
        controller.Dispose(); Assert.Equal(1, active.Disposals);
    }

    [Fact]
    public void FailedFreshCandidateRetainsIdentityThenDisableAllowsAnotherFreshOwner()
    {
        using var controller = Controller(); var active = new ProbeRigid(); controller.EnablePhysicsAt(active, 0);
        var failed = new ProbeRigid { FailInitialize = true };
        Assert.Throws<InvalidOperationException>(() => controller.EnablePhysicsAt(failed, 0));
        Assert.Equal(1, failed.Disposals); Assert.Equal(0, active.Disposals);
        int initializations = active.Initializations;
        Assert.Throws<InvalidOperationException>(() => controller.EnablePhysicsAt(active, 0));
        Assert.Equal(initializations, active.Initializations); Assert.Equal(0, active.Disposals);
        controller.DisablePhysics(); Assert.Equal(1, active.Disposals);
        var replacement = new ProbeRigid(); controller.EnablePhysicsAt(replacement, 0);
        controller.Advance(1, 1d / 30); Assert.True(replacement.Steps > 0);
        controller.Dispose(); Assert.Equal(1, replacement.Disposals); Assert.Equal(1, active.Disposals);
    }

    private static CharacterAnimationController Controller()
    {
        var source = PmxSoftBodyTests.MaterialFixture();
        var model = new PmxModel
        {
            Header = source.Header, Name = source.Name, EnglishName = source.EnglishName, Comment = source.Comment, EnglishComment = source.EnglishComment,
            Vertices = source.Vertices, Faces = source.Faces, Textures = source.Textures, Materials = source.Materials, Bones = source.Bones,
            Morphs = source.Morphs, DisplayFrames = source.DisplayFrames, Joints = source.Joints, SoftBodies = source.SoftBodies,
            RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.1f), Translation = new(100), Rotation = Vector3.Zero, Mass = 1,
                TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }]
        };
        return new(model, new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }] },
            PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));
    }

    private sealed class ProbeRigid : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        public int Initializations, Disposals, Steps;
        public bool FailInitialize;
        public string Name => "identity probe";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { ObjectDisposedException.ThrowIf(Disposals != 0, this); Initializations++; if (FailInitialize) throw new InvalidOperationException("injected initialize"); worlds = transforms.ToArray(); }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => worlds[index] = world;
        public Matrix4x4 GetBodyWorldTransform(int index) { ObjectDisposedException.ThrowIf(Disposals != 0, this); return worlds[index]; }
        public void Step(float seconds)
        { ObjectDisposedException.ThrowIf(Disposals != 0, this); Steps++; for (int i = 0; i < worlds.Length; i++) worlds[i].Translation += new Vector3(0, -seconds, 0); }
        public void Dispose() => Disposals++;
    }

    private sealed class ProbeSoft : ISoftBodyBackend, ISoftBodyRigidProxyUpdater
    {
        private int[] ids = []; private Vector3[] positions = [];
        public int Resets, Batches;
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders)
            => ids = definitions.SelectMany(definition => definition.VertexIndices).Distinct().ToArray();
        public void Reset(ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids) { Resets++; positions = targets.ToArray(); }
        public void Step(float seconds, ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids) { }
        public IReadOnlyList<SoftBodySample> Sample() => [new(ids, ids.Select(id => positions[id]).ToArray(), new Vector3[ids.Length])];
        public void UpdateRigidProxies(ReadOnlySpan<Matrix4x4> rigids) => Batches++;
        public void SetGravity(Vector3 gravity) { }
        public void Dispose() { }
    }
}
