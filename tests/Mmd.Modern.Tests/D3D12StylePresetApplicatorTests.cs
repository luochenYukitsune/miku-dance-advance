using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12StylePresetApplicatorTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"), Guid.NewGuid().ToString("N"));

    [Fact]
    public void DisjointInstancesConsumeTheirOwnPresetAndPreserveTheSourceFrame()
    {
        var store = new D3D12StylePresetStore(directory);
        var first = store.SaveAsNew("Same display name", "mmd", new(.25f), new(.6f, .12f));
        var second = store.SaveAsNew("Same display name", "mmd", new(.7f, new Vector4(.1f, .2f, .3f, .4f)), new(.4f, .2f));
        var catalog = store.Load();
        var source = Frame();
        var originalFlags = source.Materials.ToArray();
        D3D12StylePresetMaterialRange[] ranges =
        [new(Guid.NewGuid(), 2, 1, second.StableId, true), new(Guid.NewGuid(), 0, 2, first.StableId, true)];

        var result = D3D12StylePresetApplicator.Apply(source, catalog, ranges);

        Assert.NotSame(source, result);
        Assert.Same(source.Surface, result.Surface);
        Assert.Same(source.Outline, result.Outline);
        Assert.Same(source.OutlineOptions, result.OutlineOptions);
        Assert.Same(source.Options, result.Options);
        Assert.Same(source.Lighting, result.Lighting);
        Assert.Same(source.EditorOverlay, result.EditorOverlay);
        for (int i = 0; i < 3; i++)
        {
            var preset = i == 2 ? second : first;
            Assert.Equal(originalFlags[i] with
            {
                CharacterOutlineStyle = preset.CharacterOutlineStyle,
                CharacterToonResponse = preset.CharacterToonResponse,
                GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands,
            }, result.Materials[i]);
        }
        Assert.Null(result.Materials[0].CharacterOutlineStyle!.Color);
        Assert.Same(originalFlags[3], result.Materials[3]);
        Assert.Equal(originalFlags, source.Materials);
        Assert.All(source.Surface.Snapshot.Materials, material => Assert.Equal("same-source.pmx", material.Name));

        // Applying another immutable snapshot cannot change a frame already retained for temporal rendering.
        var replacement = D3D12StylePresetApplicator.Apply(result, catalog,
            [new(ranges[0].ObjectId, 2, 1, first.StableId, true)]);
        ranges[0] = ranges[0] with { PresetId = first.StableId };
        Assert.Equal(second.CharacterOutlineStyle, result.Materials[2].CharacterOutlineStyle);
        Assert.Equal(first.CharacterOutlineStyle, replacement.Materials[2].CharacterOutlineStyle);
        Assert.Equal(originalFlags, source.Materials);
    }

    [Fact]
    public void EnvironmentAndPropRangesPreserveTheIncomingRolePolicy()
    {
        var source = Frame();
        var result = D3D12StylePresetApplicator.Apply(source, D3D12StylePresetCatalog.BuiltIn,
            [new(Guid.NewGuid(), 0, 2, "mmd", false), new(Guid.NewGuid(), 2, 2, "mmd", false)]);

        Assert.Same(source, result);
        Assert.All(result.Materials, flags =>
        {
            Assert.Null(flags.CharacterOutlineStyle);
            Assert.Null(flags.CharacterToonResponse);
        });
        Assert.Equal(RayGiToonResponse.Continuous, result.Materials[0].GiToonResponseOverride);
        Assert.Null(result.Materials[1].GiToonResponseOverride);
    }

    [Fact]
    public void ExplicitNullParametersKeepIncomingMaterialBehaviorAndCharacterGiRemainsNeutral()
    {
        var store = new D3D12StylePresetStore(directory);
        var preset = store.SaveAsNew("Keep material parameters", "mmd", null, null);
        var source = Frame();
        var flags = source.Materials.ToArray();
        flags[0] = flags[0] with { CharacterOutlineStyle = new(.3f), CharacterToonResponse = new(.6f, .1f) };
        source = Copy(source, flags);

        var result = D3D12StylePresetApplicator.Apply(source, store.Load(),
            [new(Guid.NewGuid(), 0, 2, preset.StableId, true)]);

        for (int i = 0; i < 2; i++)
        {
            Assert.Same(source.Materials[i].CharacterOutlineStyle, result.Materials[i].CharacterOutlineStyle);
            Assert.Same(source.Materials[i].CharacterToonResponse, result.Materials[i].CharacterToonResponse);
            Assert.Equal(RayGiToonResponse.PreservePaletteBands, result.Materials[i].GiToonResponseOverride);
        }
        Assert.Null(result.Materials[1].CharacterOutlineStyle);
        Assert.Null(result.Materials[1].CharacterToonResponse);
        Assert.Equal(RayGiToonResponse.Continuous, source.Materials[0].GiToonResponseOverride);
        Assert.Null(source.Materials[1].GiToonResponseOverride);
        Assert.Same(source.Surface.Snapshot.Materials[0], result.Surface.Snapshot.Materials[0]);
        Assert.Same(source.Outline!.Materials[0], result.Outline!.Materials[0]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(.2f, false)]
    public void ExplicitDisabledOutlineIsConsumedWithoutReplacingItWithADefault(float width, bool enabled)
    {
        var store = new D3D12StylePresetStore(directory);
        var preset = store.SaveAsNew("No extra edge", "mmd", new(width, Vector4.Zero, enabled), null);
        var source = Frame();

        var result = D3D12StylePresetApplicator.Apply(source, store.Load(),
            [new(Guid.NewGuid(), 0, 1, preset.StableId, true)]);

        Assert.Equal(preset.CharacterOutlineStyle, result.Materials[0].CharacterOutlineStyle);
        var resolved = CharacterOutlineMath.ResolveMaterial(source.Outline!.Materials[0], result.Materials[0].CharacterOutlineStyle);
        Assert.Equal(width, resolved.Width);
        Assert.Equal(Vector4.Zero, resolved.Color);
        Assert.False(resolved.DrawEnabled);
        Assert.Null(source.Materials[0].CharacterOutlineStyle);
    }

    [Theory]
    [InlineData("future-surface-a", D3D12StylePresetStatus.UnsupportedPipeline)]
    [InlineData("future-surface-b", D3D12StylePresetStatus.UnsupportedPipeline)]
    [InlineData("custom-not-installed", D3D12StylePresetStatus.Missing)]
    public void UnavailablePresetRejectsTheWholeApplicationEvenForANonCharacter(string id, D3D12StylePresetStatus status)
    {
        var source = Frame();
        var originalFlags = source.Materials.ToArray();
        var unavailableObject = Guid.NewGuid();
        var store = new D3D12StylePresetStore(directory);
        if (status == D3D12StylePresetStatus.UnsupportedPipeline)
            id = store.SaveAsNew("Unsupported pipeline", id, null, null).StableId;

        var error = Assert.Throws<D3D12StylePresetApplicationException>(() => D3D12StylePresetApplicator.Apply(
            source, store.Load(),
            [new(Guid.NewGuid(), 0, 1, "mmd", true), new(unavailableObject, 1, 1, id, false)]));

        Assert.Equal(unavailableObject, error.ObjectId);
        Assert.Equal(id, error.Result.StableId);
        Assert.Equal(status, error.Result.Status);
        Assert.False(error.Result.IsAvailable);
        Assert.NotEmpty(error.Result.Diagnostic);
        Assert.Contains(id, error.Message);
        Assert.Equal(originalFlags, source.Materials);
        Assert.All(source.Materials, flags => Assert.Null(flags.CharacterOutlineStyle));
    }

    [Theory]
    [InlineData("negative-start")]
    [InlineData("zero-count")]
    [InlineData("negative-count")]
    [InlineData("outside-start")]
    [InlineData("outside-count")]
    [InlineData("overflow-start")]
    [InlineData("overflow-count")]
    [InlineData("empty-identity")]
    [InlineData("duplicate-identity")]
    [InlineData("overlap")]
    [InlineData("invalid-preset-id")]
    [InlineData("null-range")]
    public void InvalidRangesAreRejectedWithoutChangingAnySourceMaterial(string kind)
    {
        var source = Frame();
        var originalFlags = source.Materials.ToArray();
        var first = new D3D12StylePresetMaterialRange(Guid.NewGuid(), 0, 1, "mmd", true);
        var second = new D3D12StylePresetMaterialRange(Guid.NewGuid(), 1, 1, "mmd", true);
        second = kind switch
        {
            "negative-start" => second with { MaterialStart = -1 },
            "zero-count" => second with { MaterialCount = 0 },
            "negative-count" => second with { MaterialCount = -1 },
            "outside-start" => second with { MaterialStart = source.Materials.Count },
            "outside-count" => second with { MaterialCount = source.Materials.Count },
            "overflow-start" => second with { MaterialStart = int.MaxValue },
            "overflow-count" => second with { MaterialCount = int.MaxValue },
            "empty-identity" => second with { ObjectId = Guid.Empty },
            "duplicate-identity" => second with { ObjectId = first.ObjectId },
            "overlap" => second with { MaterialStart = 0 },
            "invalid-preset-id" => second with { PresetId = "../mmd" },
            "null-range" => null!,
            _ => throw new InvalidOperationException(),
        };

        Assert.Throws<ArgumentException>(() => D3D12StylePresetApplicator.Apply(source,
            D3D12StylePresetCatalog.BuiltIn, [first, second]));
        Assert.Equal(originalFlags, source.Materials);
    }

    [Fact]
    public void EmptySelectionAndAnEqualPresetRetainTheExistingImmutableFrame()
    {
        var source = Frame();
        var catalog = D3D12StylePresetCatalog.BuiltIn;
        Assert.Same(source, D3D12StylePresetApplicator.Apply(source, catalog, []));
        var range = new D3D12StylePresetMaterialRange(Guid.NewGuid(), 0, source.Materials.Count, "mmd", true);
        var applied = D3D12StylePresetApplicator.Apply(source, catalog, [range]);
        Assert.Same(applied, D3D12StylePresetApplicator.Apply(applied, catalog, [range]));
        Assert.Throws<ArgumentNullException>(() => D3D12StylePresetApplicator.Apply(null!, catalog, []));
        Assert.Throws<ArgumentNullException>(() => D3D12StylePresetApplicator.Apply(source, null!, []));
        Assert.Throws<ArgumentNullException>(() => D3D12StylePresetApplicator.Apply(source, catalog, null!));
    }

    [Fact]
    public void CharacterSelectionRequiresModernFrameOptionsWithoutChangingTheSource()
    {
        var original = Frame();
        var source = new OriginalFrameScene(original.Surface, original.Outline, original.OutlineOptions,
            materials: original.Materials, lighting: original.Lighting, editorOverlay: original.EditorOverlay);
        var flags = source.Materials.ToArray();
        var catalog = D3D12StylePresetCatalog.BuiltIn;
        var range = new D3D12StylePresetMaterialRange(Guid.NewGuid(), 0, source.Materials.Count, "genshin", true);

        var error = Assert.Throws<NotSupportedException>(() => D3D12StylePresetApplicator.Apply(source, catalog, [range]));

        Assert.Contains("modern frame options", error.Message);
        Assert.Null(source.Options);
        Assert.Equal(flags, source.Materials);
        Assert.Same(source, D3D12StylePresetApplicator.Apply(source, catalog, []));
        Assert.Same(source, D3D12StylePresetApplicator.Apply(source, catalog, [range with { IsCharacter = false }]));
    }

    [Fact]
    public void OutlineOnlyPresetWithNullToonCanUseTheLegacySurfaceEntry()
    {
        var original = Frame();
        var source = new OriginalFrameScene(original.Surface, original.Outline, original.OutlineOptions,
            materials: original.Materials, lighting: original.Lighting, editorOverlay: original.EditorOverlay);
        var flags = source.Materials.ToArray();
        var store = new D3D12StylePresetStore(directory);
        var outlineOnly = store.SaveAsNew("Outline only", "mmd", new(.4f), null);

        var result = D3D12StylePresetApplicator.Apply(source, store.Load(),
            [new(Guid.NewGuid(), 0, 1, outlineOnly.StableId, true)]);

        Assert.Null(result.Options);
        Assert.Equal(outlineOnly.CharacterOutlineStyle, result.Materials[0].CharacterOutlineStyle);
        Assert.Null(result.Materials[0].CharacterToonResponse);
        Assert.Equal(RayGiToonResponse.PreservePaletteBands, result.Materials[0].GiToonResponseOverride);
        Assert.Equal(flags, source.Materials);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void ReopenedPresetsReachActualToonAndOutlinePixelsAndRestoreTheDefaultWithoutRenderReadbacks()
    {
        const int width = 257, height = 191;
        var source = GpuFixture();
        var originalFlags = source.Materials.ToArray();
        var store = new D3D12StylePresetStore(directory);
        var defaults = store.SaveAsNew("Explicit modern parameters", "mmd", new(), new());
        var darkerToon = store.SaveAsNew("Darker toon", "mmd", new(), new(.7f, .1f));
        var widerEdge = store.SaveAsNew("Wider edge", "mmd", new(1.5f), new());
        var reopened = new D3D12StylePresetStore(directory).Load();
        var objectId = Guid.NewGuid();
        OriginalFrameScene Selected(string id) => D3D12StylePresetApplicator.Apply(source, reopened,
            [new(objectId, 0, source.Materials.Count, id, true)]);
        var manualDefault = Copy(source, source.Materials.Select(flags => flags with
        {
            CharacterOutlineStyle = new(),
            CharacterToonResponse = new(),
            GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands,
        }));

        using var session = new D3D12FrameSession();
        Assert.False(session.IsWarp);
        var originalPixels = session.Capture(source, width, height);
        var manualPixels = session.Capture(manualDefault, width, height);
        var defaultPixels = session.Capture(Selected(defaults.StableId), width, height);
        Assert.Equal(manualPixels.Rgba, defaultPixels.Rgba);

        var darkerPixels = session.Capture(Selected(darkerToon.StableId), width, height);
        int center = (height / 2 * width + width / 2) * 4;
        Assert.True(defaultPixels.Rgba[center] > darkerPixels.Rgba[center] + 50,
            "The installed toon threshold must visibly darken the actual receiver interior.");

        var widerPixels = session.Capture(Selected(widerEdge.StableId), width, height);
        int borderChanges = 0;
        for (int offset = 0; offset < defaultPixels.Rgba.Length; offset += 4)
            if (!defaultPixels.Rgba.AsSpan(offset, 4).SequenceEqual(widerPixels.Rgba.AsSpan(offset, 4))) borderChanges++;
        Assert.True(borderChanges > 50, "The installed edge width must change a visible contour.");
        Assert.Equal(defaultPixels.Rgba.AsSpan(center, 4).ToArray(), widerPixels.Rgba.AsSpan(center, 4).ToArray());
        Assert.Equal(defaultPixels.Rgba, session.Capture(Selected(defaults.StableId), width, height).Rgba);
        Assert.Equal(originalPixels.Rgba, session.Capture(source, width, height).Rgba);
        Assert.Equal(originalFlags, source.Materials);

        long readbacks = session.Runtime.Readbacks;
        using var rendered = session.Render(Selected(defaults.StableId), width, height);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
    }

    private static OriginalFrameScene GpuFixture()
    {
        var box = D3D12OriginalOutlineTests.Box();
        var original = box.Surface.Snapshot;
        const float nDotL = .175f;
        var normal = new Vector3(nDotL, 0, -MathF.Sqrt(1 - nDotL * nDotL));
        var vertices = original.Vertices.ToArray().Select(vertex => vertex with { Normal = normal }).ToArray();
        var material = original.Materials[0] with { Diffuse = new(.6f, .4f, .2f, 1) };
        var mesh = new SceneSnapshot(vertices, original.Indices, [material], camera: original.Camera, lightDirection: Vector3.UnitX);
        var surface = new OriginalStyleScene(mesh, [new(new(.2f))], box.Surface.Profile);
        var options = D3D12OriginalFrameTests.Options(shadow: new(Matrix4x4.CreateScale(.2f, .2f, .1f),
            32, 32, false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual));
        return new(surface, box.Outline, box.OutlineOptions, options);
    }

    private static OriginalFrameScene Frame()
    {
        SceneVertex[] vertices =
        [new(new(-1, -1, 0), Vector3.UnitZ, Vector2.Zero), new(new(0, 1, 0), Vector3.UnitZ, Vector2.UnitY),
         new(new(1, -1, 0), Vector3.UnitZ, Vector2.UnitX)];
        var materials = Enumerable.Range(0, 4).Select(i =>
            new SceneMaterial("same-source.pmx", i * 3, 3, new(.8f, .6f, .4f, 1), new(.1f), new(.2f), 8)).ToArray();
        var snapshot = new SceneSnapshot(vertices, Enumerable.Repeat(new uint[] { 0, 1, 2 }, 4).SelectMany(v => v).ToArray(), materials);
        var surface = new OriginalStyleScene(snapshot, materials.Select(_ => new OriginalStyleMaterialBinding(new(.3f))));
        var outline = new OriginalOutlineData(vertices.Select(v =>
                new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1, 0)),
            materials.Select(_ => new OriginalOutlineMaterial(2, Vector4.One, false)));
        var flags = Enumerable.Range(0, 4).Select(i => new OriginalFrameMaterialFlags(i % 2 == 0, i % 2 != 0, true,
            SurfaceEnabled: i != 2, ShadowDoubleSided: i == 1, CullTransparentSurface: i == 3)
            { GiToonResponseOverride = i % 2 == 0 ? RayGiToonResponse.Continuous : null });
        return new(surface, outline, OriginalOutlineTests.Options(), new(OriginalAlphaBlendMode.Normal, true, false),
            flags, SceneLightingSnapshot.Empty, new ViewportOverlaySnapshot([]));
    }

    private static OriginalFrameScene Copy(OriginalFrameScene frame, IEnumerable<OriginalFrameMaterialFlags> flags)
        => new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options, flags, frame.Lighting, frame.EditorOverlay);

    public void Dispose()
    {
        string cache = Path.GetFullPath(Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"));
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Preset test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
