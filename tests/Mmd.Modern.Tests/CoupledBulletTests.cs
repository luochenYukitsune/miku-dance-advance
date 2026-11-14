using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CoupledBulletTests
{
    private const float Dt = 1f / 120;
    private static PhysicsBodyDefinition Rigid(PhysicsBodyMode mode = PhysicsBodyMode.Dynamic) =>
        new("actual", -1, PhysicsShapeKind.Sphere, new(.1f), new(0, 3, 0), Vector3.Zero, 1, 0, 0, 0, .5f, 0, 0, mode);
    private static BulletCharacterPhysicsBackend Owner(bool rigid = true, PhysicsBodyMode mode = PhysicsBodyMode.Dynamic, Vector3? gravity = null)
    {
        var owner = new BulletCharacterPhysicsBackend(gravity ?? Vector3.Zero, null, coupledSoftBodies: true);
        var definition = new CharacterPhysicsDefinition([], rigid ? [Rigid(mode)] : [], []);
        owner.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()); return owner;
    }
    private static SoftBodyDefinition Rope(bool anchored = true, bool near = false) => new("rope", SoftBodyShape.Rope,
        [0, 1], [new(2, 3, 0), new(3, 3, 0)], [], [1], anchored ? [new(0, 0, near)] : [], new() { CollisionMask = 0 });

    [Fact]
    public void LegacyOwnerRequiresExplicitCoupledSelection()
    {
        using var owner = new BulletCharacterPhysicsBackend(Vector3.Zero, null);
        owner.Initialize(new([], [], []), []);
        Assert.False(owner.SupportsCoupledSoftBodies);
        Assert.Throws<NotSupportedException>(() => owner.CreateSoftBodyBinding([]));
    }

    [Fact]
    public void OrdinaryAnchorReactionReachesActualRigidAndFollowingPosition()
    {
        using var owner = Owner(); using var control = Owner();
        using var binding = owner.CreateSoftBodyBinding([0]); using var loose = control.CreateSoftBodyBinding([0]);
        binding.Initialize([Rope()], [new(2, 3, 0), new(3, 3, 0)]);
        loose.Initialize([Rope(anchored: false)], [new(2, 3, 0), new(3, 3, 0)]);
        owner.Step(Dt); control.Step(Dt);
        float largestNodeDifference = 0;
        for (int i = 0; i < 12; i++)
        {
            binding.SetTargets([new(2, 3, 0), new(4, 3, 0)]); loose.SetTargets([new(2, 3, 0), new(4, 3, 0)]);
            owner.Step(Dt); control.Step(Dt);
            largestNodeDifference = MathF.Max(largestNodeDifference,
                Vector3.Distance(binding.Sample()[0].Positions[0], loose.Sample()[0].Positions[0]));
        }
        Assert.True(MathF.Abs(owner.GetBodyWorldTransform(0).Translation.X) > .001f);
        Assert.Equal(0, control.GetBodyWorldTransform(0).Translation.X);
        // Both ropes can settle at the same endpoint; reaction is visible during
        // the transient and in the actual rigid's following integrated position.
        Assert.True(largestNodeDifference > .01f);
    }

    [Theory]
    [InlineData(PhysicsBodyMode.Dynamic)]
    [InlineData(PhysicsBodyMode.DynamicAligned)]
    public void NearMovesActualRigidOnlyAfterOwnerStepAndPreservesRotation(PhysicsBodyMode mode)
    {
        using var owner = Owner(mode: mode); using var binding = owner.CreateSoftBodyBinding([0]);
        var source = Rope(near: true); binding.Initialize([source], source.Positions.ToArray());
        Assert.Equal(new Vector3(0, 3, 0), owner.GetBodyWorldTransform(0).Translation);
        _ = binding.Sample();
        Assert.Equal(new Vector3(0, 3, 0), owner.GetBodyWorldTransform(0).Translation);
        owner.ApplyImpulse(0, new(.2f, 0, 0), Vector3.Zero); owner.Step(Dt);
        var after = owner.GetBodyWorldTransform(0);
        Assert.Equal(binding.Sample()[0].Positions[0], after.Translation);
        Assert.Equal(1, after.M11); Assert.Equal(1, after.M22); Assert.Equal(1, after.M33);
        _ = binding.Sample(); Assert.Equal(after, owner.GetBodyWorldTransform(0));
        owner.Step(Dt); Assert.Equal(binding.Sample()[0].Positions[0], owner.GetBodyWorldTransform(0).Translation);
    }

    [Fact]
    public void TemporaryKinematicCommandsTakePrecedenceOverDynamicNear()
    {
        using var owner = Owner(); using var binding = owner.CreateSoftBodyBinding([0]);
        var source = Rope(near: true); binding.Initialize([source], source.Positions.ToArray());
        owner.SetBodyKinematic(0, true); var target = Matrix4x4.CreateTranslation(11, 3, 0);
        owner.SetKinematicTarget(0, target, Dt); owner.Step(Dt);
        Assert.Equal(target, owner.GetBodyWorldTransform(0));
    }

    [Fact]
    public void InterleavedCommonNodeComponentsRetainOriginalNearOrder()
    {
        using var owner = Owner(); using var binding = owner.CreateSoftBodyBinding([0]);
        SoftBodyDefinition Part(string name, int[] ids, int nearVertex) => new(name, SoftBodyShape.Rope, ids,
            ids.Select(id => new Vector3(id, 3, 0)), [], ids, [new(0, nearVertex, true)], new() { CollisionMask = 0 });
        // Component A contains first and last authored sources. Component B must
        // not win just because its native body is registered after component A.
        var a0 = Part("A0", [9, 8], 9); var b = Part("B", [6, 5], 6); var a1 = Part("A1", [8, 1], 1);
        var targets = Enumerable.Range(0, 10).Select(id => new Vector3(id, 3, 0)).ToArray();
        void Check()
        {
            owner.Step(Dt); Assert.Equal(targets[1], owner.GetBodyWorldTransform(0).Translation);
            Assert.Contains(binding.Diagnostics, text => text.Contains("multiple pull") && text.Contains("A1"));
        }
        binding.Initialize([a0, b, a1], targets); Check();
        binding.Initialize([a0, b, a1], targets); Check();
        Assert.Equal(5, binding.Sample().Sum(s => s.VertexIndices.Length));
    }

    [Fact]
    public void BindingPreventsRemovingItsMappedRigidUntilDisposed()
    {
        using var owner = Owner(); var binding = owner.CreateSoftBodyBinding([0]);
        binding.Initialize([Rope()], [new(2, 3, 0), new(3, 3, 0)]);
        Assert.Throws<InvalidOperationException>(() => owner.RemoveBody(0));
        Assert.Equal(new Vector3(0, 3, 0), owner.GetBodyWorldTransform(0).Translation);
        binding.Dispose(); owner.RemoveBody(0);
    }

    [Fact]
    public void UninitializedBindingAlsoReservesItsMappedRigid()
    {
        using var owner = Owner(); var binding = owner.CreateSoftBodyBinding([0]);
        Assert.Throws<InvalidOperationException>(() => owner.RemoveBody(0));
        Assert.Throws<ArgumentException>(() => binding.Initialize([Rope()], []));
        Assert.Throws<InvalidOperationException>(() => owner.RemoveBody(0));
        binding.Initialize([Rope()], [new(2, 3, 0), new(3, 3, 0)]);
        binding.Dispose(); owner.RemoveBody(0);
    }

    [Fact]
    public void FrozenAnchorsAndNearDoNotPullAnOtherwiseActiveRigid()
    {
        using var owner = Owner(); using var binding = owner.CreateSoftBodyBinding([0]);
        var source = Rope(near: true); binding.Initialize([source], source.Positions.ToArray());
        binding.SetFrozen(true); var before = binding.Sample()[0].Positions;
        owner.ApplyImpulse(0, new(.2f, 0, 0), Vector3.Zero);
        for (int i = 0; i < 10; i++) owner.Step(Dt);
        Assert.InRange(MathF.Abs(owner.GetBodyWorldTransform(0).Translation.X - .2f * 10 * Dt), 0, .00001f);
        Assert.Equal(before, binding.Sample()[0].Positions);
    }

    [Fact]
    public void OwnerReplacementInvalidatesBindingsAndFailedCandidatePreservesThem()
    {
        using var owner = Owner(); using var binding = owner.CreateSoftBodyBinding([0]);
        binding.Initialize([Rope()], [new(2, 3, 0), new(3, 3, 0)]);
        var before = binding.Sample()[0].Positions;
        Assert.Throws<ArgumentException>(() => owner.Initialize(new([], [Rigid()], []), [Matrix4x4.CreateScale(2)]));
        Assert.Equal(before, binding.Sample()[0].Positions);
        owner.Initialize(new([], [], []), []);
        Assert.Throws<ObjectDisposedException>(() => binding.Sample());
    }

    [Fact]
    public void DisposedOwnerInvalidatesOutstandingBinding()
    {
        var owner = Owner(); var binding = owner.CreateSoftBodyBinding([0]);
        binding.Initialize([Rope()], [new(2, 3, 0), new(3, 3, 0)]); owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => binding.Sample()); binding.Dispose();
    }

    [Fact]
    public void FailedTopologyOrTargetsPreserveCurrentBinding()
    {
        using var owner = Owner(); using var binding = owner.CreateSoftBodyBinding([0]);
        binding.Initialize([Rope()], [new(2, 3, 0), new(3, 3, 0)]); var before = binding.Sample()[0].Positions;
        Assert.Throws<ArgumentException>(() => binding.Initialize([Rope()], [new(2, 3, 0)]));
        Assert.Equal(before, binding.Sample()[0].Positions);
        Assert.Throws<ArgumentException>(() => binding.SetTargets([new(2, 3, 0), new(float.NaN, 3, 0)]));
        Assert.Equal(before, binding.Sample()[0].Positions);
        Assert.Throws<ArgumentException>(() => binding.Rebase(Matrix4x4.CreateScale(2)));
        Assert.Equal(before, binding.Sample()[0].Positions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void FreezePreservesHistoryAndResumeMatchesUnadvancedControl(int flags)
    {
        using var owner = Owner(rigid: false, gravity: new(0, -9.8f, 0));
        using var control = Owner(rigid: false, gravity: new(0, -9.8f, 0));
        using var binding = owner.CreateSoftBodyBinding([]); using var reference = control.CreateSoftBodyBinding([]);
        var source = new SoftBodyDefinition("cloth", SoftBodyShape.TriMesh, [0, 1, 2], [new(0, 5, 0), new(1, 5, 0), new(0, 5, 1)],
            [0, 1, 2], [], [], new() { Flags = flags, ClusterCount = 1, CollisionMask = 0 });
        binding.Initialize([source], source.Positions.ToArray()); reference.Initialize([source], source.Positions.ToArray());
        for (int i = 0; i < 8; i++) { owner.Step(Dt); control.Step(Dt); }
        var before = binding.Sample()[0].Positions; binding.SetFrozen(true);
        for (int i = 0; i < 20; i++) owner.Step(Dt);
        Assert.Equal(before, binding.Sample()[0].Positions); binding.SetFrozen(false);
        owner.Step(Dt); control.Step(Dt); Assert.Equal(reference.Sample()[0].Positions, binding.Sample()[0].Positions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RebaseTransformsPreviousPositionsAndVelocityWithoutReset(bool frozen)
    {
        using var owner = Owner(rigid: false); using var control = Owner(rigid: false);
        using var binding = owner.CreateSoftBodyBinding([]); using var reference = control.CreateSoftBodyBinding([]);
        var source = Rope(anchored: false); binding.Initialize([source], source.Positions.ToArray()); reference.Initialize([source], source.Positions.ToArray());
        for (int i = 0; i < 8; i++)
        {
            binding.SetTargets([new(2, 3, 0), new(4, 3, 0)]); reference.SetTargets([new(2, 3, 0), new(4, 3, 0)]);
            owner.Step(Dt); control.Step(Dt);
        }
        var delta = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(10, 8, 7);
        if (frozen) binding.SetFrozen(true); binding.Rebase(delta);
        var expected = reference.Sample()[0].Positions.Select(p => Vector3.Transform(p, delta)).ToArray();
        for (int i = 0; i < expected.Length; i++) Assert.InRange(Vector3.Distance(expected[i], binding.Sample()[0].Positions[i]), 0, .00001f);
        if (frozen) binding.SetFrozen(false);
        owner.Step(Dt); control.Step(Dt); expected = reference.Sample()[0].Positions.Select(p => Vector3.Transform(p, delta)).ToArray();
        for (int i = 0; i < expected.Length; i++) Assert.InRange(Vector3.Distance(expected[i], binding.Sample()[0].Positions[i]), 0, .0002f);
    }

    [Fact]
    public void SoftOnlyBindingsShareOneOwnerStepAndActorLocalIdsStaySeparate()
    {
        using var owner = Owner(rigid: false, gravity: new(0, -9.8f, 0));
        using var a = owner.CreateSoftBodyBinding([]); using var b = owner.CreateSoftBodyBinding([]);
        var source = Rope(anchored: false); a.Initialize([source], source.Positions.ToArray());
        b.Initialize([source], source.Positions.Select(p => p + new Vector3(100, 0, 0)).ToArray());
        owner.Step(Dt); var ap = a.Sample()[0].Positions; var bp = b.Sample()[0].Positions;
        Assert.InRange(ap[0].Y, 2.999f, 3); Assert.InRange(bp[0].Y, 2.999f, 3);
        Assert.InRange(bp[0].X - ap[0].X, 99.9999f, 100.0001f);
    }

    [Fact]
    public void FullyPinnedContentCanRebaseBeforeItsFirstOwnerStep()
    {
        using var owner = Owner(rigid: false); using var binding = owner.CreateSoftBodyBinding([]);
        var source = new SoftBodyDefinition("all pins", SoftBodyShape.Rope, [0, 1], [new(2, 3, 0), new(3, 3, 0)], [], [0, 1], [], new());
        binding.Initialize([source], source.Positions.ToArray());
        var delta = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(10, 8, 7);
        binding.Rebase(delta); owner.Step(Dt);
        var actual = binding.Sample()[0].Positions;
        for (int i = 0; i < actual.Length; i++) Assert.InRange(Vector3.Distance(Vector3.Transform(source.Positions[i], delta), actual[i]), 0, .00001f);
    }

    [Fact]
    public void FrozenClusterRebasePreservesVelocityAndOwnedTopologyArrays()
    {
        using var owner = Owner(rigid: false, gravity: new(0, -9.8f, 0));
        using var control = Owner(rigid: false, gravity: new(0, -9.8f, 0));
        using var binding = owner.CreateSoftBodyBinding([]); using var reference = control.CreateSoftBodyBinding([]);
        var source = new SoftBodyDefinition("cluster", SoftBodyShape.TriMesh, [0, 1, 2], [new(0, 5, 0), new(1, 5, 0), new(0, 5, 1)],
            [0, 1, 2], [], [], new() { Flags = 2, ClusterCount = 1, CollisionMask = 0 });
        binding.Initialize([source], source.Positions.ToArray()); reference.Initialize([source], source.Positions.ToArray());
        for (int i = 0; i < 8; i++) { owner.Step(Dt); control.Step(Dt); }
        var delta = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(10, 8, 7);
        binding.SetFrozen(true); binding.Rebase(delta);
        for (int i = 0; i < 20; i++) owner.Step(Dt);
        binding.SetFrozen(false); owner.Step(Dt); control.Step(Dt);
        var actual = binding.Sample()[0].Positions; var expected = reference.Sample()[0].Positions;
        for (int i = 0; i < actual.Length; i++) Assert.InRange(Vector3.Distance(Vector3.Transform(expected[i], delta), actual[i]), 0, .0001f);
    }
}
