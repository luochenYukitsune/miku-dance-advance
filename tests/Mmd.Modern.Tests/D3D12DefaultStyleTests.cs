using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>The new character families are opt-in per project or instance. This asserts
/// dispatch and selection, not original-MMD executable pixel equivalence.</summary>
public sealed class D3D12DefaultStyleTests
{
    [Fact]
    public void BuiltInMmdInheritsMaterialParametersWhileTheFourOptInFamiliesKeepTheirOwnDefaults()
    {
        var builtIn = D3D12StylePresetCatalog.BuiltIn;
        Assert.Single(builtIn.List());
        var mmd = builtIn.Find("mmd").Definition!;
        Assert.Null(mmd.CharacterOutlineStyle);
        Assert.Null(mmd.CharacterToonResponse);
        foreach (string id in new[] { "genshin", "star-rail", "wuthering-waves", "endfield" })
            Assert.Equal(D3D12StylePresetStatus.Missing, builtIn.Find(id).Status);

        var catalog = TestStylePlugins.AllPresetCatalog;
        foreach (string id in new[] { "genshin", "star-rail", "wuthering-waves", "endfield" })
        {
            var selected = catalog.Find(id).Definition!;
            Assert.Equal(new CharacterOutlineStyle(), selected.CharacterOutlineStyle);
            Assert.Equal(new CharacterToonResponse(), selected.CharacterToonResponse);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitDefaultMmdPreservesRawMaterialFlagsAndDoesNotRequireModernOptions(bool modern)
    {
        var raw = Frame(modern);
        var catalog = D3D12StylePresetCatalog.BuiltIn;
        var binding = new SceneRenderingStyleBinding(Guid.NewGuid(), new("mmd"), ModelRole.Character, 0, 1, true);
        Assert.All(raw.Materials, flags =>
        {
            Assert.Null(flags.CharacterToonResponse);
            Assert.Null(flags.CharacterOutlineStyle);
            Assert.Null(flags.GiToonResponseOverride);
        });

        var direct = D3D12StylePresetApplicator.Apply(raw, catalog,
            [new(binding.ObjectId, 0, 1, "mmd", true)]);
        var view = D3D12ViewRenderingStyles.Resolve(raw, catalog, [binding]);

        Assert.Same(raw, direct);
        Assert.Same(raw, view.Scene);
        Assert.True(view.SurfaceStyles.IsDefault);
        Assert.Equal(raw.Materials, view.Scene.Materials);
        Assert.Same(raw.Options, view.Scene.Options);
        Assert.Same(raw.Outline, view.Scene.Outline);
        Assert.Same(raw.OutlineOptions, view.Scene.OutlineOptions);
    }

    [Theory]
    [InlineData(RenderingStyleId.Genshin, "genshin")]
    [InlineData(RenderingStyleId.StarRail, "star-rail")]
    [InlineData(RenderingStyleId.WutheringWaves, "wuthering-waves")]
    [InlineData(RenderingStyleId.Endfield, "endfield")]
    public void NewProjectUsesMmdAndOnlyAnExplicitInstanceChangesUntilItsOverrideIsRemoved(
        RenderingStyleId selected, string expected)
    {
        var layout = new SceneCompositionEditor();
        var source = Frame();
        var first = layout.Add("same actor", "same-source.pmx", ModelRole.Character, source);
        var second = layout.Add("same actor", "same-source.pmx", ModelRole.Character, source);
        using var project = new SceneProjectSession(layout, new(), [], new ProjectSettingsBackendProvider(
            (_, _) => throw new InvalidOperationException("The static selection fixture does not create physics."),
            _ => throw new InvalidOperationException("The static selection fixture does not create physics.")));
        var catalog = TestStylePlugins.AllPresetCatalog;
        var viewCatalog = TestStylePlugins.AllViewCatalog;
        // Match the production view ordering: compose/evaluate first, then supply view
        // frame options, then resolve the explicitly selected surface family.
        static OriginalFrameScene Ready(SceneProjectRenderSnapshot snapshot)
            => D3D12ViewScene.Resolve(snapshot.Scene, snapshot.Camera, snapshot.Light,
                snapshot.SelfShadow, snapshot.MorphChannels, new(), snapshot.ResolvedCamera);
        var initial = project.CaptureRenderSnapshot();
        Assert.Equal("mmd", project.RenderingStyles.DefaultStyle.StableId);
        Assert.Empty(project.RenderingStyles.ModelOverrides);
        Assert.All(initial.RenderingStyles, binding => Assert.Equal("mmd", binding.Preset.StableId));
        var baseline = D3D12ViewRenderingStyles.Resolve(Ready(initial), catalog, initial.RenderingStyles, styleCatalog: viewCatalog);
        Assert.NotNull(baseline.Scene.Options);
        Assert.True(baseline.SurfaceStyles.IsDefault);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, baseline.SurfaceStyles));

        project.SetModelRenderingStyle(first.Id, selected, TestContext.Current.CancellationToken);
        var changed = project.CaptureRenderSnapshot();
        Assert.Same(initial.Scene, changed.Scene);
        Assert.Equal("mmd", project.RenderingStyles.DefaultStyle.StableId);
        Assert.Equal("mmd", project.GetRenderingStyle(second.Id).StableId);
        var explicitStyle = D3D12ViewRenderingStyles.Resolve(Ready(changed), catalog, changed.RenderingStyles, styleCatalog: viewCatalog);
        Assert.Equal(new[] { expected, "mmd" }, explicitStyle.SurfaceStyles.StyleIds);
        Assert.Equal(baseline.Scene.Materials[1], explicitStyle.Scene.Materials[1]);
        Assert.All(explicitStyle.SurfaceStyles.CharacterMaterials,
            material => Assert.Equal(D3D12CharacterMaterialRole.Body, material.Role));

        project.SetModelRenderingStyle(first.Id, null, TestContext.Current.CancellationToken);
        var restored = project.CaptureRenderSnapshot();
        var restoredStyle = D3D12ViewRenderingStyles.Resolve(Ready(restored), catalog, restored.RenderingStyles, styleCatalog: viewCatalog);
        Assert.Same(initial.Scene, restored.Scene);
        Assert.Empty(project.RenderingStyles.ModelOverrides);
        Assert.All(restored.RenderingStyles, binding => Assert.Equal("mmd", binding.Preset.StableId));
        Assert.True(restoredStyle.SurfaceStyles.IsDefault);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(baseline.SurfaceStyles, restoredStyle.SurfaceStyles));
        Assert.Equal(baseline.Scene.Materials, restoredStyle.Scene.Materials);
        Assert.Equal("mmd", project.RenderingStyles.DefaultStyle.StableId);
    }

    [Fact]
    public void AbsentViewMetadataUsesTheUnmodifiedMmdSurfaceEvenWithAllFourFamiliesAvailable()
    {
        var source = Frame();
        var catalog = TestStylePlugins.AllPresetCatalog;
        Assert.Single(D3D12StylePresetCatalog.BuiltIn.List());
        Assert.Equal(5, catalog.List().Count);
        Assert.All(catalog.List(), entry => Assert.True(entry.IsAvailable));

        var resolved = D3D12ViewRenderingStyles.Resolve(source, catalog, [], styleCatalog: TestStylePlugins.AllViewCatalog);

        Assert.Same(source, resolved.Scene);
        Assert.True(resolved.SurfaceStyles.IsDefault);
        Assert.All(resolved.SurfaceStyles.StyleIds, styleId => Assert.Equal("mmd", styleId));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, resolved.SurfaceStyles));
        Assert.Empty(resolved.Diagnostics);
        Assert.All(resolved.SurfaceStyles.CharacterMaterials,
            material => Assert.Equal(D3D12CharacterMaterialRole.Body, material.Role));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Gpu")]
    public void ExplicitDefaultMetadataAndAllMmdKeepActualRawPixelsForLegacyAndModernFrames(bool modern)
    {
        const int width = 129, height = 97;
        var original = D3D12OriginalOutlineTests.Box();
        var mesh = original.Surface.Snapshot;
        const float nDotL = .175f;
        var normal = new Vector3(nDotL, 0, -MathF.Sqrt(1 - nDotL * nDotL));
        var surface = new OriginalStyleScene(new SceneSnapshot(
            mesh.Vertices.ToArray().Select(vertex => vertex with { Normal = normal }).ToArray(), mesh.Indices,
            [mesh.Materials[0] with { Diffuse = new(.6f, .4f, .2f, 1) }], camera: mesh.Camera,
            lightDirection: Vector3.UnitX), [new(new(.2f))], original.Surface.Profile);
        var raw = new OriginalFrameScene(surface, original.Outline, original.OutlineOptions,
            modern ? D3D12OriginalFrameTests.Options() : null);
        var binding = new SceneRenderingStyleBinding(Guid.NewGuid(), new("mmd"), ModelRole.Character, 0, 1, true);
        var selected = D3D12ViewRenderingStyles.Resolve(raw, D3D12StylePresetCatalog.BuiltIn, [binding]);
        var absent = D3D12ViewRenderingStyles.Resolve(raw, D3D12StylePresetCatalog.BuiltIn, []);
        using var session = new D3D12FrameSession();
        Assert.False(session.IsWarp);
        var baseline = session.Capture(raw, width, height);
        int center = (height / 2 * width + width / 2) * 4;
        Assert.NotEqual(baseline.Rgba.AsSpan(0, 4).ToArray(), baseline.Rgba.AsSpan(center, 4).ToArray());
        Assert.Equal(baseline.Rgba,
            session.Capture(selected.Scene, width, height, surfaceStyles: selected.SurfaceStyles).Rgba);
        Assert.Equal(baseline.Rgba,
            session.Capture(absent.Scene, width, height, surfaceStyles: absent.SurfaceStyles).Rgba);
        Assert.Equal(baseline.Rgba,
            session.Capture(raw, width, height, surfaceStyles: new(["mmd"])).Rgba);
        Assert.Equal(baseline.Rgba, session.Capture(raw, width, height).Rgba);
        long readbacks = session.Runtime.Readbacks;
        using var rendered = session.Render(selected.Scene, width, height, surfaceStyles: selected.SurfaceStyles);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        Assert.All(raw.Materials, flags =>
        {
            Assert.Null(flags.CharacterToonResponse);
            Assert.Null(flags.CharacterOutlineStyle);
            Assert.Null(flags.GiToonResponseOverride);
        });
    }

    private static OriginalFrameScene Frame(bool modern = true)
    {
        var frame = D3D12OriginalOutlineTests.Box();
        return new(frame.Surface, frame.Outline, frame.OutlineOptions,
            modern ? D3D12OriginalFrameTests.Options() : null, frame.Materials);
    }
}
