using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Finite headless readbacks of the independent material multiplier, without GI or a window.</summary>
[Trait("Category", "Gpu")]
public sealed class OutlineCaptureTests
{
    private const int Width = 257, Height = 191;
    private static readonly Guid Actor = Guid.Parse("aece9815-068b-4d93-8f65-25774f352918");
    private static readonly Vector4 Red = new(1, 0, 0, 1), Blue = new(0, 0, 1, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuarterMultiplierMatchesAnIndependentlyReducedWidthAndRestoresTheSameSession(bool modern)
    {
        var box = D3D12OriginalOutlineTests.Box();
        var source = new OriginalFrameScene(box.Surface,
            new OriginalOutlineData(box.Outline!.Vertices, [new(2, Red)]), box.OutlineOptions,
            D3D12OriginalFrameTests.Options(),
            [new(false, false, false) { CharacterOutlineStyle = modern ? new(2, Red) : null }]);
        var baseline = Configured(source, 1);
        var quarter = Configured(source, .25f);
        var manual = ManualWidth(baseline, 0, .25f);
        using var session = new D3D12FrameSession();
        var fullPixels = session.Capture(baseline, Width, Height, RayGiOptions.Off);
        var quarterPixels = session.Capture(quarter, Width, Height, RayGiOptions.Off);
        var manualPixels = session.Capture(manual, Width, Height, RayGiOptions.Off);
        var restoredPixels = session.Capture(baseline, Width, Height, RayGiOptions.Off);
        Save(modern ? "modern-box" : "original-box", session, fullPixels, quarterPixels, manualPixels, restoredPixels);

        Assert.Equal(manualPixels.Rgba, quarterPixels.Rgba);
        Assert.NotEqual(fullPixels.Rgba, quarterPixels.Rgba);
        Assert.True(CountColor(quarterPixels, Red) > 0, "The quarter-width control must retain an actual visible contour.");
        Assert.True(CountColor(fullPixels, Red) > CountColor(quarterPixels, Red));
        Assert.Equal(fullPixels.Rgba, restoredPixels.Rgba);
        session.WaitForIdle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScalingOneSharedVertexMaterialPreservesTheOtherDrawAndMatchesIndependentTriangleWidths(bool modern)
    {
        var source = SharedTriangles(modern);
        var baseline = Configured(source, 1);
        var quarter = Configured(source, .25f);
        // The oracle duplicates the two triangles so their selected-material width tables are
        // independent. At full size this produces the same expansion as the shared source;
        // only the first triangle's actual width is reduced for the expected quarter image.
        var manual = IndependentTriangles(baseline, modern);
        using var session = new D3D12FrameSession();
        var fullPixels = session.Capture(baseline, Width, Height, RayGiOptions.Off);
        var quarterPixels = session.Capture(quarter, Width, Height, RayGiOptions.Off);
        var manualPixels = session.Capture(manual, Width, Height, RayGiOptions.Off);
        var restoredPixels = session.Capture(baseline, Width, Height, RayGiOptions.Off);
        Save(modern ? "modern-shared" : "original-shared", session, fullPixels, quarterPixels, manualPixels, restoredPixels);

        Assert.Equal(manualPixels.Rgba, quarterPixels.Rgba);
        Assert.NotEqual(fullPixels.Rgba, quarterPixels.Rgba);
        Assert.True(CountColor(fullPixels, Red) > 100);
        Assert.True(CountColor(fullPixels, Blue) > 100);
        // Blue is the second draw, so every existing blue pixel belongs to the untouched
        // triangle and cannot disappear merely because the first red triangle shrinks.
        Assert.Equal(CountColor(fullPixels, Blue), CountColor(quarterPixels, Blue));
        for (int offset = 0; offset < fullPixels.Rgba.Length; offset += 4)
            if (fullPixels.Rgba[offset] == 0 && fullPixels.Rgba[offset + 1] == 0
                && fullPixels.Rgba[offset + 2] == 255 && fullPixels.Rgba[offset + 3] == 255)
                Assert.True(fullPixels.Rgba.AsSpan(offset, 4).SequenceEqual(quarterPixels.Rgba.AsSpan(offset, 4)));
        Assert.Equal(fullPixels.Rgba, restoredPixels.Rgba);
        session.WaitForIdle();
    }

    private static OriginalFrameScene Configured(OriginalFrameScene source, float scale) =>
        D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn,
            [new SceneRenderingStyleBinding(Actor, new("mmd"), ModelRole.Character, 0, source.Materials.Count, true)],
            outlineWidths: [new(Actor, 0, scale)]);

    private static OriginalFrameScene ManualWidth(OriginalFrameScene source, int index, float scale)
    {
        var edges = source.Outline!.Materials.ToArray();
        var flags = source.Materials.ToArray();
        if (flags[index].CharacterOutlineStyle is { } style)
            flags[index] = flags[index] with { CharacterOutlineStyle = style with { Width = style.Width * scale } };
        else edges[index] = edges[index] with { Width = edges[index].Width * scale };
        return new(source.Surface, new(source.Outline.Vertices, edges), source.OutlineOptions, source.Options,
            flags, source.Lighting, source.EditorOverlay);
    }

    private static OriginalFrameScene SharedTriangles(bool modern)
    {
        var template = D3D12OriginalOutlineTests.Box();
        Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        var vertices = positions.Select(position => new SceneVertex(position,
            new(position.X, position.Y, 0), Vector2.Zero)).ToArray();
        var materials = new SceneMaterial[] {
            new("scaled-red", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1),
            new("unchanged-blue", 3, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1) };
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2, 0, 2, 3], materials,
            camera: template.Surface.Snapshot.Camera);
        var surface = new OriginalStyleScene(snapshot, [new(Vector3.One), new(Vector3.One)], template.Surface.Profile);
        // Equal source widths select material zero for shared vertices 0 and 2; vertex 3
        // belongs only to material one. This is the original strict-minimum load selection.
        var outline = new OriginalOutlineData(vertices.Select((vertex, index) => new OriginalOutlineVertex(
            vertex.Position, vertex.Normal, OriginalOutlineVertexMode.Expand, 1, index == 3 ? 1 : 0)),
            [new(.5f, Red), new(.5f, Blue)]);
        OriginalFrameMaterialFlags[] flags = [
            new(false, false, false, SurfaceEnabled: false) { CharacterOutlineStyle = modern ? new(.5f, Red) : null },
            new(false, false, false, SurfaceEnabled: false) { CharacterOutlineStyle = modern ? new(.5f, Blue) : null }];
        return new(surface, outline, template.OutlineOptions, D3D12OriginalFrameTests.Options(), flags);
    }

    private static OriginalFrameScene IndependentTriangles(OriginalFrameScene source, bool modern)
    {
        var original = source.Surface.Snapshot;
        var originalIndices = original.Indices.ToArray();
        var vertices = originalIndices.Select(index => original.Vertices[(int)index]).ToArray();
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2, 3, 4, 5], original.Materials,
            world: original.World, camera: original.Camera);
        var surface = new OriginalStyleScene(snapshot, source.Surface.Materials, source.Surface.Profile);
        var outline = new OriginalOutlineData(originalIndices.Select((index, position) =>
            source.Outline!.Vertices[(int)index] with { SelectedMaterialIndex = position / 3 }),
            [new(modern ? .5f : .125f, Red), new(.5f, Blue)]);
        var flags = source.Materials.ToArray();
        if (modern) flags[0] = flags[0] with { CharacterOutlineStyle = new(.125f, Red) };
        return new(surface, outline, source.OutlineOptions, source.Options, flags);
    }

    private static int CountColor(RenderedFrame frame, Vector4 color)
    {
        byte red = (byte)MathF.Round(color.X * 255), green = (byte)MathF.Round(color.Y * 255),
            blue = (byte)MathF.Round(color.Z * 255);
        int result = 0;
        for (int offset = 0; offset < frame.Rgba.Length; offset += 4)
            if (frame.Rgba[offset] == red && frame.Rgba[offset + 1] == green
                && frame.Rgba[offset + 2] == blue && frame.Rgba[offset + 3] == 255) result++;
        return result;
    }

    private static void Save(string name, D3D12FrameSession session, RenderedFrame baseline, RenderedFrame quarter,
        RenderedFrame manual, RenderedFrame restored)
    {
        string? root = Environment.GetEnvironmentVariable("MMD_OUTLINE_SCALE_ARTIFACTS");
        if (root is null) return;
        Directory.CreateDirectory(root);
        foreach (var (label, frame) in new[] { ("full", baseline), ("quarter", quarter), ("manual", manual), ("restored", restored) })
        {
            File.WriteAllBytes(Path.Combine(root, $"{name}-{label}.rgba"), frame.Rgba);
            using var output = File.Create(Path.Combine(root, $"{name}-{label}.ppm"));
            output.Write(Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n"));
            for (int offset = 0; offset < frame.Rgba.Length; offset += 4) output.Write(frame.Rgba, offset, 3);
        }
        File.WriteAllText(Path.Combine(root, $"{name}.json"), JsonSerializer.Serialize(new {
            Width, Height, Gi = "Off", session.AdapterName, session.IsWarp,
            session.DebugRequested, session.DebugLayerEnabled, session.DebugInfoQueueAvailable,
            FullSha256 = Convert.ToHexString(SHA256.HashData(baseline.Rgba)),
            QuarterSha256 = Convert.ToHexString(SHA256.HashData(quarter.Rgba)),
            ManualSha256 = Convert.ToHexString(SHA256.HashData(manual.Rgba)),
            RestoredSha256 = Convert.ToHexString(SHA256.HashData(restored.Rgba)),
            QuarterMatchesManual = quarter.Rgba.AsSpan().SequenceEqual(manual.Rgba),
            QuarterDiffersFromFull = !quarter.Rgba.AsSpan().SequenceEqual(baseline.Rgba),
            RestoredMatchesFull = restored.Rgba.AsSpan().SequenceEqual(baseline.Rgba),
            FullRedPixels = CountColor(baseline, Red), QuarterRedPixels = CountColor(quarter, Red),
            FullBluePixels = CountColor(baseline, Blue), QuarterBluePixels = CountColor(quarter, Blue)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
