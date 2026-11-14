using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SoftSharedNodesTests
{
    private static readonly Vector3[] Points = [new(0, 3, 0), new(1, 3, 0), new(0, 3, 1), new(1, 3, 1)];
    private static SoftBodyDefinition Part(string name, int[] ids, SoftBodySettings? settings = null, int[]? pins = null)
        => new(name, SoftBodyShape.TriMesh, ids, ids.Select(i => Points[i]), [0, 1, 2], pins ?? [], [], settings ?? new());

    [Fact]
    public void SharedMaterialsHaveOneNativeNodeAndRetainMassAndMaterials()
    {
        var a = Part("left", [0, 1, 2], new() { Mass = 3, Stiffness = [.2f, .3f, .4f] });
        var b = Part("right", [1, 3, 2], new() { Mass = 6, Stiffness = [.8f, .7f, .6f] });
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        backend.Initialize([a, b], []); backend.Reset(Points, []);
        var sample = Assert.Single(backend.Sample());
        Assert.Equal(new[] { 0, 1, 2, 3 }, sample.VertexIndices);
        var (counts, masses) = Topology(backend, 0, 4);
        Assert.Equal(new[] { 4, 2, 6, 2 }, counts);
        Assert.Equal(new float[] { 1, 3, 3, 2 }, masses);
        for (int i = 0; i < 60; i++) backend.Step(1f / 120, Points, []);
        Assert.All(backend.Sample()[0].Positions, p => Assert.True(p.Y < 2.9f));
    }

    [Fact]
    public void PinFromEitherMaterialConstrainsTheCommonNodeAndResetReplays()
    {
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        backend.Initialize([Part("left", [0, 1, 2]), Part("right", [1, 3, 2], pins: [1])], []);
        backend.Reset(Points, []);
        for (int i = 0; i < 60; i++) backend.Step(1f / 120, Points, []);
        var expected = Assert.Single(backend.Sample());
        Assert.Equal(Points[1], expected.Positions[1]);
        Assert.True(expected.Positions[3].Y < Points[3].Y-.01f);
        backend.Reset(Points, []);
        for (int i = 0; i < 60; i++) backend.Step(1f / 120, Points, []);
        Assert.Equal(expected.Positions, Assert.Single(backend.Sample()).Positions);
    }

    [Fact]
    public void TransitiveOverlapIsOneComponentAndCoordinateDuplicatesStaySeparate()
    {
        var a = new SoftBodyDefinition("a", SoftBodyShape.Rope, [0, 1], [Points[0], Points[1]], [], [], [], new());
        var b = new SoftBodyDefinition("b", SoftBodyShape.Rope, [1, 2], [Points[1], Points[2]], [], [], [], new());
        var c = new SoftBodyDefinition("c", SoftBodyShape.Rope, [2, 3], [Points[2], Points[3]], [], [], [], new());
        var d = new SoftBodyDefinition("separate IDs", SoftBodyShape.Rope, [4, 5], [Points[0], Points[1]], [], [], [], new());
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([a, d, c, b], []); backend.Reset([.. Points, Points[0], Points[1]], []);
        Assert.Equal(2, backend.Sample().Count);
        Assert.Equal(4, backend.Sample()[0].VertexIndices.Length);
        Assert.Equal(new[] { 4, 5 }, backend.Sample()[1].VertexIndices);
    }

    [Theory]
    [InlineData("Margin")]
    [InlineData("Config")]
    [InlineData("Iterations")]
    [InlineData("CollisionMask")]
    public void BodyWideConflictRejectsWithFieldAndPreservesLiveWorld(string field)
    {
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        var a = Part("left", [0, 1, 2]);
        backend.Initialize([a], []); backend.Reset(Points, []);
        var before = backend.Sample()[0].Positions;
        var conflict = field switch
        {
            "Margin" => new SoftBodySettings { Margin = .5f },
            "Config" => new SoftBodySettings { Config = [1, .5f, 0, 0, 0, 0, .2f, 0, 1, .1f, 1, .7f] },
            "Iterations" => new SoftBodySettings { Iterations = [0, 9, 0, 4] },
            _ => new SoftBodySettings { CollisionMask = 0 }
        };
        var error = Assert.Throws<NotSupportedException>(() => backend.Initialize([a, Part("right", [1, 3, 2], conflict)], []));
        Assert.Contains(field, error.Message); Assert.Equal(before, backend.Sample()[0].Positions);
    }

    [Fact]
    public void SharedRestMismatchAndMixedClusterSwitchRejectBeforeReplacingWorld()
    {
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        var a = Part("left", [0, 1, 2]);
        var mismatch = new SoftBodyDefinition("bad rest", SoftBodyShape.Rope, [1, 3], [new(99), Points[3]], [], [], [], new());
        Assert.Throws<ArgumentException>(() => backend.Initialize([a, mismatch], []));
        var cluster = new SoftBodySettings { Flags = 2, ClusterCount = 1 };
        Assert.Contains("cluster", Assert.Throws<NotSupportedException>(() => backend.Initialize(
            [a, Part("right", [1, 3, 2], cluster)], [])).Message.ToLowerInvariant());
    }

    [Fact]
    public void SparseRopeSourcesKeepBendingInsideTheirOwnGraphs()
    {
        int[] left = [2, 8, 14], right = [14, 20, 26];
        var targets = new Vector3[27];
        for (int i = 0; i < 5; i++) targets[new[] { 2, 8, 14, 20, 26 }[i]] = new(i, 3, 0);
        SoftBodyDefinition Rope(string name, int[] ids) => new(name, SoftBodyShape.Rope, ids,
            ids.Select(i => targets[i]), [], [], [], new() { Flags = 1, BendingDistance = 2 });
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([Rope("left", left), Rope("right", right)], []); backend.Reset(targets, []);
        Assert.Equal(new[] { 2, 8, 14, 20, 26 }, Assert.Single(backend.Sample()).VertexIndices);
        // Two structural links and one local bending link per source; no 8--20 cross-source bending link.
        Assert.Equal(new[] { 5, 0, 6, 2 }, Topology(backend, 0, 5).Counts);
    }

    [Fact]
    public void MeshAndRopeShareOneSparseNodeWithoutAddingRopeFaces()
    {
        var targets = new Vector3[30];
        targets[4] = Points[0]; targets[9] = Points[1]; targets[16] = Points[2]; targets[29] = Points[3];
        var mesh = new SoftBodyDefinition("mesh", SoftBodyShape.TriMesh, [4, 9, 16],
            [targets[4], targets[9], targets[16]], [0, 1, 2], [], [], new());
        var rope = new SoftBodyDefinition("rope", SoftBodyShape.Rope, [16, 29],
            [targets[16], targets[29]], [], [], [], new());
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([mesh, rope], []); backend.Reset(targets, []);
        Assert.Equal(new[] { 4, 9, 16, 29 }, Assert.Single(backend.Sample()).VertexIndices);
        Assert.Equal(new[] { 4, 1, 4, 2 }, Topology(backend, 0, 4).Counts);
    }

    [Fact]
    public void EachSourceStiffnessAffectsTheCommonSeam()
    {
        Vector3 Run(float leftStiffness, float rightStiffness)
        {
            Vector3[] points = Enumerable.Range(0, 5).Select(i => new Vector3(i, 3, 0)).ToArray();
            SoftBodyDefinition Rope(string name, int[] ids, int pin, float stiffness) => new(name, SoftBodyShape.Rope,
                ids, ids.Select(i => points[i]), [], [pin], [], new() { Stiffness = [stiffness, 1, 1] });
            using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
            backend.Initialize([Rope("left", [0, 1, 2], 0, leftStiffness), Rope("right", [2, 3, 4], 4, rightStiffness)], []);
            backend.Reset(points, []);
            for (int i = 0; i < 30; i++) backend.Step(1f / 120, points, []);
            return Assert.Single(backend.Sample()).Positions[2];
        }
        var stiff = Run(1, 1);
        Assert.True(Vector3.Distance(stiff, Run(.01f, 1)) > 1e-5f);
        Assert.True(Vector3.Distance(stiff, Run(1, .01f)) > 1e-5f);
    }

    [Fact]
    public void IdenticalAnchorsDeduplicateButConflictingNearModeRejects()
    {
        SoftBodyDefinition Rope(string name, int[] ids, bool near) => new(name, SoftBodyShape.Rope,
            ids, ids.Select(i => Points[i]), [], [], [new(0, 1, near)], new());
        var collider = new PhysicsBodyDefinition("proxy", -1, PhysicsShapeKind.Sphere, new(.1f), Points[1],
            Vector3.Zero, 0, 0, 0, 0, .5f, 0, ushort.MaxValue, PhysicsBodyMode.Kinematic);
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([Rope("left", [0, 1], false), Rope("right", [1, 2], false)], [collider]);
        backend.Reset(Points, [Matrix4x4.CreateTranslation(Points[1])]);
        var before = Assert.Single(backend.Sample()).Positions;
        var error = Assert.Throws<NotSupportedException>(() => backend.Initialize(
            [Rope("left", [0, 1], false), Rope("right", [1, 2], true)], [collider]));
        Assert.Contains("Near", error.Message);
        Assert.Equal(before, Assert.Single(backend.Sample()).Positions);
    }

    [Fact]
    public void AdapterRetainsBothSourceDefinitionsWhenMaterialsShareGlobalIds()
    {
        var t = PmxSoftBodyTests.MaterialFixture();
        var source = t.SoftBodies[0];
        PmxSoftBody Soft(int material) => new()
        {
            Name = "part"+material, EnglishName = "", Type = source.Type, MaterialIndex = material,
            Group = source.Group, CollisionGroup = source.CollisionGroup, Flags = source.Flags,
            BLinkLength = source.BLinkLength, ClusterCount = source.ClusterCount, TotalMass = source.TotalMass,
            CollisionMargin = source.CollisionMargin, AeroModel = source.AeroModel, Config = source.Config,
            ClusterConfig = source.ClusterConfig, Iterations = source.Iterations, MaterialConfig = source.MaterialConfig,
            Anchors = [], PinVertexIndices = []
        };
        var model = new PmxModel
        {
            Header = t.Header, Name = t.Name, EnglishName = t.EnglishName, Comment = t.Comment, EnglishComment = t.EnglishComment,
            Vertices = t.Vertices, Faces = [new(4, 5, 6), new(5, 3, 6)], Textures = t.Textures,
            Materials = t.Materials, Bones = t.Bones, Morphs = t.Morphs, DisplayFrames = t.DisplayFrames,
            RigidBodies = t.RigidBodies, Joints = t.Joints, SoftBodies = [Soft(0), Soft(1)]
        };
        var adaptation = PmxSoftBodyAdapter.Convert(model);
        Assert.Equal(2, adaptation.Definitions.Count);
        Assert.Equal(new[] { 4, 5, 6 }, adaptation.Definitions[0].VertexIndices);
        Assert.Equal(new[] { 5, 3, 6 }, adaptation.Definitions[1].VertexIndices);
    }

    [Fact]
    public void PlaybackRejectsDuplicateBackendOwnershipInsteadOfLastWriter()
    {
        var model = PmxSoftBodyTests.MaterialFixture();
        using var playback = new PmxSoftBodyPlayback(model, new DuplicateBackend());
        var skinned = model.Vertices.Select(v => new SceneVertex(v.Position, v.Normal, v.Uv)).ToArray();
        playback.Reset(skinned);
        Assert.Throws<InvalidOperationException>(() => playback.Apply(skinned));
    }

    private sealed class DuplicateBackend : ISoftBodyBackend
    {
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders) { }
        public void Reset(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Matrix4x4> rigidWorlds) { }
        public void Step(float seconds, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Matrix4x4> rigidWorlds) { }
        public IReadOnlyList<SoftBodySample> Sample() => [new([4], [Vector3.Zero], [Vector3.UnitY]), new([4], [Vector3.One], [Vector3.UnitY])];
        public void SetGravity(Vector3 gravity) { }
        public void Dispose() { }
    }

    private static (int[] Counts, float[] Masses) Topology(SoftBodyBulletBackend backend, int index, int count)
    {
        var world = (SafeHandle)typeof(SoftBodyBulletBackend).GetField("world", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(backend)!;
        var library = NativeLibrary.Load("mmd_bullet_soft", typeof(SoftBodyBulletBackend).Assembly, null);
        try
        {
            var inspect = Marshal.GetDelegateForFunctionPointer<Inspect>(NativeLibrary.GetExport(library, "mmd_soft_topology"));
            var counts = new int[4]; var masses = new float[count];
            Assert.Equal(0, inspect(world.DangerousGetHandle(), index, counts, masses, count));
            return (counts, masses);
        }
        finally { NativeLibrary.Free(library); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Inspect(IntPtr world, int index, [Out] int[] counts, [Out] float[] masses, int count);
}
