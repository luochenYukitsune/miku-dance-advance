using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>GI-only reprojection. History stores incident RGB before spatial filtering; alpha stays current visibility.
/// The owner swaps only after Submit. Explicit frame leases protect replaced history through every GPU consumer.</summary>
internal sealed partial class D3D12RayGiTemporalPass(D3D12GpuRuntime runtime):IDisposable
{
    private sealed class Buffers:IDisposable
    {
        internal readonly ID3D12Resource Signal,Moments,Position,Normal,Triangles;
        internal Buffers(D3D12GpuRuntime runtime,int width,int height,ID3D12Resource triangles)
        {
            var created=new List<ID3D12Resource>();
            ID3D12Resource Buffer(){var r=runtime.Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer(checked((ulong)width*(ulong)height*16),ResourceFlags.AllowUnorderedAccess),ResourceStates.UnorderedAccess);created.Add(r);return r;}
            try
            {
                Signal=Buffer();Moments=Buffer();Position=Buffer();Normal=Buffer();
                // The geometry cache can replace its wrapper even in an aborted frame.
                // History owns a separate COM reference to the immutable, shader-readable buffer.
                Triangles=triangles.QueryInterface<ID3D12Resource>();
            }
            catch{foreach(var resource in created)resource.Dispose();throw;}
        }
        internal ID3D12Resource[] All=>[Signal,Moments,Position,Normal];
        public void Dispose(){foreach(var resource in All)resource.Dispose();Triangles.Dispose();}
    }
    private sealed record History(D3D12RetainedResource Owner,Buffers Gpu,OriginalFrameScene Frame,RayGiScene Scene,
        RayGiOptions Options,int Width,int Height,ulong Frames);
    // Stable spatial sample indices own separate preceding logical-frame histories. Each
    // bank has four private buffers and holds a reference to the frame's shared triangle buffer.
    private Dictionary<int,History> previous=[];
    private Dictionary<int,History> pending=[];
    private int submittedSampleCount,pendingSampleCount;
    private int preparedSampleIndex=-1;
    private ID3D12RootSignature? root;
    private ID3D12PipelineState? pipeline;
    private string invalidationReason="FirstFrame";
    internal bool LastReset {get;private set;}
    internal string LastResetReason {get;private set;}="FirstFrame";
    internal ulong LastHistoryFrames {get;private set;}
    internal uint LastSeed {get;private set;}
    internal ID3D12Resource? RecordedSignal=>pending.TryGetValue(preparedSampleIndex,out var value)?value.Gpu.Signal:null;
    internal ID3D12Resource? RecordedMoments=>pending.TryGetValue(preparedSampleIndex,out var value)?value.Gpu.Moments:null;
    internal bool HasPendingHistory=>pending.Count>0;
    internal long HistoryBytes=>previous.Values.Sum(value=>checked((long)value.Width*value.Height*D3D12TemporalGiMemoryBudget.HistoryBytesPerPixel));

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Constants(RayGiUInt4 Dimensions,RayGiUInt4 Control,Matrix4x4 PreviousViewProjection,
        Vector4 PreviousEye,Vector4 PreviousForwardPixel);

    internal uint Prepare(OriginalFrameScene frame,RayGiScene scene,RayGiOptions options,int width,int height,int sampleIndex=0,int sampleCount=1)
    {
        _=SpatialSampling.JitterPixels(sampleCount,sampleIndex); // Validate the existing fixed pattern/range.
        if(preparedSampleIndex>=0&&!pending.ContainsKey(preparedSampleIndex))
            throw new InvalidOperationException("The prepared temporal GI sample has not been recorded.");
        if(pendingSampleCount!=0&&pendingSampleCount!=sampleCount)
            throw new InvalidOperationException("All temporal GI samples in one submission must use the same spatial sample count.");
        if(pending.ContainsKey(sampleIndex))throw new InvalidOperationException("A temporal GI spatial sample can be recorded only once per submitted frame.");
        pendingSampleCount=sampleCount;preparedSampleIndex=sampleIndex;
        previous.TryGetValue(sampleIndex,out var history);
        string? reason=submittedSampleCount!=0&&submittedSampleCount!=sampleCount?"SampleCountChanged"
            :ResetReason(history,frame,scene,options,width,height);
        LastReset=reason is not null;LastResetReason=reason??"None";
        ulong phase=LastReset?0:history!.Frames;
        LastHistoryFrames=phase+1;
        // Independent samples across successful frames. Aborted recordings leave both phase and seed unchanged.
        LastSeed=unchecked(options.Seed+(uint)phase*0x9e3779b9u);return LastSeed;
    }

    private string? ResetReason(History? previous,OriginalFrameScene frame,RayGiScene scene,RayGiOptions options,int width,int height)
    {
        if(previous is null)return invalidationReason;
        if(options.ResetTemporalHistory)return "ExplicitReset";
        if(previous.Width!=width||previous.Height!=height)return "Resized";
        if(previous.Options.TemporalHistoryGeneration!=options.TemporalHistoryGeneration)return "GenerationChanged";
        if(previous.Options.TemporalCameraCutKey!=options.TemporalCameraCutKey)return "CameraCut";
        static RayGiOptions Settings(RayGiOptions value)=>value with {ResetTemporalHistory=false,TemporalHistoryGeneration=0,TemporalCameraCutKey=0};
        if(Settings(previous.Options)!=Settings(options))return "SettingsChanged";
        var a=frame.Surface.Snapshot;var b=previous.Frame.Surface.Snapshot;
        if(!frame.Materials.Select(value=>value.GiToonResponseOverride)
            .SequenceEqual(previous.Frame.Materials.Select(value=>value.GiToonResponseOverride)))return "ReceiverResponseChanged";
        if(!frame.Materials.Select(value=>value.CharacterToonResponse)
            .SequenceEqual(previous.Frame.Materials.Select(value=>value.CharacterToonResponse)))return "ReceiverResponseChanged";
        if(!frame.Materials.Select(value=>value.PreserveDirectLightPalette)
            .SequenceEqual(previous.Frame.Materials.Select(value=>value.PreserveDirectLightPalette)))return "ReceiverResponseChanged";
        if(a.VertexCount!=b.VertexCount||!new D3D12IndexStreamIdentity(a).Equals(new D3D12IndexStreamIdentity(b))||a.Materials.Count!=b.Materials.Count)return "TopologyChanged";
        for(int i=0;i<a.Materials.Count;i++)
            if(a.Materials[i].StartIndex!=b.Materials[i].StartIndex||a.Materials[i].IndexCount!=b.Materials[i].IndexCount)return "TopologyChanged";
        if(!D3D12GpuSkinning.HaveSameBaseUvs(a,b))return "TextureCoordinatesChanged";
        // Compare the consumed triangle-order UV1.xy values, never stream owners or
        // unused author channels. Equal fresh poses must retain submitted history.
        var uvs=scene.SubTextureUvs;var oldUvs=previous.Scene.SubTextureUvs;
        if(uvs.Length!=oldUvs.Length)return "TextureCoordinatesChanged";
        for(int i=0;i<uvs.Length;i++)
            if(uvs[i].Uv01!=oldUvs[i].Uv01||uvs[i].Uv2.X!=oldUvs[i].Uv2.X||uvs[i].Uv2.Y!=oldUvs[i].Uv2.Y)return "TextureCoordinatesChanged";
        var camera=frame.Options?.CameraOverride??a.Camera;var oldCamera=previous.Frame.Options?.CameraOverride??b.Camera;
        if(camera.LeftHanded!=oldCamera.LeftHanded||camera.Near!=oldCamera.Near||camera.Far!=oldCamera.Far
            ||camera.VerticalFovDegrees!=oldCamera.VerticalFovDegrees
            ||(camera.VerticalFovDegrees is null&&camera.OrthographicHeight!=oldCamera.OrthographicHeight))return "ProjectionChanged";
        if(!MemoryMarshal.AsBytes(scene.Materials).SequenceEqual(MemoryMarshal.AsBytes(previous.Scene.Materials))
            ||!MemoryMarshal.AsBytes(scene.DiffuseTextureFactors).SequenceEqual(MemoryMarshal.AsBytes(previous.Scene.DiffuseTextureFactors)))return "MaterialChanged";
        static (bool Additive,float ToonW) SubTextureState(OriginalStyleScene style,int index)
        {
            var binding=style.Materials[index];
            // The no-diffuse technique always multiplies and restores raw alpha.
            if(binding.SphereMode!=OriginalSphereMode.SubTexture||style.Snapshot.Materials[index].DiffuseTexture<0)return(false,1);
            return(binding.SubTextureAdditive,binding.TextureFactors.Toon.Multiply.W+binding.TextureFactors.Toon.Add.W);
        }
        for(int i=0;i<a.Materials.Count;i++)
            if(SubTextureState(frame.Surface,i)!=SubTextureState(previous.Frame.Surface,i))return "MaterialChanged";
        if(a.Textures.Count!=b.Textures.Count)return "TexturesChanged";
        for(int i=0;i<a.Textures.Count;i++)if(!ReferenceEquals(a.Textures[i],b.Textures[i]))return "TexturesChanged";
        if((frame.Lighting?.Settings.EnvironmentStrength??0)!=(previous.Frame.Lighting?.Settings.EnvironmentStrength??0)
            ||a.LightDirection!=b.LightDirection||!MemoryMarshal.AsBytes(NativeRayGiLights.From(frame).UploadRows.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(NativeRayGiLights.From(previous.Frame).UploadRows.AsSpan())))return "LightingChanged";
        return null;
    }

    internal ID3D12Resource Record(D3D12RecordingContext context,ID3D12Resource input,ID3D12Resource triangles,
        GpuDescriptorHandle guides,OriginalFrameScene frame,RayGiScene scene,RayGiOptions options,int width,int height,DxcShaderCompiler compiler)
    {
        if(preparedSampleIndex<0||pending.ContainsKey(preparedSampleIndex))
            throw new InvalidOperationException("Prepare a new temporal GI spatial sample before recording it.");
        EnsurePipeline(compiler);
        var gpu=new Buffers(runtime,width,height,triangles);var owner=new D3D12RetainedResource(new object(),gpu);
        var recorded=new History(owner,gpu,frame,scene,options,width,height,LastHistoryFrames);
        pending.Add(preparedSampleIndex,recorded);
        context.Own(owner.Lease());runtime.OnAbort(AbortFrame);
        var history=LastReset?recorded:previous[preparedSampleIndex];
        if(!ReferenceEquals(history,recorded))context.Own(history.Owner.Lease());
        var oldTriangles=history.Gpu.Triangles;
        var camera=history.Frame.Options?.CameraOverride??history.Frame.Surface.Snapshot.Camera;
        var forward=Vector3.Normalize(camera.Target-camera.Eye);
        float pixel=camera.VerticalFovDegrees is {} fov?2*MathF.Tan(fov*MathF.PI/360)/height:camera.OrthographicHeight/height;
        var commands=context.Commands;
        commands.SetComputeRootSignature(root!);commands.SetPipelineState(pipeline!);
        commands.SetComputeRootConstantBufferView(0,context.UploadConstants(new Constants(new((uint)width,(uint)height,(uint)scene.TriangleCount,(uint)scene.Materials.Length),
            new(LastReset?0u:1u,camera.VerticalFovDegrees is null?0u:1u,0,0),camera.ViewProjection(width,height),new(camera.Eye,0),new(forward,pixel))));
        commands.SetComputeRootShaderResourceView(1,triangles.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(2,oldTriangles.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(3,input.GPUVirtualAddress);
        commands.SetComputeRootDescriptorTable(4,guides);
        // Reset dispatch does not read old history. Bind the readable raw input for unused SRVs,
        // avoiding an SRV/UAV alias even on the first frame.
        var old=LastReset?new[]{input,input,input,input}:history.Gpu.All;
        for(int i=0;i<4;i++)commands.SetComputeRootShaderResourceView(5+i,old[i].GPUVirtualAddress);
        var output=gpu.All;
        for(int i=0;i<4;i++)commands.SetComputeRootUnorderedAccessView(9+i,output[i].GPUVirtualAddress);
        commands.Dispatch((width+7)/8,(height+7)/8,1);
        foreach(var resource in output){commands.ResourceBarrierUnorderedAccessView(resource);commands.ResourceBarrierTransition(resource,ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);}
        return gpu.Signal;
    }

    internal void ValidatePendingFrame()
    {
        if(pendingSampleCount==0)return; // A successfully submitted empty scene invalidates at the caller.
        if(pending.Count!=pendingSampleCount)
            throw new InvalidOperationException("Every temporal GI spatial sample must be recorded before submitting its logical frame.");
    }
    internal void CommitFrame()
    {
        ValidatePendingFrame();if(pendingSampleCount==0)return;
        var retired=previous;previous=pending;pending=[];
        submittedSampleCount=pendingSampleCount;pendingSampleCount=0;preparedSampleIndex=-1;invalidationReason="FirstFrame";
        // Publish the complete group first. Each old history has a fence lease for every
        // command list using it, so releasing its owner cannot retire in-flight GPU data.
        foreach(var history in retired.Values)history.Owner.Dispose();
    }
    internal void AbortFrame()
    {
        foreach(var history in pending.Values)history.Owner.Dispose();
        pending.Clear();pendingSampleCount=0;preparedSampleIndex=-1;
    }
    internal void Invalidate(string reason)
    {
        AbortFrame();foreach(var history in previous.Values)history.Owner.Dispose();
        previous.Clear();submittedSampleCount=0;invalidationReason=reason;
    }
    internal static DxcShaderWarmupRequest GetPrewarmRequest() => new(Source, "CSMain", "cs_6_0");

    private void EnsurePipeline(DxcShaderCompiler compiler)
    {
        if(pipeline is not null)return;
        var shader=compiler.CompileCompute(Source,"CSMain","cs_6_0");
        var parameters=new List<RootParameter>{new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All)};
        for(int i=0;i<3;i++)parameters.Add(new(RootParameterType.ShaderResourceView,new RootDescriptor(i,0),ShaderVisibility.All));
        parameters.Add(new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,2,3)),ShaderVisibility.All));
        for(int i=5;i<9;i++)parameters.Add(new(RootParameterType.ShaderResourceView,new RootDescriptor(i,0),ShaderVisibility.All));
        for(int i=0;i<4;i++)parameters.Add(new(RootParameterType.UnorderedAccessView,new RootDescriptor(i,0),ShaderVisibility.All));
        var signature=runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.None,parameters.ToArray()),RootSignatureVersion.Version10);
        try{pipeline=runtime.CreateComputePipelineState(new ComputePipelineStateDescription{RootSignature=signature,ComputeShader=shader.Dxil});root=signature;runtime.ShaderCompilations++;runtime.PipelineCreations++;}
        catch{signature.Dispose();throw;}
    }
    public void Dispose(){Invalidate("Disposed");pipeline?.Dispose();root?.Dispose();pipeline=null;root=null;}
}
