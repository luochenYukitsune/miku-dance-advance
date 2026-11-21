using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ModernDirectionalShadowProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposedPropsFitTheSameShadowWithFullCoverageOrStaticGaps(bool gaps)
    {
        SceneSnapshot Compose(bool deform)
        {
            var parts = new List<OriginalFrameScene>();
            for (int i = 0; i < 7; i++)
            {
                float x = i % 2 == 0 ? -20 * i : 3 * i;
                SceneVertex[] vertices = [new(new(x, 0, i), Vector3.UnitY, default),
                    new(new(x + 2, 4, i), Vector3.UnitY, default), new(new(x, 2, i + 2), Vector3.UnitY, default)];
                var part = new SceneSnapshot(vertices, [2u, 0u, 1u],
                    [new("prop", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0)]);
                if (deform && (!gaps || i % 2 != 0))
                {
                    var binding = new SceneSkinBinding(SceneSkinKind.Bdef1, 0, -1, -1, -1, Vector4.UnitX);
                    var model = new SceneSkinningModel(vertices, [binding, binding, binding], [], 1);
                    part = part.WithDeferredDeformation(new(model, [Matrix4x4.Identity], [], [], [], null, () => vertices));
                }
                var outline = new OriginalOutlineData(vertices.Select(v =>
                    new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1, 0)), [new(1, Vector4.One, false)]);
                parts.Add(new(new(part, [new(Vector3.One)]), outline, new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1)));
            }
            return ProjectSceneAssembler.Combine(parts, Vector3.One, -Vector3.UnitZ).Surface.Snapshot;
        }
        var actual = Compose(true);
        Assert.Equal(gaps ? 3 : 7, actual.DeformationRanges.Count);
        Assert.Equal(ModernDirectionalShadowProjection.Fit(Compose(false)), ModernDirectionalShadowProjection.Fit(actual));
    }

    [Theory]
    [InlineData(0, -1, 0)]
    [InlineData(.3f, -.7f, 1)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 1)]
    public void AllSceneGeometryFitsIncludingFarCameraInvisibleCasters(float x, float y, float z)
    {
        var scene = Fixture(new(x, y, z)); var matrix = ModernDirectionalShadowProjection.Fit(scene);
        foreach (uint index in scene.Indices)
        {
            var clip = Vector4.Transform(new Vector4(scene.Vertices[(int)index].Position, 1), matrix);
            var projected = OriginalFrameMath.Project(clip);
            Assert.True(projected.Inside); Assert.Equal(1, clip.W);
            Assert.InRange(projected.Uv.X, .001f, .999f); Assert.InRange(projected.Uv.Y, .001f, .999f);
            Assert.InRange(projected.Depth, .001f, .999f);
        }
    }

    [Fact]
    public void AxisAlignedLightOrdersDepthTowardTheLightAndCentersBounds()
    {
        var scene = Fixture(Vector3.UnitY); var matrix = ModernDirectionalShadowProjection.Fit(scene);
        var low = Vector4.Transform(new Vector4(0, -3, 0, 1), matrix);
        var high = Vector4.Transform(new Vector4(0, 7, 0, 1), matrix);
        Assert.True(high.Z < low.Z);
        // Fixture AABB center is (19,2,4.5), independent of eye/camera defaults.
        var center = Vector4.Transform(new Vector4(19, 2, 4.5f, 1), matrix);
        Assert.InRange(center.X, -1e-6f, 1e-6f); Assert.InRange(center.Y, -1e-6f, 1e-6f);
        Assert.InRange(center.Z, .49999f, .50001f);
    }

    [Fact]
    public void AffineWorldIsAppliedOnceAndCameraChangesDoNotInvalidateFit()
    {
        var shear = Matrix4x4.Identity; shear.M12 = .4f; shear.M23 = -.6f;
        var world = Matrix4x4.CreateScale(-2, .7f, 1.3f) * shear * Matrix4x4.CreateRotationY(.6f) * Matrix4x4.CreateTranslation(13, -4, 8);
        var local = Fixture(new(.3f, .7f, -1), world);
        SceneVertex[] baked = local.Vertices.ToArray().Select(v => v with { Position = Vector3.Transform(v.Position, world) }).ToArray();
        var transformed = new SceneSnapshot(baked, local.Indices, local.Materials, lightDirection: local.LightDirection,
            camera: SceneCamera.Default with { Eye = new(700, 300, -800) });
        var localProjection = ModernDirectionalShadowProjection.Fit(local); var bakedProjection = ModernDirectionalShadowProjection.Fit(transformed);
        for (int i = 0; i < baked.Length; i++)
        {
            var a = Vector4.Transform(new Vector4(local.Vertices[i].Position, 1), localProjection);
            var b = Vector4.Transform(new Vector4(baked[i].Position, 1), bakedProjection);
            Assert.True(Vector4.Distance(a, b) < 1e-5f);
        }
    }

    [Fact]
    public void InvalidPaddingIsRejectedAndPlanarGeometryStillHasDepthRange()
    {
        var scene = Fixture(Vector3.UnitY);
        foreach (float padding in new[] { 0, -1, float.NaN, float.PositiveInfinity, 1.01f })
            Assert.Throws<ArgumentOutOfRangeException>(() => ModernDirectionalShadowProjection.Fit(scene, padding));
        var plane = new SceneSnapshot([new(new(0, 0, 0), Vector3.UnitY, default), new(new(1, 0, 0), Vector3.UnitY, default),
            new(new(0, 0, 1), Vector3.UnitY, default)], [0u, 1u, 2u], [new("plane", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0)], lightDirection: Vector3.UnitY);
        Assert.True(Matrix4x4.Invert(ModernDirectionalShadowProjection.Fit(plane), out _));
    }

    [Fact]
    public void FiniteVerticesWhoseDerivedBoundsOverflowAreRejected()
    {
        var fixture = Fixture(Vector3.UnitY);
        var vertices = fixture.Vertices.ToArray().Select(v => v with { Position = v.Position * 1e20f }).ToArray();
        var huge = new SceneSnapshot(vertices, fixture.Indices, fixture.Materials, lightDirection: Vector3.UnitY);
        Assert.Throws<ArgumentException>(() => ModernDirectionalShadowProjection.Fit(huge));
    }

    private static SceneSnapshot Fixture(Vector3 light, Matrix4x4? world = null) => new(
        [new(new(-2, -3, -1), Vector3.UnitY, default), new(new(2, 7, -1), Vector3.UnitY, default),
         new(new(0, 1, 2), Vector3.UnitY, default), new(new(40, 5, 10), Vector3.UnitY, default)],
        [0u, 1u, 2u, 1u, 2u, 3u], [new("all", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 0)], world: world, lightDirection: light);
}
