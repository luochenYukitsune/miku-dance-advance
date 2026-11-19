using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicSpecularStyleMapTests
{
    private static StyleMaterialBindingData Make(
        int lightmapSlot = 0,
        int matcapSlot = 1,
        float enableMetallics = 1f,
        Vector3? color = null,
        Vector3? sharpColor = null,
        float shininess = 16f,
        float scale = 1f,
        float attenuation = .5f,
        float sharpOffset = 1f,
        int rampSlot = 2,
        float rampMix = .5f,
        bool hasSpecular = true)
        => TestStylePlugins.GenshinMetallic(
            lightmapSlot, matcapSlot, "LinearRgb",
            Vector3.One, Vector3.One, Vector3.One,
            enableMetallics, 1f, 1f, "PrimaryResponse", 1f,
            hasSpecular: hasSpecular,
            specularColor: color ?? new(.2f),
            specularSharpColor: sharpColor ?? new(.8f),
            specularShininess: shininess,
            specularScale: scale,
            specularAttenuation: attenuation,
            specularSharpOffset: sharpOffset,
            specularRampSlot: rampSlot,
            specularRampMix: rampMix);

    private static SceneTexture Image(byte r=240,byte g=100,byte b=128,byte a=255)
        =>new("explicit numerical image",1,1,[r,g,b,a]);
    private static OriginalFrameScene Frame(SceneTexture[]? images=null)
        =>T8bTestHelpers.CreateMetallicFrame(textures:images??[Image(),Image(),Image()]);
    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData binding)
        =>T8bTestHelpers.CreateGenshinMap(D3D12CharacterMaterialRole.Body,binding);

    [Fact]
    public void StructuralParentAndExplicitRampGuardsApplyEvenWhenDisabled()
    {
        var parent=Make(); Map(parent).Validate(Frame());
        Assert.Equal("metallic-specular",Map(parent).ResolveShaderModule(0)!.ModuleId);
        Assert.Equal("default",Map(Make(enableMetallics:0)).ResolveShaderModule(0)!.ModuleId);
        Assert.Throws<ArgumentOutOfRangeException>(()=>Map(Make(enableMetallics:1f,rampSlot:3)).Validate(Frame()));
        foreach(float enable in new[]{0f,1f})
        {
            Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body,Frame(),Make(enableMetallics:enable,rampSlot:3)).IsValid);
            var procFrame=Frame([Image(),Image(),SceneTexture.ProceduralToon()]);
            var layout=D3D12MaterialBindingLayout.Capture(procFrame);
            var guards=new[]{layout.CaptureTexture(0),layout.CaptureTexture(1),
                new D3D12MaterialBindingProfileTexture(2,1,1,SceneTextureKind.IndexedProceduralToon,layout.CaptureTexture(2).RgbaSha256)};
            var profile=new D3D12MaterialBindingProfile("proc","proc",layout.Fingerprint,
                [new(0,D3D12CharacterMaterialRole.Body,pluginBindings:[Make(enableMetallics:enable)],textureGuards:guards)]);
            Assert.Throws<ArgumentException>(()=>D3D12MaterialBindingProfileResolver.Resolve(profile,procFrame,Guid.NewGuid(),"genshin",TestStylePlugins.AllViewCatalog));
        }
        Map(Make(rampMix:0,rampSlot:-1)).Validate(Frame([Image(),Image()]));
    }

    [Fact]
    public void ConsumedSnapshotTracksBoundTexturesAndRebasesAllThreeSlots()
    {
        var map=Map(Make());
        var initial=map.CaptureResolvedInputs(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(initial,map.CaptureResolvedInputs(Frame([Image(),Image(),Image()]))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(initial,map.CaptureResolvedInputs(Frame([Image(g:1,a:0),Image(a:0),Image(a:0)]))));
        foreach(var images in new[]{new[]{Image(b:1),Image(),Image()},new[]{Image(),Image(),Image(r:1)},
            new[]{Image(),Image(),Image(g:1)},new[]{Image(),Image(),Image(b:1)}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(initial,map.CaptureResolvedInputs(Frame(images))));
        var rebased=Map(Make(lightmapSlot:2,matcapSlot:0,rampSlot:1)).CaptureResolvedInputs(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(initial,rebased));
    }

    [Fact]
    public void UnconsumedRampDoesNotChangeAppearanceButAllLobeParametersDo()
    {
        var map=Map(Make(rampMix:0));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map.CaptureResolvedInputs(Frame()),
            map.CaptureResolvedInputs(Frame([Image(),Image(),Image(r:1,g:2,b:3)]))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map,Map(Make(rampMix:0,rampSlot:-1))));
        var active=Map(Make());
        foreach(var child in new[]{Make(color:new(.1f)),Make(sharpColor:new(.7f)),
            Make(shininess:17),Make(scale:2),Make(attenuation:.3f),
            Make(sharpOffset:.4f),Make(rampMix:.3f)})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(active,Map(child)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Make(enableMetallics:0,hasSpecular:true)),
            Map(Make(enableMetallics:0,hasSpecular:false))));
    }
}
