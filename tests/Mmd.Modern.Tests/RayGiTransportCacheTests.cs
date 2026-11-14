using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiTransportCacheTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object Storage(RayGiScene scene, string name) => typeof(RayGiScene).GetField(name, Private)!.GetValue(scene)!;
    private static void AssertSharedTriangles(RayGiScene first, RayGiScene second)
        => Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(first.Triangles), ref MemoryMarshal.GetReference(second.Triangles)));

    [Fact]
    public void ContinuousSphereRotationSharesStaticTransportAndBoundsCpuEntries()
    {
        var style = RayGiBvhTests.Style();
        var initial = RayGiScene.FromOriginal(style);
        var firstBvhNodes = typeof(RayGiBvh).GetField("nodes", Private)!.GetValue(initial.Bvh);
        for (int i = 1; i <= 128; i++)
        {
            var scene = RayGiScene.FromOriginal(new OriginalFrameScene(style, options: new(OriginalAlphaBlendMode.Normal, true, false, SphereRotationOverride: Matrix4x4.CreateRotationY(i * .01f))));
            Assert.Equal(initial.GeometryIdentity, scene.GeometryIdentity);
            AssertSharedTriangles(initial, scene);
            Assert.Same(Storage(initial, "materials"), Storage(scene, "materials"));
            Assert.Same(Storage(initial, "texels"), Storage(scene, "texels"));
            Assert.Same(firstBvhNodes, typeof(RayGiBvh).GetField("nodes", Private)!.GetValue(scene.Bvh));
        }
        var table = typeof(RayGiScene).GetField("SceneCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object?[] args = [style, null];
        Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
        var entries = (ICollection)args[1]!.GetType().GetField("entries", Private)!.GetValue(args[1])!;
        Assert.InRange(entries.Count, 1, 4);
    }

    [Fact]
    public void RawWorldNormalsMatchRasterInterpolationUnderNonUniformWorld()
    {
        var normals = new[] { Vector3.Normalize(new Vector3(1, 0, 1)), Vector3.Normalize(new Vector3(0, 1, 1)), Vector3.UnitZ };
        var world = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateRotationY(.3f);
        var style = Style(normals, world);
        var scene = RayGiScene.FromOriginal(style);
        var triangle = scene.Triangles[0];
        Matrix4x4.Invert(world, out var inverse);
        var normalWorld = Matrix4x4.Transpose(inverse);
        Vector3 Transform(int i) => Vector3.TransformNormal(normals[i], normalWorld);
        Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);
        Assert.Equal(Transform(0), Xyz(triangle.N0U0));
        Assert.Equal(Transform(1), Xyz(triangle.N1U1));
        Assert.Equal(Transform(2), Xyz(triangle.N2U2));
        var bary = new Vector3(.2f, .3f, .5f);
        var expected = Vector3.Normalize(Transform(0) * bary.X + Transform(1) * bary.Y + Transform(2) * bary.Z);
        var actual = Vector3.Normalize(Xyz(triangle.N0U0) * bary.X + Xyz(triangle.N1U1) * bary.Y + Xyz(triangle.N2U2) * bary.Z);
        Assert.Equal(expected, actual);
        var prematurelyNormalized = Vector3.Normalize(Vector3.Normalize(Transform(0)) * bary.X + Vector3.Normalize(Transform(1)) * bary.Y + Vector3.Normalize(Transform(2)) * bary.Z);
        Assert.True(Vector3.Distance(expected, prematurelyNormalized) > .01f);
    }

    [Fact]
    public void SphereSourceNormalsRemainRawAndEligibilityOnlyChangesMaterialTable()
    {
        Vector3[] normals = [new(2, 0, 1), new(0, 3, 1), new(0, 0, 4)];
        var style = Style(normals, Matrix4x4.Identity);
        var first = RayGiScene.FromOriginal(style);
        var disabled = RayGiScene.FromOriginal(new OriginalFrameScene(style, materials: [new(false, false, false, SurfaceEnabled: false)]));
        AssertSharedTriangles(first, disabled);
        Assert.Same(Storage(first, "texels"), Storage(disabled, "texels"));
        Assert.NotSame(Storage(first, "materials"), Storage(disabled, "materials"));
        var triangle = first.Triangles[0];
        Assert.Equal(1u, triangle.Meta.Z);
        Assert.Equal(new Vector4(2, 0, 1, 0), triangle.SphereUv01);
        Assert.Equal(new Vector4(3, 1, 0, 0), triangle.SphereUv2);
        Assert.Equal(4, triangle.V012.W);
    }

    [Fact]
    public void SphereRotationChangesCpuVisibilityWithoutRebuildingTransport()
    {
        Vector3[] normals = [new(2, 0, 1), new(2, 0, 1), new(2, 0, 1)];
        var baseStyle = Style(normals, Matrix4x4.Identity);
        var texture = new SceneTexture("sphere-alpha", 2, 1, [255,255,255,0, 255,255,255,255]);
        var snapshot = new SceneSnapshot(baseStyle.Snapshot.Vertices, baseStyle.Snapshot.Indices, baseStyle.Snapshot.Materials, [texture]);
        var style = new OriginalStyleScene(snapshot, [new(Vector3.One, 0, OriginalSphereMode.Multiply)],
            OriginalStyleProfile.SelectedReference with { TextureSampling = OriginalTextureSampling.PointClamp });
        var first = RayGiScene.FromOriginal(style);
        var rotation = Matrix4x4.CreateRotationY(MathF.PI);
        var rotated = RayGiScene.FromOriginal(new OriginalFrameScene(style, options: new(OriginalAlphaBlendMode.Normal, true, false, SphereRotationOverride: rotation)));
        var ray = new RayGiRay(new(0, 0, 2), -Vector3.UnitZ, 0, 5);
        Assert.True(first.Bvh.TraceAny(ray));
        Assert.False(rotated.Bvh.TraceAny(ray));
        Assert.Equal(OriginalStyleMath.SphereUv(normals[0], rotation), rotated.SphereUv(0, new(.25f, .5f)));
        AssertSharedTriangles(first, rotated);
        Assert.Equal(first.MaterialIdentity, rotated.MaterialIdentity);
        Assert.Equal(first.TexelIdentity, rotated.TexelIdentity);
        var baked = new RayGiTriangleGpu(new(0,0,0,0), new(1,0,0,0), new(0,1,0,0),
            Vector4.UnitZ, Vector4.UnitZ, Vector4.UnitZ, Vector4.Zero, new(.125f,.25f,.375f,.5f), new(.625f,.75f,0,0), default);
        var explicitFixture = new RayGiScene([baked], first.Materials, first.Texels);
        Assert.Equal(new Vector2(.4375f,.5625f), explicitFixture.SphereUv(0, new(.25f,.5f)));
    }

    [Fact]
    public void MaterialAlphaAboveOneIsClampedBeforeTextureAlphaLikeRasterHost()
    {
        var texture = new SceneTexture("half-alpha", 1, 1, [255,255,255,128]);
        var style = RayGiBvhTests.Style(alpha: 2, texture: texture);
        var snapshot = new SceneSnapshot(style.Snapshot.Vertices, style.Snapshot.Indices,
            style.Snapshot.Materials.Select(m => m with { AlphaCutoff = .75f }), style.Snapshot.Textures);
        style = new(snapshot, style.Materials, style.Profile);
        var scene = RayGiScene.FromOriginal(style);
        Assert.Equal(OriginalStyleMath.ResolveHostColors(snapshot.Materials[0], style.Profile).EgColor.W, scene.Materials[0].DiffuseFactor.W);
        Assert.Equal(Vector3.One, new Vector3(scene.Materials[0].DiffuseFactor.X, scene.Materials[0].DiffuseFactor.Y, scene.Materials[0].DiffuseFactor.Z));
        Assert.False(scene.Bvh.TraceAny(new(new(0, 0, 2), -Vector3.UnitZ, 0, 5)));
    }

    private static OriginalStyleScene Style(Vector3[] normals, Matrix4x4 world)
    {
        var vertices = new[] { new SceneVertex(new(-1,-1,0), normals[0], Vector2.Zero), new SceneVertex(new(1,-1,0), normals[1], Vector2.UnitX), new SceneVertex(new(0,1,0), normals[2], Vector2.UnitY) };
        var snapshot = new SceneSnapshot(vertices, [0,1,2], [new("surface", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], world: world);
        return new(snapshot, [new(Vector3.One)]);
    }
}
