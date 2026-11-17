using Mmd.Modern.Core;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12OriginalStyleRenderer
{
    private static readonly CharacterToonResponse StylizedDefaultResponse=new();
    private sealed record StylizedSurfacePipelines(ID3D12PipelineState Back,ID3D12PipelineState Both);
    // Finite actual frame state only: never keyed by ObjectId, preset IDs or parameter
    // values. Primary/native shadows, ground and root signatures remain shared.
    private readonly Dictionary<(D3D12SurfaceStyle Family,FramePipelineKey Frame,bool FaceSdf,bool GenshinHair,bool StarMcHair,bool EndfieldHair,bool EndfieldRim,bool GenshinAtlas,bool EndfieldMatcap,bool GenshinMetallic,bool GenshinMetallicSpecular,bool EndfieldPalette,bool EndfieldBaseImage,bool StarMcBody),StylizedSurfacePipelines> stylizedSurfacePipelines=[];
    private readonly Dictionary<(D3D12SurfaceStyle Family,string Entry,bool Native,bool Gi,bool Morph,bool SubTexture,bool FaceSdf,bool GenshinHair,bool StarMcHair,bool EndfieldHair,bool EndfieldRim,bool GenshinAtlas,bool EndfieldMatcap,bool GenshinMetallic,bool GenshinMetallicSpecular,bool EndfieldPalette,bool EndfieldBaseImage,bool StarMcBody),byte[]> stylizedSurfaceShaders=[];

    private byte[] GetStylizedSurfaceShader(D3D12SurfaceStyle family,string entry,FramePipelineKey key,bool faceSdf,bool genshinHair,bool starMcHair,bool endfieldHair,bool endfieldRim,bool genshinAtlas,bool endfieldMatcap,bool genshinMetallic,bool genshinMetallicSpecular,bool endfieldPalette,bool endfieldBaseImage,bool starMcBody)
    {
        var shaderKey=(family,entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,faceSdf,genshinHair,starMcHair,endfieldHair,endfieldRim,genshinAtlas,endfieldMatcap,genshinMetallic,genshinMetallicSpecular,endfieldPalette,endfieldBaseImage,starMcBody);
        if(stylizedSurfaceShaders.TryGetValue(shaderKey,out var found))return found;
        var compiled=starMcBody?OriginalFrameShaders.CompileStarMcBodySurface(family,entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture):endfieldBaseImage?OriginalFrameShaders.CompileEndfieldBaseImageSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,endfieldHair,endfieldRim):endfieldPalette?OriginalFrameShaders.CompileEndfieldPaletteSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,endfieldHair,endfieldRim):genshinMetallicSpecular?OriginalFrameShaders.CompileGenshinMetallicSpecularSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,genshinHair,genshinAtlas):genshinMetallic?OriginalFrameShaders.CompileGenshinMetallicSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,genshinHair,genshinAtlas):endfieldMatcap?OriginalFrameShaders.CompileEndfieldMatcapSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,endfieldHair,endfieldRim):genshinAtlas?OriginalFrameShaders.CompileGenshinHairAtlasSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,genshinHair):endfieldRim?OriginalFrameShaders.CompileEndfieldRimSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture,endfieldHair):endfieldHair?OriginalFrameShaders.CompileEndfieldHairSurface(entry,false,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture):starMcHair?OriginalFrameShaders.CompileStarMcHairSurface(family,entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture):genshinHair?OriginalFrameShaders.CompileGenshinHairSurface(entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture):faceSdf?OriginalFrameShaders.CompileFaceSdfSurface(family,entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture):family switch
        {
            D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx
                =>OriginalFrameShaders.CompileStylizedFamilySurface(family,entry,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture),
            _=>throw new ArgumentOutOfRangeException(nameof(family))
        };
        runtime.ShaderCompilations++;stylizedSurfaceShaders.Add(shaderKey,compiled);return compiled;
    }

    private StylizedSurfacePipelines GetStylizedSurfacePipelines(FramePipelines common,D3D12SurfaceStyle family,bool faceSdf=false,bool genshinHair=false,bool starMcHair=false,bool endfieldHair=false,bool endfieldRim=false,bool genshinAtlas=false,bool endfieldMatcap=false,bool genshinMetallic=false,bool genshinMetallicSpecular=false,bool endfieldPalette=false,bool endfieldBaseImage=false,bool starMcBody=false)
    {
        var key=common.Key;
        var cacheKey=(family,key,faceSdf,genshinHair,starMcHair,endfieldHair,endfieldRim,genshinAtlas,endfieldMatcap,genshinMetallic,genshinMetallicSpecular,endfieldPalette,endfieldBaseImage,starMcBody);
        if(stylizedSurfacePipelines.TryGetValue(cacheKey,out var found))return found;
        var both=RasterizerDescription.CullNone;if(endfieldRim||endfieldMatcap||starMcBody)both.FrontCounterClockwise=key.FrontCounterClockwise;var back=both;back.CullMode=CullMode.Back;back.FrontCounterClockwise=key.FrontCounterClockwise;
        var depth=DepthStencilDescription.Default;
        depth.DepthFunc=ComparisonFunction.LessEqual;depth.DepthWriteMask=key.WriteDepth?DepthWriteMask.All:DepthWriteMask.Zero;
        if(key.Stencil)
        {
            depth.StencilEnable=true;depth.StencilReadMask=255;depth.StencilWriteMask=255;
            depth.FrontFace.StencilFunc=ComparisonFunction.Always;depth.FrontFace.StencilPassOp=StencilOperation.Replace;depth.BackFace=depth.FrontFace;
        }
        string entry=key.NativeLights?(key.Gi?"PSSurfaceGiNative":"PSSurfaceNative"):(key.Gi?"PSSurfaceGi":"PSSurface");
        var vertex=endfieldHair?GetEndfieldHairVertexShader(key):key.SubTexture?GetSubTextureFrameShader("VSSurface",true,key.NativeLights,key.Gi):GetFrameShader("VSSurface",true);
        var pixel=GetStylizedSurfaceShader(family,entry,key,faceSdf,genshinHair,starMcHair,endfieldHair,endfieldRim,genshinAtlas,endfieldMatcap,genshinMetallic,genshinMetallicSpecular,endfieldPalette,endfieldBaseImage,starMcBody);
        ID3D12PipelineState Create(RasterizerDescription raster)
        {
            runtime.PipelineCreations++;
            return device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature=common.Root,VertexShader=vertex,PixelShader=pixel,
                InputLayout=new InputLayoutDescription(endfieldHair?[..SurfaceLayout(key.SubTexture),new InputElementDescription("RAW_RADIAL",0,Format.R32G32B32_Float,0,2)]:SurfaceLayout(key.SubTexture)),BlendState=FrameBlend(key.Blend,key.AlphaOutput),
                SampleMask=uint.MaxValue,RasterizerState=raster,DepthStencilState=depth,
                PrimitiveTopologyType=PrimitiveTopologyType.Triangle,
                RenderTargetFormats=[key.AlphaOutput==FrameAlphaOutput.Straight?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm],
                DepthStencilFormat=key.Depth,SampleDescription=new(1,0)
            });
        }
        var surfaceBack=Create(back);
        try
        {
            var result=new StylizedSurfacePipelines(surfaceBack,Create(both));
            stylizedSurfacePipelines.Add(cacheKey,result);return result;
        }
        catch {surfaceBack.Dispose();throw;}
    }

    private readonly Dictionary<(bool Native,bool Gi,bool Morph,bool Sub),byte[]> endfieldHairVertexShaders=[];
    private byte[] GetEndfieldHairVertexShader(FramePipelineKey key)
    {
        var variant=(key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture);
        if(endfieldHairVertexShaders.TryGetValue(variant,out var found))return found;
        var shader=OriginalFrameShaders.CompileEndfieldHairSurface("VSSurface",true,key.NativeLights,key.Gi,key.TextureMorph,key.SubTexture);
        runtime.ShaderCompilations++;endfieldHairVertexShaders.Add(variant,shader);return shader;
    }

    private void DisposeStylizedSurfacePipelines()
    {
        foreach(var pipeline in stylizedSurfacePipelines.Values){pipeline.Both.Dispose();pipeline.Back.Dispose();}
        stylizedSurfacePipelines.Clear();stylizedSurfaceShaders.Clear();endfieldHairVertexShaders.Clear();
    }
}
