using System.Collections.ObjectModel;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinHairAtlasBindingTests
{
    [Fact]
    public void ExplicitSharedPaletteAllowsBodyAndHairButKeepsRbHairOnly()
    {
        var binding=GenshinHairAtlasSurfaceTests.Binding;
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        var map=GenshinHairAtlasSurfaceTests.Map(binding,TestStylePlugins.GenshinHair(0));
        map.Validate(frame);
        Assert.Equal("hair-atlas-rb",map.ResolveShaderModule(0)!.ModuleId);
        var body=T8bTestHelpers.CreateGenshinMap(D3D12CharacterMaterialRole.Body,binding);
        body.Validate(frame);
        Assert.Equal("hair-atlas",body.ResolveShaderModule(0)!.ModuleId);
        Assert.Equal(2,body.GetPluginDraw(0)!.TextureBindings!.Count);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body,frame,binding,TestStylePlugins.GenshinHair(0)).IsValid);
        foreach(var role in new[]{StyleMaterialRole.Face,StyleMaterialRole.Eye})
            Assert.False(T8bTestHelpers.ValidateGenshin(role,frame,binding).IsValid);
        foreach(var family in new[]{"mmd","star-rail","wuthering-waves","endfield"})
            Assert.Throws<NotSupportedException>(()=>new D3D12SurfaceStyleMap([family],map.CharacterMaterials,TestStylePlugins.AllPresetCatalog).Validate(frame));
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair,frame,binding,new StyleMaterialBindingData("genshin","default")).IsValid);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair,frame,binding,
            new StyleMaterialBindingData("endfield","hair",new ReadOnlyDictionary<string,int>(new Dictionary<string,int>(StringComparer.Ordinal){["texture"]=0}))).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(()=>GenshinHairAtlasSurfaceTests.Map(GenshinHairAtlasSurfaceTests.AtlasBinding(lightmapSlot:3)).Validate(frame));
        Assert.Throws<ArgumentOutOfRangeException>(()=>GenshinHairAtlasSurfaceTests.Map(GenshinHairAtlasSurfaceTests.AtlasBinding(atlasSlot:3)).Validate(frame));
    }

    [Fact]
    public void FiniteSignedSourceDomainRetainsSafeZeroAndRejectsMalformedInputs()
    {
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair,frame,
            GenshinHairAtlasSurfaceTests.AtlasBinding(shadowPosition:0,shadowRampWidth:0,vertexWidth:0,vertexAo:-16)).IsValid);
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair,frame,
            GenshinHairAtlasSurfaceTests.AtlasBinding(shadowPosition:-16,shadowRampWidth:-16,vertexWidth:16,vertexAo:16,explicitRowId:2.5f)).IsValid);
        foreach(var bad in new[]{
            GenshinHairAtlasSurfaceTests.AtlasBinding(lightmapSlot:-1),
            GenshinHairAtlasSurfaceTests.AtlasBinding(atlasSlot:-1),
            GenshinHairAtlasSurfaceTests.AtlasBinding(atlasEncoding:"Unknown"),
            GenshinHairAtlasSurfaceTests.AtlasBinding(explicitRowId:.9f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(explicitRowId:5.1f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(dayNight:1.1f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(useLightmapAo:-.1f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(useVertexColorAo:1.1f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(shadowPosition:17),
            GenshinHairAtlasSurfaceTests.AtlasBinding(shadowRampWidth:float.NaN),
            GenshinHairAtlasSurfaceTests.AtlasBinding(vertexWidth:float.PositiveInfinity),
            GenshinHairAtlasSurfaceTests.AtlasBinding(vertexAo:float.NegativeInfinity)})
        {
            Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Hair,frame,bad).IsValid);
            Assert.ThrowsAny<ArgumentException>(()=>D3D12ViewPluginMaterialBindings.Validate([],
                [TestStylePlugins.Bind(Guid.NewGuid(),0,bad)],TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact]
    public void EveryConsumedScalarAndRemovalChangesDetachedKeyButUnusedChannelsDoNot()
    {
        var map=GenshinHairAtlasSurfaceTests.Map(GenshinHairAtlasSurfaceTests.Binding);
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        var first=map.CaptureResolvedInputs(frame);
        Assert.NotSame(map,first);
        foreach(var changed in new[]{
            GenshinHairAtlasSurfaceTests.AtlasBinding(atlasEncoding:"SrgbRgb"),
            GenshinHairAtlasSurfaceTests.AtlasBinding(explicitRowId:2),
            GenshinHairAtlasSurfaceTests.AtlasBinding(dayNight:.5f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(shadowPosition:.7f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(shadowRampWidth:.7f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(vertexWidth:.7f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(vertexAo:.7f),
            GenshinHairAtlasSurfaceTests.AtlasBinding(useLightmapAo:1),
            GenshinHairAtlasSurfaceTests.AtlasBinding(useVertexColorAo:1)})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first,GenshinHairAtlasSurfaceTests.Map(changed).CaptureResolvedInputs(frame)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,GenshinHairAtlasSurfaceTests.Map(null)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureResolvedInputs(GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas()))));
        var colors=GenshinHairAtlasSurfaceTests.Atlas();for(int i=3;i<colors.Length;i+=4)colors[i]=255;
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureResolvedInputs(GenshinHairAtlasSurfaceTests.Frame(colors,unusedAlpha:255))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureResolvedInputs(GenshinHairAtlasSurfaceTests.Frame(colors,green:80))));
        colors[0]++;
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureResolvedInputs(GenshinHairAtlasSurfaceTests.Frame(colors))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureResolvedInputs(frame)));
    }

    [Fact]
    public void BothSelectedSlotsRebaseByConsumedImageContentsWithoutMutatingOldSnapshot()
    {
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        var b=GenshinHairAtlasSurfaceTests.Binding;
        var first=GenshinHairAtlasSurfaceTests.Map(b).CaptureResolvedInputs(frame);
        var old=frame.Surface.Snapshot;
        var swapped=new SceneSnapshot(old.Vertices.ToArray(),old.Indices.ToArray(),old.Materials,
            [old.Textures[1],old.Textures[0],old.Textures[2]],camera:old.Camera,lightDirection:old.LightDirection);
        var rebuilt=new OriginalFrameScene(new(swapped,frame.Surface.Materials,frame.Surface.Profile),options:frame.Options);
        var second=GenshinHairAtlasSurfaceTests.Map(GenshinHairAtlasSurfaceTests.AtlasBinding(lightmapSlot:1,atlasSlot:0)).CaptureResolvedInputs(rebuilt);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,second));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,GenshinHairAtlasSurfaceTests.Map(b).CaptureResolvedInputs(frame)));
    }
}
