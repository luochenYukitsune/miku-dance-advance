using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12MeshTests
{
    [Fact]
    public void VerticalToonAndTransparentCutoutHaveActualPixelResponse()
    {
        using var renderer = new D3D12MeshRenderer();
        var quad = Quad();
        SceneMaterial material = new("vertical toon", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, -1, 0,
            ToonUvTransform: new(0, -1, 0, 1));
        var vertical = new SceneSnapshot(quad.Vertices, quad.Indices, [material],
            [new("vertical toon", 1, 2, [255,0,0,255, 0,255,0,255])], camera: quad.Camera);
        Assert.Equal((255, 0, 0), Pixel(renderer.Render(vertical, 257, 191), 128, 95));
        var cutout = new SceneSnapshot(quad.Vertices, quad.Indices, [material with { DiffuseTexture = 1 }],
            [vertical.Textures[0],new("fully transparent", 1, 1, [255,255,255,0])], camera: quad.Camera);
        Assert.Equal((20, 24, 32), Pixel(renderer.Render(cutout, 257, 191), 128, 95));
    }

    [Fact]
    public void IndexedMeshSamplesUvToonAndDepthAtUnalignedWidthAndRepeatsExactly()
    {
        using var renderer = new D3D12MeshRenderer();
        var first = renderer.Render(Quad(), 257, 191);
        Assert.Equal(1, first.DrawCalls);
        Assert.Equal(6, renderer.LastIndexCount);
        Assert.Equal(first.Rgba, renderer.Render(Quad(), 257, 191).Rgba);
        Assert.NotEqual(first.Rgba, renderer.Render(Quad(flipUv: true), 257, 191).Rgba);
        Assert.NotEqual(first.Rgba, renderer.Render(Quad(darkToon: true), 257, 191).Rgba);
        // The existing RH view looks from negative Z, so screen-left maps to world-positive X.
        // Fixed top-quadrant samples prove addressing, not merely a changed frame hash.
        var left = Pixel(first, 95, 70); var right = Pixel(first, 161, 70);
        Assert.True(left.G > 200 && left.R < 10, $"left {left}");
        Assert.True(right.R > 200 && right.G < 10, $"right {right}");
        Assert.Equal(left, Pixel(renderer.Render(Quad(flipUv: true), 257, 191), 161, 70));
        var depth = renderer.Render(Quad(withOccludedTriangle: true), 257, 191);
        Assert.Equal(Pixel(first, 128, 95), Pixel(depth, 128, 95));
        Assert.Equal(2, depth.DrawCalls);
        var wide = renderer.Render(Quad(largeIndices: true), 257, 191);
        Assert.Equal(first.Rgba, wide.Rgba);
        Assert.Equal(65540, renderer.LastVertexCount);
    }

    private static (byte R, byte G, byte B) Pixel(RenderedFrame f, int x, int y)
    { int i = (y * f.Width + x) * 4; return (f.Rgba[i], f.Rgba[i + 1], f.Rgba[i + 2]); }

    public static SceneSnapshot Quad(bool flipUv = false, bool darkToon = false, bool largeIndices = false, bool withOccludedTriangle = false)
    {
        int offset = largeIndices ? 65536 : 0;
        var vertices = new SceneVertex[offset + (withOccludedTriangle ? 7 : 4)];
        Array.Fill(vertices, new SceneVertex(Vector3.Zero, -Vector3.UnitZ, Vector2.Zero));
        Vector3[] p = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        Vector2[] uv = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
        for (int i = 0; i < 4; i++) vertices[offset + i] = new(p[i], -Vector3.UnitZ, flipUv ? new(1 - uv[i].X, uv[i].Y) : uv[i]);
        uint o = (uint)offset;
        uint[] indices = [o, o+1, o+2, o, o+2, o+3];
        SceneMaterial[] materials = [new("checker", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, 0, 1)];
        if (withOccludedTriangle)
        {
            vertices[4] = new(new(-1, -1, 1), -Vector3.UnitZ, Vector2.Zero);
            vertices[5] = new(new(1, -1, 1), -Vector3.UnitZ, Vector2.Zero);
            vertices[6] = new(new(0, 1, 1), -Vector3.UnitZ, Vector2.Zero);
            indices = [0, 1, 2, 0, 2, 3, 4, 5, 6];
            materials = [materials[0], new("behind", 6, 3, new(0,0,1,1), Vector3.Zero, Vector3.Zero, 1, -1, 1)];
        }
        byte value = darkToon ? (byte)64 : (byte)255;
        SceneTexture[] textures = [new("checker", 2, 2, [255,0,0,255, 0,255,0,255, 0,0,255,255, 255,255,0,255]), new("toon", 1, 1, [value,value,value,255])];
        return new(vertices, indices, materials, textures, camera: new(new(0,0,-5), Vector3.Zero, Vector3.UnitY, 4, .1f, 20));
    }
}
