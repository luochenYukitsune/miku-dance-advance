using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class StarMcBodyBindingTests
{
    [Fact]
    public void DedicatedBodyRejectsOtherRolesAndOtherFamilyData()
    {
        var binding = new D3D12StarMcBodyBinding(0, 1, 2, 3, D3D12MatcapColorEncoding.LinearRgb);
        new D3D12CharacterMaterialStyle(StarMcBody: binding).Validate();
        new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair, StarMcBody: binding).Validate();
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face, StarMcBody: binding).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(StarMcBody: binding, PmxMaterialTreatment: new()).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { NormalStrength = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (binding with { DetailCombinedTextureIndex = -1 }).Validate());
    }

    [Theory]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    public void ActualSelectedFrameAndAllFourTextureIndicesAreValidated(D3D12SurfaceStyle family)
    {
        var frame = D3D12EndfieldHairBindingTests.Frame(textures: Enumerable.Range(0, 4)
            .Select(i => new Mmd.Modern.Core.SceneTexture("numerical/body input", 1, 1, [128, 128, 128, 255])).ToArray());
        D3D12SurfaceStyleMap Map(D3D12StarMcBodyBinding b, D3D12SurfaceStyle? selected = null) => new([selected ?? family], [new(StarMcBody: b)]);
        var binding = new D3D12StarMcBodyBinding(0, 1, 2, 3, D3D12MatcapColorEncoding.LinearRgb);
        Map(binding).Validate(frame);
        Assert.Throws<NotSupportedException>(() => Map(binding, D3D12SurfaceStyle.Mmd).Validate(frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(binding with { MatcapCombinedTextureIndex = 4 }).Validate(frame));
    }
    [Theory]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    public void DetachedHistoryTracksOnlyConsumedChannelsAndRebasesTextureSlots(D3D12SurfaceStyle family)
    {
        static Mmd.Modern.Core.SceneTexture Image(byte r=120,byte g=70,byte b=40,byte a=230)
            => new("independent numerical input",1,1,[r,g,b,a]);
        var binding = new D3D12StarMcBodyBinding(0,1,2,3,D3D12MatcapColorEncoding.LinearRgb);
        D3D12SurfaceStyleMap Capture(D3D12StarMcBodyBinding b, params Mmd.Modern.Core.SceneTexture[] images)
            => new D3D12SurfaceStyleMap([family],[new(StarMcBody:b)]).CaptureFaceTextures(
                D3D12EndfieldHairBindingTests.Frame(textures:images));
        var first = Capture(binding,Image(),Image(),Image(),Image());
        var ignored = Capture(binding,Image(b:255,a:0),Image(r:255,b:255,a:0),Image(g:255,a:0),Image(a:0));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,ignored));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,Capture(binding,Image(),Image(),Image(r:121),Image())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,Capture(binding,Image(),Image(),Image(b:41),Image())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,Capture(binding,Image(),Image(),Image(),Image(g:71))));
        var normalChanged = Capture(binding,Image(r:121),Image(),Image(),Image());
        var detailChanged = Capture(binding,Image(),Image(g:71),Image(),Image());
        Assert.Equal(family==D3D12SurfaceStyle.StarRailPmx,D3D12SurfaceStyleMap.Equivalent(first,normalChanged));
        Assert.Equal(family==D3D12SurfaceStyle.StarRailPmx,D3D12SurfaceStyleMap.Equivalent(first,detailChanged));
        var rebased = binding with { NormalCombinedTextureIndex=3,DetailCombinedTextureIndex=2,MaskCombinedTextureIndex=1,MatcapCombinedTextureIndex=0 };
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,Capture(rebased,Image(),Image(),Image(),Image())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,Capture(binding with { RimDirectionRadians=.4f },Image(),Image(),Image(),Image())));
    }
}
