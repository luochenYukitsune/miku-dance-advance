using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
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
        var binding = new D3D12GenshinNonMetallicSpecularBinding(0);
        Assert.Equal(1, binding.GlobalSpecularScale);
        Assert.Equal(10, binding.Shininess);
        Assert.Equal(.1f, binding.Multiplier);
        Assert.Equal(1, binding.Intensity);
        binding.Validate();
        var oldCaller = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face, .35f, .6f, null, .2f);
        Assert.Null(oldCaller.GenshinNonMetallicSpecular);
        var map = Map(binding);
        map.Validate(Frame(Texture()));
        Assert.True(map.HasConsumedGenshinNonMetallicSpecular);
        Assert.Same(binding, map.EffectiveGenshinNonMetallicSpecular(0));
        Assert.Equal(1, map.EffectiveNormalShadeStrength(0));
        Assert.Equal(1, map.EffectiveGeometricShadowStrength(0));
    }

    public static IEnumerable<object[]> InvalidScalars()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.01f })
        {
            yield return [new D3D12GenshinNonMetallicSpecularBinding(0, GlobalSpecularScale: value)];
            yield return [new D3D12GenshinNonMetallicSpecularBinding(0, Shininess: value)];
            yield return [new D3D12GenshinNonMetallicSpecularBinding(0, Multiplier: value)];
            yield return [new D3D12GenshinNonMetallicSpecularBinding(0, Intensity: value)];
        }
        yield return [new D3D12GenshinNonMetallicSpecularBinding(0, GlobalSpecularScale: 2.01f)];
        yield return [new D3D12GenshinNonMetallicSpecularBinding(0, Shininess: 64.01f)];
        yield return [new D3D12GenshinNonMetallicSpecularBinding(0, Multiplier: 16.01f)];
        yield return [new D3D12GenshinNonMetallicSpecularBinding(0, Intensity: 16.01f)];
    }

    [Theory, MemberData(nameof(InvalidScalars))]
    public void NonFiniteAndOutOfDomainScalarsAreRejected(D3D12GenshinNonMetallicSpecularBinding binding)
        => Assert.Throws<ArgumentOutOfRangeException>(binding.Validate);

    [Fact]
    public void DomainEndpointsAreAcceptedWithoutSilentlyClamping()
    {
        new D3D12GenshinNonMetallicSpecularBinding(0, 0, 0, 0, 0).Validate();
        new D3D12GenshinNonMetallicSpecularBinding(0, 2, 64, 16, 16).Validate();
    }

    [Theory, InlineData(-1), InlineData(1), InlineData(int.MaxValue)]
    public void TextureSlotMustExistInTheActualComposedTable(int index)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Map(new(index)).Validate(Frame(Texture())));

    [Fact]
    public void HairInputRequiresTheExplicitRoleAndMatchingFamilyAndCannotShareFaceSdf()
    {
        var binding = new D3D12GenshinNonMetallicSpecularBinding(0);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(role,
                GenshinNonMetallicSpecular: binding).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            FaceSdf: new(0, -Vector3.UnitZ, Vector3.UnitX), GenshinNonMetallicSpecular: binding).Validate());
        foreach (var family in new[] { D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.StarRailPmx,
            D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx })
            Assert.Throws<NotSupportedException>(() => Map(binding, family).Validate(Frame(Texture())));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(binding with { GlobalSpecularScale = float.NaN }).Validate(Frame(Texture())));
    }

    [Theory]
    [InlineData(D3D12SurfaceStyle.Mmd)]
    [InlineData(D3D12SurfaceStyle.GenshinPmx)]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [InlineData(D3D12SurfaceStyle.EndfieldPmx)]
    public void OrdinaryHairKeepsBodyValuesAcrossAllFamilies(D3D12SurfaceStyle family)
    {
        var hair = Map(null, family);
        var body = new D3D12SurfaceStyleMap([family]);
        hair.Validate(Frame(Texture()));
        Assert.Null(hair.EffectiveGenshinNonMetallicSpecular(0));
        Assert.False(hair.HasConsumedGenshinNonMetallicSpecular);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(body, hair));
        Assert.Equal(body.EffectiveNormalShadeStrength(0), hair.EffectiveNormalShadeStrength(0));
        Assert.Equal(body.EffectiveGeometricShadowStrength(0), hair.EffectiveGeometricShadowStrength(0));
    }

    [Fact]
    public void ConsumedKeyIncludesEveryActiveScalarAndBindingRemoval()
    {
        var binding = new D3D12GenshinNonMetallicSpecularBinding(0);
        var first = Map(binding);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(binding with { })));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(null)));
        foreach (var changed in new[] { binding with { GlobalSpecularScale = .5f }, binding with { Shininess = 2 },
            binding with { Multiplier = .2f }, binding with { Intensity = 2 }, binding with { CombinedTextureIndex = 1 } })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(changed)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(binding with { Multiplier = 0 }), Map(binding with { Multiplier = -0f })));
    }

    [Fact]
    public void DetachedKeysCompareOnlyNumericalRedBlueDimensionsAndEffectiveSlots()
    {
        var binding = new D3D12GenshinNonMetallicSpecularBinding(0);
        var map = Map(binding);
        var first = map.CaptureFaceTextures(Frame(Texture()));
        Assert.NotSame(map, first);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(binding with { }).CaptureFaceTextures(Frame(Texture("new label")))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(g: 240, a: 255)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(r: 65)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(b: 127)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(new SceneTexture("same channels larger", 2, 1,
            [64, 17, 128, 0, 64, 17, 128, 0])))));
        var rebased = Map(binding with { CombinedTextureIndex = 1 }).CaptureFaceTextures(Frame(Texture(r: 12), Texture("rebased")));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture()))));
        // Capturing a later frame never replaces first's detached submitted comparison.
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture(r: 1)))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(Texture()))));
    }

    [Fact]
    public void MixedFaceAndHairDetachedKeysRetainBothIndependentDataSets()
    {
        var hair = new D3D12GenshinNonMetallicSpecularBinding(1);
        var face = new D3D12FaceSdfBinding(0, -Vector3.UnitZ, Vector3.UnitX);
        var map = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.GenshinPmx],
            [new(D3D12CharacterMaterialRole.Face, FaceSdf: face), new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: hair)]);
        var first = map.CaptureFaceTextures(TwoMaterials(Texture(), Texture()));
        Assert.True(map.HasConsumedFaceSdf); Assert.True(map.HasConsumedGenshinNonMetallicSpecular);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(TwoMaterials(Texture(g: 200, b: 1), Texture(g: 200, a: 99)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(TwoMaterials(Texture(r: 1), Texture()))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(TwoMaterials(Texture(), Texture(b: 1)))));
    }

    private static D3D12SurfaceStyleMap Map(D3D12GenshinNonMetallicSpecularBinding? binding, D3D12SurfaceStyle family = D3D12SurfaceStyle.GenshinPmx)
        => new([family], [new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: binding)]);

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
