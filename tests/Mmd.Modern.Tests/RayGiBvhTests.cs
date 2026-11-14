using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiBvhTests
{
    [Fact]
    public void FrozenGpuLayoutsHaveExplicitRows()
    {
        Assert.Equal(16, Marshal.SizeOf<RayGiUInt4>());
        Assert.Equal(160, Marshal.SizeOf<RayGiTriangleGpu>());
        Assert.Equal(96, Marshal.SizeOf<RayGiMaterialGpu>());
        Assert.Equal(48, Marshal.SizeOf<RayGiBvhNodeGpu>());
        Assert.Equal(144, Marshal.OffsetOf<RayGiTriangleGpu>(nameof(RayGiTriangleGpu.Meta)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<RayGiMaterialGpu>(nameof(RayGiMaterialGpu.Flags)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<RayGiBvhNodeGpu>(nameof(RayGiBvhNodeGpu.Data)).ToInt32());
    }

    [Fact]
    public void AdapterKeepsGlobalIdsTransformsWorldAndCachesImmutableIdentity()
    {
        var style = Style(world: Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(7, 8, 9));
        var scene = RayGiScene.FromOriginal(style);
        Assert.Same(scene, RayGiScene.FromOriginal(style));
        Assert.Same(scene.Bvh, scene.Bvh);
        Assert.Equal(new Vector4(5, 5, 9, 0), scene.Triangles[0].P0);
        Assert.Equal(0u, scene.Triangles[0].Meta.Y);
        Assert.Equal(1u, scene.Triangles[1].Meta.Y);
        Assert.Equal(0xffffffffu, scene.Texels[0]);
        Assert.Equal(new Vector3(0, 0, .25f), new Vector3(scene.Triangles[0].N0U0.X, scene.Triangles[0].N0U0.Y, scene.Triangles[0].N0U0.Z));
    }

    [Fact]
    public void ClosestMissBarycentricsAndNonUnitDirectionMatchBruteForce()
    {
        var scene = RayGiScene.FromOriginal(Style());
        var ray = new RayGiRay(new(.1f, .2f, 5), -Vector3.UnitZ, .001f, 100);
        Assert.True(scene.Bvh.TraceClosest(ray, out var hit));
        Assert.Equal(0u, hit.TriangleIndex);
        Assert.InRange(MathF.Abs(hit.T - 5), 0, 1e-5f);
        Assert.InRange(MathF.Abs(hit.Barycentrics.X - .25f), 0, 1e-5f);
        Assert.InRange(MathF.Abs(hit.Barycentrics.Y - .6f), 0, 1e-5f);
        Assert.False(scene.Bvh.TraceAny(ray with { Origin = new(20, 20, 5) }));
        var random = new Random(1729);
        for (int i = 0; i < 400; i++)
        {
            var r = new RayGiRay(new((float)random.NextDouble() * 5 - 2.5f, (float)random.NextDouble() * 5 - 2.5f, 5), new(0, 0, -2), 0, 100);
            bool expected = scene.Bvh.TraceBruteForce(r, out var oracle);
            Assert.Equal(expected, scene.Bvh.TraceClosest(r, out var actual));
            if (expected) { Assert.Equal(oracle.TriangleIndex, actual.TriangleIndex); Assert.InRange(MathF.Abs(oracle.T - actual.T), 0, 1e-5f); }
        }
    }

    [Fact]
    public void EmptyEligibleDegenerateAndHugeCoordinatesAreExplicit()
    {
        var disabled = RayGiScene.FromOriginal(new OriginalFrameScene(Style(), materials: [new(false,false,false,SurfaceEnabled:false)]));
        Assert.NotEmpty(disabled.Bvh.Nodes.ToArray());
        Assert.False(disabled.Bvh.TraceAny(new(new(0, 0, 5), -Vector3.UnitZ, 0, 100)));
        var degenerate = Style();
        var originalTriangle = RayGiScene.FromOriginal(degenerate).Triangles[0];
        var triangle = new RayGiTriangleGpu(originalTriangle.P0, new(-1, -1, 0, 0), new(-1, -1, 0, 0),
            originalTriangle.N0U0, originalTriangle.N1U1, originalTriangle.N2U2, originalTriangle.V012,
            originalTriangle.SphereUv01, originalTriangle.SphereUv2, originalTriangle.Meta);
        var empty = new RayGiScene([triangle], RayGiScene.FromOriginal(degenerate).Materials, [0xffffffff]);
        Assert.Empty(empty.Bvh.Nodes.ToArray());
        var large = RayGiScene.FromOriginal(Style(world: Matrix4x4.CreateTranslation(1e15f, 0, 0)));
        Assert.True(large.Bvh.MaximumDepth <= RayGiBvh.MaximumAllowedDepth);
        Assert.Throws<ArgumentException>(() => disabled.Bvh.TraceAny(new(Vector3.Zero, Vector3.Zero, 0, 1)));
    }

    [Fact]
    public void AlphaCutoutAndSidednessUseSelectedSamplingAndKeepOffscreenGeometry()
    {
        var texture = new SceneTexture("cutout", 2, 1, [255,0,0,0, 0,255,0,255]);
        var scene = RayGiScene.FromOriginal(Style(texture: texture));
        Assert.Equal(new Vector4(1, 0, 0, 0), scene.SampleTexture(scene.Materials[0].DiffuseTexture, new(.1f, .5f)));
        Assert.False(scene.Bvh.TraceAny(new(new(-.8f, -.8f, 2), -Vector3.UnitZ, 0, 20)));
        Assert.True(scene.Bvh.TraceAny(new(new(.8f, -.8f, 2), -Vector3.UnitZ, 0, 20)));
        Assert.False(scene.Bvh.TraceAny(new(new(.8f, -.8f, -2), Vector3.UnitZ, 0, 20)));
        var both = RayGiScene.FromOriginal(Style(texture: texture, doubleSided: true));
        Assert.True(both.Bvh.TraceAny(new(new(.8f, -.8f, -2), Vector3.UnitZ, 0, 20)));
        Assert.True(scene.Bvh.TraceAny(new(new(.8f, -.8f, 200), -Vector3.UnitZ, 0, 400)));
    }

    internal static OriginalStyleScene Style(Matrix4x4? world = null, float alpha = 1, SceneTexture? texture = null, bool doubleSided = false)
    {
        SceneVertex[] vertices = [new(new(-1,-1,0), Vector3.UnitZ, new(0,0)), new(new(1,-1,0), Vector3.UnitZ, new(1,0)), new(new(0,1,0), Vector3.UnitZ, new(.5f,1)), new(new(-1,-1,-3),Vector3.UnitZ,Vector2.Zero), new(new(1,-1,-3),Vector3.UnitZ,Vector2.UnitX),new(new(0,1,-3),Vector3.UnitZ,Vector2.UnitY)];
        var material = new SceneMaterial("surface", 0, 6, new(1,1,1,alpha), Vector3.Zero, Vector3.Zero, 1, texture is null ? -1 : 0, AlphaCutoff: .5f);
        var snapshot = new SceneSnapshot(vertices, [0,1,2,3,4,5], [material], texture is null ? [] : [texture], world);
        return new(snapshot, [new(Vector3.One, DoubleSided: doubleSided)], OriginalStyleProfile.SelectedReference with { TextureSampling = OriginalTextureSampling.PointClamp });
    }
}
