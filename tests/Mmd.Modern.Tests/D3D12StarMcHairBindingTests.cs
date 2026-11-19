using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12StarMcHairBindingTests
{
    [Fact]
    public void ExplicitTwoMaskDefaultsAppendWithoutChangingExistingMaterialCalls()
    {
        var binding = T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1);
        Assert.Equal("Blue", binding.StringValues!["highlightChannel"]);
        Assert.Equal(0, binding.NumberValues!["highlightCoefficient"]);
        Assert.Equal(2, binding.NumberValues["highlightBrightness"]);
        Assert.Equal(.5f, binding.NumberValues["shadowCoefficient"]);
        Assert.Equal(.5f, binding.NumberValues["shadowSoftness"]);
        Assert.Equal(.1f, binding.NumberValues["intensity"]);
        Assert.True(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, binding, D3D12CharacterMaterialRole.Hair).IsValid);

        var oldCaller = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair, .35f, .6f, .2f, null,
            new(0.5f, 0.85f, 0f, Vector3.One, 0.35f, Vector3.One, 0f, 0.5f, 0f, 1f, 1f, 1f));
        Assert.Null(oldCaller.PluginDraw);
        foreach (var family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        {
            var famBinding = T8cTestHelpers.StarMcHairBinding(family, 0, 1);
            Assert.True(T8cTestHelpers.ValidateMaterialBinding(family, famBinding, D3D12CharacterMaterialRole.Hair).IsValid);
            var map = Map(famBinding, family);
            map.Validate(Frame(Texture(), Texture()));
            var draw = Assert.IsType<D3D12PluginDrawData>(map.GetPluginDraw(0));
            Assert.Equal("hair", draw.ShaderModuleId);
            Assert.Equal([0, 1], draw.TextureBindings!.Select(t => t.SceneTextureIndex).ToArray());
            Assert.Equal(1, map.EffectiveNormalShadeStrength(0));
            Assert.Equal(1, map.EffectiveGeometricShadowStrength(0));
        }
    }

    public static IEnumerable<object[]> InvalidScalars()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, highlightCoefficient: value)];
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, highlightBrightness: value)];
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, shadowCoefficient: value)];
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, shadowSoftness: value)];
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, intensity: value)];
        }
        foreach (float value in new[] { -.01f, 1.01f })
        {
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, highlightCoefficient: value)];
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, shadowCoefficient: value)];
        }
        foreach (float value in new[] { -1.01f, 10.01f })
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, highlightBrightness: value)];
        foreach (float value in new[] { .099f, 1.01f })
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, shadowSoftness: value)];
        foreach (float value in new[] { -.01f, 16.01f })
            yield return [T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, intensity: value)];
    }

    [Theory, MemberData(nameof(InvalidScalars))]
    public void NonFiniteAndOutOfDomainScalarsAreRejected(StyleMaterialBindingData binding)
    {
        foreach (var family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        {
            var styled = binding with { StyleId = family };
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, styled, D3D12CharacterMaterialRole.Hair).IsValid);
        }
    }

    [Fact]
    public void InclusiveAdapterEndpointsRetainNegativeAndLargeBrightness()
    {
        foreach (var family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        {
            var minimum = T8cTestHelpers.StarMcHairBinding(family, 0, 1, highlightCoefficient: 0, highlightBrightness: -1,
                shadowCoefficient: 0, shadowSoftness: .1f, intensity: 0);
            var maximum = T8cTestHelpers.StarMcHairBinding(family, 0, 1, highlightCoefficient: 1, highlightBrightness: 10,
                shadowCoefficient: 1, shadowSoftness: 1, intensity: 16);
            Assert.True(T8cTestHelpers.ValidateMaterialBinding(family, minimum, D3D12CharacterMaterialRole.Hair).IsValid);
            Assert.True(T8cTestHelpers.ValidateMaterialBinding(family, maximum, D3D12CharacterMaterialRole.Hair).IsValid);
            var minFloats = MemoryMarshal.Cast<byte, float>(T8cTestHelpers.BuildPluginDraw(family, minimum, D3D12CharacterMaterialRole.Hair)!.ConstantBufferData.Span);
            var maxFloats = MemoryMarshal.Cast<byte, float>(T8cTestHelpers.BuildPluginDraw(family, maximum, D3D12CharacterMaterialRole.Hair)!.ConstantBufferData.Span);
            Assert.Equal(-1, minFloats[1]);
            Assert.Equal(10, maxFloats[1]);
        }
    }

    [Theory, InlineData(-1), InlineData(2), InlineData(int.MaxValue)]
    public void BothTextureSlotsMustExistInTheActualComposedTable(int index)
    {
        var frame = Frame(Texture(), Texture());
        foreach (var family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        {
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, T8cTestHelpers.StarMcHairBinding(family, index, 1),
                D3D12CharacterMaterialRole.Hair, sourceTextureCount: 2).IsValid);
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, T8cTestHelpers.StarMcHairBinding(family, 0, index),
                D3D12CharacterMaterialRole.Hair, sourceTextureCount: 2).IsValid);
            if (index >= 0)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => Map(T8cTestHelpers.StarMcHairBinding(family, index, 1), family).Validate(frame));
                Assert.Throws<ArgumentOutOfRangeException>(() => Map(T8cTestHelpers.StarMcHairBinding(family, 0, index), family).Validate(frame));
            }
        }
    }

    [Fact]
    public void HairRoleAndExclusiveFamilyPayloadAreRequired()
    {
        foreach (var family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        {
            var binding = T8cTestHelpers.StarMcHairBinding(family, 0, 1);
            foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
                Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, binding, role).IsValid);
            var style = TestStylePlugins.GetStyle(family);
            var combinedFace = style.ValidateMaterial(new StyleMaterialValidationContext(
                Guid.Empty, 0, null, StyleMaterialRole.Hair, [binding, T8cTestHelpers.FaceSdfBinding(family, 0)], 4));
            Assert.False(combinedFace.IsValid);
            var combinedBody = style.ValidateMaterial(new StyleMaterialValidationContext(
                Guid.Empty, 0, null, StyleMaterialRole.Hair, [binding, T8cTestHelpers.StarMcBodyBinding(family, 0, 1, 2, 3)], 4));
            Assert.False(combinedBody.IsValid);
        }

        foreach (var other in new[] { T8cTestHelpers.GenshinStyleId, T8cTestHelpers.EndfieldStyleId })
        {
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(other, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1), D3D12CharacterMaterialRole.Hair).IsValid);
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(other, T8cTestHelpers.StarMcHairBinding(other, 0, 1), D3D12CharacterMaterialRole.Hair).IsValid);
        }

        Assert.Throws<NotSupportedException>(() => T8cTestHelpers.CreateDirectMap(
            [T8cTestHelpers.MmdStyleId],
            [T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.StarRailStyleId, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1), D3D12CharacterMaterialRole.Hair)])
            .Validate(Frame(Texture(), Texture())));
    }

    [Fact]
    public void NumericalAlphaIsExplicitAndOnlySupportedForWuthering()
    {
        var wuthering = T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, "Alpha");
        Assert.True(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.WutheringStyleId, wuthering, D3D12CharacterMaterialRole.Hair).IsValid);
        var map = Map(wuthering, T8cTestHelpers.WutheringStyleId);
        map.Validate(Frame(Texture(), Texture()));
        Assert.Equal("hair", map.GetPluginDraw(0)!.ShaderModuleId);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId,
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, "Alpha"),
            D3D12CharacterMaterialRole.Hair).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.WutheringStyleId,
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, ((D3D12CharacterMaterialRole)123).ToString()),
            D3D12CharacterMaterialRole.Hair).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.WutheringStyleId,
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, "Green"),
            D3D12CharacterMaterialRole.Hair).IsValid);
    }

    [Theory]
    [InlineData(T8cTestHelpers.MmdStyleId)]
    [InlineData(T8cTestHelpers.GenshinStyleId)]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    [InlineData(T8cTestHelpers.EndfieldStyleId)]
    public void OrdinaryHairKeepsTheEstablishedBodyValuesAcrossEveryFamily(string family)
    {
        var hair = Map(null, family);
        var body = T8cTestHelpers.CreateDirectMap([family]);
        hair.Validate(Frame(Texture()));
        Assert.Null(hair.GetPluginDraw(0));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(body, hair));
        Assert.Same(hair, hair.CaptureResolvedInputs(Frame(Texture())));
        Assert.Equal(body.EffectiveNormalShadeStrength(0), hair.EffectiveNormalShadeStrength(0));
        Assert.Equal(body.EffectiveGeometricShadowStrength(0), hair.EffectiveGeometricShadowStrength(0));
    }

    [Fact]
    public void UncapturedKeysIncludeBothSlotsEveryScalarTheChannelAndRemoval()
    {
        var binding = T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1);
        var first = Map(binding, T8cTestHelpers.WutheringStyleId);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(binding with { }, T8cTestHelpers.WutheringStyleId)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(null, T8cTestHelpers.WutheringStyleId)));
        foreach (var changed in new[] {
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 2, 1),
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 2),
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, "Alpha"),
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, highlightCoefficient: .2f),
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, highlightBrightness: -1),
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, shadowCoefficient: .25f),
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, shadowSoftness: .6f),
            T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, intensity: .3f) })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(changed, T8cTestHelpers.WutheringStyleId)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(
            Map(T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, highlightCoefficient: 0, highlightBrightness: 0, shadowCoefficient: 0, intensity: 0)),
            Map(T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, highlightCoefficient: -0f, highlightBrightness: -0f, shadowCoefficient: -0f, intensity: -0f))));
    }

    [Fact]
    public void DetachedBlueKeysConsumeTextureBytesAndRebaseAcrossOwnerReplacements()
    {
        var map = Map(T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1));
        var first = map.CaptureResolvedInputs(Frame(Texture(), Texture()));
        Assert.NotSame(first, map);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture("new owner"), Texture("new shadow owner")))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(r: 0, g: 200, a: 255), Texture(g: 200, b: 0, a: 255)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(b: 129), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(), Texture(r: 65)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(WideTexture(), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(), WideTexture()))));
        var rebased = Map(T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 2, 0))
            .CaptureResolvedInputs(Frame(Texture("shadow rebased"), Texture(b: 0), Texture("highlight rebased")));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        // Later captures of the reusable public map cannot replace the submitted detached data.
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(b: 1), Texture(r: 1)))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(), Texture()))));
    }

    [Fact]
    public void DetachedAlphaKeysRetainSelectionAndTextureByteIdentity()
    {
        var map = Map(T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, "Alpha"), T8cTestHelpers.WutheringStyleId);
        var first = map.CaptureResolvedInputs(Frame(Texture(), Texture()));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture("owner"), Texture("shadow")))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(a: 1), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(), Texture(r: 0)))));
        var blue = Map(T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, "Blue"), T8cTestHelpers.WutheringStyleId)
            .CaptureResolvedInputs(Frame(Texture(), Texture()));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, blue));
    }

    [Theory, InlineData("Blue"), InlineData("Alpha")]
    public void OneImageMayExplicitlySupplyBothMasksWithResolvedBytesRetained(string channel)
    {
        var map = Map(T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 0, channel), T8cTestHelpers.WutheringStyleId);
        var frame = Frame(Texture()); map.Validate(frame);
        var first = map.CaptureResolvedInputs(frame);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture(r: 0)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(channel == "Blue" ? Texture(b: 0) : Texture(a: 1)))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Texture("reowned")))));
    }

    [Fact]
    public void MixedFaceGenshinHairAndBothNewHairFamiliesKeepIndependentDetachedInputs()
    {
        var map = T8cTestHelpers.CreateDirectMap(
            [T8cTestHelpers.StarRailStyleId, T8cTestHelpers.GenshinStyleId, T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId, T8cTestHelpers.MmdStyleId],
            [
                T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.StarRailStyleId, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 0), D3D12CharacterMaterialRole.Face),
                T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.GenshinStyleId, TestStylePlugins.GenshinHair(1), D3D12CharacterMaterialRole.Hair),
                T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.StarRailStyleId, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 2, 3), D3D12CharacterMaterialRole.Hair),
                T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.WutheringStyleId, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 4, 5, "Alpha"), D3D12CharacterMaterialRole.Hair),
                new(),
            ]);
        SceneTexture[] textures = Enumerable.Range(0, 6).Select(i => Texture($"mixed data {i}")).ToArray();
        var frame = MakeFrame(5, textures); map.Validate(frame);
        var first = map.CaptureResolvedInputs(frame);
        Assert.Equal("face-sdf", map.GetPluginDraw(0)!.ShaderModuleId);
        Assert.Equal("hair", map.GetPluginDraw(1)!.ShaderModuleId);
        Assert.Equal("hair", map.GetPluginDraw(2)!.ShaderModuleId);
        Assert.Equal("hair", map.GetPluginDraw(3)!.ShaderModuleId);
        SceneTexture[] reowned = Enumerable.Range(0, 6).Select(i => Texture($"renamed data {i}")).ToArray();
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(MakeFrame(5, reowned))));
        for (int i = 0; i < textures.Length; i++)
        {
            var changed = textures.ToArray();
            changed[i] = i switch { 2 => Texture(b: 0), 4 => Texture(a: 1), _ => Texture(r: 0) };
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(MakeFrame(5, changed))));
        }
    }

    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding, string family = T8cTestHelpers.StarRailStyleId)
        => T8cTestHelpers.CreateSingleBindingMap(family, binding, D3D12CharacterMaterialRole.Hair);

    private static SceneTexture Texture(string name = "explicit numerical masks", byte r = 64, byte g = 17, byte b = 128, byte a = 0)
        => new(name, 1, 1, [r, g, b, a]);
    private static SceneTexture WideTexture() => new("same channels larger", 2, 1, [64, 17, 128, 0, 64, 17, 128, 0]);
    private static OriginalFrameScene Frame(params SceneTexture[] textures) => MakeFrame(1, textures);
    private static OriginalFrameScene MakeFrame(int count, params SceneTexture[] textures)
    {
        SceneVertex[] vertices = [new(new(-1, -1, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, 0), -Vector3.UnitZ, Vector2.Zero), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.Zero)];
        var materials = Enumerable.Range(0, count).Select(i => new SceneMaterial($"explicit material {i}", i * 3, 3,
            Vector4.One, Vector3.One, Vector3.Zero, 10)).ToArray();
        var indices = Enumerable.Range(0, count).SelectMany(_ => new uint[] { 0, 2, 1 }).ToArray();
        var snapshot = new SceneSnapshot(vertices, indices, materials, textures);
        return new(new(snapshot, Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true), count).ToArray(),
            new(Vector3.One, Vector3.One, Matrix4x4.Identity)), options: new(OriginalAlphaBlendMode.Normal, true, false));
    }
}
