using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>A single geometry-guided pass over incident RGB. Every resource belongs to the current frame fence.</summary>
internal sealed class D3D12RayGiDenoiser(D3D12GpuRuntime runtime) : IDisposable
{
    private ID3D12RootSignature? root;
    private ID3D12PipelineState? pipeline;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Constants(RayGiUInt4 dimensions,Vector4 viewPixel)
    {
        public readonly RayGiUInt4 Dimensions=dimensions;
        public readonly Vector4 ViewPixel=viewPixel;
    }

    internal static bool IsEnabled(RayGiOptions options)
        =>options.Enabled&&options.Strength>0&&options.Denoiser is RayGiDenoiser.Spatial or RayGiDenoiser.TemporalSpatial;

    // Double intermediates keep finite camera coordinates from overflowing the direction subtraction/length.
    internal static Vector4 CameraGuide(SceneCamera camera,int height)
    {
        double x=(double)camera.Target.X-camera.Eye.X,y=(double)camera.Target.Y-camera.Eye.Y,z=(double)camera.Target.Z-camera.Eye.Z;
        double length=Math.Sqrt(x*x+y*y+z*z),pixel=(double)camera.OrthographicHeight/height;
        if(height<=0||!double.IsFinite(length)||length<=0||!double.IsFinite(pixel)||pixel<=0||pixel>float.MaxValue)return default;
        var guide=new Vector4((float)(x/length),(float)(y/length),(float)(z/length),(float)pixel);
        return guide.W>0?guide:default;
    }

    /// <summary>Input and triangle table must be NonPixelShaderResource. Guides are contiguous position/normal SRVs
    /// in the already-bound heap. The returned separate buffer is ready for pixel composition.</summary>
    internal ID3D12Resource Record(D3D12RecordingContext context,ID3D12Resource input,ID3D12Resource triangles,
        Vortice.Direct3D12.GpuDescriptorHandle guides,int width,int height,int triangleCount,int materialCount,
        SceneCamera camera,DxcShaderCompiler compiler,D3D12GiWorkspaceResource? reusableOutput=null)
    {
        EnsurePipeline(compiler);
        var trackedOutput=reusableOutput??new D3D12GiWorkspaceResource(context.Own(runtime.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer(checked((ulong)width*(ulong)height*16),ResourceFlags.AllowUnorderedAccess),ResourceStates.UnorderedAccess)),ResourceStates.UnorderedAccess);
        trackedOutput.Transition(context,ResourceStates.UnorderedAccess);
        var output=trackedOutput.Resource;
        var commands=context.Commands;
        commands.SetComputeRootSignature(root!);commands.SetPipelineState(pipeline!);
        commands.SetComputeRootConstantBufferView(0,context.UploadConstants(new Constants(new((uint)width,(uint)height,(uint)triangleCount,(uint)materialCount),CameraGuide(camera,height))));
        commands.SetComputeRootShaderResourceView(1,triangles.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(2,input.GPUVirtualAddress);
        commands.SetComputeRootDescriptorTable(3,guides);
        commands.SetComputeRootUnorderedAccessView(4,output.GPUVirtualAddress);
        commands.Dispatch((width+7)/8,(height+7)/8,1);
        commands.ResourceBarrierUnorderedAccessView(output);
        trackedOutput.Transition(context,ResourceStates.PixelShaderResource);
        return output;
    }

