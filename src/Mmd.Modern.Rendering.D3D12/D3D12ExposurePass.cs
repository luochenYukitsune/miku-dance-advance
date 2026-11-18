using System.Numerics;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Uniform exposure of the final display image, in linear sRGB, before editor helpers.
/// Existing SDR clipping remains; this does not reconstruct highlights already clipped by shading.</summary>
internal sealed class D3D12ExposurePass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Vertex, byte[] Pixel)? shaders;
    internal static ulong Measure(D3D12GpuRuntime runtime,int width,int height,bool active)
    {
        if(!active) return 0;
        ulong image=runtime.Device.GetResourceAllocationInfo(0,[ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm,
            (uint)width,(uint)height,mipLevels:1,flags:ResourceFlags.AllowRenderTarget)]).SizeInBytes;
        ulong constants=runtime.Device.GetResourceAllocationInfo(0,[ResourceDescription.Buffer(1024*1024)]).SizeInBytes;
        if(image is 0 or ulong.MaxValue || constants is 0 or ulong.MaxValue)
            throw new NotSupportedException("Device cannot estimate exposure resources.");
        return checked((image+constants)*D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
    }

    internal D3D12GpuTarget Record(D3D12RecordingContext context,D3D12GpuTarget input,float ev)
    {
        if(!ReferenceEquals(context.Runtime,runtime)||!runtime.IsRecording||!input.Initialized||input.Depth||input.Format!=Format.R8G8B8A8_UNorm)
            throw new InvalidOperationException("Exposure requires this runtime's recording frame and initialized display RGBA8.");
        if(!float.IsFinite(ev)||ev is < -8 or > 8) throw new ArgumentOutOfRangeException(nameof(ev));
        if(ev==0) return input;
        var device=context.Device;var commands=context.Commands;var compiled=GetShaders();
        var root=cache.Get("lighting-exposure:root",()=>device.CreateRootSignature<ID3D12RootSignature>(
            new RootSignatureDescription(RootSignatureFlags.None,[
                new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,0)),ShaderVisibility.Pixel),
                new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.Pixel)]),RootSignatureVersion.Version10));
        // The opaque full-target draw overwrites every texel of the pooled image.
        var resource=runtime.Transients.Resource(context,HeapType.Default,
            ResourceDescription.Texture2D(input.Format,(uint)input.Width,(uint)input.Height,mipLevels:1,flags:ResourceFlags.AllowRenderTarget),ResourceStates.RenderTarget);
        var rtv=runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView,1));
        var srv=runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,1,DescriptorHeapFlags.ShaderVisible));
        device.CreateRenderTargetView(resource,null,rtv.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(input.Resource,null,srv.GetCPUDescriptorHandleForHeapStart());
        runtime.DescriptorHighWater=Math.Max(runtime.DescriptorHighWater,1);
        var output=runtime.Transients.Track(new D3D12GpuTarget("$lighting-exposure",resource,rtv.GetCPUDescriptorHandleForHeapStart(),input.Width,input.Height,input.Format,false));
        var pipeline=cache.Get("lighting-exposure:pso",()=>
        {
            var pso=device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
                RootSignature=root,VertexShader=compiled.Vertex,PixelShader=compiled.Pixel,BlendState=BlendDescription.Opaque,
                SampleMask=uint.MaxValue,RasterizerState=RasterizerDescription.CullNone,DepthStencilState=DepthStencilDescription.None,
                PrimitiveTopologyType=PrimitiveTopologyType.Triangle,RenderTargetFormats=[input.Format],SampleDescription=new(1,0) });
            runtime.PipelineCreations++;return pso;
        });
        commands.UnsetRenderTargets();context.Transition(input,ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0,0,input.Width,input.Height,0,1);commands.RSSetScissorRect(new RawRect(0,0,input.Width,input.Height));
        commands.SetGraphicsRootSignature(root);commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootDescriptorTable(0,srv.GetGPUDescriptorHandleForHeapStart());
        commands.SetGraphicsRootConstantBufferView(1,context.UploadConstants(new Vector4(MathF.Pow(2,ev),0,0,0)));
        commands.OMSetRenderTargets(output.Handle);commands.DrawInstanced(3,1,0,0);commands.UnsetRenderTargets();
        output.Initialized=true;return output;
    }

    private (byte[] Vertex,byte[] Pixel) GetShaders()
    {
        if(shaders is { } value) return value;
        const ShaderFlags flags=ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness;
        value=(Compiler.Compile(Source,"VSMain","lighting-exposure.hlsl","vs_5_1",flags).ToArray(),
            Compiler.Compile(Source,"PSMain","lighting-exposure.hlsl","ps_5_1",flags).ToArray());
        runtime.ShaderCompilations+=2;shaders=value;return value;
    }
    internal const string Source="""
        Texture2D<float4> InputTexture:register(t0);
        cbuffer ExposureConstants:register(b0) { float4 Parameters; };
        float4 VSMain(uint id:SV_VertexID):SV_Position {
            float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),0,1);
        }
        float Decode(float v) { return v<=.04045?v/12.92:pow((v+.055)/1.055,2.4); }
        float Encode(float v) { return v<=.0031308?v*12.92:1.055*pow(max(v,0),1/2.4)-.055; }
        float4 PSMain(float4 p:SV_Position):SV_Target {
            float4 c=InputTexture.Load(int3(int2(p.xy),0));
            return float4(Encode(Decode(c.r)*Parameters.x),Encode(Decode(c.g)*Parameters.x),Encode(Decode(c.b)*Parameters.x),c.a);
        }
        """;
}
