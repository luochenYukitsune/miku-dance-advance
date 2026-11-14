using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Closed inverted hull, using the existing original-outline fixture and actual GPU output.</summary>
[Trait("Category", "Gpu")]
public sealed class CharacterOutlineGpuTests
{
    private const int Width = 257, Height = 191;
    private static readonly Vector4 Red = new(1, 0, 0, 1), Blue = new(0, 0, 1, 1);

    [Fact]
    public void SharedVerticesDoNotChangeTheOriginalNullMaterialWhenItsModernNeighbourWidens()
    {
        var template = Box(false);
        Vector3[] p = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        var vertices = p.Select(v => new SceneVertex(v, new(v.X, v.Y, 0), Vector2.Zero)).ToArray();
        var materials = new SceneMaterial[] {
            new("original-blue", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1),
            new("modern-red", 3, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1) };
        var mesh = new SceneSnapshot(vertices, [0, 1, 2, 0, 2, 3], materials, camera: template.Surface.Snapshot.Camera);
        var surface = new OriginalStyleScene(mesh, [new(Vector3.One), new(Vector3.One)], template.Surface.Profile);
        // Both ranges share vertices 0 and 2; every selected binding deliberately points
        // to material 1. A global modern stream would incorrectly widen the blue range.
        var outline = new OriginalOutlineData(vertices.Select(v => new OriginalOutlineVertex(v.Position, v.Normal,
            OriginalOutlineVertexMode.Expand, 1, 1)), [new(.5f, Blue, true), new(.5f, Red, false)]);
        OriginalFrameMaterialFlags[] flags = [new(false, false, false, SurfaceEnabled: false), new(false, false, false, SurfaceEnabled: false)];
        var source = new OriginalFrameScene(surface, outline, template.OutlineOptions, template.Options, flags);
        var mixed = new OriginalFrameScene(surface, outline, template.OutlineOptions, template.Options,
            [flags[0], flags[1] with { CharacterOutlineStyle = new(3, Red) }]);
        using var session = new D3D12FrameSession();
        var baseline = session.Capture(source, Width, Height);
        var actual = session.Capture(mixed, Width, Height);
        Assert.True(CountColor(baseline, Blue) > 100);
        Assert.True(CountColor(actual, Red) > 100);
        Assert.Equal(CountColor(baseline, Blue), CountColor(actual, Blue));
        for (int i = 0; i < baseline.Rgba.Length; i += 4)
            if (baseline.Rgba[i] == 0 && baseline.Rgba[i + 1] == 0 && baseline.Rgba[i + 2] == 255)
                Assert.Equal(baseline.Rgba.AsSpan(i, 4).ToArray(), actual.Rgba.AsSpan(i, 4).ToArray());
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, .15f)]
    [InlineData(true, .15f)]
    public void DoubleSidedCoplanarClothKeepsItsInteriorAndOnlyAddsTheOuterContour(bool stencil, float slope)
    {
        // Two opposite windings share precisely the same thin cloth. Tangent normals
        // expand its silhouette while keeping the hull in the surface's plane.
        // Without a modern-only depth separation, LessEqual paints the entire face.
        Vector3[] p = [new(-1, -1, -slope), new(1, -1, slope), new(1, 1, slope), new(-1, 1, -slope)];
        var vertices = p.Select(v => new SceneVertex(v, new(v.X, v.Y, slope * v.X), Vector2.Zero)).ToArray();
        uint[] indices = [0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3];
        var template = Box(false);
        var mesh = new SceneSnapshot(vertices, indices,
            [new("thin-cloth", 0, indices.Length, Vector4.One, Vector3.Zero, Vector3.Zero, 1)],
            camera: template.Surface.Snapshot.Camera);
        var surface = new OriginalStyleScene(mesh, [new(Vector3.One, DoubleSided: true)], template.Surface.Profile);
        var outline = new OriginalOutlineData(vertices.Select(v => new OriginalOutlineVertex(v.Position, v.Normal,
            OriginalOutlineVertexMode.Expand, 1, 0)), [new(.5f, Red, false)]);
        var options = template.Options! with { GroundShadow = stencil ? new(Vector3.UnitY, Vector4.Zero, false, true) : null };
        var source = new OriginalFrameScene(surface, outline, template.OutlineOptions, options);
        using var session = new D3D12FrameSession();
        var off = session.Capture(source, Width, Height);
        var modern = session.Capture(Styled(source, new(.5f, Red)), Width, Height);
        int interior = 0;
        for (int y = Height / 2 - 20; y < Height / 2 + 20; y++)
        for (int x = Width / 2 - 20; x < Width / 2 + 20; x++)
        {
            int offset = (y * Width + x) * 4;
            Assert.Equal(off.Rgba.AsSpan(offset, 4).ToArray(), modern.Rgba.AsSpan(offset, 4).ToArray());
            interior++;
        }
        Assert.Equal(1600, interior);
        Assert.True(CountColor(modern, Red) > 20, "Depth separation must retain a real outer contour.");
        var legacy = new OriginalFrameScene(surface, new(outline.Vertices, [new(.5f, Red, true)]),
            source.OutlineOptions, options);
        var original = session.Capture(legacy, Width, Height);
        Assert.True(CountColor(original, Red) > CountColor(modern, Red) + 100,
            "The source control must reproduce the coplanar hull fill that the modern path fixes.");
        Assert.Equal(off.Rgba, session.Capture(source, Width, Height).Rgba);
    }

    [Fact]
    public void DisabledAuthorEdgeCanBeOverriddenAndWidthColorAndNullChangesReachTheActualBorder()
    {
        using var session = new D3D12FrameSession();
        var source = Box(false);
        var off = session.Capture(source, Width, Height);
        Assert.Equal(0, session.VisibilityStatistics!.OutlineDrawn);
        var thin = session.Capture(Styled(source, new(.5f, Red)), Width, Height);
        Assert.Equal(1, session.VisibilityStatistics!.OutlineDrawn);
        // White untextured fixture: automatic per-pixel palette is .55 * white + .025.
        var defaultColor = new Vector4(.575f, .575f, .575f, 1);
        Assert.True(CountColor(session.Capture(Styled(source, new()), Width, Height), defaultColor) > 0);
        var thick = session.Capture(Styled(source, new(2, Red)), Width, Height);
        Assert.True(CountColor(thin, Red) > 0);
        Assert.True(CountColor(thick, Red) > CountColor(thin, Red));
        var blue = session.Capture(Styled(source, new(.5f, Blue)), Width, Height);
        Assert.Equal(CountColor(thin, Red), CountColor(blue, Blue));
        Assert.Equal(0, CountColor(blue, Red));
        Assert.NotEqual(thin.Rgba, blue.Rgba);
        foreach (var style in new CharacterOutlineStyle[] { new(0, Red), new(2, Red, false) })
        {
            Assert.Equal(off.Rgba, session.Capture(Styled(source, style), Width, Height).Rgba);
            Assert.Equal(0, session.VisibilityStatistics!.OutlineDrawn);
        }
        Assert.Equal(off.Rgba, session.Capture(Styled(source, null), Width, Height).Rgba);
        // The surface-only entry also records the override without frame options.
        using var original = new D3D12OriginalStyleRenderer();
        var noOptions = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: Styled(source, new(.5f, Red)).Materials);
        Assert.True(CountColor(original.Render(noOptions, Width, Height), Red) > 0);
        var author = Box(true);
        var originalBytes = session.Capture(author, Width, Height).Rgba;
        session.Capture(Styled(author, new(2, Blue)), Width, Height);
        Assert.Equal(originalBytes, session.Capture(Styled(author, null), Width, Height).Rgba);
    }

    [Fact]
    public void MixedMaterialsRetainNullMaterialPixelsAndEqualFreshStylesReuseBoundedCaches()
    {
        using var session = new D3D12FrameSession();
        var source = MixedBoxes();
        var before = session.Capture(source, Width, Height);
        OriginalFrameScene Selected() => new(source.Surface, source.Outline, source.OutlineOptions, source.Options,
            [source.Materials[0], source.Materials[1] with { CharacterOutlineStyle = new(.5f, Red) }]);
        var selected = session.Capture(Selected(), Width, Height);
        int nullMaterialOpaque = 0;
        var firstClip = Vector4.Transform(new Vector4(-1.6f, 0, 0, 1),
            source.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        bool firstIsLeft = firstClip.X / firstClip.W < 0;
        // Select the first box by projection so the control works with the RH camera.
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if ((x < Width / 2) != firstIsLeft) continue;
            int offset = (y * Width + x) * 4;
            Assert.Equal(before.Rgba.AsSpan(offset, 4).ToArray(), selected.Rgba.AsSpan(offset, 4).ToArray());
            if (before.Rgba[offset] == 0 && before.Rgba[offset + 1] == 0 && before.Rgba[offset + 2] == 0)
                nullMaterialOpaque++;
        }
        Assert.True(nullMaterialOpaque > 10, "The null control includes visible original black outline pixels.");
        Assert.True(CountColor(selected, Red) > 0);
        for (int i = 0; i < 6; i++) { using var warm = session.Render(Selected(), Width, Height); }
        var statistics = session.Statistics!;
        long bounds = session.VisibilityStatistics!.OutlineBoundsBuilds;
        long readbacks = session.Runtime.Readbacks;
        for (int i = 0; i < 12; i++)
        {
            using var rendered = session.Render(Selected(), Width, Height);
            Assert.Equal(0, rendered.Statistics.ReadbackCount);
            Assert.Equal(readbacks, session.Runtime.Readbacks);
            Assert.Equal(statistics.BufferUploads, rendered.Statistics.BufferUploads);
            Assert.Equal(statistics.CachedResources, rendered.Statistics.CachedResources);
            Assert.Equal(bounds, session.VisibilityStatistics!.OutlineBoundsBuilds);
            Assert.InRange(rendered.Statistics.PendingFrames, 0, 3);
        }
        Assert.Equal(before.Rgba, session.Capture(source, Width, Height).Rgba);
        Assert.Equal(selected.Rgba, session.Capture(Selected(), Width, Height).Rgba);
        session.WaitForIdle();
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing, FrameAntialiasing.Off, 1)]
    [InlineData(LightingMode.HardwareRayTracing, FrameAntialiasing.Off, 1)]
    [InlineData(LightingMode.SoftwareRayTracing, FrameAntialiasing.Smaa1X, 4)]
    [InlineData(LightingMode.HardwareRayTracing, FrameAntialiasing.Smaa1X, 4)]
    public void GiAndSmaaSsaaKeepTheModernOutlineAndRenderDoesNotReadBack(
        LightingMode mode, FrameAntialiasing aa, int samples)
    {
        using var session = new D3D12FrameSession();
        var support = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? support.HardwareAvailable : support.SoftwareAvailable,
            support.UnavailableReason);
        var source = Box(false);
        source = new(source.Surface, source.Outline, source.OutlineOptions,
            source.Options! with { Antialiasing = aa, SpatialSamples = samples, SmaaQuality = SmaaQuality.Ultra }, source.Materials);
        var styled = Styled(source, new(2, Red));
        var gi = new RayGiOptions(mode, Strength: 2, SamplesPerPixel: 8, Seed: 17,
            DirectionalLightDirection: Vector3.UnitZ, DirectionalIrradiance: new(3));
        var off = session.Capture(styled, Width, Height);
        var on = session.Capture(styled, Width, Height, gi);
        int red = 0;
        for (int offset = 0; offset < off.Rgba.Length; offset += 4)
        {
            if (off.Rgba[offset] != 255 || off.Rgba[offset + 1] != 0 || off.Rgba[offset + 2] != 0) continue;
            red++;
            Assert.Equal(off.Rgba.AsSpan(offset, 4).ToArray(), on.Rgba.AsSpan(offset, 4).ToArray());
        }
        Assert.True(red > 20, "The closed hull must retain actual visible border coverage after AA.");
        Assert.Equal(mode, session.RayGiStatistics!.Effective);
        long readbacks = session.Runtime.Readbacks;
        using var rendered = session.Render(styled, Width, Height, gi);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        Assert.Equal(off.Rgba, session.Capture(styled, Width, Height, RayGiOptions.Off).Rgba);
    }

    [Fact]
    public void GroundShadowUsesOriginalPositionsEvenWhenDisabledOverrideRequestsAnotherWidth()
    {
        using var session = new D3D12FrameSession();
        var box = Box(false);
        var camera = box.Surface.Snapshot.Camera with { Eye = new(3, 3, -5) };
        var source = new OriginalFrameScene(box.Surface, box.Outline, box.OutlineOptions,
            box.Options! with { CameraOverride = camera, GroundShadow = new(new(.3f, -1, .2f), new(0, 1, 0, 1), false, false) },
            [new(false, false, true, SurfaceEnabled: false)]);
        var baseline = session.Capture(source, Width, Height);
        Assert.True(CountColor(baseline, new(0, 1, 0, 1)) > 20, "The ground control must actually rasterize.");
        var wideDisabled = Styled(source, new(12, Red, false));
        Assert.Equal(baseline.Rgba, session.Capture(wideDisabled, Width, Height).Rgba);
        var ineligible = new OriginalFrameScene(source.Surface, source.Outline,
            source.OutlineOptions! with { Eligible = false }, source.Options, source.Materials);
        Assert.Equal(baseline.Rgba, session.Capture(Styled(ineligible, new(12, Red)), Width, Height).Rgba);
    }

    private static OriginalFrameScene Box(bool authorEdge)
    {
        var b = D3D12OriginalOutlineTests.Box();
        return new(b.Surface, new(b.Outline!.Vertices, [b.Outline.Materials[0] with { DrawEnabled = authorEdge }]),
            b.OutlineOptions, D3D12OriginalFrameTests.Options());
    }

    private static OriginalFrameScene Styled(OriginalFrameScene source, CharacterOutlineStyle? style) =>
        new(source.Surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials.Select(flags => flags with { CharacterOutlineStyle = style }));

    private static OriginalFrameScene MixedBoxes()
    {
        var box = Box(true); var s = box.Surface.Snapshot;
        var vertices = new List<SceneVertex>(); var edge = new List<OriginalOutlineVertex>();
        var indices = new List<uint>(); var materials = new List<SceneMaterial>();
        for (int i = 0; i < 2; i++)
        {
            var delta = new Vector3(i == 0 ? -1.6f : 1.6f, 0, 0);
            uint start = (uint)vertices.Count;
            vertices.AddRange(s.Vertices.ToArray().Select(v => v with { Position = v.Position + delta }));
            edge.AddRange(box.Outline!.Vertices.Select(v => v with { Position = v.Position + delta, SelectedMaterialIndex = i }));
            indices.AddRange(s.Indices.ToArray().Select(index => index + start));
            materials.Add(s.Materials[0] with { StartIndex = i * 36 });
        }
        var scene = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials,
            camera: s.Camera with { OrthographicHeight = 4.8f });
        var surface = new OriginalStyleScene(scene, [new(Vector3.One), new(Vector3.One)], box.Surface.Profile);
        var outline = new OriginalOutlineData(edge,
            [box.Outline!.Materials[0], box.Outline.Materials[0] with { DrawEnabled = false }]);
        return new(surface, outline, box.OutlineOptions, box.Options);
    }

    private static int CountColor(RenderedFrame frame, Vector4 color)
    {
        byte r = (byte)MathF.Round(color.X * 255), g = (byte)MathF.Round(color.Y * 255), b = (byte)MathF.Round(color.Z * 255);
        int count = 0;
        for (int i = 0; i < frame.Rgba.Length; i += 4)
            if (frame.Rgba[i] == r && frame.Rgba[i + 1] == g && frame.Rgba[i + 2] == b && frame.Rgba[i + 3] == 255) count++;
        return count;
    }
}
