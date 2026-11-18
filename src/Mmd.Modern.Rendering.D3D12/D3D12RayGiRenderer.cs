using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D12;
using Vortice.DXGI;
namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Raster receivers and common one-bounce integration, borrowing the frame device/list.
/// Transient inputs and outputs belong to its fence; the original surface composes indirect light.</summary>
internal sealed class D3D12RayGiRenderer : IDisposable
{
    internal sealed record Prepared(ID3D12Resource Indirect,GpuDescriptorHandle Inputs,float Strength,float OcclusionStrength,RayGiComposition Composition,
        RayGiToonResponse ToonResponse=RayGiToonResponse.Continuous);
    private sealed record Pipeline(ID3D12RootSignature Root,ID3D12PipelineState State,int TextureFactorRoot=-1,int NativeLightRoot=-1,int SubTextureRoot=-1);
    private static readonly RayGiBvhNodeGpu[] HardwareBvhNodes=[new(new(float.PositiveInfinity),new(float.NegativeInfinity),new(0,0,1,1))];
    private static readonly uint[] HardwareTriangleOrder=[0u];
    private static readonly object HardwareBvhIdentity=new();
    private readonly D3D12GpuRuntime runtime;
    private readonly D3D12GpuCache cache;
    private readonly D3D12RayGiTriangleScratch triangleScratch=new();
    private readonly Dictionary<(bool Hardware,RayGiComposition Composition,bool TextureMorph,bool NativeLights,bool SubTexture,bool Diagnostics),Pipeline> computePipelines=[];
    private D3D12RayGiCapabilities? capabilities;
    private DxcShaderCompiler? compiler;
    private D3D12RayGiDenoiser? denoiser;
    private D3D12RayGiTemporalPass? temporalDenoiser;
    private bool disposed;
    private sealed record DiagnosticReadback(string Name,ID3D12Resource Resource,int Bytes,int Width,int Height,int RowPitch);
    private readonly List<DiagnosticReadback> readbacks=[];
    private sealed record TimingCapture(string Directory,D3D12CaptureTiming Timing,string? ActualLabel=null,bool IsStage=false);
    private readonly List<TimingCapture> timings=[];
    private Dictionary<string,long>? cpuStageTicks;
    private readonly HashSet<D3D12HardwareRayGi.AccelerationStructure> countedAcceleration=[];
    private readonly HashSet<D3D12RayGiWorkspace> receiverWorkspaces=[];
    internal int DiagnosticReadbackCount=>readbacks.Count+timings.Count;
    internal D3D12RayGiStatistics? LastStatistics {get;private set;}
    internal string? LastBackendIdentity {get;private set;}
    internal string? CompilerDiagnostics {get;private set;}
    internal D3D12RayGiRenderer(D3D12GpuRuntime runtime,D3D12GpuCache cache){this.runtime=runtime;this.cache=cache;}
    internal D3D12RayGiCapabilities QueryCapabilities()
    {
        if(capabilities is not null)return capabilities;
        bool available;try{_=DxcShaderCompiler.ResolveCompilerPath();available=true;}catch{available=false;}
        return capabilities=D3D12HardwareRayGi.Inspect(runtime,available);
    }
    internal void BeginFrame(bool retainTriangleScratch)
    {
        LastStatistics=null;LastBackendIdentity=null;readbacks.Clear();timings.Clear();cpuStageTicks=null;countedAcceleration.Clear();receiverWorkspaces.Clear();
        if(!retainTriangleScratch)triangleScratch.Clear();
    }
    internal static bool UsesTriangleScratch(SceneSnapshot scene,RayGiOptions? options,bool capture)
        =>!capture&&scene.HasDynamicVertices&&options is {Mode:LightingMode.HardwareRayTracing,Strength:>0};
    internal static bool IsTemporalEnabled(RayGiOptions? options)
        =>options?.Enabled==true&&options.Strength>0&&options.Denoiser==RayGiDenoiser.TemporalSpatial;
    internal void ValidatePendingFrame()=>temporalDenoiser?.ValidatePendingFrame();
    internal void CommitFrame()
    {
        if(temporalDenoiser is null)return;
        // A successfully submitted empty scene has no receivers to carry into the next frame.
        if(!temporalDenoiser.HasPendingHistory)temporalDenoiser.Invalidate("EmptyFrame");
        else temporalDenoiser.CommitFrame();
    }
    internal void AbortFrame()=>temporalDenoiser?.AbortFrame();
    internal void InvalidateTemporalHistory(string reason="ExplicitReset")=>temporalDenoiser?.Invalidate(reason);
    internal static void Validate(OriginalFrameScene frame,RayGiOptions options)
    {
        options.Validate();if(!options.Enabled)return;
        if(options.Strength>0&&options.Composition!=RayGiComposition.ToonPalette
            &&frame.Materials.Where((flags,index)=>flags.SurfaceEnabled&&frame.Surface.Snapshot.Materials[index].IndexCount>0)
                .Any(flags=>flags.GiToonResponseOverride==RayGiToonResponse.PreservePaletteBands))
            throw new NotSupportedException("Character palette receivers require ToonPalette GI; Additive would bypass the character color policy. No GPU frame was begun.");
        if(frame.Options?.Transp==true)throw new NotSupportedException("The alpha=.5 transparency diagnostic cannot be combined with ray-traced GI.");
        if(frame.Options?.WriteDepth==false)throw new NotSupportedException("Ray-traced GI requires surface depth writes; multilayer GI with WriteDepth=false is unsupported.");
        bool toonPalette=options.Composition==RayGiComposition.ToonPalette;
        bool nativeLights=D3D12OriginalStyleRenderer.HasNativeLights(frame);
        // ToonPalette's bounds require a nonnegative diffuse lit endpoint and a toon palette in
        // [0,1]. Reject unsupported authored domains instead of silently clamping original inputs.
        double maxEmission=0;
        for(int i=0;i<frame.Surface.Materials.Count;i++)
        {
            var material=frame.Surface.Snapshot.Materials[i];
            if(!frame.Materials[i].SurfaceEnabled||material.IndexCount==0)continue;
            ValidateTextureFactors(frame.Surface,material,i,toonPalette,nativeLights &&
                (frame.Lighting!.Settings.EnvironmentStrength>0 || frame.Lighting.ReceiverMask(frame.Materials[i].LightReceiverObjectId)!=0),frame.Materials[i].UseSceneLighting);
            maxEmission=Math.Max(maxEmission,Math.Max(material.Emission.X,Math.Max(material.Emission.Y,material.Emission.Z)));
            if(!toonPalette)continue;
            var eg=OriginalStyleMath.ResolveHostColors(material,frame.Surface.Profile,frame.Materials[i].UseSceneLighting).EgColor;
            var endpoint=new Vector3(eg.X,eg.Y,eg.Z);var toon=frame.Materials[i].UseSceneLighting?Vector3.Zero:D3D12OriginalStyleRenderer.ResolveTextureMorphToon(frame.Surface.Materials[i]);
            static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
            if(!Finite(endpoint)||Vector3.Min(endpoint,Vector3.Zero)!=Vector3.Zero
                ||Vector3.Min(toon,Vector3.Zero)!=Vector3.Zero||Vector3.Max(toon,Vector3.One)!=Vector3.One)
                throw new NotSupportedException($"ToonPalette requires finite nonnegative Eg and toon RGB in [0,1] (material {i}). Use Additive for this authored domain.");
        }
        // Keep the pre-existing zero-emission Additive input domain. Nonzero emission is a
        // new input and requires a finite transport/compose bound in either composition.
        double nativeIrradiance=NativeRayGiLights.IrradianceBound(frame);
        // GiNativeOutgoing evaluates colour * intensity, distance attenuation and the
        // nonnegative incident sum before applying DirectScale. A zero/tiny scale must
        // not hide overflow in those earlier operations (Inf * 0 would become NaN).
        // This bound omits the <= 1/pi cosine factor and includes every active light,
        // so it also bounds each colour-first product and the unscaled incident sum.
        if(nativeIrradiance>float.MaxValue)
            throw new NotSupportedException("Unscaled native GI light radiance would exceed the supported finite float domain.");
        if(!toonPalette&&maxEmission==0&&nativeIrradiance==0)return;
        // The shader normalizes its secondary light direction. Avoid overflowing that normalize;
        // the legacy Additive policy deliberately keeps its existing numeric input contract.
        var light=options.DirectionalLightDirection??Vector3.Normalize(frame.Surface.Snapshot.LightDirection);
        float lightLengthSquared=light.LengthSquared();
        if(toonPalette&&(!float.IsFinite(lightLengthSquared)||lightLengthSquared<1e-12f))
            throw new NotSupportedException("ToonPalette requires a light direction whose float squared length is finite and nonzero.");
        // Secondary albedo is saturated. Include authored emission before direct light and use
        // double throughout to protect outgoing radiance and accumulation before division.
        // For new nonzero emission also bound the final mean * Strength; do not multiply
        // the pre-division sum by Strength since the shader first divides by sample count.
        // Zero emission retains the previous conservative direct-light bound.
        double direct=(double)MathF.Max(options.LightIrradiance.X,MathF.Max(options.LightIrradiance.Y,options.LightIrradiance.Z))+nativeIrradiance;
        double worst=(maxEmission+direct*options.DirectScale)*4
            *((maxEmission>0||nativeIrradiance>0)?Math.Max(options.SamplesPerPixel,options.Strength):options.SamplesPerPixel);
        if(worst>float.MaxValue)throw new NotSupportedException("GI incident radiance or emissive composition would exceed the supported finite float domain.");
    }
    internal static (CpuDescriptorHandle Position,CpuDescriptorHandle Normal) GBufferHandles(CpuDescriptorHandle first,int stride)
    {var second=first;second.Offset(1,stride);return(first,second);}

