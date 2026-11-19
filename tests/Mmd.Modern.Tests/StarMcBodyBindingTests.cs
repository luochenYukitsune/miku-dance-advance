using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class StarMcBodyBindingTests
{
    [Fact]
    public void DedicatedBodyRejectsOtherRolesAndOtherFamilyData()
    {
        foreach (var family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        {
            var binding = T8cTestHelpers.StarMcBodyBinding(family, 0, 1, 2, 3, "LinearRgb");
            Assert.True(T8cTestHelpers.ValidateMaterialBinding(family, binding, D3D12CharacterMaterialRole.Body).IsValid);
            Assert.True(T8cTestHelpers.ValidateMaterialBinding(family, binding, D3D12CharacterMaterialRole.Hair).IsValid);
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, binding, D3D12CharacterMaterialRole.Face).IsValid);
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, binding, D3D12CharacterMaterialRole.Eye).IsValid);
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family,
                T8cTestHelpers.StarMcBodyBinding(family, 0, 1, 2, 3, "LinearRgb", normalStrength: float.NaN),
                D3D12CharacterMaterialRole.Body).IsValid);
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family,
                T8cTestHelpers.StarMcBodyBinding(family, 0, -1, 2, 3, "LinearRgb"),
                D3D12CharacterMaterialRole.Body).IsValid);

            var withTreatment = T8cTestHelpers.CreateDirectMap([family],
                [T8cTestHelpers.MaterialWithBinding(family, binding, D3D12CharacterMaterialRole.Body,
                    pmxMaterialTreatment: new())]);
            Assert.Null(withTreatment.EffectivePmxMaterialTreatment(0));
        }
    }

    [Theory]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    public void ActualSelectedFrameAndAllFourTextureIndicesAreValidated(string family)
    {
        var frame = D3D12EndfieldHairBindingTests.Frame(textures: Enumerable.Range(0, 4)
            .Select(i => new SceneTexture("numerical/body input", 1, 1, [128, 128, 128, 255])).ToArray());
        D3D12SurfaceStyleMap Map(StyleMaterialBindingData b, string? selected = null)
            => T8cTestHelpers.CreateDirectMap([selected ?? family],
                [T8cTestHelpers.MaterialWithBinding(family, b, D3D12CharacterMaterialRole.Body, frame: frame)]);
        var binding = T8cTestHelpers.StarMcBodyBinding(family, 0, 1, 2, 3, "LinearRgb");
        Assert.True(T8cTestHelpers.ValidateMaterialBinding(family, binding, D3D12CharacterMaterialRole.Body, 4).IsValid);
        Map(binding).Validate(frame);
        Assert.Throws<NotSupportedException>(() => Map(binding, T8cTestHelpers.MmdStyleId).Validate(frame));
        var badSlot = T8cTestHelpers.StarMcBodyBinding(family, 0, 1, 2, 4, "LinearRgb");
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, badSlot, D3D12CharacterMaterialRole.Body, 4).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(badSlot).Validate(frame));
    }

    [Theory]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    public void DetachedHistoryTracksBoundTexturesScalarsAndRebasesTextureSlots(string family)
    {
        static SceneTexture Image(byte r = 120, byte g = 70, byte b = 40, byte a = 230)
            => new("independent numerical input", 1, 1, [r, g, b, a]);
        var binding = T8cTestHelpers.StarMcBodyBinding(family, 0, 1, 2, 3, "LinearRgb");
        D3D12SurfaceStyleMap Capture(StyleMaterialBindingData b, params SceneTexture[] images)
        {
            var frame = D3D12EndfieldHairBindingTests.Frame(textures: images);
            return T8cTestHelpers.CreateDirectMap([family],
                [T8cTestHelpers.MaterialWithBinding(family, b, D3D12CharacterMaterialRole.Body, frame: frame)])
                .CaptureResolvedInputs(frame);
        }
        var first = Capture(binding, Image(), Image(), Image(), Image());
        var reowned = Capture(binding,
            new("new normal owner", 1, 1, [120, 70, 40, 230]),
            new("new detail owner", 1, 1, [120, 70, 40, 230]),
            new("new mask owner", 1, 1, [120, 70, 40, 230]),
            new("new matcap owner", 1, 1, [120, 70, 40, 230]));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, reowned));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(binding, Image(), Image(), Image(r: 121), Image())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(binding, Image(), Image(), Image(b: 41), Image())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(binding, Image(), Image(), Image(), Image(g: 71))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(binding, Image(r: 121), Image(), Image(), Image())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(binding, Image(), Image(g: 71), Image(), Image())));
        var rebased = T8cTestHelpers.StarMcBodyBinding(family, 3, 2, 1, 0, "LinearRgb");
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(rebased, Image(), Image(), Image(), Image())));
        var turnedRim = T8cTestHelpers.StarMcBodyBinding(family, 0, 1, 2, 3, "LinearRgb", rimDirectionRadians: .4f);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(turnedRim, Image(), Image(), Image(), Image())));
    }
}
