using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiTextureStorageTests
{
    [Fact]
    public void DeformationAndWorldChangeReusePixelsButRebuildTransportPositions()
    {
        var snapshot = Mesh([new SceneTexture("rgba", 2, 1, [1,2,3,4, 5,6,7,8])]);
        var first = Transport(snapshot);
        var moved = snapshot.WithDeformedVertices(snapshot.Vertices.ToArray().Select(v =>
            v with { Position = v.Position + Vector3.UnitX }).ToArray());
        var second = Transport(moved.WithWorld(Matrix4x4.CreateTranslation(0, 3, 0)));
        Assert.NotEqual(first.GeometryIdentity, second.GeometryIdentity);
        Assert.NotEqual(first.Triangles[0].P0, second.Triangles[0].P0);
        Assert.Equal(first.TexelIdentity, second.TexelIdentity);
        Assert.Same(Pixels(first), Pixels(second));
        Assert.Equal(new uint[] { 0xffffffff, 0x04030201, 0x08070605 }, second.Texels.ToArray());
        Assert.Equal(first.Materials[0].DiffuseTexture, second.Materials[0].DiffuseTexture);
    }

    [Fact]
    public void NewTextureOwnerAndReorderingCannotReuseAnOldPackedTable()
    {
        var red = new SceneTexture("same-name", 1, 1, [255,0,0,255]);
        var blue = new SceneTexture("same-name", 1, 1, [0,0,255,255]);
        var first = Transport(Mesh([red, blue]));
        var reordered = Transport(Mesh([blue, red]));
        var replacement = Transport(Mesh([new SceneTexture("same-name", 1, 1, [0,255,0,255]), blue]));
        Assert.NotEqual(first.TexelIdentity, reordered.TexelIdentity);
        Assert.NotEqual(first.TexelIdentity, replacement.TexelIdentity);
        Assert.Equal(new Vector4(1, 0, 0, 1), first.SampleTexture(first.Materials[0].DiffuseTexture, Vector2.Zero));
        Assert.Equal(new Vector4(0, 0, 1, 1), reordered.SampleTexture(reordered.Materials[0].DiffuseTexture, Vector2.Zero));
        Assert.Equal(new Vector4(0, 1, 0, 1), replacement.SampleTexture(replacement.Materials[0].DiffuseTexture, Vector2.Zero));
    }

    [Fact]
    public void NextPoseDoesNotAllocateAnotherLargePixelTable()
    {
        var snapshot = Mesh([new SceneTexture("large", 512, 512, new byte[512 * 512 * 4])]);
        var first = Transport(snapshot);
        var moved = snapshot.WithDeformedVertices(snapshot.Vertices.ToArray().Select(v =>
            v with { Position = v.Position + Vector3.UnitZ }).ToArray());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var second = Transport(moved);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 128 * 1024, $"Next tiny pose allocated {allocated} bytes with unchanged 1MiB pixels.");
        Assert.Same(Pixels(first), Pixels(second));
    }

    [Fact]
    public void ConcurrentPosesShareTheOneLiveTextureOwner()
    {
        var snapshot = Mesh([new SceneTexture("rgba", 1, 1, [1,2,3,255])]);
        var scenes = Enumerable.Range(0, 16).AsParallel().Select(i =>
            Transport(snapshot.WithWorld(Matrix4x4.CreateTranslation(i, 0, 0)))).ToArray();
        foreach (var scene in scenes)
        {
            Assert.Same(Pixels(scenes[0]), Pixels(scene));
            Assert.Equal(scenes[0].TexelIdentity, scene.TexelIdentity);
        }
    }

    [Fact]
    public void CacheDoesNotRootRetiredSnapshotTextureListOrPackedPixels()
    {
        var retired = Retired();
        for (int i = 0; i < 4 && retired.Any(reference => reference.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(retired, reference => Assert.False(reference.IsAlive));
    }

    [Fact]
    public void PublicTransportKeepsPackedRowsWithoutRootingTheSource()
    {
        var (scene, retired) = MaterializedTransport();
        for (int i = 0; i < 4 && retired.Any(reference => reference.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(retired, reference => Assert.False(reference.IsAlive));
        Assert.Equal(1, scene.Triangles.Length);
        Assert.Equal(2, scene.Texels.Length);
        GC.KeepAlive(scene);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RayGiScene Scene, WeakReference[] Retired) MaterializedTransport()
    {
        var snapshot = Mesh([new SceneTexture("temporary", 1, 1, [1,2,3,255])]);
        return (Transport(snapshot), [new(snapshot), new(snapshot.Textures), new(snapshot.Textures[0])]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Retired()
    {
        var snapshot = Mesh([new SceneTexture("temporary", 1, 1, [1,2,3,255])]);
        var scene = Transport(snapshot);
        return [new(snapshot), new(snapshot.Textures), new(scene), new(Pixels(scene))];
    }

    private static object Pixels(RayGiScene scene) => typeof(RayGiScene)
        .GetField("texels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;
    private static RayGiScene Transport(SceneSnapshot snapshot)
        => RayGiScene.FromOriginal(new OriginalStyleScene(snapshot, [new(Vector3.One)]));
    private static SceneSnapshot Mesh(SceneTexture[] textures)
        => new([new(new(-1,-1,0), Vector3.UnitZ, Vector2.Zero),
            new(new(1,-1,0), Vector3.UnitZ, Vector2.UnitX), new(new(0,1,0), Vector3.UnitZ, Vector2.UnitY)],
            [0,1,2], [new("surface", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0)], textures);
}
