using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeLightShadowProjectionTests
{
    [Fact]
    public void PointFacesCoverAllSixAxesAndRespectTheLightPosition()
    {
        var light = Light(SceneLightKind.Point) with { Position = new(3, 5, -7), Range = 20 };
        var matrices = NativeLightShadowProjection.Create(Fixture(), light);
        Vector3[] axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        Assert.Equal(6, matrices.Length);
        for (int face = 0; face < 6; face++)
        {
            var clip = Vector4.Transform(new Vector4(light.Position + axes[face] * 4, 1), matrices[face]);
            Assert.InRange(Math.Abs(clip.X / clip.W), 0, .00001f);
            Assert.InRange(Math.Abs(clip.Y / clip.W), 0, .00001f);
            Assert.InRange(clip.Z / clip.W, .001f, .999f);
            var beyond = Vector4.Transform(new Vector4(light.Position + axes[face] * 21, 1), matrices[face]);
            Assert.True(beyond.Z / beyond.W > 1);
        }
    }

    [Fact]
    public void SpotFollowsRotatedNegativeZAndUsesTheOuterHalfAngle()
    {
        var light = Light(SceneLightKind.Spot) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Range = 20 };
        var matrix = Assert.Single(NativeLightShadowProjection.Create(Fixture(), light));
        Assert.True(Vector3.Distance(-Vector3.UnitX, NativeLightShadowProjection.Direction(light)) < .00001f);
        var center = Vector4.Transform(new Vector4(-4, 0, 0, 1), matrix);
        Assert.InRange(Math.Abs(center.X / center.W), 0, .00001f);
        Assert.InRange(Math.Abs(center.Y / center.W), 0, .00001f);
        var outside = Vector4.Transform(new Vector4(-4, 4, 0, 1), matrix);
        Assert.True(Math.Abs(outside.Y / outside.W) > 1);
    }

    [Fact]
    public void DirectionalProjectionIncludesWorldOnceAndOffCameraGeometry()
    {
        var scene = Fixture();
        var matrix = Assert.Single(NativeLightShadowProjection.Create(scene, Light(SceneLightKind.Directional)));
        foreach (uint index in scene.Indices)
        {
            var p = Vector3.Transform(scene.Vertices[(int)index].Position, scene.World);
            var clip = Vector4.Transform(new Vector4(p, 1), matrix);
            Assert.InRange(clip.X, -.999f, .999f);
            Assert.InRange(clip.Y, -.999f, .999f);
            Assert.InRange(clip.Z, .001f, .999f);
            Assert.Equal(1, clip.W);
        }
        var baked = new SceneSnapshot(scene.Vertices.ToArray().Select(v => v with { Position = Vector3.Transform(v.Position, scene.World) }).ToArray(),
            scene.Indices, scene.Materials, camera: SceneCamera.Default);
        Assert.Equal(matrix, Assert.Single(NativeLightShadowProjection.Create(baked, Light(SceneLightKind.Directional))));
    }

    [Fact]
    public void ConstantLayoutMatchesTheFourLightTwentyFourMatrixShaderContract()
    {
        Assert.Equal(64, Marshal.SizeOf<D3D12OriginalStyleRenderer.NativeLightConstants>());
        Assert.Equal(1824, Marshal.SizeOf<D3D12OriginalStyleRenderer.NativeFrameConstants>());
    }

    private static NativeSceneLight Light(SceneLightKind kind) => new(Guid.NewGuid(), "test", kind, Vector3.Zero, Quaternion.Identity, Vector3.One);
    private static SceneSnapshot Fixture() => new(
        [new(new(-2, -3, -1), Vector3.UnitZ, default), new(new(2, 7, -1), Vector3.UnitZ, default),
         new(new(0, 1, 2), Vector3.UnitZ, default), new(new(40, 5, 10), Vector3.UnitZ, default)],
        [0u, 1u, 2u, 1u, 2u, 3u], [new("all", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 0)],
        world: Matrix4x4.CreateScale(1.3f, .7f, -2) * Matrix4x4.CreateTranslation(20, -4, 8));
}
