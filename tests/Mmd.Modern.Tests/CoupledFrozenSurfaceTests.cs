using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CoupledFrozenSurfaceTests
{
    private const float Dt = 1f / 120;

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ProjectionPreservesOriginalHistoryAndNextStepExactly(int flags)
    {
        using var owner = Owner(); using var control = Owner();
        using var binding = owner.CreateSoftBodyBinding([]); using var reference = control.CreateSoftBodyBinding([]);
        var cloth = Cloth(flags, 0);
        binding.Initialize([cloth], cloth.Positions.ToArray()); reference.Initialize([cloth], cloth.Positions.ToArray());
        for (int i = 0; i < 9; i++) { owner.Step(Dt); control.Step(Dt); }
        var original = binding.Sample().Single().Positions.ToArray();
        binding.SetFrozen(true);
        Assert.True(binding.SupportsFrozenCollisionSurface);
        for (int i = 0; i < 8; i++)
        {
            var projected = original.Select((p, n) => p + new Vector3(10 + i, n * .2f, -n * .3f)).ToArray();
            binding.SetFrozenCollisionSurface(projected);
            Assert.Equal(projected, binding.Sample().Single().Positions);
            owner.Step(Dt);
            Assert.Equal(projected, binding.Sample().Single().Positions);
        }
        binding.SetFrozen(false);
        Assert.Equal(original, binding.Sample().Single().Positions);
        for (int i = 0; i < 12; i++)
        {
            owner.Step(Dt); control.Step(Dt);
            Assert.Equal(reference.Sample().Single().Positions, binding.Sample().Single().Positions);
        }
    }

    [Fact]
    public void InvalidManagedAndNativeBatchesPreserveEveryComponent()
    {
        using var owner = Owner(); using var binding = owner.CreateSoftBodyBinding([]);
        var first = Cloth(0, 0); var second = new SoftBodyDefinition("other", SoftBodyShape.Rope,
            [4, 5], [new(8, 5, 0), new(9, 5, 0)], [], [], [], new() { CollisionMask = 0 });
        Vector3[] points = [.. first.Positions, Vector3.Zero, .. second.Positions];
        binding.Initialize([first, second], points);
        Assert.Throws<InvalidOperationException>(() => binding.SetFrozenCollisionSurface(points));
        binding.SetFrozen(true);
        var before = binding.Sample().SelectMany(s => s.Positions).ToArray();
        Assert.Throws<ArgumentException>(() => binding.SetFrozenCollisionSurface(points[..5]));
        var invalid = points.ToArray(); invalid[5] = new(float.NaN, 0, 0);
        Assert.Throws<ArgumentException>(() => binding.SetFrozenCollisionSurface(invalid));
        Assert.Equal(before, binding.Sample().SelectMany(s => s.Positions));
        // Bypass managed checks to prove the export validates the whole batch first.
        var library = NativeLibrary.Load("mmd_bullet", typeof(BulletCharacterPhysicsBackend).Assembly, null);
        try
        {
            var native = Marshal.GetDelegateForFunctionPointer<NativeSurface>(NativeLibrary.GetExport(library, "mmd_soft_attached_frozen_surface"));
            var handle = (IntPtr)binding.GetType().GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!;
            float[] batch = before.SelectMany(p => new[] { p.X + 10, p.Y, p.Z }).ToArray();
            Assert.NotEqual(0, native(handle, batch, before.Length - 1));
            Assert.Equal(before, binding.Sample().SelectMany(s => s.Positions));
            batch[^1] = float.NaN;
            Assert.NotEqual(0, native(handle, batch, before.Length));
            Assert.Equal(before, binding.Sample().SelectMany(s => s.Positions));
        }
        finally { NativeLibrary.Free(library); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public void RestoredFaceAerodynamicsMatchWithoutAnInterveningSample(int flags, bool collapsed)
    {
        using var owner = Owner(); using var control = Owner();
        using var binding = owner.CreateSoftBodyBinding([]); using var reference = control.CreateSoftBodyBinding([]);
        var baseCloth = Cloth(flags, 0);
        var config = baseCloth.Settings.Config.ToArray(); config[2] = .8f; config[3] = .2f;
        var cloth = new SoftBodyDefinition("face aero", baseCloth.Shape, baseCloth.VertexIndices,
            baseCloth.Positions, baseCloth.TriangleIndices, [], [], baseCloth.Settings with { AeroModel = 3, Config = config });
        binding.Initialize([cloth], cloth.Positions.ToArray()); reference.Initialize([cloth], cloth.Positions.ToArray());
        for (int i = 0; i < 30; i++) { owner.Step(Dt); control.Step(Dt); }
        binding.SetFrozen(true);
        binding.SetFrozenCollisionSurface(collapsed
            ? [new(0, 10, 0), new(0, 11, 0), new(0, 12, 0)]
            : [new(0, 10, 0), new(0, 11, 0), new(0, 10, 1)]);
        var frozen = binding.Sample().Single();
        foreach (var normal in frozen.Normals)
            Assert.True(float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z));
        if (collapsed) Assert.All(frozen.Normals, normal => Assert.Equal(Vector3.Zero, normal));
        binding.SetFrozen(false);
        // Sample() recomputes normals and would hide a stale saved face normal.
        owner.Step(Dt); control.Step(Dt);
        Assert.Equal(reference.Sample().Single().Positions, binding.Sample().Single().Positions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ActualRigidCollidesWithProjectedFrozenSurfaceAgainstMaskAndOldLocationControls(int flags)
    {
        using var owner = Owner(rigid: true); using var masked = Owner(rigid: true); using var unmoved = Owner(rigid: true);
        using var binding = owner.CreateSoftBodyBinding([]); using var noContact = masked.CreateSoftBodyBinding([]);
        using var oldLocation = unmoved.CreateSoftBodyBinding([]);
        var cloth = Cloth(flags, ushort.MaxValue);
        binding.Initialize([cloth], cloth.Positions.ToArray());
        var silent = Cloth(flags, 0); noContact.Initialize([silent], silent.Positions.ToArray());
        oldLocation.Initialize([cloth], cloth.Positions.ToArray());
        binding.SetFrozen(true); noContact.SetFrozen(true); oldLocation.SetFrozen(true);
        Vector3[] projected = [new(0, 2, 0), new(2, 2.1f, 0), new(0, 2, 2)];
        binding.SetFrozenCollisionSurface(projected); noContact.SetFrozenCollisionSurface(projected);
        float difference = 0;
        for (int i = 0; i < 90; i++)
        {
            owner.Step(Dt); masked.Step(Dt); unmoved.Step(Dt);
            var active = owner.GetBodyWorldTransform(0).Translation;
            var baseline = masked.GetBodyWorldTransform(0).Translation;
            Assert.Equal(baseline, unmoved.GetBodyWorldTransform(0).Translation);
            difference = MathF.Max(difference, Vector3.Distance(active, baseline));
        }
        Assert.True(difference > .05f, $"Projected flags {flags} collider did not affect the actual rigid; max difference {difference}.");
        Assert.Equal(projected, binding.Sample().Single().Positions);
    }

    [Fact]
    public void OverflowingDerivedFrozenGeometryRejectsBeforeAnyNodeChanges()
    {
        using var owner = Owner(); using var binding = owner.CreateSoftBodyBinding([]);
        var cloth = Cloth(0, 0); binding.Initialize([cloth], cloth.Positions.ToArray()); binding.SetFrozen(true);
        var before = binding.Sample().Single().Positions.ToArray();
        Assert.Throws<InvalidOperationException>(() => binding.SetFrozenCollisionSurface(
            [new(0, 5, 0), new(1e30f, 5, 0), new(0, 5, 1e30f)]));
        Assert.Equal(before, binding.Sample().Single().Positions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SurfaceValidationBeforeFreezeLeavesActiveTrajectoryUntouched(int flags)
    {
        using var owner = Owner(); using var control = Owner();
        using var binding = owner.CreateSoftBodyBinding([]); using var reference = control.CreateSoftBodyBinding([]);
        var cloth = Cloth(flags, 0);
        binding.Initialize([cloth], cloth.Positions.ToArray()); reference.Initialize([cloth], cloth.Positions.ToArray());
        for (int i = 0; i < 9; i++) { owner.Step(Dt); control.Step(Dt); }
        var before = binding.Sample().Single().Positions.ToArray();
        _ = reference.Sample();
        Vector3[] projected = [new(0, 2, 0), new(2, 2.1f, 0), new(0, 2, 2)];
        binding.ValidateFrozenCollisionSurface(projected);
        Assert.Throws<InvalidOperationException>(() => binding.ValidateFrozenCollisionSurface(
            [new(0, 5, 0), new(1e30f, 5, 0), new(0, 5, 1e30f)]));
        Assert.Equal(before, binding.Sample().Single().Positions);
        Assert.Throws<InvalidOperationException>(() => binding.SetFrozenCollisionSurface(projected));
        owner.Step(Dt); control.Step(Dt);
        Assert.Equal(reference.Sample().Single().Positions, binding.Sample().Single().Positions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenProjectionBeforeFirstStepSuppressesAnchorsAndNear(bool near)
    {
        using var owner = Owner(rigid: true, gravity: Vector3.Zero);
        using var binding = owner.CreateSoftBodyBinding([0]);
        var rope = new SoftBodyDefinition("anchored", SoftBodyShape.Rope, [0, 1],
            [new(20, 3, 0), new(21, 3, 0)], [], [1], [new(0, 0, near)], new() { CollisionMask = 0 });
        binding.Initialize([rope], rope.Positions.ToArray()); binding.SetFrozen(true);
        Vector3[] projected = [new(30, 4, 0), new(34, 5, 0)]; binding.SetFrozenCollisionSurface(projected);
        var original = owner.GetBodyWorldTransform(0);
        for (int i = 0; i < 10; i++) owner.Step(Dt);
        Assert.Equal(original, owner.GetBodyWorldTransform(0));
        Assert.Equal(projected, binding.Sample().Single().Positions);
    }

    private static SoftBodyDefinition Cloth(int flags, ushort mask) => new("cloth", SoftBodyShape.TriMesh,
        [0, 1, 2], [new(20, 5, 0), new(21, 5, 0), new(20, 5, 1)], [0, 1, 2], [], [],
        new() { Flags = flags, ClusterCount = 1, CollisionMask = mask });

    private static BulletCharacterPhysicsBackend Owner(bool rigid = false, Vector3? gravity = null)
    {
        var owner = new BulletCharacterPhysicsBackend(gravity ?? new Vector3(0, -9.8f, 0), null, coupledSoftBodies: true);
        var body = new PhysicsBodyDefinition("falling", -1, PhysicsShapeKind.Sphere, new(.5f), new(0, 3, 0),
            Vector3.Zero, 1, 0, 0, 0, .5f, 0, ushort.MaxValue, PhysicsBodyMode.Dynamic);
        var definition = new CharacterPhysicsDefinition([], rigid ? [body] : [], []);
        owner.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()); return owner;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeSurface(IntPtr binding, [In] float[] points, int count);
}
