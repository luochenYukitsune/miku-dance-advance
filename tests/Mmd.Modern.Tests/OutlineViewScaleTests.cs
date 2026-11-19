using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OutlineViewScaleTests
{
    private static readonly Guid First = Guid.Parse("370afcd1-3fb7-4cac-b993-0258d30a6e11");
    private static readonly Guid Second = Guid.Parse("370afcd1-3fb7-4cac-b993-0258d30a6e12");
    private static readonly Guid Hidden = Guid.Parse("370afcd1-3fb7-4cac-b993-0258d30a6e13");

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void QuarterScaleUsesTheSelectedStylesEffectiveWidthOnlyForTheSelectedInstanceMaterial(string preset)
    {
        var source = Frame();
        var objects = Objects(preset);
        var baseline = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, objects, styleCatalog: TestStylePlugins.AllViewCatalog);
        var result = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, objects,
            outlineWidths: [new(First, 0, .25f)], styleCatalog: TestStylePlugins.AllViewCatalog);

        Assert.Equal(baseline.Scene.Materials[0].CharacterOutlineStyle, result.Scene.Materials[0].CharacterOutlineStyle);
        Assert.Same(baseline.Scene.Outline, result.Scene.Outline);
        for (int index = 1; index < source.Materials.Count; index++)
        {
            Assert.Equal(baseline.Scene.Materials[index], result.Scene.Materials[index]);
            Assert.Equal(baseline.Scene.Outline!.Materials[index], result.Scene.Outline!.Materials[index]);
        }
        Assert.Equal(baseline.Scene.Materials[0] with
        { OutlineWidthScale = .25f }, result.Scene.Materials[0]);
        Assert.Equal(baseline.SurfaceStyles.StyleIds, result.SurfaceStyles.StyleIds);
        Assert.Same(source.Surface, result.Scene.Surface);
        Assert.Same(source.Outline!.Vertices, result.Scene.Outline!.Vertices);
        Assert.Equal(source.Outline.VertexRevision, result.Scene.Outline.VertexRevision);
        Assert.Same(source.OutlineOptions, result.Scene.OutlineOptions);
        Assert.Same(source.Options, result.Scene.Options);
        Assert.Same(source.Lighting, result.Scene.Lighting);
        Assert.Same(source.EditorOverlay, result.Scene.EditorOverlay);
        Assert.Equal(2, source.Outline.Materials[0].Width);
        Assert.Null(source.Materials[0].CharacterOutlineStyle);
    }

    [Fact]
    public void DisabledOutlineDoesNotRejectStraightAlphaOutputForUnusedCoverage()
    {
        var source = Frame();
        var flags = source.Materials.ToArray();
        var edges = source.Outline!.Materials.ToArray();
        edges[0] = edges[0] with { Color = new(0, 0, 0, 2) };
        var enabled = new OriginalFrameScene(source.Surface, new(source.Outline.Vertices, edges), source.OutlineOptions,
            source.Options, flags, source.Lighting, source.EditorOverlay);
        Assert.Throws<NotSupportedException>(() => D3D12AlphaResolvePass.ValidateCoverage(enabled));
        flags[0] = flags[0] with { OutlineWidthScale = 0 };
        var disabled = new OriginalFrameScene(source.Surface, enabled.Outline, source.OutlineOptions,
            source.Options, flags, source.Lighting, source.EditorOverlay);
        D3D12AlphaResolvePass.ValidateCoverage(disabled);
    }

    [Fact]
    public void IdentityScalePreservesTheCompleteInputAndNullOutlineStyle()
    {
        var source = Frame();
        var result = D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn, Objects(),
            outlineWidths: [new(First, 0), new(First, 1), new(Second, 0), new(Second, 1)]);

        Assert.Same(source, result);
        Assert.Null(result.Materials[0].CharacterOutlineStyle);
        Assert.Null(result.Materials[1].CharacterOutlineStyle);
    }

    [Fact]
    public void OriginalOutlineKeepsSourceColorEligibilityVertexScaleAndOriginalPass()
    {
        var source = Frame();
        var result = D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn, Objects(),
            outlineWidths: [new(First, 0, .25f), new(First, 1, .25f)]);

        Assert.Equal(source.Materials.Select((value, index) => index < 2 ? value with { OutlineWidthScale = .25f } : value), result.Materials);
        Assert.Null(result.Materials[0].CharacterOutlineStyle);
        Assert.Null(result.Materials[1].CharacterOutlineStyle);
        Assert.Same(source.Outline, result.Outline);
        Assert.Equal(source.Outline!.Materials[0], result.Outline!.Materials[0]);
        Assert.Equal(source.Outline.Materials[1], result.Outline.Materials[1]);
        Assert.False(result.Outline.Materials[1].DrawEnabled);
        Assert.Same(source.Outline.Vertices, result.Outline.Vertices);
        Assert.Equal(source.Outline.VertexRevision, result.Outline.VertexRevision);
    }

    [Fact]
    public void ZeroClosesBothOutlinePathsAndNonzeroScaleNeverEnablesDisabledOutlines()
    {
        var source = Frame();
        var zero = D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn, Objects(),
            outlineWidths: [new(First, 0, 0), new(Second, 0, 0)]);
        Assert.Equal(0, zero.Materials[0].OutlineWidthScale);
        Assert.Equal(0, zero.Materials[2].OutlineWidthScale);
        Assert.Same(source.Outline, zero.Outline);
        Assert.Same(source.Materials[2].CharacterOutlineStyle, zero.Materials[2].CharacterOutlineStyle);

        var disabled = D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn, Objects(),
            outlineWidths: [new(First, 1, 4), new(Second, 1, 4)]);
        Assert.False(disabled.Outline!.Materials[1].DrawEnabled);
        Assert.False(disabled.Materials[3].CharacterOutlineStyle!.Enabled);
        Assert.Equal(4, disabled.Materials[1].OutlineWidthScale);
        Assert.Equal(4, disabled.Materials[3].OutlineWidthScale);
        Assert.Equal(4, disabled.Outline.Materials[1].Width);
        Assert.Equal(.4f, disabled.Materials[3].CharacterOutlineStyle!.Width);
        Assert.Equal(source.Materials[3].CharacterOutlineStyle!.Color, disabled.Materials[3].CharacterOutlineStyle!.Color);
    }

    [Fact]
    public void WidthSelectionDoesNotRequireOrSynthesizeCharacterMaterialTreatment()
    {
        var source = Frame();
        var objects = Objects().Select(value => value with { Role = ModelRole.Environment }).ToArray();
        var baseline = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects);
        var result = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects,
            outlineWidths: [new(First, 0, .25f)]);

        Assert.Equal(source.Materials[0] with { OutlineWidthScale = .25f }, result.Scene.Materials[0]);
        for (int index = 1; index < source.Materials.Count; index++) Assert.Same(source.Materials[index], result.Scene.Materials[index]);
        Assert.Equal(baseline.SurfaceStyles.StyleIds, result.SurfaceStyles.StyleIds);
        Assert.Equal(baseline.SurfaceStyles.CharacterMaterials, result.SurfaceStyles.CharacterMaterials);
        Assert.Same(source.Outline, result.Scene.Outline);
    }

    [Fact]
    public void NoGeometryAndHiddenLocalSelectionsStayDormant()
    {
        var source = Frame();
        var noOutline = new OriginalFrameScene(source.Surface, options: source.Options,
            materials: source.Materials, lighting: source.Lighting, editorOverlay: source.EditorOverlay);
        var result = D3D12ViewRenderingStyles.Apply(noOutline, D3D12StylePresetCatalog.BuiltIn, Objects(),
            outlineWidths: [new(First, 0, 0), new(Second, 0, .25f)]);
        Assert.Same(noOutline, result);
        Assert.Null(result.Outline);
        Assert.Same(source, D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn, Objects(),
            outlineWidths: [new(Hidden, int.MaxValue, .25f)]));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("visible-range")]
    [InlineData("negative-index")]
    [InlineData("empty-id")]
    [InlineData("duplicate")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("negative")]
    [InlineData("above-max")]
    [InlineData("null")]
    public void PreflightAndResolveRejectInvalidSelectionsBeforeChangingTheSource(string invalid)
    {
        var source = Frame();
        D3D12ViewOutlineWidthBinding[] widths = invalid switch
        {
            "unknown" => [new(Guid.NewGuid(), 0)],
            "visible-range" => [new(First, 2)],
            "negative-index" => [new(First, -1)],
            "empty-id" => [new(Guid.Empty, 0)],
            "duplicate" => [new(First, 0), new(First, 0, .25f)],
            "nan" => [new(First, 0, float.NaN)],
            "infinity" => [new(First, 0, float.PositiveInfinity)],
            "negative" => [new(First, 0, -.01f)],
            "above-max" => [new(First, 0, 4.01f)],
            "null" => [null!],
            _ => throw new InvalidOperationException(),
        };
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(source,
            D3D12StylePresetCatalog.BuiltIn, Objects(), outlineWidths: widths));
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(source,
            D3D12StylePresetCatalog.BuiltIn, Objects(), outlineWidths: widths));
        Assert.Equal(2, source.Outline!.Materials[0].Width);
        Assert.Null(source.Materials[0].CharacterOutlineStyle);
    }

    [Fact]
    public void DetachedBindingsFreezeHostSelectionAndReuseTheirFrozenSnapshot()
    {
        D3D12ViewOutlineWidthBinding[] host = [new(First, 0, .25f)];
        var detached = D3D12ViewOutlineWidths.Detach(host);
        host[0] = new(Second, 1, 0);
        Assert.Equal(new D3D12ViewOutlineWidthBinding(First, 0, .25f), Assert.Single(detached));
        Assert.Same(detached, D3D12ViewOutlineWidths.Detach(detached));
    }

    [Fact]
    public void SharedVerticesUseAnIndependentDrawFactorAndInwardPositionsKeepTheirFixedOffset()
    {
        var source = Frame();
        var vertex = source.Outline!.Vertices[0];
        Vector4 expanded = new(vertex.Position + new Vector3(0, 0, 2), 1);
        Assert.Equal(new Vector3(expanded.X, expanded.Y, expanded.Z), D3D12OutlineDrawScale.Position(vertex, expanded, 1));
        Assert.Equal(vertex.Position + new Vector3(0, 0, .5f), D3D12OutlineDrawScale.Position(vertex, expanded, .25f));
        Assert.Equal(new Vector3(expanded.X, expanded.Y, expanded.Z),
            D3D12OutlineDrawScale.Position(vertex with { Mode = OriginalOutlineVertexMode.Inward }, expanded, .25f));
        Assert.Equal(new Vector3(expanded.X, expanded.Y, expanded.Z), D3D12OutlineDrawScale.Position(vertex, expanded, 1));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-.01f)]
    [InlineData(4.01f)]
    public void FrameRejectsInvalidIndependentOutlineFactors(float scale)
    {
        var source = Frame();
        Assert.ThrowsAny<ArgumentException>(() => new OriginalFrameScene(source.Surface, source.Outline,
            source.OutlineOptions, source.Options, source.Materials.Select(value => value with { OutlineWidthScale = scale })));
    }

    private static SceneRenderingStyleBinding[] Objects(string preset = "mmd") =>
    [new(First, new(preset), ModelRole.Character, 0, 2, true),
        new(Second, new(preset), ModelRole.Character, 2, 2, true),
        new(Hidden, new(preset), ModelRole.Character, 4, 0, false)];

    private static OriginalFrameScene Frame()
    {
        var vertices = Enumerable.Range(0, 12).Select(index => new SceneVertex(
            new Vector3(index / 3 * 3 + index % 3, index % 3 == 1 ? 1 : -1, 0), Vector3.UnitZ, Vector2.Zero)).ToArray();
        var materials = Enumerable.Range(0, 4).Select(index =>
            new SceneMaterial("same-name.pmx", index * 3, 3, new(.8f, .6f, .4f, 1), new(.1f), new(.2f), 8)).ToArray();
        var snapshot = new SceneSnapshot(vertices, Enumerable.Range(0, 12).Select(index => (uint)index).ToArray(), materials);
        var surface = new OriginalStyleScene(snapshot, materials.Select(_ => new OriginalStyleMaterialBinding(new(.3f))));
        var outline = new OriginalOutlineData(vertices.Select((vertex, index) => new OriginalOutlineVertex(
            vertex.Position, vertex.Normal, OriginalOutlineVertexMode.Expand, .3f + index * .1f, index / 3)),
            materials.Select((_, index) => new OriginalOutlineMaterial(2 + index * 2,
                new(.1f + index * .1f, .2f, .3f, .8f), index != 1)));
        var flags = Enumerable.Range(0, 4).Select(index => new OriginalFrameMaterialFlags(index % 2 == 0,
            index % 2 != 0, true, SurfaceEnabled: index != 1, ShadowDoubleSided: index == 2, CullTransparentSurface: index == 3)
        {
            PreserveDirectLightPalette = true,
            GiToonResponseOverride = index % 2 == 0 ? RayGiToonResponse.Continuous : null,
            CharacterOutlineStyle = index switch { 2 => new(.6f), 3 => new(.4f, new Vector4(.2f, .3f, .4f, .8f), false), _ => null },
        });
        return new(surface, outline, OriginalOutlineTests.Options(), new(OriginalAlphaBlendMode.Normal, true, false), flags,
            SceneLightingSnapshot.Empty, new ViewportOverlaySnapshot([]));
    }
}
