using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3D12Api = Vortice.Direct3D12.D3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Independent original character surface slice: opaque/cutout, default morph, in-bounds visibility=1.
/// Frame resources live until the submitted fence completes; readback is screenshot/CI only.</summary>
public sealed partial class D3D12OriginalStyleRenderer : IDisposable
{
    private enum SceneCpuStage
    {
        OriginalRecorder,Preflight,SurfaceStyleValidation,Pipelines,PrimaryShadow,NativeLights,Ground,SurfaceLoop,
        CombinedOutline,OriginalOutline,ModernOutline,OriginalPositions,ModernPositions,ModernPacking,
        OriginalOutlineUpload,ModernOutlineUpload,SurfaceBoundsLookup,SurfaceBoundsBuild,OutlineBoundsLookup,OutlineBoundsBuild,
        SurfaceBoundsTopology,SurfaceBoundsNewEntry,SurfaceBoundsInvalidation,SurfaceBoundsPreparation,SurfaceBoundsReduction,
        OutlineBoundsTopology,OutlineBoundsNewEntry,OutlineBoundsInvalidation,OutlineBoundsPreparation,OutlineBoundsReduction,Count
    }
    private struct SceneCpuMetric
    {
        internal long Ticks,AllocatedBytes;
        internal int Calls;
    }
    private readonly struct SceneCpuScope(D3D12OriginalStyleRenderer? owner,SceneCpuStage stage,long started,long allocated):IDisposable
    {
        public void Dispose()
        {
            if(owner is null)return;
            ref var metric=ref owner.sceneCpuMetrics[(int)stage];
            metric.Ticks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            metric.AllocatedBytes+=GC.GetAllocatedBytesForCurrentThread()-allocated;
            metric.Calls++;
        }
    }
    private readonly SceneCpuMetric[] sceneCpuMetrics=new SceneCpuMetric[(int)SceneCpuStage.Count];
    private bool sceneRecordCpuActive;
    private int sceneCpuInvocations;
    private long sceneCpuOriginalExpandedVertices,sceneCpuModernExpandedVertices,sceneCpuBoundsMaterialRangeComparisons;
    private long sceneCpuBoundsPositionComparisons,sceneCpuBoundsScannedIndices,sceneCpuBoundsRebuiltMaterials;
    private long sceneCpuBoundsFullScanMaterials,sceneCpuBoundsSparseScanMaterials,sceneCpuBoundsParallelMaterials,sceneCpuBoundsMarkedLeafIncidences;
    internal void ResetSceneRecordCpuStatistics()
    {
        Array.Clear(sceneCpuMetrics);sceneRecordCpuActive=false;sceneCpuInvocations=0;
        sceneCpuOriginalExpandedVertices=sceneCpuModernExpandedVertices=sceneCpuBoundsMaterialRangeComparisons=0;
        sceneCpuBoundsPositionComparisons=sceneCpuBoundsScannedIndices=sceneCpuBoundsRebuiltMaterials=0;
        sceneCpuBoundsFullScanMaterials=sceneCpuBoundsSparseScanMaterials=sceneCpuBoundsParallelMaterials=sceneCpuBoundsMarkedLeafIncidences=0;
    }
    internal void BeginSceneRecordCpu(){sceneRecordCpuActive=true;sceneCpuInvocations++;}
    internal void EndSceneRecordCpu()=>sceneRecordCpuActive=false;
    private SceneCpuScope MeasureSceneCpu(SceneCpuStage stage)=>sceneRecordCpuActive
        ?new(this,stage,System.Diagnostics.Stopwatch.GetTimestamp(),GC.GetAllocatedBytesForCurrentThread()):default;
    internal D3D12SceneRecordCpuStatistics SceneRecordCpuStatistics
    {
        get
        {
            D3D12CpuStageStatistics Read(SceneCpuStage stage)
            {
                var value=sceneCpuMetrics[(int)stage];
                return new(value.Ticks*1000d/System.Diagnostics.Stopwatch.Frequency,value.AllocatedBytes,value.Calls);
            }
            return new() {
                SceneInvocations=sceneCpuInvocations,OriginalRecorder=Read(SceneCpuStage.OriginalRecorder),
                Preflight=Read(SceneCpuStage.Preflight),SurfaceStyleValidation=Read(SceneCpuStage.SurfaceStyleValidation),
                Pipelines=Read(SceneCpuStage.Pipelines),PrimaryShadow=Read(SceneCpuStage.PrimaryShadow),
                NativeLights=Read(SceneCpuStage.NativeLights),Ground=Read(SceneCpuStage.Ground),SurfaceLoop=Read(SceneCpuStage.SurfaceLoop),
                CombinedOutline=Read(SceneCpuStage.CombinedOutline),OriginalOutline=Read(SceneCpuStage.OriginalOutline),ModernOutline=Read(SceneCpuStage.ModernOutline),
                OriginalPositions=Read(SceneCpuStage.OriginalPositions),ModernPositions=Read(SceneCpuStage.ModernPositions),ModernPacking=Read(SceneCpuStage.ModernPacking),
                OriginalOutlineUpload=Read(SceneCpuStage.OriginalOutlineUpload),ModernOutlineUpload=Read(SceneCpuStage.ModernOutlineUpload),
                SurfaceBoundsLookup=Read(SceneCpuStage.SurfaceBoundsLookup),SurfaceBoundsBuild=Read(SceneCpuStage.SurfaceBoundsBuild),
                OutlineBoundsLookup=Read(SceneCpuStage.OutlineBoundsLookup),OutlineBoundsBuild=Read(SceneCpuStage.OutlineBoundsBuild),
                SurfaceBoundsTopology=Read(SceneCpuStage.SurfaceBoundsTopology),SurfaceBoundsNewEntry=Read(SceneCpuStage.SurfaceBoundsNewEntry),
                SurfaceBoundsInvalidation=Read(SceneCpuStage.SurfaceBoundsInvalidation),SurfaceBoundsPreparation=Read(SceneCpuStage.SurfaceBoundsPreparation),
                SurfaceBoundsReduction=Read(SceneCpuStage.SurfaceBoundsReduction),
                OutlineBoundsTopology=Read(SceneCpuStage.OutlineBoundsTopology),OutlineBoundsNewEntry=Read(SceneCpuStage.OutlineBoundsNewEntry),
                OutlineBoundsInvalidation=Read(SceneCpuStage.OutlineBoundsInvalidation),OutlineBoundsPreparation=Read(SceneCpuStage.OutlineBoundsPreparation),
                OutlineBoundsReduction=Read(SceneCpuStage.OutlineBoundsReduction),
                OriginalExpandedVertices=sceneCpuOriginalExpandedVertices,ModernExpandedVertices=sceneCpuModernExpandedVertices,
                BoundsMaterialRangeComparisons=sceneCpuBoundsMaterialRangeComparisons,
                BoundsPositionComparisons=sceneCpuBoundsPositionComparisons,BoundsScannedIndices=sceneCpuBoundsScannedIndices,
                BoundsRebuiltMaterials=sceneCpuBoundsRebuiltMaterials,BoundsFullScanMaterials=sceneCpuBoundsFullScanMaterials,
                BoundsSparseScanMaterials=sceneCpuBoundsSparseScanMaterials,BoundsParallelMaterials=sceneCpuBoundsParallelMaterials,
                BoundsMarkedLeafIncidences=sceneCpuBoundsMarkedLeafIncidences };
        }
    }
    private readonly D3D12GpuRuntime runtime;
    private readonly bool ownsRuntime;
    private ID3D12Device device=>runtime.Device;
    private readonly Dictionary<(OriginalTextureSampling,Format,bool,bool,bool),PipelineSet> pipelines=[];
    private D3D12FrameRenderer? captureRecorder;
    private bool disposed;
    public string AdapterName=>runtime.AdapterName;
    public bool IsWarp=>runtime.IsWarp;
    public FeatureLevel FeatureLevel=>runtime.FeatureLevel;
    public bool DebugRequested=>runtime.DebugRequested;
    public bool DebugLayerEnabled=>runtime.DebugLayerEnabled;
    public bool DebugInfoQueueAvailable=>runtime.DebugQueue is not null;
    public int LastVertexCount {get;private set;}
    public int LastIndexCount {get;private set;}
    public int LastUploadedTextures {get;private set;}
    public IReadOnlyList<string> LastDebugMessages=>captureRecorder?.LastDebugMessages??[];
    public D3D12OriginalStyleRenderer():this(new D3D12GpuRuntime(),true){}
    internal D3D12OriginalStyleRenderer(D3D12GpuRuntime runtime,bool ownsRuntime=false)
    {this.runtime=runtime;this.ownsRuntime=ownsRuntime;}
    public RenderedFrame Render(OriginalStyleScene style,int width,int height)=>RenderCore(style,null,width,height);
    private RenderedFrame RenderCore(OriginalStyleScene style,OriginalFrameScene? frame,int width,int height)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ArgumentNullException.ThrowIfNull(style);
        captureRecorder??=new D3D12FrameRenderer(runtime,false,this);
        LastVertexCount=style.Snapshot.VertexCount;LastIndexCount=style.Snapshot.Indices.Length;LastUploadedTextures=style.Snapshot.Textures.Count+2;
        return captureRecorder.Render(frame??new OriginalFrameScene(style),width,height) with {Backend="Direct3D12OriginalSurface"};
    }
    internal int RecordBorrowed(D3D12RecordingContext context,D3D12GpuTarget color,D3D12GpuTarget depth,OriginalFrameScene frame,
        ID3D12Resource vb,ID3D12Resource ib,ID3D12DescriptorHeap srvHeap,int srvStride,int slotBase,D3D12GpuCache cache,ref PreparedFrame? prepared,D3D12RayGiRenderer.Prepared? gi=null,D3D12SurfaceStyleMap? surfaceStyles=null)
    {
        using var recorderCpu=MeasureSceneCpu(SceneCpuStage.OriginalRecorder);
        var preflightCpu=MeasureSceneCpu(SceneCpuStage.Preflight);
        ObjectDisposedException.ThrowIf(disposed,this);
        using(var validationCpu=MeasureSceneCpu(SceneCpuStage.SurfaceStyleValidation))surfaceStyles?.Validate(frame);
        if(context.Device.NativePointer!=device.NativePointer)throw new InvalidOperationException("Cross-device original frame recording is unsupported.");
        bool straight=frame.Options?.AlphaOutput==FrameAlphaOutput.Straight;
        if(color.Format!=(straight?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm)||depth.Format is not (Format.D32_Float or Format.D24_UNorm_S8_UInt))throw new InvalidOperationException("Original frame requires its selected RGBA color format with D32 or D24S8 depth.");
        var commands=context.Commands;var scene=frame.Surface.Snapshot;var style=frame.Surface;int width=color.Width,height=color.Height;
        bool subTexture=HasSubTexture(frame);
        bool textureMorph=subTexture||style.Materials.Any(binding=>!binding.TextureFactors.IsIdentity);
        if(!HasNativeLights(frame)){LastNativeLightStatistics=null;nativeShadowCasterVersions.Reset();}
        BeginVisibility(frame);
        ID3D12Resource Own(ID3D12Resource value)=>context.Own(value);
        ID3D12Resource UploadBuffer(D3D12ExpandedPositions positions)
        {
            using var uploadCpu=MeasureSceneCpu(SceneCpuStage.OriginalOutlineUpload);
            return cache.ExpandedPositionBuffer("original-edge",positions);
        }
        bool needAdditionalUvs=subTexture||(surfaceStyles?.HasGeometryRequirement(D3D12StyleGeometryRequirements.SubTextureUv)==true);
        if(needAdditionalUvs)BindAdditionalUvs(context,cache,scene);
        if(surfaceStyles?.HasGeometryRequirement(D3D12StyleGeometryRequirements.RawRadialSeeds)==true)
        {
            var seeds=D3D12TangentCornerGeometry.GetOrComputeRawRadialSeeds(scene,out var seedIdentity);
            var buffer=cache.BufferByIdentity("original-radial-seeds",MemoryMarshal.AsBytes(seeds),ResourceStates.VertexAndConstantBuffer,seedIdentity);
            commands.IASetVertexBuffers(2,new VertexBufferView(buffer.GPUVirtualAddress,checked(scene.VertexCount*12),12));
        }
        preflightCpu.Dispose();
        if(frame.Options is not null)
            return RecordFrame(device,commands,GetFramePipelines(frame,depth.Format,gi is not null,surfaceStyles?.HasAnyPluginSpace2==true),frame,width,height,vb,ib,srvHeap,srvStride,color.Handle,depth.Handle,depth.Format,UploadBuffer,Own,value=>context.Own(value),ref prepared,slotBase,context,cache,gi,surfaceStyles);
        var selected=GetPipelines(style.Profile.TextureSampling,depth.Format,textureMorph,subTexture,D3D12RasterWinding.FrontCounterClockwise(frame));var root=selected.Root;
        commands.SetGraphicsRootSignature(root);commands.SetDescriptorHeaps(srvHeap);commands.OMSetRenderTargets(color.Handle,depth.Handle);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetVertexBuffers(0,new VertexBufferView(vb.GPUVirtualAddress,checked(scene.VertexCount*32),32));
        commands.IASetIndexBuffer(new IndexBufferView(ib.GPUVirtualAddress,checked(scene.Indices.Length*4),Format.R32_UInt));
        Matrix4x4.Invert(scene.World,out var inverse);var normal=Matrix4x4.Transpose(inverse);int draws=0;
        using(var surfaceCpu=MeasureSceneCpu(SceneCpuStage.SurfaceLoop))
        {
            for (int materialIndex = 0; materialIndex < scene.Materials.Count; materialIndex++)
            {
                var material = scene.Materials[materialIndex];
                var binding = style.Materials[materialIndex];
                if (material.IndexCount == 0) continue;
                commands.SetPipelineState(binding.DoubleSided ? selected.DoubleSided : selected.CullBack);
                var host = OriginalStyleMath.ResolveHostColors(material, style.Profile);
                var constants=context.UploadConstants(new SurfaceConstants(scene.World, normal, (frame.Options?.CameraOverride??scene.Camera).ViewProjection(width, height), style.Profile.SphereRotation,
                    host.EgColor, host.SpecularPower, new(Vector3.Normalize(scene.LightDirection),0), new((frame.Options?.CameraOverride??scene.Camera).Eye,1),
                    SurfaceToon(binding), new(material.AlphaCutoff,SurfaceMode(binding),material.DiffuseTexture>=0?1:0,0)));
                commands.SetGraphicsRootConstantBufferView(0, constants);
                if(textureMorph)commands.SetGraphicsRootConstantBufferView(3,context.UploadConstants(new TextureMorphConstants(binding.TextureFactors)));
                commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(material.DiffuseTexture < 0 ? 0 : material.DiffuseTexture + 2), srvStride));
                commands.SetGraphicsRootDescriptorTable(2, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(binding.SphereTexture < 0 ? 0 : binding.SphereTexture + 2), srvStride));
                commands.DrawIndexedInstanced(material.IndexCount, 1, material.StartIndex, 0, 0); draws++;
            }
        }
            if(frame?.Outline is not null && frame.OutlineOptions is {Eligible:true,ModelWidth:>0})
                draws+=RecordCombinedOutline(device,commands,GetOutlinePipeline(depth.Format,frame),frame,width,height,vb,ib,UploadBuffer,resource=>Own(resource),context,
                    srvHeap,srvStride,slotBase,depth.Format,cache,surfaceStyles);
        return draws;
    }
    public void Dispose()
    {
        if(disposed)return;runtime.WaitForIdleForDisposal();disposed=true;captureRecorder?.Dispose();
        DisposeOutlinePipeline();DisposeFramePipelines();DisposeCharacterOutlinePipelines();
        DisposeGiGBufferPipelines();
        foreach(var value in pipelines.Values){value.DoubleSided.Dispose();value.CullBack.Dispose();value.Root.Dispose();}pipelines.Clear();
        if(ownsRuntime)runtime.Dispose();
    }
    private sealed record PipelineSet(ID3D12RootSignature Root,ID3D12PipelineState DoubleSided,ID3D12PipelineState CullBack);
    private PipelineSet GetPipelines(OriginalTextureSampling sampling,Format depthFormat,bool textureMorph=false,bool subTexture=false,bool frontCounterClockwise=true)
    {
        using var pipelineCpu=MeasureSceneCpu(SceneCpuStage.Pipelines);
        if(pipelines.TryGetValue((sampling,depthFormat,textureMorph,subTexture,frontCounterClockwise),out var cached)) return cached;
        RootParameter[] parameters=[
            new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,0)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,1)),ShaderVisibility.Pixel)];
        if(textureMorph)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(3,0),ShaderVisibility.Pixel)];
        bool linear=sampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.LinearWrap;
        bool wrap=sampling is OriginalTextureSampling.LinearWrap or OriginalTextureSampling.PointWrap;
        var sampler=new StaticSamplerDescription(ShaderVisibility.Pixel,0,0) {
            Filter=linear?Filter.MinMagMipLinear:Filter.MinMagMipPoint,
            AddressU=wrap?TextureAddressMode.Wrap:TextureAddressMode.Clamp,
            AddressV=wrap?TextureAddressMode.Wrap:TextureAddressMode.Clamp,
            AddressW=wrap?TextureAddressMode.Wrap:TextureAddressMode.Clamp };
        var createdRoot=runtime.CreateRootSignature(new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout,parameters,[sampler]),RootSignatureVersion.Version10);
        ID3D12PipelineState? doubleSided=null;
        try {
            var shader=subTexture?GetSubTextureSurfaceShaders():textureMorph?GetTextureMorphSurfaceShaders():GetSurfaceShaders();
            ID3D12PipelineState Create(RasterizerDescription raster) {runtime.PipelineCreations++;return runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
                RootSignature=createdRoot,VertexShader=shader.Vertex,PixelShader=shader.Pixel,
                InputLayout=new InputLayoutDescription(SurfaceLayout(subTexture)),
                BlendState=BlendDescription.Opaque,SampleMask=uint.MaxValue,RasterizerState=raster,
                DepthStencilState=DepthStencilDescription.Default,PrimitiveTopologyType=PrimitiveTopologyType.Triangle,
                RenderTargetFormats=[Format.R8G8B8A8_UNorm],DepthStencilFormat=depthFormat,
                SampleDescription=new SampleDescription(1,0),IndexBufferStripCutValue=IndexBufferStripCutValue.Disabled });}
            doubleSided=Create(RasterizerDescription.CullNone);
            var backRaster=RasterizerDescription.CullNone;
            backRaster.CullMode=CullMode.Back;
            // PMX front faces reverse screen winding between RH and LH cameras.
            backRaster.FrontCounterClockwise=frontCounterClockwise;
            var result=new PipelineSet(createdRoot,doubleSided,Create(backRaster));
            pipelines.Add((sampling,depthFormat,textureMorph,subTexture,frontCounterClockwise),result);return result;
        } catch { doubleSided?.Dispose();createdRoot.Dispose();throw; }
    }
    private readonly D3D12OutlinePositionCache edgePositionCache=new();
    private D3D12ExpandedPositions GetEdgePositions(OriginalFrameScene frame)
    {
        using var positionsCpu=MeasureSceneCpu(SceneCpuStage.OriginalPositions);
        var positions=edgePositionCache.GetChunks(frame.Outline!,frame.OutlineOptions!);
        if(sceneRecordCpuActive)sceneCpuOriginalExpandedVertices+=edgePositionCache.LastExpandedVertices;
        return positions;
    }
    private (byte[] Vertex,byte[] Pixel)? surfaceShaders;
    private (byte[] Vertex,byte[] Pixel) GetSurfaceShaders()
    {if(surfaceShaders is {} value)return value;runtime.ShaderCompilations+=2;return (surfaceShaders=OriginalStyleShaders.Compile()).Value;}
    private readonly Dictionary<(string,bool),byte[]> frameShaders=[];
    private byte[] GetFrameShader(string entry,bool vertex)
    {
        if(frameShaders.TryGetValue((entry,vertex),out var value))return value;
        value=OriginalFrameShaders.Compile(entry,vertex);frameShaders.Add((entry,vertex),value);runtime.ShaderCompilations++;return value;
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SurfaceConstants(Matrix4x4 World,Matrix4x4 NormalWorld,Matrix4x4 ViewProjection,Matrix4x4 SphereRotation,
        Vector4 EgColor,Vector4 SpecularPower,Vector4 IncomingLight,Vector4 EyePosition,Vector4 ToonShadowColor,Vector4 Options);
}