    private static void ValidateTextureFactors(OriginalStyleScene style,SceneMaterial material,int index,bool toonPalette,bool nativeLights,bool useSceneLighting)
    {
        var binding=style.Materials[index];
        bool subTexture=binding.SphereMode==OriginalSphereMode.SubTexture;
        if(binding.TextureFactors.IsIdentity&&!subTexture)return; // Preserve the previously accepted ordinary identity domain.
        NotSupportedException Unsupported()=>new($"GI texture factors exceed the supported finite colour domain (material {index}).");
        float Checked(float value)=>float.IsFinite(value)?value:throw Unsupported();
        if(subTexture&&material.DiffuseTexture>=0)
        {
            float toonWeight=Checked(ScalarAdd(binding.TextureFactors.Toon.Multiply.W,binding.TextureFactors.Toon.Add.W));
            if(toonWeight is <0 or >1)
                throw new NotSupportedException($"SubTexture GI requires effective Toon W in [0,1] (material {index}).");
            // Raw material alpha and the colour-independent texture alpha are each <=1.
            // Check the exact coverage endpoints consumed before receiver classification.
            float rawAlpha=MathF.Min(material.Diffuse.W,1);
            _=Checked(ScalarMultiply(rawAlpha,toonWeight));
        }
        (Vector3 Min,Vector3 Max) FactorRange(OriginalTextureFactor factor,bool additive=false)
        {
            float weight=Checked(ScalarAdd(factor.Multiply.W,factor.Add.W));
            float fill=additive?0:Checked(ScalarAdd(1,-weight));
            Vector3 a=default,b=default;
            for(int channel=0;channel<3;channel++)
            {
                float At(float sample)
                {
                    float scaled=Checked(ScalarMultiply(sample,factor.Multiply[channel]));
                    float biased=Checked(ScalarAdd(scaled,factor.Add[channel]));
                    return Checked(ScalarAdd(Checked(ScalarMultiply(biased,weight)),fill));
                }
                a[channel]=At(0);b[channel]=At(1);
            }
            return(Vector3.Min(a,b),Vector3.Max(a,b));
        }
        (Vector3 Min,Vector3 Max) Product((Vector3 Min,Vector3 Max) a,(Vector3 Min,Vector3 Max) b)
        {
            Vector3 min=default,max=default;
            for(int channel=0;channel<3;channel++)
            {
                float p0=Checked(ScalarMultiply(a.Min[channel],b.Min[channel])),p1=Checked(ScalarMultiply(a.Min[channel],b.Max[channel]));
                float p2=Checked(ScalarMultiply(a.Max[channel],b.Min[channel])),p3=Checked(ScalarMultiply(a.Max[channel],b.Max[channel]));
                min[channel]=MathF.Min(MathF.Min(p0,p1),MathF.Min(p2,p3));
                max[channel]=MathF.Max(MathF.Max(p0,p1),MathF.Max(p2,p3));
            }
            return(min,max);
        }
        var diffuse=material.DiffuseTexture<0?(Vector3.One,Vector3.One):FactorRange(binding.TextureFactors.Diffuse);
        var eg4=OriginalStyleMath.ResolveHostColors(material,style.Profile,useSceneLighting).EgColor;
        var eg=new Vector3(eg4.X,eg4.Y,eg4.Z);
        var budget=Product((eg,eg),diffuse);
        // The secondary estimator remains saturated diffuse albedo, without sphere/toon RGB.
        var albedoFactor=new Vector3(material.Diffuse.X,material.Diffuse.Y,material.Diffuse.Z);
        var diffuseAlbedo=Product((albedoFactor,albedoFactor),diffuse);
        bool additiveSphere=binding.SphereMode==OriginalSphereMode.Add
            ||subTexture&&binding.SubTextureAdditive&&material.DiffuseTexture>=0;
        bool multiplySphere=binding.SphereMode==OriginalSphereMode.Multiply||subTexture&&!additiveSphere;
        if(multiplySphere)
        {
            var sphere=FactorRange(binding.TextureFactors.Sphere);
            budget=Product(budget,sphere);
            // NativeDirect starts from material diffuse rather than host Eg. Eg can be zero
            // while this separate product overflows; secondary GI does not use sphere RGB.
            if(nativeLights)_=Product(diffuseAlbedo,sphere);
        }
        var toon=useSceneLighting?Vector3.Zero:D3D12OriginalStyleRenderer.ResolveTextureMorphToon(binding);
        if(!float.IsFinite(toon.X)||!float.IsFinite(toon.Y)||!float.IsFinite(toon.Z))throw Unsupported();
        if(toonPalette&&(Vector3.Min(budget.Min,Vector3.Zero)!=Vector3.Zero
            ||Vector3.Min(toon,Vector3.Zero)!=Vector3.Zero||Vector3.Max(toon,Vector3.One)!=Vector3.One))
            throw new NotSupportedException($"ToonPalette requires a nonnegative effective diffuse-lit endpoint and effective toon RGB in [0,1] (material {index}). Use Additive for this authored domain.");
        var surface=budget;
        if(additiveSphere)
        {
            var sphere=FactorRange(binding.TextureFactors.Sphere,true);
            Vector3 min=default,max=default;
            for(int channel=0;channel<3;channel++)
            {
                min[channel]=Checked(ScalarAdd(surface.Min[channel],sphere.Min[channel]));
                max[channel]=Checked(ScalarAdd(surface.Max[channel],sphere.Max[channel]));
            }
            surface=(min,max);
        }
        // Across c in [0,1], the ordinary toon multiplier lies between toon and 1.
        _=Product(surface,(Vector3.Min(toon,Vector3.One),Vector3.Max(toon,Vector3.One)));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static float ScalarMultiply(float a,float b)=>a*b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float ScalarAdd(float a,float b)=>a+b;

    internal Prepared Record(D3D12RecordingContext context,OriginalFrameScene frame,D3D12GpuTarget target,D3D12GpuTarget surfaceDepth,
        RayGiOptions options,D3D12OriginalStyleRenderer original,ID3D12Resource vb,ID3D12Resource ib,
        ID3D12DescriptorHeap heap,int stride,int textureBase,int giSlot,bool capture=false,
        D3D12OriginalStyleRenderer.PreparedFrame? preparedFrame=null,int sampleIndex=0,int spatialSampleCount=1,
        D3D12RayGiWorkspace? workspace=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);Validate(frame,options);var timer=Stopwatch.StartNew();
        bool stageTiming=capture&&Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES")=="1";
        // Ordinary playback can inspect CPU recording without GPU queries, waits or readbacks.
        bool cpuStageTiming=Environment.GetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES")=="1";
        var cpuStages=stageTiming||cpuStageTiming?(cpuStageTicks??=new(StringComparer.Ordinal)):null;
        long cpuStageStart=cpuStages is null?0:Stopwatch.GetTimestamp();
        void EndCpuStage(string name)
        {
            if(cpuStages is null)return;
            long end=Stopwatch.GetTimestamp();
            cpuStages[name]=cpuStages.GetValueOrDefault(name)+(end-cpuStageStart);
            cpuStageStart=end;
        }

        int width=target.Width,height=target.Height;
        if(!options.Enabled)throw new InvalidOperationException("A GI recording requires an enabled mode.");
        if(target.Format!=Format.R8G8B8A8_UNorm && !(target.Format==Format.R16G16B16A16_Float && frame.Options?.AlphaOutput==FrameAlphaOutput.Straight))throw new NotSupportedException("Ray GI requires RGBA8 or an explicit straight-output FP16 scene target.");
        if(width is <1 or >4096||height is <1 or >4096)throw new ArgumentOutOfRangeException(nameof(target));
        var support=QueryCapabilities();bool hardware=options.Mode==LightingMode.HardwareRayTracing;
        if(hardware?!support.HardwareAvailable:!support.SoftwareAvailable)throw new NotSupportedException(support.UnavailableReason??"The requested GPU GI backend is unavailable.");
        EndCpuStage("OtherUploadAndPreparation");
        // Obtain current-frame leases on every call. No lookup may survive an aborted upload.
        bool dynamicGeometry=frame.Surface.Snapshot.HasDynamicVertices;
        bool useScratch=UsesTriangleScratch(frame.Surface.Snapshot,options,capture);
        RayGiScene scene=useScratch?RayGiScene.FromOriginalForGpu(frame):RayGiScene.FromOriginal(frame);
        EndCpuStage("FromOriginal");
        RayGiBvh? bvh=hardware?null:scene.Bvh;
        EndCpuStage("OtherUploadAndPreparation");
        ID3D12Resource Upload<T>(string name,ReadOnlySpan<T> bytes,object identity) where T:unmanaged
            =>cache.BufferByIdentity("gi:"+name,MemoryMarshal.AsBytes(bytes),ResourceStates.NonPixelShaderResource,identity);
        RayGiTriangleGpu[]? scratchRows=null;
        var triangles=useScratch
            ?cache.DeferredBufferByIdentity("gi:triangles",ResourceStates.NonPixelShaderResource,scene.GeometryIdentity,() =>
            {
                // The cache invokes this synchronously only when it needs new triangle bytes.
                // Keep its actual upload separate from scratch allocation and row assembly.
                EndCpuStage("TriangleUpload");
                scratchRows=triangleScratch.GetBuffer(scene.TriangleCount);
                ReadOnlySpan<RayGiTriangleGpu> rows;
                if(scratchRows is null)rows=scene.Triangles;
                else{scene.WriteTriangleRows(scratchRows);rows=scratchRows;}
                EndCpuStage("TriangleAssembly");
                return MemoryMarshal.AsBytes(rows);
            })
            :Upload("triangles",scene.Triangles,scene.GeometryIdentity);
        EndCpuStage("TriangleUpload");
        var materials=Upload("materials",scene.Materials,scene.MaterialIdentity);
        bool textureMorph=!scene.DiffuseTextureFactors.IsEmpty;
        var textureFactors=textureMorph?Upload("diffuse-texture-factors",scene.DiffuseTextureFactors,scene.MaterialIdentity):null;
        bool subTexture=!scene.SubTextureUvs.IsEmpty;
        var subTextureUvs=subTexture?Upload("subtexture-uv1",scene.SubTextureUvs,scene.SubTextureUvIdentity):null;
        var texels=Upload("texels",scene.Texels,scene.TexelIdentity);
        var nativeLights=NativeRayGiLights.From(frame);bool hasNativeLights=nativeLights.HasContribution;
        var nativeLightBuffer=hasNativeLights?Upload<NativeRayGiLight>("native-lights",nativeLights.UploadRows,nativeLights):null;
        EndCpuStage("MaterialAndTextureUpload");
        // DXR does not read the software BVH. Keep its existing root slots bound without
        // forcing the lazy CPU build or replacing these fixed buffers for every pose.
        ReadOnlySpan<RayGiBvhNodeGpu> nodes=hardware?[]:bvh!.Nodes;
        var gpuNodes=hardware?Upload<RayGiBvhNodeGpu>("nodes",HardwareBvhNodes,HardwareBvhIdentity)
            :Upload("nodes",nodes.Length==0?[new RayGiBvhNodeGpu(new(float.PositiveInfinity),new(float.NegativeInfinity),new(0,0,1,1))]:nodes,scene.GeometryIdentity);
        var order=hardware?Upload<uint>("order",HardwareTriangleOrder,HardwareBvhIdentity)
            :Upload("order",bvh!.TriangleOrder.Length==0?[0u]:bvh.TriangleOrder,scene.GeometryIdentity);
        workspace??=new(context,width,height);
        receiverWorkspaces.Add(workspace);
        workspace.BeginSample(context,width,height);
        var position=workspace.Position.Resource;var normal=workspace.Normal.Resource;
        var rtv=workspace.PositionRtv;var normalRtv=workspace.NormalRtv;var dsv=workspace.Dsv;
        runtime.Device.CreateShaderResourceView(position,null,heap.GetCPUDescriptorHandleForHeapStart().Offset(giSlot,stride));
        runtime.Device.CreateShaderResourceView(normal,null,heap.GetCPUDescriptorHandleForHeapStart().Offset(giSlot+1,stride));
        var inputs=heap.GetGPUDescriptorHandleForHeapStart().Offset(giSlot,stride);
        string? timingDirectory=capture?Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING"):null;
        D3D12CaptureTiming? gpuTiming=string.IsNullOrWhiteSpace(timingDirectory)?null:new(context,"GI GBuffer + AS build + compute");
        gpuTiming?.Begin();
        D3D12CaptureTiming? BeginStage(string name)
        {
            if(gpuTiming is null||!stageTiming)return null;
            var timing=new D3D12CaptureTiming(context,"GI stage/"+name);timing.Begin();return timing;
        }
        void EndStage(D3D12CaptureTiming? timing,string? actualLabel=null)
        {
            if(timing is null)return;
            timing.EndAndResolve();timings.Add(new(timingDirectory!,timing,actualLabel,true));
        }
        EndCpuStage("OtherUploadAndPreparation");
        var gbufferTiming=BeginStage("GBuffer");
        int gbufferDraws=original.RecordGiGBuffer(context,frame,width,height,vb,ib,heap,stride,textureBase,rtv,normalRtv,dsv,surfaceDepth.Format,preparedFrame);
        var commands=context.Commands;var shaderRead=ResourceStates.NonPixelShaderResource|ResourceStates.PixelShaderResource;
        workspace.Position.Transition(context,shaderRead);workspace.Normal.Transition(context,shaderRead);
        EndStage(gbufferTiming);
        EndCpuStage("GBuffer");
        workspace.ResetDiagnostics(context);
        var indirect=workspace.RawIndirect.Resource;var diagnostics=workspace.Diagnostics.Resource;
        var light=options.DirectionalLightDirection??Vector3.Normalize(frame.Surface.Snapshot.LightDirection);
        bool temporal=IsTemporalEnabled(options);
        uint actualSeed=options.Seed;
        if(temporal)
        {
            EndCpuStage("OtherUploadAndPreparation");
            actualSeed=(temporalDenoiser??=new(runtime)).Prepare(frame,scene,options,width,height,sampleIndex,spatialSampleCount);
            EndCpuStage("TemporalPrepare");
        }
        var constants=new RayGiConstants(new((uint)width,(uint)height,(uint)options.SamplesPerPixel,actualSeed),new((uint)scene.TriangleCount,(uint)scene.Materials.Length,(uint)nodes.Length,hardware?1u:0u),
            new(light,options.MaxRayDistance),new(options.DirectionalIrradiance??new Vector3(3),options.Strength),new(options.RayEpsilon,options.DirectScale,options.EffectiveOcclusionRadius,0),new(nativeLights.Values.Length,nativeLights.HasReceiverMasks?1:0,nativeLights.EnvironmentStrength,0),scene.SphereRotation);
        // Millions of rays must not contend on shared diagnostic atomics in ordinary playback.
        // Only the existing explicit debug capture compiles them; overflow protection stays active.
        bool diagnosticCapture=capture&&Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS") is not null;
        var pipeline=GetComputePipeline(hardware,options.Composition,textureMorph,hasNativeLights,subTexture,diagnosticCapture);string identity="GPU-BVH";
        EndCpuStage("OtherUploadAndPreparation");
        D3D12HardwareRayGi.AccelerationStructure? acceleration=null;
        if(hardware)
        {
            var asTiming=BeginStage("AS");
            acceleration=D3D12HardwareRayGi.GetAccelerationStructure(context,cache,scene,dynamicGeometry,
                triangleBuffer:triangles,compiler:compiler,scratchRows:scratchRows);identity=acceleration.BackendIdentity;
            EndStage(asTiming,"GI stage/AS "+(acceleration.OperationFrameId!=runtime.FrameId||countedAcceleration.Contains(acceleration)?"reuse":acceleration.WasUpdate?"update":"build"));
            EndCpuStage("DxrAccelerationStructure");
        }
        commands.SetDescriptorHeaps(heap);commands.SetComputeRootSignature(pipeline.Root);commands.SetPipelineState(pipeline.State);commands.SetComputeRootConstantBufferView(0,context.UploadConstants(constants));
        commands.SetComputeRootShaderResourceView(1,triangles.GPUVirtualAddress);commands.SetComputeRootShaderResourceView(2,materials.GPUVirtualAddress);commands.SetComputeRootShaderResourceView(3,texels.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(4,gpuNodes.GPUVirtualAddress);commands.SetComputeRootShaderResourceView(5,order.GPUVirtualAddress);commands.SetComputeRootDescriptorTable(6,inputs);
        if(hardware)commands.SetComputeRootShaderResourceView(7,acceleration!.TopLevel.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(hardware?8:7,indirect.GPUVirtualAddress);commands.SetComputeRootUnorderedAccessView(hardware?9:8,diagnostics.GPUVirtualAddress);
        if(pipeline.TextureFactorRoot>=0)commands.SetComputeRootShaderResourceView(pipeline.TextureFactorRoot,textureFactors!.GPUVirtualAddress);
        if(pipeline.NativeLightRoot>=0)commands.SetComputeRootShaderResourceView(pipeline.NativeLightRoot,nativeLightBuffer!.GPUVirtualAddress);
        if(pipeline.SubTextureRoot>=0)commands.SetComputeRootShaderResourceView(pipeline.SubTextureRoot,subTextureUvs!.GPUVirtualAddress);
        var integrateTiming=BeginStage("Integrate");
        commands.Dispatch((width+7)/8,(height+7)/8,1);commands.ResourceBarrierUnorderedAccessView(indirect);
        EndStage(integrateTiming);
        bool filter=D3D12RayGiDenoiser.IsEnabled(options);var rawIndirect=indirect;
        if(filter)
        {
            workspace.RawIndirect.Transition(context,ResourceStates.NonPixelShaderResource);
            if(temporal)
            {
                var temporalTiming=BeginStage("Temporal");
                indirect=temporalDenoiser!.Record(context,indirect,triangles,inputs,frame,scene,options,width,height,compiler!);
                EndStage(temporalTiming);
            }
            denoiser??=new(runtime);
            var spatialTiming=BeginStage("Spatial");
            indirect=denoiser.Record(context,indirect,triangles,inputs,width,height,scene.TriangleCount,scene.Materials.Length,
                frame.Options?.CameraOverride??frame.Surface.Snapshot.Camera,compiler!,workspace.SpatialOutput(context));
            EndStage(spatialTiming);
        }
        else workspace.RawIndirect.Transition(context,ResourceStates.PixelShaderResource);
        if(gpuTiming is not null){gpuTiming.EndAndResolve();timings.Add(new(timingDirectory!,gpuTiming,
            hardware&&frame.Surface.Snapshot.HasDynamicVertices
                ? "GI GBuffer + AS "+(acceleration!.OperationFrameId!=runtime.FrameId||countedAcceleration.Contains(acceleration)?"reuse":acceleration.WasUpdate?"update":"build")+" + compute" : null));}
        EndCpuStage("IntegrationAndFilters");
        if(diagnosticCapture)
        {
            void Buffer(string name,ID3D12Resource source,ResourceStates state,int size)
            {
                var cpu=context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer((ulong)size),ResourceStates.CopyDest));
                commands.ResourceBarrierTransition(source,state,ResourceStates.CopySource);commands.CopyBufferRegion(cpu,0,source,0,(ulong)size);commands.ResourceBarrierTransition(source,ResourceStates.CopySource,state);
                readbacks.Add(new(name,cpu,size,width,height,0));
            }
            void Image(string name,ID3D12Resource source)
            {
                var footprints=new PlacedSubresourceFootPrint[1];runtime.Device.GetCopyableFootprints(source.Description,0,1,0,footprints,new int[1],new ulong[1],out ulong size);
                var cpu=context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(size),ResourceStates.CopyDest));
                commands.ResourceBarrierTransition(source,shaderRead,ResourceStates.CopySource);commands.CopyTextureRegion(new TextureCopyLocation(cpu,footprints[0]),0,0,0,new TextureCopyLocation(source,0));commands.ResourceBarrierTransition(source,ResourceStates.CopySource,shaderRead);
                readbacks.Add(new(name,cpu,(int)size,width,height,footprints[0].Footprint.RowPitch));
            }
            string suffix=readbacks.Count.ToString();Image("position-"+suffix,position);Image("normal-id-"+suffix,normal);
            if(filter)Buffer("raw-indirect-"+suffix,rawIndirect,ResourceStates.NonPixelShaderResource,checked(width*height*16));
            if(temporal)
            {
                Buffer("temporal-indirect-"+suffix,temporalDenoiser!.RecordedSignal!,ResourceStates.NonPixelShaderResource,checked(width*height*16));
                Buffer("temporal-moments-"+suffix,temporalDenoiser.RecordedMoments!,ResourceStates.NonPixelShaderResource,checked(width*height*16));
            }
            Buffer("indirect-"+suffix,indirect,ResourceStates.PixelShaderResource,checked(width*height*16));
            commands.ResourceBarrierUnorderedAccessView(diagnostics);Buffer("counters-"+suffix,diagnostics,ResourceStates.UnorderedAccess,16);
        }
        LastBackendIdentity=identity;var previous=LastStatistics;
        long builds=previous?.AccelerationBuilds??0,updates=previous?.AccelerationUpdates??0;
        if(acceleration?.OperationFrameId==runtime.FrameId&&countedAcceleration.Add(acceleration))
        {if(acceleration.WasUpdate)updates++;else builds++;}
        LastStatistics=new(options.Mode,options.Mode,(previous?.DispatchCount??0)+1,(previous?.GBufferDrawCalls??0)+gbufferDraws,runtime.BufferUploads,builds,options.SamplesPerPixel,actualSeed,options.Strength,width,height,cache.LiveResourceCount)
            {AccelerationUpdates=updates,Composition=options.Composition,ToonResponse=options.ToonResponse,
             PaletteBandReceiverMaterials=options.Composition==RayGiComposition.ToonPalette?frame.Materials.Where((flags,index)=>flags.SurfaceEnabled&&frame.Surface.Snapshot.Materials[index].IndexCount>0)
                .Count(flags=>(flags.GiToonResponseOverride??options.ToonResponse)==RayGiToonResponse.PreservePaletteBands):0,
             SecondaryNativeLights=nativeLights.Values.Length,Denoiser=filter?options.Denoiser:previous?.Denoiser??RayGiDenoiser.Off,
             TemporalResetHistory=temporal&&((previous?.TemporalResetHistory??false)||temporalDenoiser!.LastReset),
             TemporalResetReason=temporal?(previous?.TemporalResetHistory==true?previous.TemporalResetReason:temporalDenoiser!.LastResetReason):"None",
             TemporalHistoryFrames=temporal?temporalDenoiser!.LastHistoryFrames:0,
             TemporalHistoryBytes=temporal?checked((previous?.TemporalHistoryBytes??0)+(long)width*height*D3D12TemporalGiMemoryBudget.HistoryBytesPerPixel):0,
             DenoiseDispatchCount=(previous?.DenoiseDispatchCount??0)+(filter?temporal?2:1:0),RecordCpuMilliseconds=(previous?.RecordCpuMilliseconds??0)+timer.Elapsed.TotalMilliseconds,
             ReceiverWorkspaceCount=receiverWorkspaces.Count,ReceiverWorkspaceBytes=receiverWorkspaces.Sum(value=>checked((long)value.AllocationBytes))};
        if(cpuStageTiming)PublishCpuStages();
        return new(indirect,inputs,options.Strength,options.OcclusionStrength,options.Composition,options.ToonResponse);
    }
    private void PublishCpuStages()
    {
        if(LastStatistics is not null&&cpuStageTicks is not null)
            LastStatistics=LastStatistics with {CpuStages=Array.AsReadOnly(cpuStageTicks.Select(stage=>
                new D3D12RayGiCpuStageStatistics(stage.Key,stage.Value*1000.0/Stopwatch.Frequency)).ToArray())};
    }
    internal void CompleteDiagnosticCapture()
    {
        // Capture's existing wait completed the shared frame fence. The helper verifies it again.
        PublishCpuStages();
        if(timings.Count>0)
        {
            var results=timings.Select(item=>(item.Directory,item.IsStage,Result:item.ActualLabel is null?item.Timing.ReadResult():item.Timing.ReadResult() with {Label=item.ActualLabel})).ToArray();
            if(LastStatistics is not null)LastStatistics=LastStatistics with {
                GpuMilliseconds=results.Where(item=>!item.IsStage).Sum(item=>item.Result.GpuMilliseconds),
                GpuStages=Array.AsReadOnly(results.Where(item=>item.IsStage).Select(item=>new D3D12RayGiGpuStageStatistics(item.Result.Label,item.Result.GpuMilliseconds)).ToArray())};
            foreach(var group in results.GroupBy(item=>item.Directory,StringComparer.Ordinal))
            {
                string timingPath=Path.Combine(group.Key,runtime.DeviceIdentity,"frame-"+runtime.FrameId);
                Directory.CreateDirectory(timingPath);
                File.WriteAllText(Path.Combine(timingPath,"timings.json"),System.Text.Json.JsonSerializer.Serialize(group.Select(item=>item.Result),new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            }
        }
        if(readbacks.Count==0)return;
        string directory=Path.Combine(Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS")!,runtime.DeviceIdentity,"frame-"+runtime.FrameId);
        Directory.CreateDirectory(directory);
        uint secondary=0,overflow=0;
        foreach(var item in readbacks)
        {
            var mapped=item.Resource.Map<byte>(0,item.Bytes);byte[] bytes;
            try{
                if(item.RowPitch==0)bytes=mapped.ToArray();
                else{bytes=new byte[item.Width*item.Height*16];for(int y=0;y<item.Height;y++)mapped.Slice(y*item.RowPitch,item.Width*16).CopyTo(bytes.AsSpan(y*item.Width*16,item.Width*16));}
            }finally{item.Resource.Unmap(0);}
            File.WriteAllBytes(Path.Combine(directory,item.Name+".bin"),bytes);
            if(item.Name.StartsWith("counters",StringComparison.Ordinal)){overflow+=BitConverter.ToUInt32(bytes,0);secondary+=BitConverter.ToUInt32(bytes,4);}
        }
        if(LastStatistics is not null)LastStatistics=LastStatistics with {GpuCountersCaptured=true,TraversalOverflows=overflow,SecondaryHits=secondary};
        File.WriteAllText(Path.Combine(directory,"statistics.json"),System.Text.Json.JsonSerializer.Serialize(LastStatistics,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }
    private Pipeline GetComputePipeline(bool hardware,RayGiComposition composition,bool textureMorph=false,bool nativeLights=false,bool subTexture=false,bool diagnostics=false)
    {
        var key=(hardware,composition,textureMorph,nativeLights,subTexture,diagnostics);
        if(computePipelines.TryGetValue(key,out var found))return found;compiler??=runtime.GetOrCreateDxcCompiler();
        var defines=new Dictionary<string,string?>();if(hardware)defines.Add("GI_HARDWARE","1");
        if(composition==RayGiComposition.ToonPalette)defines.Add("GI_TOON","1");
        if(diagnostics)defines.Add("GI_DIAGNOSTICS","1");
        var shader=compiler.CompileCompute(D3D12RayGiShaders.Source(hardware,textureMorph,nativeLights,subTexture),"CSMain",hardware?"cs_6_5":"cs_6_0",defines);CompilerDiagnostics=shader.Diagnostics;
        var parameters=new List<RootParameter>{new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All)};
        for(int i=0;i<5;i++)parameters.Add(new(RootParameterType.ShaderResourceView,new RootDescriptor(i,0),ShaderVisibility.All));
        parameters.Add(new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,2,5)),ShaderVisibility.All));
        if(hardware)parameters.Add(new(RootParameterType.ShaderResourceView,new RootDescriptor(7,0),ShaderVisibility.All));
        parameters.Add(new(RootParameterType.UnorderedAccessView,new RootDescriptor(0,0),ShaderVisibility.All));parameters.Add(new(RootParameterType.UnorderedAccessView,new RootDescriptor(1,0),ShaderVisibility.All));
        int textureFactorRoot=textureMorph?parameters.Count:-1;
        if(textureMorph)parameters.Add(new(RootParameterType.ShaderResourceView,new RootDescriptor(8,0),ShaderVisibility.All));
        int nativeLightRoot=nativeLights?parameters.Count:-1;
        if(nativeLights)parameters.Add(new(RootParameterType.ShaderResourceView,new RootDescriptor(9,0),ShaderVisibility.All));
        int subTextureRoot=subTexture?parameters.Count:-1;
        if(subTexture)parameters.Add(new(RootParameterType.ShaderResourceView,new RootDescriptor(10,0),ShaderVisibility.All));
        var root=runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.None,parameters.ToArray()),RootSignatureVersion.Version10);
        try{runtime.ShaderCompilations++;runtime.PipelineCreations++;var state=runtime.CreateComputePipelineState(new ComputePipelineStateDescription{RootSignature=root,ComputeShader=shader.Dxil});var result=new Pipeline(root,state,textureFactorRoot,nativeLightRoot,subTextureRoot);computePipelines.Add(key,result);return result;}catch{root.Dispose();throw;}
    }
    public void Dispose(){if(disposed)return;disposed=true;triangleScratch.Clear();temporalDenoiser?.Dispose();denoiser?.Dispose();foreach(var p in computePipelines.Values){p.State.Dispose();p.Root.Dispose();}computePipelines.Clear();}
}
