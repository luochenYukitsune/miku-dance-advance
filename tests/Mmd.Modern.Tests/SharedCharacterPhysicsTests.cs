using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SharedCharacterPhysicsTests
{
    [Fact]
    public void RealBulletCharactersCollideAcrossMembershipAndMasksExcludeContacts()
    {
        float contact = Separation(ushort.MaxValue), excluded = Separation(0);
        Assert.True(contact > .95f, $"Contact separation {contact}");
        Assert.InRange(excluded, .49f, .51f);
    }

    [Fact]
    public void RemovingOneActorKeepsOtherMomentumAndStableLocalMapping()
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(Vector3.Zero, null));
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        world.AddActor(a, Definition(), Matrix4x4.Identity, [Matrix4x4.Identity]);
        world.AddActor(b, Definition(), Matrix4x4.CreateTranslation(10, 0, 0), [Matrix4x4.Identity]);
        SharedPhysicsActorSample[] sample = [new(a, [Matrix4x4.Identity]) { Impulses = [new(0, false, Vector3.UnitX, Vector3.Zero)] },
            new(b, [Matrix4x4.Identity])];
        world.Step(sample);
        float before = world.GetBodyWorldTransform(a, 0).Translation.X;
        world.RemoveActor(b);
        world.Step([new(a, [Matrix4x4.Identity])]);
        float after = world.GetBodyWorldTransform(a, 0).Translation.X;
        Assert.InRange(after - before, .0082f, .0085f);
        Assert.Throws<ArgumentException>(() => world.GetBodyWorldTransform(b, 0));
    }

    [Fact]
    public void FrozenActorRemainsAtItsPhysicalPoseWhileAnotherContinues()
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(new(0, -12, 0), null));
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        world.AddActor(a, Definition(), Matrix4x4.Identity, [Matrix4x4.Identity]);
        world.AddActor(b, Definition(), Matrix4x4.CreateTranslation(10, 0, 0), [Matrix4x4.Identity]);
        var result = world.Step([new(a, [Matrix4x4.Identity]), new(b, [Matrix4x4.Identity])]);
        var frozen = result[a];
        for (int i = 0; i < 10; i++) result = world.Step([new(a, frozen) { Frozen = true }, new(b, [Matrix4x4.Identity])]);
        Assert.Equal(frozen, result[a]);
        Assert.True(result[b][0].Translation.Y < frozen[0].Translation.Y - .02f);
        Assert.Equal(11, world.StepCount);
    }

    [Fact]
    public void NonRigidPlacementIsRejectedWithoutChangingLiveBodies()
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(Vector3.Zero, null));
        var a = Guid.NewGuid(); world.AddActor(a, Definition(), Matrix4x4.Identity, [Matrix4x4.Identity]);
        Assert.Throws<ArgumentException>(() => world.AddActor(Guid.NewGuid(), Definition(), Matrix4x4.CreateScale(2), [Matrix4x4.Identity]));
        Assert.Equal(Matrix4x4.Identity, world.GetBodyWorldTransform(a, 0));
        Assert.Single(world.ActorIds);
    }

    [Fact]
    public void RotatedPlacementPreservesModelSpaceBoneReflectionWithoutEulerConversion()
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(Vector3.Zero, null));
        var id = Guid.NewGuid();
        var placement = Matrix4x4.CreateRotationZ(.73f) * Matrix4x4.CreateRotationX(-.29f) * Matrix4x4.CreateTranslation(7, -2, 4);
        var local = Matrix4x4.CreateRotationY(.47f) * Matrix4x4.CreateTranslation(1, 2, 3);
        world.AddActor(id, Definition(), placement, [local]);
        var result = world.Step([new(id, [local])]);
        Assert.InRange(Vector3.Distance(local.Translation, result[id][0].Translation), 0, 2e-5f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, local), Vector3.Transform(Vector3.UnitX, result[id][0])), 0, 2e-5f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(local.Translation, placement), world.GetBodyWorldTransform(id, 0).Translation), 0, 2e-5f);
    }

    [Fact]
    public void PerBoneDisableFollowsAnimationAndRestoresWithoutOldMomentum()
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(Vector3.Zero, null));
        var id = Guid.NewGuid(); world.AddActor(id, Definition(), Matrix4x4.Identity, [Matrix4x4.Identity]);
        world.Step([new(id, [Matrix4x4.Identity]) { Impulses = [new(0, false, new(120, 0, 0), Vector3.Zero)] }]);
        var disabled = new HashSet<int> { 0 }; var target = Matrix4x4.CreateTranslation(3, 2, 1);
        var result = world.Step([new(id, [target]) { DisabledBoneIndices = disabled }]);
        Assert.Equal(target, result[id][0]); Assert.Equal(target, world.GetBodyWorldTransform(id, 0));
        result = world.Step([new(id, [target])]);
        Assert.Equal(target, result[id][0]);
    }

    [Fact]
    public void ReplacingActorKeepsUnrelatedBodyMomentum()
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(Vector3.Zero, null));
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        world.AddActor(a, Definition(), Matrix4x4.Identity, [Matrix4x4.Identity]);
        world.AddActor(b, Definition(), Matrix4x4.CreateTranslation(10, 0, 0), [Matrix4x4.Identity]);
        world.Step([new(a, [Matrix4x4.Identity]) { Impulses = [new(0, false, Vector3.UnitX, Vector3.Zero)] }, new(b, [Matrix4x4.Identity])]);
        float before = world.GetBodyWorldTransform(a, 0).Translation.X;
        world.ReplaceActor(b, Definition(), Matrix4x4.CreateTranslation(20, 0, 0), [Matrix4x4.Identity]);
        world.Step([new(a, [Matrix4x4.Identity]), new(b, [Matrix4x4.Identity])]);
        Assert.InRange(world.GetBodyWorldTransform(a, 0).Translation.X - before, .0082f, .0085f);
        Assert.Equal(2, world.StepCount);
    }

    [Fact]
    public void FrozenUnboundDynamicColliderKeepsItsSimulatedPoseAndFollowsOnlyRootDelta()
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(new(0, -12, 0), null));
        var id = Guid.NewGuid(); var original = Definition();
        var unbound = new CharacterPhysicsDefinition(original.Bones, [original.Bodies[0] with { BoneIndex = -1 }], []);
        world.AddActor(id, unbound, Matrix4x4.CreateTranslation(7, 0, 0), [Matrix4x4.Identity]);
        for (int i = 0; i < 12; i++) world.Step([new(id, [Matrix4x4.Identity])]);
        var fallen = world.GetBodyWorldTransform(id, 0);
        Assert.True(fallen.M42 < -.05f);
        world.Step([new(id, [Matrix4x4.Identity]) { Frozen = true }]);
        Assert.Equal(fallen, world.GetBodyWorldTransform(id, 0));
        world.Step([new(id, [Matrix4x4.CreateTranslation(3, 0, 0)]) { Frozen = true }]);
        Assert.InRange(Vector3.Distance(fallen.Translation + new Vector3(3, 0, 0), world.GetBodyWorldTransform(id, 0).Translation), 0, 1e-5f);
    }

    private static float Separation(ushort mask)
    {
        using var world = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(Vector3.Zero, null));
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        world.AddActor(a, Definition(mask), Matrix4x4.Identity, [Matrix4x4.Identity]);
        world.AddActor(b, Definition(mask), Matrix4x4.CreateTranslation(.5f, 0, 0), [Matrix4x4.Identity]);
        for (int i = 0; i < 60; i++) world.Step([new(a, [Matrix4x4.Identity]), new(b, [Matrix4x4.Identity])]);
        return Vector3.Distance(world.GetBodyWorldTransform(a, 0).Translation, world.GetBodyWorldTransform(b, 0).Translation);
    }
    private static CharacterPhysicsDefinition Definition(ushort mask = ushort.MaxValue) => new([new(-1, Matrix4x4.Identity)],
        [new("sphere", 0, PhysicsShapeKind.Sphere, new(.5f), Vector3.Zero, Vector3.Zero, 1, 0, 0, 0, 0, 0, mask, PhysicsBodyMode.Dynamic)], []);
}
