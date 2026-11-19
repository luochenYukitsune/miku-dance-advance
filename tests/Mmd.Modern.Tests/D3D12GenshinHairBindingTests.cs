using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12GenshinHairBindingTests
{
    [Fact]
    public void ExplicitHairDefaultsAppendToTheEstablishedMaterialContract()
    {
        Assert.Equal(0, (int)D3D12CharacterMaterialRole.Body);
        Assert.Equal(1, (int)D3D12CharacterMaterialRole.Face);
        Assert.Equal(2, (int)D3D12CharacterMaterialRole.Eye);
        Assert.Equal(3, (int)D3D12CharacterMaterialRole.Hair);
        var mode = TestStylePlugins.Genshin.MaterialModes.Single(m => m.ModeId == "hair");
        Assert.Equal(1.0, mode.Parameters.Single(p => p.Key == "globalSpecularScale").DefaultNumber);
        Assert.Equal(10.0, mode.Parameters.Single(p => p.Key == "shininess").DefaultNumber);
        Assert.Equal(0.1, mode.Parameters.Single(p => p.Key == "multiplier").DefaultNumber);
        Assert.Equal(1.0, mode.Parameters.Single(p => p.Key == "intensity").DefaultNumber);
        var binding = TestStylePlugins.GenshinHair(0);
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair, Frame(Texture()), binding).IsValid);
        var oldCaller = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face, .35f, .6f, .2f);
        Assert.Null(oldCaller.PluginDraw);
        var map = Map(binding);
        map.Validate(Frame(Texture()));
        Assert.Equal("hair", map.ResolveShaderModule(0)!.ModuleId);
        Assert.Single(map.GetPluginDraw(0)!.TextureBindings!);
        Assert.Equal(1, map.EffectiveNormalShadeStrength(0));
        Assert.Equal(1, map.EffectiveGeometricShadowStrength(0));
    }

    public static IEnumerable<object[]> InvalidScalars()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.01f })
        {
            yield return [TestStylePlugins.GenshinHair(0, globalSpecularScale: value)];
            yield return [TestStylePlugins.GenshinHair(0, shininess: value)];
            yield return [TestStylePlugins.GenshinHair(0, multiplier: value)];
            yield return [TestStylePlugins.GenshinHair(0, intensity: value)];
        }
        yield return [TestStylePlugins.GenshinHair(0, globalSpecularScale: 2.01f)];
        yield return [TestStylePlugins.GenshinHair(0, shininess: 64.01f)];
        yield return [TestStylePlugins.GenshinHair(0, multiplier: 16.01f)];
        yield return [TestStylePlugins.GenshinHair(0, intensity: 16.01f)];
    }

    [Theory, MemberData(nameof(InvalidScalars))]
    public void NonFiniteAndOutOfDomainScalarsAreRejected(StyleMaterialBindingData binding)
    {
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair, Frame(Texture()), binding).IsValid);
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([],
            [TestStylePlugins.Bind(Guid.NewGuid(), 0, binding)], TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void DomainEndpointsAreAcceptedWithoutSilentlyClamping()
    {
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair, Frame(Texture()),
            TestStylePlugins.GenshinHair(0, 0, 0, 0, 0)).IsValid);
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair, Frame(Texture()),
            TestStylePlugins.GenshinHair(0, 2, 64, 16, 16)).IsValid);
    }

    [Theory, InlineData(-1), InlineData(1), InlineData(int.MaxValue)]
    public void TextureSlotMustExistInTheActualComposedTable(int index)
    {
        var binding = TestStylePlugins.GenshinHair(index);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair, Frame(Texture()), binding).IsValid);
        if (index >= 0)
            Assert.Throws<ArgumentOutOfRangeException>(() => Map(binding).Validate(Frame(Texture())));
        else
            Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([],
                [TestStylePlugins.Bind(Guid.NewGuid(), 0, binding)], TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void HairInputRequiresTheExplicitRoleAndMatchingFamilyAndCannotShareFaceSdf()
    {
        var binding = TestStylePlugins.GenshinHair(0);
        foreach (var role in new[] { StyleMaterialRole.Body, StyleMaterialRole.Face, StyleMaterialRole.Eye })
            Assert.False(T8bTestHelpers.ValidateGenshin(role, Frame(Texture()), binding).IsValid);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair, Frame(Texture()),
            TestStylePlugins.FaceSdf("genshin", 0, 1), binding).IsValid);
        Assert.Throws<NotSupportedException>(() =>
            new D3D12SurfaceStyleMap(["mmd"],
                [new(D3D12CharacterMaterialRole.Hair, PluginDraw: T8bTestHelpers.BuildGenshinDrawData(D3D12CharacterMaterialRole.Hair, 0.35f, 1f, 0.2f, binding))],
                TestStylePlugins.AllPresetCatalog).Validate(Frame(Texture())));
        foreach (string styleId in new[] { "star-rail", "wuthering-waves", "endfield" })
            Assert.False(T8bTestHelpers.ValidateStyle(TestStylePlugins.GetStyle(styleId), StyleMaterialRole.Hair, Frame(Texture()), binding).IsValid);
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([],
            [TestStylePlugins.Bind(Guid.NewGuid(), 0, TestStylePlugins.GenshinHair(0, globalSpecularScale: float.NaN))],
            TestStylePlugins.AllViewCatalog));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void OrdinaryHairKeepsBodyValuesAcrossAllFamilies(string styleId)
    {
        var hair = T8bTestHelpers.CreateDirectMap([styleId], (D3D12CharacterMaterialRole.Hair, null, 0.35f, 1f, 0.2f));
        var body = T8bTestHelpers.CreateDirectMap(styleId);
        hair.Validate(Frame(Texture()));
        Assert.Null(hair.GetPluginDraw(0)?.TextureBindings);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(body, hair));
        Assert.Equal(body.EffectiveNormalShadeStrength(0), hair.EffectiveNormalShadeStrength(0));
        Assert.Equal(body.EffectiveGeometricShadowStrength(0), hair.EffectiveGeometricShadowStrength(0));
    }

    [Fact]
    public void ConsumedKeyIncludesEveryActiveScalarAndBindingRemoval()
    {
        var binding = TestStylePlugins.GenshinHair(0);
        var first = Map(binding);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(binding with { })));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(null)));
        foreach (var changed in new[]
        {
            TestStylePlugins.GenshinHair(0, globalSpecularScale: .5f),
            TestStylePlugins.GenshinHair(0, shininess: 2),
            TestStylePlugins.GenshinHair(0, multiplier: .2f),
            TestStylePlugins.GenshinHair(0, intensity: 2),
            TestStylePlugins.GenshinHair(1),
        })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(changed)));
    }

    [Fact]
    public void DetachedKeysCompareOnlyNumericalRedBlueDimensionsAndEffectiveSlots()
    {
        var binding = TestStylePlugins.GenshinHair(0);
        var map = Map(binding);
        var first = map.CaptureResolvedInputs(Frame(Texture()));
        Assert.NotSame(map, first);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(binding with { }).CaptureResolvedInputs(Frame(Texture("new label")))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(r: 65)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(b: 127)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(new SceneTexture("same channels larger", 2, 1,
            [64, 17, 128, 0, 64, 17, 128, 0])))));
        var rebased = Map(TestStylePlugins.GenshinHair(1)).CaptureResolvedInputs(Frame(Texture(r: 12), Texture("rebased")));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture()))));
        // Capturing a later frame never replaces first's detached submitted comparison.
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(r: 1)))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture()))));
    }

    [Fact]
    public void MixedFaceAndHairDetachedKeysRetainBothIndependentDataSets()
    {
        var hair = TestStylePlugins.GenshinHair(1);
        var face = TestStylePlugins.FaceSdf("star-rail", 0, encoding: "GrayRed", transition: 1f, offset: 1f);
        var map = T8bTestHelpers.CreateDirectMap(
            ["star-rail", "genshin"],
            (D3D12CharacterMaterialRole.Face, [face], 0.35f, 1f, 0.2f),
            (D3D12CharacterMaterialRole.Hair, [hair], 0.35f, 1f, 0.2f));
        var first = map.CaptureResolvedInputs(TwoMaterials(Texture(), Texture()));
        Assert.Equal("face-sdf", map.ResolveShaderModule(0)!.ModuleId);
        Assert.Equal("hair", map.ResolveShaderModule(1)!.ModuleId);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(TwoMaterials(Texture("renamed"), Texture("renamed 2")))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(TwoMaterials(Texture(r: 1), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(TwoMaterials(Texture(), Texture(b: 1)))));
    }

    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding, string styleId = "genshin")
        => T8bTestHelpers.CreateDirectMap([styleId], (D3D12CharacterMaterialRole.Hair, binding is null ? null : [binding], 0.35f, 1f, 0.2f));

    private static SceneTexture Texture(string name = "original packed data", byte r = 64, byte g = 17, byte b = 128, byte a = 0)
        => new(name, 1, 1, [r, g, b, a]);

    private static OriginalFrameScene Frame(params SceneTexture[] textures) => MakeFrame(textures, twoMaterials: false);
    private static OriginalFrameScene TwoMaterials(params SceneTexture[] textures) => MakeFrame(textures, twoMaterials: true);
    private static OriginalFrameScene MakeFrame(SceneTexture[] textures, bool twoMaterials)
    {
        SceneVertex[] vertices = [new(new(-1, -1, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, 0), -Vector3.UnitZ, Vector2.Zero), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.Zero)];
        var materials = new List<SceneMaterial> { new("explicit hair", 0, 3, Vector4.One, Vector3.One, Vector3.Zero, 10) };
        if (twoMaterials) materials.Add(new("second explicit material", 3, 3, Vector4.One, Vector3.One, Vector3.Zero, 10));
        var snapshot = new SceneSnapshot(vertices, twoMaterials ? [0, 2, 1, 0, 2, 1] : [0, 2, 1], materials, textures);
        return new(new(snapshot, Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true), materials.Count).ToArray(),
            new(Vector3.One, Vector3.One, Matrix4x4.Identity)), options: new(OriginalAlphaBlendMode.Normal, true, false));
    }
}
