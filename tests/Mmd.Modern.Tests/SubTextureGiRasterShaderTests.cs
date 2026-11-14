using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SubTextureGiRasterShaderTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void EveryGiAndNativeVariantIncludesReceiverAndCompositionEntries(bool native,bool character)
    {
        Assert.NotEmpty(OriginalFrameShaders.CompileSubTexture("VSSurface",true,native,true));
        Assert.NotEmpty(OriginalFrameShaders.CompileSubTexture("PSGBuffer",false,native,true));
        string entry=native?"PSSurfaceGiNative":"PSSurfaceGi";
        Assert.NotEmpty(character?OriginalFrameShaders.CompileCharacterToon(entry,native,true,true,true)
            :OriginalFrameShaders.CompileSubTexture(entry,false,native,true));
        string source=OriginalFrameShaders.CharacterToonSource(native,true,true,true);
        Assert.Contains("alpha=PSSurface(v).a; clip(alpha-Options.x);clip(alpha-.999);",source);
        Assert.Contains("Options.y>=3?v.AdditionalUv",source);
        Assert.Contains("if(Options.y==1 || (Options.y>=3 && (Options.y!=4 || Options.z==0))) fill=fill*MorphSphere(",source);
        Assert.Contains("if(Options.y==1 || (Options.y>=3 && (Options.y!=4 || Options.z==0))) diffuseLit=diffuseLit*MorphSphere(",source);
        Assert.Contains("originalC<1) alpha*=originalC+ToonShadowColor.w*(1-originalC)",source);
    }
}
