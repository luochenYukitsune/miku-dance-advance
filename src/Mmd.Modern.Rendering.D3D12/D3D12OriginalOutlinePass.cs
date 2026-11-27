using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12OriginalStyleRenderer
{
    private readonly record struct OutlinePipelineKey(
        Format Depth,
        Format SurfaceFormat,
        OriginalAlphaBlendMode Blend,
        FrameAlphaOutput AlphaOutput,
        bool WriteDepth,
        bool Stencil,
        bool Simple,
        bool FrontCounterClockwise);
    private readonly Dictionary<OutlinePipelineKey,OutlinePipeline> outlinePipelines=[];

    public RenderedFrame Render(OriginalFrameScene frame,int width,int height)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return RenderCore(frame.Surface,frame,width,height);
    }

    private delegate ID3D12Resource FrameBufferUpload(D3D12ExpandedPositions positions);
    private sealed record OutlinePipeline(ID3D12RootSignature Root,ID3D12PipelineState State);

    /// <summary>Records into the caller's active target/depth and command list. No submission, fence, reset or readback.
    /// Upload and resource ownership are borrowed from that frame, including the surface's index buffer.</summary>
    private int RecordOutline(ID3D12Device device,ID3D12GraphicsCommandList commands,OutlinePipeline pipeline,
        OriginalFrameScene frame,int width,int height,ID3D12Resource sharedSurfaceBuffer,ID3D12Resource sharedIndexBuffer,FrameBufferUpload upload,
        Func<ID3D12Resource,ID3D12Resource> own,D3D12RecordingContext context,D3D12GpuCache cache,int firstMaterial=0,int materialCount=int.MaxValue)
    {
        using var outlineCpu=MeasureSceneCpu(SceneCpuStage.OriginalOutline);
        var data=frame.Outline!; var options=frame.OutlineOptions!; var scene=frame.Surface.Snapshot;
        bool deferred=data.DeferredSurface is not null||scene.HasDeferredDeformation;
        var positions=deferred?null:GetEdgePositions(frame);
        var deferredMask=deferred?GetDeferredMaterialMask(scene):null;
        int vertexCount=data.VertexCount;
        ID3D12Resource? vb=null;
        ID3D12Resource? scaleSource=null;
        commands.SetGraphicsRootSignature(pipeline.Root); commands.SetPipelineState(pipeline.State);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetIndexBuffer(new IndexBufferView(sharedIndexBuffer.GPUVirtualAddress,checked(scene.Indices.Length*4),Format.R32_UInt));
        int draws=0;
        var worldViewProjection=scene.World*(frame.Options?.CameraOverride??scene.Camera).ViewProjection(width,height);
        int end=(int)Math.Min(data.Materials.Count,(long)firstMaterial+materialCount);
        bool needWidthSource=false;
        if(deferred)
            for(int k=firstMaterial;k<end;k++)
                if(frame.Materials[k].CharacterOutlineStyle is null&&frame.Materials[k].OutlineWidthScale!=1){needWidthSource=true;break;}
        for(int i=firstMaterial;i<end;i++)
        {
            if(frame.Materials[i].CharacterOutlineStyle is not null)continue;
            var edge=CharacterOutlineMath.ResolveMaterial(data.Materials[i],frame.Materials[i].CharacterOutlineStyle); var range=scene.Materials[i];
            // Alpha==0 is deliberately not a material gate. Colour alpha is retained for the next blend slice.
            float scale=frame.Materials[i].OutlineWidthScale;
            if(!edge.DrawEnabled || scale==0 || range.IndexCount==0) continue;
            if(deferred?!ShouldDrawOutlineDeferred(frame,i,deferredMask!,worldViewProjection):!ShouldDrawOutline(frame,i,positions!,worldViewProjection))continue;
            if(vb is null)
            {
                if(deferred)
                {
                    var gpuOutline=cache.GpuSkinning.GetOutlineStreams(frame,sharedSurfaceBuffer,modern:false,needWidthSource:needWidthSource);
                    commands.SetPipelineState(pipeline.State);
                    vb=gpuOutline.Stream;
                    scaleSource=gpuOutline.WidthSource;
                }
                else
                {
                    vb=upload(positions!);
                }
                commands.IASetVertexBuffers(0,new VertexBufferView(vb.GPUVirtualAddress,checked(vertexCount*16),16));
            }
            var source=scale==1?sharedSurfaceBuffer:(scaleSource??=D3D12OutlineDrawScale.SourceBuffer(data,cache));
            int sourceStride=scale==1?32:16;
            commands.IASetVertexBuffers(1,new VertexBufferView(source.GPUVirtualAddress,checked(vertexCount*sourceStride),sourceStride));
            commands.SetGraphicsRootConstantBufferView(0,context.UploadConstants(new OutlineConstants(worldViewProjection,edge.Color,new(scale,0,0,0))));
            commands.DrawIndexedInstanced(range.IndexCount,1,range.StartIndex,0,0); draws++;
        }
        return draws;
    }

    private int RecordCombinedOutline(ID3D12Device device,ID3D12GraphicsCommandList commands,OutlinePipeline pipeline,
        OriginalFrameScene frame,int width,int height,ID3D12Resource sharedSurfaceBuffer,ID3D12Resource sharedIndexBuffer,FrameBufferUpload upload,
        Func<ID3D12Resource,ID3D12Resource> own,D3D12RecordingContext context,ID3D12DescriptorHeap srvHeap,int srvStride,
        int slotBase,Format depthFormat,D3D12GpuCache cache,D3D12SurfaceStyleMap? surfaceStyles=null)
    {
        using var combinedCpu=MeasureSceneCpu(SceneCpuStage.CombinedOutline);
        // Contiguous runs retain source material order when blended modern and original
        // contours overlap. All-null snapshots take precisely the original recorder once.
        int draws=0,first=0;
        while(first<frame.Materials.Count)
        {
            bool modern=frame.Materials[first].CharacterOutlineStyle is not null;
            int end=first+1;
            while(end<frame.Materials.Count&&(frame.Materials[end].CharacterOutlineStyle is not null)==modern)end++;
            draws+=modern
                ?RecordCharacterOutline(frame,width,height,sharedSurfaceBuffer,sharedIndexBuffer,srvHeap,srvStride,slotBase,depthFormat,context,cache,first,end-first,surfaceStyles)
                :RecordOutline(device,commands,pipeline,frame,width,height,sharedSurfaceBuffer,sharedIndexBuffer,upload,own,context,cache,first,end-first);
            first=end;
        }
        return draws;
    }

    private OutlinePipeline GetOutlinePipeline(Format depthFormat, OriginalFrameScene frame)
    {
        using var pipelineCpu=MeasureSceneCpu(SceneCpuStage.Pipelines);
        var o=frame.Options;
        bool simple=o is null;
        var alphaOutput=o?.AlphaOutput??FrameAlphaOutput.LegacyMax;
        var blendMode=o?.BlendMode??OriginalAlphaBlendMode.Normal;
        var surfaceFormat=alphaOutput==FrameAlphaOutput.Straight?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
        bool writeDepth=o?.WriteDepth??true;
        bool stencil=o?.GroundShadow?.UseStencil==true;
        var key=new OutlinePipelineKey(depthFormat,surfaceFormat,blendMode,alphaOutput,writeDepth,stencil,simple,D3D12RasterWinding.FrontCounterClockwise(frame));
        if(outlinePipelines.TryGetValue(key,out var cached))return cached;
        RootParameter[] parameters=[new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All)];
        var root=runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,parameters),RootSignatureVersion.Version10);
        ID3D12PipelineState? state=null;
        try
        {
            var shader=GetOutlineShaders();runtime.PipelineCreations++;
            var raster=RasterizerDescription.CullNone;
            raster.FrontCounterClockwise=key.FrontCounterClockwise; raster.CullMode=CullMode.Front;
            var depth=DepthStencilDescription.Default;
            depth.DepthFunc=ComparisonFunction.LessEqual;
            depth.DepthWriteMask=key.WriteDepth?DepthWriteMask.All:DepthWriteMask.Zero;
            if(key.Stencil)
            {
                depth.StencilEnable=true; depth.StencilReadMask=255; depth.StencilWriteMask=255;
                depth.FrontFace.StencilFunc=ComparisonFunction.Always; depth.FrontFace.StencilPassOp=StencilOperation.Replace;
                depth.BackFace=depth.FrontFace;
            }
            var blend=key.Simple?BlendDescription.Opaque:FrameBlend(key.Blend,key.AlphaOutput);
            var description=new GraphicsPipelineStateDescription {
                RootSignature=root,VertexShader=shader.Vertex,PixelShader=shader.Pixel,
                InputLayout=new InputLayoutDescription([new InputElementDescription("POSITION",0,Format.R32G32B32_Float,0,0),
                    new("TEXCOORD",2,Format.R32G32B32A32_Float,0,1)]),
                BlendState=blend,SampleMask=uint.MaxValue,RasterizerState=raster,DepthStencilState=depth,
                PrimitiveTopologyType=PrimitiveTopologyType.Triangle,RenderTargetFormats=[key.SurfaceFormat],DepthStencilFormat=depthFormat,
                SampleDescription=new SampleDescription(1,0),IndexBufferStripCutValue=IndexBufferStripCutValue.Disabled };
            state=runtime.CreateGraphicsPipelineState(description);
            var result=new OutlinePipeline(root,state);outlinePipelines.Add(key,result);return result;
        }
        catch { state?.Dispose();root.Dispose(); throw; }
    }

    private (byte[] Vertex,byte[] Pixel)? outlineShaders;
    private (byte[] Vertex,byte[] Pixel) GetOutlineShaders()
    {if(outlineShaders is {} value)return value;runtime.ShaderCompilations+=2;return (outlineShaders=OriginalOutlineShaders.Compile()).Value;}
    private void DisposeOutlinePipeline()
    {foreach(var pipeline in outlinePipelines.Values){pipeline.State.Dispose();pipeline.Root.Dispose();}outlinePipelines.Clear();}

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct OutlineConstants(Matrix4x4 WorldViewProjection,Vector4 Color,Vector4 WidthScale);
}
