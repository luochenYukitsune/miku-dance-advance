using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;
namespace Mmd.Modern.Tests;
public sealed class GenshinMetallicStyleMapTests
{
    private static StyleMaterialBindingData Make(
        int lightmapSlot = 0,
        int matcapSlot = 1,
        string matcapEncoding = "SrgbRgb",
        Vector3? darkColor = null,
        Vector3? lightColor = null,
        Vector3? shadowMultiplyColor = null,
        float enableMetallics = 1f,
        float tileScale = 1f,
        float brightness = 1f,
        string litMode = "PrimaryResponse",
        float uniformLitFactor = 1f)
        => TestStylePlugins.GenshinMetallic(
            lightmapSlot, matcapSlot, matcapEncoding,
            darkColor ?? new(.2f), lightColor ?? new(.8f), shadowMultiplyColor ?? new(.7f),
            enableMetallics, tileScale, brightness, litMode, uniformLitFactor);

    private static readonly StyleMaterialBindingData Binding = Make();
    private static SceneTexture Image(byte r=240,byte g=70,byte b=40,byte a=230)=>new("explicit",1,1,[r,g,b,a]);
    private static OriginalFrameScene Frame(SceneTexture[]? images=null)=>T8bTestHelpers.CreateMetallicFrame(textures:images??[Image(),Image()]);
    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? value,D3D12CharacterMaterialRole role=D3D12CharacterMaterialRole.Body)
        =>T8bTestHelpers.CreateGenshinMap(role,value);
    [Fact]
    public void FamilyRolesAndSharedLightmapAreExplicitEvenWhenDisabled()
    {
        Map(Binding).Validate(Frame());
        Assert.Equal("metallic",Map(Binding).ResolveShaderModule(0)!.ModuleId);
        Assert.Equal("default",Map(Make(enableMetallics:0)).ResolveShaderModule(0)!.ModuleId);
        var genshinDraw=Map(Binding).CharacterMaterials;
        foreach(var f in new[]{"mmd","endfield","star-rail","wuthering-waves"})
            Assert.Throws<NotSupportedException>(()=>new D3D12SurfaceStyleMap([f],genshinDraw,TestStylePlugins.AllPresetCatalog).Validate(Frame()));
        foreach(var role in new[]{StyleMaterialRole.Face,StyleMaterialRole.Eye})
            Assert.False(T8bTestHelpers.ValidateGenshin(role,Frame(),Binding).IsValid);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body,Frame(),Make(enableMetallics:0,matcapSlot:2)).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(()=>Map(Make(matcapSlot:2)).Validate(Frame()));
        foreach(var images in new[]{new[]{SceneTexture.ProceduralToon(),Image()},new[]{Image(),SceneTexture.ProceduralToon()}})
        {
            var proceduralFrame=Frame(images);
            var layout=D3D12MaterialBindingLayout.Capture(proceduralFrame);
            D3D12MaterialBindingProfileTexture[] guards=[
                new(0,1,1,SceneTextureKind.IndexedProceduralToon,layout.CaptureTexture(0).RgbaSha256),
                new(1,1,1,SceneTextureKind.IndexedProceduralToon,layout.CaptureTexture(1).RgbaSha256)];
            foreach(float enable in new[]{1f,0f})
            {
                var profile=new D3D12MaterialBindingProfile("proc","proc",layout.Fingerprint,
                    [new(0,D3D12CharacterMaterialRole.Body,pluginBindings:[Make(enableMetallics:enable)],textureGuards:guards)]);
                Assert.Throws<ArgumentException>(()=>D3D12MaterialBindingProfileResolver.Resolve(profile,proceduralFrame,Guid.NewGuid(),"genshin",TestStylePlugins.AllViewCatalog));
            }
        }
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair,Frame(),TestStylePlugins.GenshinHair(0),Binding).IsValid);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair,Frame(),TestStylePlugins.GenshinHair(1),Binding).IsValid);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body,Frame(),new StyleMaterialBindingData("genshin","default"),Binding).IsValid);
    }
    [Fact]
    public void HistoryConsumesLightmapRedAndColorRgbButIgnoresBothUnconsumedAlphasAndRebasedSlots()
    {
        var original=Map(Binding).CaptureResolvedInputs(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(original,Map(Binding).CaptureResolvedInputs(Frame([Image(),Image()]))));
        foreach(var images in new[]{new[]{Image(r:1),Image()},new[]{Image(),Image(r:1)},new[]{Image(),Image(g:1)},new[]{Image(),Image(b:1)}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(original,Map(Binding).CaptureResolvedInputs(Frame(images))));
        var rebased=Map(Make(lightmapSlot:1,matcapSlot:0)).CaptureResolvedInputs(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(original,rebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(null),Map(Make(enableMetallics:0))));
    }
    [Fact]
    public void AllConsumedParametersChangeHistoryWhileUniformLitFactorRemainsInCbv()
    {
        foreach(var b in new[]{Make(enableMetallics:.5f),Make(tileScale:2),Make(brightness:.5f),
            Make(darkColor:new(.1f)),Make(lightColor:new(.9f)),Make(shadowMultiplyColor:new(.4f)),
            Make(matcapEncoding:"LinearRgb"),Make(litMode:"Uniform")})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(b)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(Make())));
        var uniform=Make(litMode:"Uniform");
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(uniform),Map(Make(litMode:"Uniform",uniformLitFactor:.2f))));
    }
}
