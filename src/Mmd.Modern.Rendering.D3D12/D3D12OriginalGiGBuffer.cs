using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12OriginalStyleRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct GiSurfaceConstants(uint TriangleBase,uint Width,float Strength,float OcclusionStrength,Vector4 AmbientShare);
    private readonly Dictionary<FramePipelines, (ID3D12PipelineState Back,ID3D12PipelineState Both)> giGBufferPipelines=[];
    private readonly Dictionary<(string,bool),byte[]> giFrameShaders=[];

    private byte[] GetGiFrameShader(string entry,bool vertex)
    {
        if(giFrameShaders.TryGetValue((entry,vertex),out var found))return found;
        var result=OriginalFrameShaders.CompileGi(entry,vertex);runtime.ShaderCompilations++;
        giFrameShaders.Add((entry,vertex),result);return result;
    }

    internal int RecordGiGBuffer(D3D12RecordingContext context,OriginalFrameScene frame,int width,int height,
        ID3D12Resource vb,ID3D12Resource ib,ID3D12DescriptorHeap srvHeap,int srvStride,int slotBase,
        CpuDescriptorHandle position,CpuDescriptorHandle normal,CpuDescriptorHandle depth,Format surfaceDepthFormat,PreparedFrame? prepared=null)
    {
        bool subTexture=HasSubTexture(frame);
        if(subTexture&&prepared is null)throw new InvalidOperationException("SubTexture GI requires the current primary shadow preparation.");
        var pipeline=GetFramePipelines(frame,surfaceDepthFormat,true);
        var key=pipeline;
        if(!giGBufferPipelines.TryGetValue(key,out var states))
        {
            var raster=RasterizerDescription.CullNone;
            ID3D12PipelineState Create(RasterizerDescription selected)
            {
                runtime.PipelineCreations++;
                return device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
                    RootSignature=pipeline.Root,VertexShader=subTexture?GetSubTextureFrameShader("VSSurface",true,gi:true):GetFrameShader("VSSurface",true),
                    PixelShader=subTexture?GetSubTextureFrameShader("PSGBuffer",false,gi:true):GetGiFrameShader("PSGBuffer",false),
                    InputLayout=new InputLayoutDescription(SurfaceLayout(subTexture)),
                    BlendState=BlendDescription.Opaque,SampleMask=uint.MaxValue,RasterizerState=selected,
                    DepthStencilState=DepthStencilDescription.Default with {DepthFunc=ComparisonFunction.LessEqual},
                    PrimitiveTopologyType=PrimitiveTopologyType.Triangle,RenderTargetFormats=[Format.R32G32B32A32_Float,Format.R32G32B32A32_Float],
                    DepthStencilFormat=Format.D32_Float,SampleDescription=new(1,0)});
            }
            var both=Create(raster);raster.CullMode=CullMode.Back;raster.FrontCounterClockwise=pipeline.Key.FrontCounterClockwise;
            try {states=(Create(raster),both);giGBufferPipelines.Add(key,states);}catch{both.Dispose();throw;}
        }
        var scene=frame.Surface.Snapshot;var style=frame.Surface;var options=frame.Options!;
        var camera=options.CameraOverride??scene.Camera;Matrix4x4.Invert(scene.World,out var inverse);
        var common=new FrameConstants(scene.World,Matrix4x4.Transpose(inverse),camera.ViewProjection(width,height),options.SphereRotationOverride??style.Profile.SphereRotation,
            options.SelfShadow?.LightViewProjection??Matrix4x4.Identity,Vector4.One,Vector4.Zero,new(Vector3.Normalize(scene.LightDirection),0),new(camera.Eye,1),Vector4.One,Vector4.Zero,
            new(0,options.SelfShadow?.PartialFar==true?1:0,options.SelfShadow is null?0:1,0),Vector4.Zero);
        var commands=context.Commands;
        commands.UnsetRenderTargets();commands.OMSetRenderTargets([position,normal],depth);
        commands.ClearRenderTargetView(position,new Color4(0,0,0,0));commands.ClearRenderTargetView(normal,new Color4(0,0,0,0));
        commands.ClearDepthStencilView(depth,ClearFlags.Depth,1,0);
        commands.RSSetViewport(0,0,width,height,0,1);commands.RSSetScissorRect(new RawRect(0,0,width,height));
        commands.SetDescriptorHeaps(srvHeap);commands.SetGraphicsRootSignature(pipeline.Root);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetVertexBuffers(0,new VertexBufferView(vb.GPUVirtualAddress,checked(scene.VertexCount*32),32));
        if(subTexture)BindAdditionalUvs(context,prepared!.Cache,scene);
        commands.IASetIndexBuffer(new IndexBufferView(ib.GPUVirtualAddress,checked(scene.Indices.Length*4),Format.R32_UInt));
        int whiteSlot=slotBase+scene.Textures.Count+2,shadowSlot=whiteSlot+1;
        if(prepared is not null)
        {
            device.CreateShaderResourceView(prepared.White,null,srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(whiteSlot,srvStride));
            device.CreateShaderResourceView(prepared.Shadow,null,srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(shadowSlot,srvStride));
        }
        int draws=0;
        foreach(int i in SurfaceDrawOrderPlanner.Plan(frame))
        {
            var material=scene.Materials[i];var binding=style.Materials[i];var flags=frame.Materials[i];
            if(!flags.SurfaceEnabled||material.IndexCount==0)continue;
            var host=OriginalStyleMath.ResolveHostColors(material,style.Profile);
            commands.SetPipelineState(binding.DoubleSided||(flags.CullTransparentSurface&&host.EgColor.W<1)?states.Both:states.Back);
            commands.SetGraphicsRootConstantBufferView(0,context.UploadConstants(common with {EgColor=host.EgColor,SpecularPower=host.SpecularPower,
                ToonShadowColor=SurfaceToon(binding),Options=new(material.AlphaCutoff,SurfaceMode(binding),material.DiffuseTexture>=0?1:0,0)}));
            if(pipeline.TextureMorphRoot>=0)commands.SetGraphicsRootConstantBufferView(pipeline.TextureMorphRoot,context.UploadConstants(new TextureMorphConstants(binding.TextureFactors)));
            commands.SetGraphicsRootConstantBufferView(4,context.UploadConstants(new GiSurfaceConstants((uint)(material.StartIndex/3),(uint)width,0,0,default)));
            commands.SetGraphicsRootDescriptorTable(1,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(material.DiffuseTexture<0?0:material.DiffuseTexture+2),srvStride));
            commands.SetGraphicsRootDescriptorTable(2,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(binding.SphereTexture<0?0:binding.SphereTexture+2),srvStride));
            if(prepared is not null)commands.SetGraphicsRootDescriptorTable(3,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(flags.ReceiveSelfShadow?shadowSlot:whiteSlot,srvStride));
            commands.DrawIndexedInstanced(material.IndexCount,1,material.StartIndex,0,0);draws++;
        }
        commands.UnsetRenderTargets();return draws;
    }

    private void DisposeGiGBufferPipelines()
    {foreach(var states in giGBufferPipelines.Values){states.Back.Dispose();states.Both.Dispose();}giGBufferPipelines.Clear();}
}
