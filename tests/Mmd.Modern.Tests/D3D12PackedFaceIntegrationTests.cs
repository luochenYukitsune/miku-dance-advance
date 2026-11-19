using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Original disjoint quads witness per-draw roots and packed-image rebinding;
/// family-specific numerical oracles live in the individual FaceSdf tests.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12PackedFaceIntegrationTests
{
    private const int Width = 401, Height = 129;
    private static readonly string[] Families =
    [
        T8cTestHelpers.GenshinStyleId,
        T8cTestHelpers.EndfieldStyleId,
        T8cTestHelpers.StarRailStyleId,
        T8cTestHelpers.WutheringStyleId,
        T8cTestHelpers.MmdStyleId,
    ];

    [Fact, Trait("Category", "Gpu")]
    public void FiveFamiliesMixPackedAndGrayFacesWithoutCrossDrawImagesAndRestoreOriginalExactly()
    {
        using var session = new D3D12FrameSession();
        foreach (int[] order in new[] { new[] { 0, 1, 2, 3, 4 }, new[] { 4, 3, 2, 1, 0 } })
        {
            var frame = Frame(order);
            var original = session.Capture(frame, Width, Height);
            var ordinary = session.Capture(frame, Width, Height, surfaceStyles: Map(order, bound: false));
            var mixed = session.Capture(frame, Width, Height, surfaceStyles: Map(order));
            for (int family = 0; family < Families.Length; family++)
            {
                // A separate capture has exactly one selected family; every other
                // draw is ordinary MMD. It therefore cannot inherit another packed
                // family's root constants, t0 or t1 in its independent reference.
                var reference = session.Capture(frame, Width, Height, surfaceStyles: Map(order, onlyFamily: family));
                int changed = CompareRegion(frame, family, reference, mixed);
                Assert.Equal(0, changed);
                if (family < 4)
                    Assert.True(CompareRegion(frame, family, ordinary, mixed) > 200,
                        $"Family {Families[family]} must consume its declared face data.");
                else
                    Assert.Equal(0, CompareRegion(frame, family, original, mixed));
            }
            // Distinct G/A and R/G control images make stale secondary texture binding observable
            // in both directions, rather than accidentally equivalent fixtures.
            foreach (int packedFamily in new[] { 0, 1 })
            {
                var crossed = session.Capture(frame, Width, Height,
                    surfaceStyles: Map(order, onlyFamily: packedFamily, crossSecondary: true));
                Assert.True(CompareRegion(frame, packedFamily, crossed, mixed) > 200);
            }
            for (int offset = 3; offset < mixed.Rgba.Length; offset += 4)
                Assert.Equal(original.Rgba[offset], mixed.Rgba[offset]);
            Assert.Equal(ordinary.Rgba,
                session.Capture(frame, Width, Height, surfaceStyles: Map(order, bound: false)).Rgba);
            Assert.Equal(original.Rgba, session.Capture(frame, Width, Height).Rgba);
            // The expanded mixed root and all families have already been warmed.
            var warm = session.Statistics!; long readbacks = session.Runtime.Readbacks;
            for (int repeat = 0; repeat < 3; repeat++)
            {
                using var rendered = session.Render(frame, Width, Height, surfaceStyles: Map(order));
                Assert.Equal(warm.ShaderCompilations, rendered.Statistics.ShaderCompilations);
                Assert.Equal(warm.PipelineCreations, rendered.Statistics.PipelineCreations);
                Assert.Equal(0, rendered.Statistics.ReadbackCount);
            }
            Assert.Equal(readbacks, session.Runtime.Readbacks);
        }
    }

    private static D3D12SurfaceStyleMap Map(int[] order, bool bound = true, int onlyFamily = -1, bool crossSecondary = false)
        => T8cTestHelpers.CreateDirectMap(
            order.Select(i => onlyFamily < 0 || onlyFamily == i ? Families[i] : T8cTestHelpers.MmdStyleId),
            order.Select(i =>
            {
                string styleId = onlyFamily < 0 || onlyFamily == i ? Families[i] : T8cTestHelpers.MmdStyleId;
                return bound && i < 4 && (onlyFamily < 0 || onlyFamily == i)
                    ? T8cTestHelpers.MaterialWithBinding(styleId, Binding(i, crossSecondary), D3D12CharacterMaterialRole.Face, 1, 1)
                    : new D3D12CharacterMaterialStyle();
            }));

    private static StyleMaterialBindingData Binding(int family, bool crossSecondary)
        => family switch
        {
            0 => T8cTestHelpers.GenshinSoftFaceBinding(0, crossSecondary ? 1 : 3, -Vector3.UnitZ, Vector3.UnitX, .4f),
            1 => T8cTestHelpers.EndfieldFaceSdfBinding(5, crossSecondary ? 3 : 1, -Vector3.UnitZ, Vector3.UnitX, .1f, 1),
            2 => T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 2, forward: -Vector3.UnitZ, right: Vector3.UnitX, transition: .3f, offset: 1),
            _ => T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.WutheringStyleId, 4, forward: -Vector3.UnitZ, right: Vector3.UnitX, transition: .1f, offset: 1.27f),
        };

    private static OriginalFrameScene Frame(int[] order)
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        var materials = new List<SceneMaterial>();
        foreach (int family in order)
        {
            float x = family - 2; int first = vertices.Count;
            vertices.AddRange([new(new(x - .35f, -.65f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(x + .35f, -.65f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(x + .35f, .65f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(x - .35f, .65f, 0), Vector3.UnitX, new(.375f, .5f))]);
            materials.Add(new(Families[family], indices.Count, 6, new(.6f, .4f, .2f, family == 4 ? .7f : 1),
                Vector3.Zero, Vector3.Zero, 1));
            indices.AddRange([(uint)first, (uint)(first + 2), (uint)(first + 1), (uint)first, (uint)(first + 3), (uint)(first + 2)]);
        }
        // Nonadjacent image indices deliberately distinguish the two t8 consumers.
        SceneTexture[] textures = [new("Genshin RGB", 1, 1, [160, 210, 60, 0]),
            new("Endfield cm RG", 1, 1, [128, 26, 0, 0]),
            new("Star red", 1, 1, [192, 4, 5, 0]),
            new("Genshin mask GA", 1, 1, [0, 180, 0, 0]),
            new("Wuthering red", 1, 1, [192, 6, 7, 0]),
            new("Endfield RGB", 1, 1, [64, 204, 128, 0])];
        var scene = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials, textures,
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: Vector3.UnitX);
        var bindings = order.Select(_ => new OriginalStyleMaterialBinding(new(.2f), -1, OriginalSphereMode.None, true));
        return new(new(scene, bindings, new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }

    private static int CompareRegion(OriginalFrameScene scene, int family, RenderedFrame expected, RenderedFrame actual)
    {
        var camera = scene.Surface.Snapshot.Camera.ViewProjection(Width, Height);
        int Project(float x)
        {
            var clip = Vector4.Transform(new Vector4(x, 0, 0, 1), camera);
            return (int)((clip.X / clip.W * .5f + .5f) * Width);
        }
        int a = Project(family - 2 - .45f), b = Project(family - 2 + .45f), changed = 0, covered = 0;
        for (int x = Math.Min(a, b); x <= Math.Max(a, b); x++)
        for (int y = 0; y < Height; y++)
        {
            int offset = (y * Width + x) * 4;
            if (!expected.Rgba.AsSpan(offset, 4).SequenceEqual(actual.Rgba.AsSpan(offset, 4))) changed++;
            if (actual.Rgba[offset + 3] != 0) covered++;
        }
        Assert.True(covered > 200, "The selected region must contain the intended quad.");
        return changed;
    }
}