    private void EnsurePipeline(DxcShaderCompiler compiler)
    {
        if(pipeline is not null)return;
        var shader=compiler.CompileCompute(Source,"CSMain","cs_6_0");
        RootParameter[] parameters=[
            new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView,new RootDescriptor(0,0),ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView,new RootDescriptor(1,0),ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,2,2)),ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView,new RootDescriptor(0,0),ShaderVisibility.All)];
        var signature=runtime.Device.CreateRootSignature<ID3D12RootSignature>(new RootSignatureDescription(RootSignatureFlags.None,parameters),RootSignatureVersion.Version10);
        try
        {
            var state=runtime.Device.CreateComputePipelineState(new ComputePipelineStateDescription{RootSignature=signature,ComputeShader=shader.Dxil});
            root=signature;pipeline=state;runtime.ShaderCompilations++;runtime.PipelineCreations++;
        }
        catch{signature.Dispose();throw;}
    }

    public void Dispose(){pipeline?.Dispose();root?.Dispose();pipeline=null;root=null;}

    // Authored for the current geometry/incident signal contract. No upstream shader is copied.
    internal const string Source="""
        struct Triangle { float4 P0; float4 P1; float4 P2; float4 N0U0; float4 N1U1; float4 N2U2; float4 V012; float4 SphereUv01; float4 SphereUv2; uint4 Meta; };
        cbuffer FilterConstants:register(b0) { uint4 Dimensions; float4 ViewPixel; };
        StructuredBuffer<Triangle> Triangles:register(t0);
        StructuredBuffer<float4> Input:register(t1);
        Texture2D<float4> Position:register(t2);
        Texture2D<float4> Normal:register(t3);
        RWStructuredBuffer<float4> Output:register(u0);

        bool UnitVector(float3 value,out float3 unit) {
            unit=0;
            if(!all(isfinite(value))) return false;
            float scale=max(abs(value.x),max(abs(value.y),abs(value.z)));
            if(!(scale>0)) return false;
            value/=scale;
            float magnitude=length(value);
            if(!isfinite(magnitude)||!(magnitude>0)) return false;
            unit=value/magnitude; return all(isfinite(unit));
        }
        bool Guide(int2 pixel,out float3 position,out float3 normal,out float3 face,out uint id,out Triangle tri) {
            position=0;normal=0;face=0;id=0;tri=(Triangle)0;
            float4 p=Position.Load(int3(pixel,0)),n=Normal.Load(int3(pixel,0));
            if(!all(isfinite(p))||!all(isfinite(n))||p.w<0||p.w>1||n.w<1||n.w>Dimensions.z||floor(n.w)!=n.w) return false;
            id=(uint)n.w-1;tri=Triangles[id];
            if(tri.Meta.x>=Dimensions.w||!all(isfinite(tri.P0.xyz))||!all(isfinite(tri.P1.xyz))||!all(isfinite(tri.P2.xyz))) return false;
            float3 a=tri.P1.xyz-tri.P0.xyz,b=tri.P2.xyz-tri.P0.xyz;
            if(!all(isfinite(a))||!all(isfinite(b))) return false;
            float scale=max(max(abs(a.x),max(abs(a.y),abs(a.z))),max(abs(b.x),max(abs(b.y),abs(b.z))));
            if(!(scale>0)||!UnitVector(cross(a/scale,b/scale),face)||!UnitVector(n.xyz,normal)) return false;
            position=p.xyz; return true;
        }
        bool Connected(Triangle a,Triangle b) {
            return all(a.P0.xyz==b.P0.xyz)||all(a.P0.xyz==b.P1.xyz)||all(a.P0.xyz==b.P2.xyz)
                ||all(a.P1.xyz==b.P0.xyz)||all(a.P1.xyz==b.P1.xyz)||all(a.P1.xyz==b.P2.xyz)
                ||all(a.P2.xyz==b.P0.xyz)||all(a.P2.xyz==b.P1.xyz)||all(a.P2.xyz==b.P2.xyz);
        }
        float Kernel(int offset) { return offset==0?1:(abs(offset)==1?4.0/6.0:1.0/6.0); }
        float BlendFinite(float a,float b,float t) {
            // Same-sign subtraction and opposite-sign weighted addition cannot overflow.
            precise float result=(a>=0)==(b>=0)?a+(b-a)*t:a*(1-t)+b*t;
            return clamp(result,min(a,b),max(a,b));
        }
        [numthreads(8,8,1)]
        void CSMain(uint3 thread:SV_DispatchThreadID) {
            if(any(thread.xy>=Dimensions.xy)) return;
            uint index=thread.y*Dimensions.x+thread.x;
            float4 center=Input[index]; Output[index]=center;
            if(!all(isfinite(center))||!all(isfinite(ViewPixel))||!(ViewPixel.w>0)) return;
            float3 p0,n0,g0;uint id0;Triangle t0;
            if(!Guide(int2(thread.xy),p0,n0,g0,id0,t0)) return;
            float angle=abs(dot(g0,ViewPixel.xyz));
            if(!isfinite(angle)||angle<.2) return;
            float footprint=ViewPixel.w/angle;
            if(!isfinite(footprint)||!(footprint>0)) return;
            float3 mean=center.rgb;float total=1;
            [loop] for(int y=-2;y<=2;y++) [loop] for(int x=-2;x<=2;x++) {
                if(x==0&&y==0) continue;
                int2 pixel=int2(thread.xy)+int2(x,y);
                if(any(pixel<0)||any(pixel>=int2(Dimensions.xy))) continue;
                float3 pi,ni,gi;uint idi;Triangle ti;
                if(!Guide(pixel,pi,ni,gi,idi,ti)||ti.Meta.x!=t0.Meta.x||(idi!=id0&&!Connected(t0,ti))) continue;
                float alignment=saturate(dot(n0,ni));
                if(alignment<.9) continue;
                float3 offset=(pi-p0)/footprint;
                if(!all(isfinite(offset))||length(offset)>4) continue;
                float residual=max(abs(dot(offset,g0)),abs(dot(offset,gi)));
                if(!isfinite(residual)||residual>.15) continue;
                float4 sample=Input[pixel.y*Dimensions.x+pixel.x];
                if(!all(isfinite(sample))) continue;
                float weight=Kernel(x)*Kernel(y)*pow(alignment,32)*exp2(-pow(residual/.05,2));
                if(!isfinite(weight)||!(weight>0)) continue;
                float next=total+weight,t=weight/next;
                mean=float3(BlendFinite(mean.x,sample.x,t),BlendFinite(mean.y,sample.y,t),BlendFinite(mean.z,sample.z,t));
                total=next;
            }
            Output[index]=float4(mean,center.a);
        }
        """;
}
