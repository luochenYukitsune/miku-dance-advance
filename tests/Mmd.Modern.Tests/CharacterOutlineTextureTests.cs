using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Actual modern hull fragments: atlas colour, alpha holes, secondary mask and alpha output.</summary>
[Trait("Category", "Gpu")]
public sealed class CharacterOutlineTextureTests
{
    private const int Width = 257, Height = 191;
    private static readonly Vector4 Explicit = new(1, 0, 1, 1);

    [Fact]
    public void TransparentTextureStripeDoesNotBecomeAnOpaqueHullAndNullRestoresSourceBytes()
    {
        using var session = new D3D12FrameSession();
        var source = Sheet(new("cutout", 3, 1, [255, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 255]), null);
        var baseline = session.Capture(source, Width, Height);
        var modern = WithStyle(source, new(1, Explicit));
        var image = session.Capture(modern, Width, Height);
        Assert.Equal(new byte[4], Pixel(image, Width / 2, Height / 2));
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(image, 100, Height / 2));
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(image, 156, Height / 2));
        Assert.NotEqual(baseline.Rgba, image.Rgba);
        Assert.Equal(baseline.Rgba, session.Capture(WithStyle(modern, null), Width, Height).Rgba);
        long reads = session.Runtime.Readbacks;
        using var rendered = session.Render(modern, Width, Height);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
        Assert.Equal(reads, session.Runtime.Readbacks);
    }

    [Fact]
    public void AutomaticOutlineUsesEachFragmentTextureHueInsteadOfOneMaterialAverage()
    {
        using var session = new D3D12FrameSession();
        var frame = WithStyle(Sheet(new("atlas", 2, 1, [255, 32, 0, 255, 0, 32, 255, 255]), null), new(1));
        var image = session.Capture(frame, Width, Height);
        var first = Pixel(image, 100, Height / 2); var second = Pixel(image, 156, Height / 2);
        Assert.True(first[0] > first[2] + 100 || first[2] > first[0] + 100);
        Assert.Equal(first[0], second[2]); Assert.Equal(first[2], second[0]);
        Assert.Equal(first[1], second[1]); Assert.Equal(255, first[3]); Assert.Equal(255, second[3]);
        Assert.InRange(Math.Max(first[0], first[2]), 146, 147);
        var explicitImage = session.Capture(WithStyle(frame, new(1, Explicit)), Width, Height);
        Assert.Equal(Pixel(explicitImage, 100, Height / 2), Pixel(explicitImage, 156, Height / 2));
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(explicitImage, 100, Height / 2));
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void SecondaryTextureAlphaAndUv1ParticipateInHullCoverage(OriginalSphereMode mode)
    {
        using var session = new D3D12FrameSession();
        // Ordinary sphere projection is the centre (.5,.5), while SubTexture reads UV1=(.75,.5).
        // Both chosen points are in the zero-alpha half, independent of UV0's opaque texture.
        var mask = new SceneTexture("secondary-mask", 2, 1, [255, 255, 255, 255, 255, 255, 255, 0]);
        var frame = WithStyle(Sheet(new("opaque", 1, 1, [255, 255, 255, 255]), mask, mode), new(1, Explicit));
        Assert.Equal(new byte[4], Pixel(session.Capture(frame, Width, Height), Width / 2, Height / 2));
        var opaque = new SceneTexture("secondary-opaque", 2, 1, [255, 255, 255, 255, 255, 255, 255, 255]);
        var control = WithStyle(Sheet(new("opaque", 1, 1, [255, 255, 255, 255]), opaque, mode), new(1, Explicit));
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(session.Capture(control, Width, Height), Width / 2, Height / 2));
    }

    [Fact]
    public void MaterialAndTextureAlphaBlendWhileExplicitColorAlphaRemainsAnIndependentMultiplier()
    {
        using var session = new D3D12FrameSession();
        var source = Sheet(new("half", 1, 1, [255, 255, 255, 128]), null, materialAlpha: .5f);
        var frame = WithStyle(source, new(1, new(1, 0, 1, .5f)));
        var pixel = Pixel(session.Capture(frame, Width, Height), Width / 2, Height / 2);
        // Source-over RGB over black, legacy MAX alpha: .5 material * 128/255 texel * .5 line alpha.
        Assert.InRange(pixel[0], (byte)31, (byte)33); Assert.Equal(0, pixel[1]);
        Assert.Equal(pixel[0], pixel[2]); Assert.InRange(pixel[3], (byte)31, (byte)33);
        var absent = WithStyle(Sheet(null, null), new(1));
        var noTexture = Pixel(session.Capture(absent, Width, Height), Width / 2, Height / 2);
        Assert.InRange(noTexture[0], (byte)146, (byte)147);
        Assert.Equal(noTexture[0], noTexture[1]); Assert.Equal(noTexture[0], noTexture[2]); Assert.Equal(255, noTexture[3]);
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(.5f, false)]
    [InlineData(.5f, true)]
    public void SubTextureHullKeepsOriginalToonAlphaAndTranspOverride(float facing, bool transp)
    {
        using var session = new D3D12FrameSession();
        var source = Sheet(new("diffuse-half", 1, 1, [255, 255, 255, 128]),
            new("sphere-half", 1, 1, [255, 255, 255, 128]), OriginalSphereMode.SubTexture);
        float z = facing / 3;
        var snapshot = source.Surface.Snapshot.WithLighting(new(MathF.Sqrt(1 - z * z), 0, z));
        var binding = source.Surface.Materials[0] with { TextureFactors = new(OriginalTextureFactor.Identity,
            OriginalTextureFactor.Identity, new(new(1, 1, 1, .2f), Vector4.Zero)) };
        var frame = WithStyle(new(new(snapshot, [binding], source.Surface.Profile), source.Outline, source.OutlineOptions,
            source.Options! with { Transp = transp }, source.Materials), new(1, Explicit));
        var pixel = Pixel(session.Capture(frame, Width, Height), Width / 2, Height / 2);
        float alpha = transp ? .5f : (128 / 255f) * (128 / 255f) * (facing + .2f * (1 - facing));
        int expected = (int)MathF.Round(alpha * 255);
        Assert.InRange((int)pixel[0], expected - 1, expected + 1);
        Assert.Equal(0, pixel[1]); Assert.Equal(pixel[0], pixel[2]);
        Assert.InRange((int)pixel[3], expected - 1, expected + 1);
    }

    private static OriginalFrameScene Sheet(SceneTexture? diffuse, SceneTexture? sphere,
        OriginalSphereMode mode = OriginalSphereMode.None, float materialAlpha = 1)
    {
        var box = D3D12OriginalOutlineTests.Box();
        Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        var vertices = positions.Select(p => new SceneVertex(p, Vector3.UnitZ, new((p.X + 1) * .5f, (1 - p.Y) * .5f))).ToArray();
        uint[] indices = [0, 1, 2, 0, 2, 3]; // Back side: the hull renders, and the surface-only entry culls it.
        var textures = new List<SceneTexture>();
        int diffuseIndex = diffuse is null ? -1 : textures.Count;
        if (diffuse is not null) textures.Add(diffuse);
        int sphereIndex = sphere is null ? -1 : textures.Count;
        if (sphere is not null) textures.Add(sphere);
        var additional = mode == OriginalSphereMode.SubTexture ? new SceneAdditionalUvs(1,
            vertices.Select(_ => new SceneAdditionalUvVertex(new(.75f, .5f, 0, 0), Vector4.Zero, Vector4.Zero, Vector4.Zero)).ToArray()) : null;
        var snapshot = new SceneSnapshot(vertices, indices,
            [new("cutout-sheet", 0, indices.Length, new(1, 1, 1, materialAlpha), Vector3.Zero, Vector3.Zero, 1, diffuseIndex)],
            textures, camera: box.Surface.Snapshot.Camera, lightDirection: Vector3.UnitZ, additionalUvs: additional);
        var surface = new OriginalStyleScene(snapshot, [new(Vector3.One, sphereIndex, mode)], box.Surface.Profile);
        var outline = new OriginalOutlineData(vertices.Select(v => new OriginalOutlineVertex(v.Position,
            new(v.Position.X, v.Position.Y, 0), OriginalOutlineVertexMode.Expand, 1, 0)), [new(1, Vector4.One, false)]);
        return new(surface, outline, box.OutlineOptions,
            D3D12OriginalFrameTests.Options() with { BackgroundColor = Vector4.Zero }, [new(false, false, false, SurfaceEnabled: false)]);
    }

    private static OriginalFrameScene WithStyle(OriginalFrameScene frame, CharacterOutlineStyle? style) =>
        new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options,
            frame.Materials.Select(flags => flags with { CharacterOutlineStyle = style }));
    private static byte[] Pixel(RenderedFrame image, int x, int y) => image.Rgba.AsSpan((y * image.Width + x) * 4, 4).ToArray();
}
