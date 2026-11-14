using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SharedClusterSoftBodyTests
{
    private static readonly Vector3[] Points = [new(0, 3, 0), new(1, 3, 0), new(0, 3, 1), new(1, 3, 1), new(2, 3, 0), new(2, 3, 1)];
    private static SoftBodyDefinition Part(string name, int[] ids, int clusters, int[]? pins = null,
        float mass = 4, float stiffness = 1, bool bending = false)
        => new(name, SoftBodyShape.TriMesh, ids, ids.Select(i => Points[i]), [0, 1, 2, 1, 3, 2], pins ?? [], [],
            new() { Flags = bending ? 3 : 2, ClusterCount = clusters, Mass = mass, Stiffness = [stiffness, 1, 1] });

    private static SoftBodyDefinition[] Parts(int first, int second, int[]? pins = null)
        => [Part("left", [0, 1, 2, 3], first), Part("right", [1, 4, 3, 5], second, pins)];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PerSourceClustersShareOneSolverNodeAndResetReplays(int count)
    {
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        backend.Initialize(Parts(count, count, [1]), []); backend.Reset(Points, []);
        for (int i = 0; i < 24; i++) backend.Step(1f / 120, Points, []);
        var expected = Assert.Single(backend.Sample());
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, expected.VertexIndices);
        Assert.Equal(Points[1], expected.Positions[1]);
        Assert.True(expected.Positions[5].Y < Points[5].Y - .001f);
        Assert.All(expected.Positions, p => Assert.True(float.IsFinite(p.X + p.Y + p.Z)));
        backend.Reset(Points, []);
        for (int i = 0; i < 24; i++) backend.Step(1f / 120, Points, []);
        Assert.Equal(expected.Positions, Assert.Single(backend.Sample()).Positions);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 1)]
    public void AuthoredClusterCountsMayDifferWithinTheSharedBody(int first, int second)
    {
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), 0);
        backend.Initialize(Parts(first, second), []); backend.Reset(Points, []);
        for (int i = 0; i < 48; i++) backend.Step(1f / 120, Points, []);
        Assert.All(Assert.Single(backend.Sample()).Positions, p => Assert.True(float.IsFinite(p.Y) && p.Y < 3));
    }

    [Fact]
    public void SharedClusterMassMaterialsAndSourceBendingRemainAuthored()
    {
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([Part("left", [0, 1, 2, 3], 2, mass: 4, stiffness: .2f, bending: true),
            Part("right", [1, 4, 3, 5], 1, mass: 8, stiffness: .8f, bending: true)], []);
        backend.Reset(Points, []);
        var (counts, masses) = Topology(backend, 6);
        Assert.Equal(new[] { 6, 4, 12, 2 }, counts);
        Assert.Equal(new float[] { 1, 3, 1, 3, 2, 2 }, masses);
    }

    [Fact]
    public void EachSourceMassAndStiffnessAffectsTheActualClusterSeam()
    {
        Vector3 Run(float leftMass, float rightMass, float leftStiffness, float rightStiffness)
        {
            using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
            backend.Initialize([Part("left", [0, 1, 2, 3], 1, [0], leftMass, leftStiffness),
                Part("right", [1, 4, 3, 5], 2, [5], rightMass, rightStiffness)], []);
            backend.Reset(Points, []);
            for (int i = 0; i < 40; i++) backend.Step(1f / 120, Points, []);
            return Assert.Single(backend.Sample()).Positions[1];
        }
        var baseline = Run(4, 4, 1, 1);
        Assert.True(Vector3.Distance(baseline, Run(20, 4, 1, 1)) > 1e-5f);
        Assert.True(Vector3.Distance(baseline, Run(4, 20, 1, 1)) > 1e-5f);
        Assert.True(Vector3.Distance(baseline, Run(4, 4, .01f, 1)) > 1e-5f);
        Assert.True(Vector3.Distance(baseline, Run(4, 4, 1, .01f)) > 1e-5f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CoupledBindingUsesTheSameSharedClusterTopology(int count)
    {
        using var owner = new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null, coupledSoftBodies: true);
        owner.Initialize(new([], [], []), []);
        using var binding = owner.CreateSoftBodyBinding([]);
        binding.Initialize(Parts(count, count, [1]), Points);
        for (int i = 0; i < 24; i++) { binding.SetTargets(Points); owner.Step(1f / 120); }
        var expected = Assert.Single(binding.Sample());
        Assert.Equal(Points[1], expected.Positions[1]);
        Assert.True(expected.Positions[5].Y < 2.999f);
        binding.Initialize(Parts(count, count, [1]), Points);
        for (int i = 0; i < 24; i++) { binding.SetTargets(Points); owner.Step(1f / 120); }
        Assert.Equal(expected.Positions, Assert.Single(binding.Sample()).Positions);
    }

    [Fact]
    public void EqualPositionsWithDifferentOriginalIdsRemainSeparateNodes()
    {
        Vector3[] targets = [.. Points, Points[0], Points[1], Points[2], Points[3]];
        var separate = new SoftBodyDefinition("equal coordinates", SoftBodyShape.TriMesh, [6, 7, 8, 9],
            targets.Skip(6), [0, 1, 2, 1, 3, 2], [], [], new() { Flags = 2, ClusterCount = 1 });
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([.. Parts(1, 2), separate], []); backend.Reset(targets, []);
        Assert.Equal(2, backend.Sample().Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, backend.Sample()[0].VertexIndices);
        Assert.Equal(new[] { 6, 7, 8, 9 }, backend.Sample()[1].VertexIndices);
    }

    [Fact]
    public void MixedClusterSwitchRejectsAndKeepsTheLiveSolverHistory()
    {
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        backend.Initialize(Parts(1, 2), []); backend.Reset(Points, []); backend.Step(1f / 120, Points, []);
        var before = Assert.Single(backend.Sample()).Positions;
        var plain = new SoftBodyDefinition("plain", SoftBodyShape.TriMesh, [1, 4, 3, 5],
            new[] { Points[1], Points[4], Points[3], Points[5] }, [0, 1, 2, 1, 3, 2], [], [], new());
        var error = Assert.Throws<NotSupportedException>(() => backend.Initialize([Parts(1, 2)[0], plain], []));
        Assert.Contains("Cluster", error.Message);
        Assert.Equal(before, Assert.Single(backend.Sample()).Positions);
        backend.Step(1f / 120, Points, []);
        Assert.NotEqual(before[5], Assert.Single(backend.Sample()).Positions[5]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidCandidateTargetsKeepSharedClusterHistory(bool coupled)
    {
        var invalid = Points.ToArray(); invalid[3] = new(float.NaN);
        if (coupled)
        {
            using var owner = new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null, coupledSoftBodies: true);
            owner.Initialize(new([], [], []), []); using var binding = owner.CreateSoftBodyBinding([]);
            binding.Initialize(Parts(0, 2), Points); owner.Step(1f / 120);
            var before = Assert.Single(binding.Sample()).Positions;
            Assert.Throws<ArgumentException>(() => binding.Initialize(Parts(0, 2), invalid));
            Assert.Equal(before, Assert.Single(binding.Sample()).Positions);
            owner.Step(1f / 120); Assert.NotEqual(before[5], Assert.Single(binding.Sample()).Positions[5]);
        }
        else
        {
            using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
            backend.Initialize(Parts(0, 2), []); backend.Reset(Points, []); backend.Step(1f / 120, Points, []);
            var before = Assert.Single(backend.Sample()).Positions;
            Assert.Throws<ArgumentException>(() => backend.Reset(invalid, []));
            Assert.Equal(before, Assert.Single(backend.Sample()).Positions);
            backend.Step(1f / 120, Points, []); Assert.NotEqual(before[5], Assert.Single(backend.Sample()).Positions[5]);
        }
    }

    private static (int[] Counts, float[] Masses) Topology(SoftBodyBulletBackend backend, int count)
    {
        var world = (SafeHandle)typeof(SoftBodyBulletBackend).GetField("world", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(backend)!;
        var library = NativeLibrary.Load("mmd_bullet_soft", typeof(SoftBodyBulletBackend).Assembly, null);
        try
        {
            var inspect = Marshal.GetDelegateForFunctionPointer<Inspect>(NativeLibrary.GetExport(library, "mmd_soft_topology"));
            var counts = new int[4]; var masses = new float[count];
            Assert.Equal(0, inspect(world.DangerousGetHandle(), 0, counts, masses, count)); return (counts, masses);
        }
        finally { NativeLibrary.Free(library); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Inspect(IntPtr world, int index, [Out] int[] counts, [Out] float[] masses, int count);
}
