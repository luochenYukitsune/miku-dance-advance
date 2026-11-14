using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

// This filter also runs against the archived feature15 DLLs in an isolated output
// directory. Expectations follow advertised capability; no test is skipped.
public sealed class SharedClusterCapabilityContractTests
{
    private static readonly Vector3[] Points = [new(0, 3, 0), new(1, 3, 0), new(0, 3, 1), new(1, 3, 1)];
    private static SoftBodyDefinition Part(string name, int[] ids, bool cluster)
        => new(name, SoftBodyShape.TriMesh, ids, ids.Select(i => Points[i]), [0, 1, 2], [], [],
            new() { Flags = cluster ? 2 : 0, ClusterCount = 1 });

    [Fact]
    public void StandaloneAdvertisedSharedClusterCapabilityControlsReset()
    {
        bool supported = (Features("mmd_bullet_soft") & 16) != 0;
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([Part("left", [0, 1, 2], true), Part("right", [1, 3, 2], true)], []);
        if (supported)
        {
            backend.Reset(Points, []); Assert.Equal(4, Assert.Single(backend.Sample()).VertexIndices.Length);
        }
        else
        {
            Assert.Contains("cluster", Assert.Throws<NotSupportedException>(() => backend.Reset(Points, [])).Message);
            Assert.Null(typeof(SoftBodyBulletBackend).GetField("world", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(backend));
        }
    }

    [Fact]
    public void CoupledMissingSharedClusterCapabilityKeepsTheActiveBinding()
    {
        bool supported = (Features("mmd_bullet") & 16) != 0;
        using var owner = new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null, coupledSoftBodies: true);
        owner.Initialize(new([], [], []), []); using var binding = owner.CreateSoftBodyBinding([]);
        binding.Initialize([Part("plain", [0, 1, 2], false)], Points); owner.Step(1f / 120);
        var before = Assert.Single(binding.Sample()).Positions;
        var clusters = new[] { Part("left", [0, 1, 2], true), Part("right", [1, 3, 2], true) };
        if (supported)
        {
            binding.Initialize(clusters, Points); Assert.Equal(4, Assert.Single(binding.Sample()).VertexIndices.Length);
        }
        else
        {
            Assert.Contains("cluster", Assert.Throws<NotSupportedException>(() => binding.Initialize(clusters, Points)).Message);
            Assert.Equal(before, Assert.Single(binding.Sample()).Positions);
            owner.Step(1f / 120); Assert.True(Assert.Single(binding.Sample()).Positions[0].Y < before[0].Y);
        }
    }

    [Fact]
    public void OrdinarySingletonAndSharedInputsKeepWorkingWithoutBit16()
    {
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        backend.Initialize([Part("single", [0, 1, 2], false)], []); backend.Reset(Points, []);
        backend.Step(1f / 120, Points, []); Assert.True(Assert.Single(backend.Sample()).Positions[0].Y < 3);
        backend.Initialize([Part("left", [0, 1, 2], false), Part("right", [1, 3, 2], false)], []);
        backend.Reset(Points, []); backend.Step(1f / 120, Points, []);
        Assert.Equal(4, Assert.Single(backend.Sample()).VertexIndices.Length);
        Assert.True(Assert.Single(backend.Sample()).Positions[0].Y < 3);
    }

    private static int Features(string name)
    {
        var library = NativeLibrary.Load(name, typeof(SoftBodyBulletBackend).Assembly, null);
        try
        {
            return NativeLibrary.TryGetExport(library, "mmd_soft_features", out var export)
                ? Marshal.GetDelegateForFunctionPointer<FeatureQuery>(export)() : 0;
        }
        finally { NativeLibrary.Free(library); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FeatureQuery();
}
