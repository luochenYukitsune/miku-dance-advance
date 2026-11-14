using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12ModernShadowProjectionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(4, 13)]
    public void ActualLightDirectionAndWorldFitRetainCameraInvisibleCaster(float casterX, float worldX)
    {
        var original = D3D12OriginalFrameTests.Shadow(true); var mesh = original.Surface.Snapshot;
        Vector3 incoming = Vector3.Normalize(new Vector3(casterX, 0, 1));
        var vertices = mesh.Vertices.ToArray();
        for (int i = 0; i < vertices.Length; i++) vertices[i] = vertices[i] with
        {
            Position = vertices[i].Position + (i >= 4 ? new Vector3(casterX, 0, 0) : Vector3.Zero), Normal = incoming
        };
        var world = Matrix4x4.CreateTranslation(worldX, 0, 0);
        var camera = mesh.Camera with { Eye = mesh.Camera.Eye + new Vector3(worldX, 0, 0), Target = mesh.Camera.Target + new Vector3(worldX, 0, 0) };
        var surface = new OriginalStyleScene(new(vertices, mesh.Indices, mesh.Materials, world: world, camera: camera, lightDirection: incoming),
            original.Surface.Materials, original.Surface.Profile);
        var shadow = original.Options!.SelfShadow! with { LightViewProjection = ModernDirectionalShadowProjection.Fit(surface.Snapshot) };
        var frame = new OriginalFrameScene(surface, options: original.Options with { SelfShadow = shadow }, materials: original.Materials);
        using var renderer = new D3D12FrameSession();
        var image = renderer.Capture(frame, 257, 191);
        D3D12OriginalFrameTests.AssertPixel(image, 128, 95, new(.2f, .2f, .2f, 1));
        if (casterX == 4)
        {
            Assert.Equal(1, renderer.VisibilityStatistics!.SurfaceCulled);
            Assert.Equal(1, renderer.VisibilityStatistics.ShadowDrawn);
        }
        var noCaster = new OriginalFrameScene(surface, options: frame.Options,
            materials: [frame.Materials[0], frame.Materials[1] with { CastSelfShadow = false }]);
        var lit = renderer.Capture(noCaster, 257, 191);
        D3D12OriginalFrameTests.AssertPixel(lit, 128, 95, Vector4.One);
        Assert.NotEqual(image.Rgba, lit.Rgba);
        D3D12OriginalFrameTests.Save($"modern-fit-caster-{casterX}-world-{worldX}", image);
    }
}
