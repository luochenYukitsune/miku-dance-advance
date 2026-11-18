using Mmd.Modern.Core;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Final GPU conversion from filtered premultiplied RGBA16F to straight RGBA8.
/// All targets and descriptors retire at the recording fence; no CPU pixel access occurs.</summary>
internal sealed class D3D12AlphaResolvePass(D3D12GpuRuntime runtime,D3D12GpuCache cache)
{
    private (byte[] Vertex,byte[] Pixel)? shaders;

    internal static void ValidateCoverage(OriginalFrameScene frame)
    {
        if(frame.Surface.Snapshot.Materials.Any(m=>m.Diffuse.W<0)
            ||frame.Outline?.Materials.Select((m,i)=>(Edge:CharacterOutlineMath.ResolveMaterial(m,frame.Materials[i].CharacterOutlineStyle),
                Scale:frame.Materials[i].OutlineWidthScale))
                .Any(item=>item.Scale>0&&item.Edge.DrawEnabled&&item.Edge.Color.W is <0 or >1)==true
            ||frame.Options?.GroundShadow is {Color.W:<0 or >1})
            throw new NotSupportedException("Straight-alpha output requires nonnegative material opacity and outline/ground coverage between zero and one.");
        for(int i=0;i<frame.Surface.Materials.Count;i++)
        {
            var binding=frame.Surface.Materials[i];
            if(binding.SphereMode!=OriginalSphereMode.SubTexture||frame.Surface.Snapshot.Materials[i].DiffuseTexture<0)continue;
            float weight=binding.TextureFactors.Toon.Multiply.W+binding.TextureFactors.Toon.Add.W;
            if(!float.IsFinite(weight)||weight is <0 or >1)
                throw new NotSupportedException("Straight-alpha SubTexture output requires effective toon opacity between zero and one; legacy output preserves the unrestricted shader calculation.");
        }
    }

    internal D3D12GpuTarget Record(D3D12RecordingContext context,D3D12GpuTarget input)
    {
        if(!ReferenceEquals(context.Runtime,runtime)||!runtime.IsRecording)
            throw new InvalidOperationException("Alpha resolve requires the owning open GPU frame.");
        if(input.Depth||input.Format!=Format.R16G16B16A16_Float||!input.Initialized)
            throw new InvalidOperationException("Alpha resolve requires initialized premultiplied RGBA16F.");
        var device=context.Device;var commands=context.Commands;
        var compiled=GetShaders();
        var root=cache.Get("alpha-resolve:root",()=>device.CreateRootSignature<ID3D12RootSignature>(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,0)),ShaderVisibility.Pixel)]),
            RootSignatureVersion.Version10));
        var pipeline=cache.Get("alpha-resolve:pso",()=>
        {
            var created=device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
                RootSignature=root,VertexShader=compiled.Vertex,PixelShader=compiled.Pixel,
                BlendState=BlendDescription.Opaque,SampleMask=uint.MaxValue,RasterizerState=RasterizerDescription.CullNone,
                DepthStencilState=DepthStencilDescription.None,PrimitiveTopologyType=PrimitiveTopologyType.Triangle,
                RenderTargetFormats=[Format.R8G8B8A8_UNorm],SampleDescription=new(1,0)});
            runtime.PipelineCreations++;return created;
        });
        // The opaque full-target draw overwrites every texel of the pooled image.
        var resource=runtime.Transients.Resource(context,HeapType.Default,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm,(uint)input.Width,(uint)input.Height,mipLevels:1,flags:ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        var rtv=runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView,1));
        var srv=runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,1,DescriptorHeapFlags.ShaderVisible));
        var handle=rtv.GetCPUDescriptorHandleForHeapStart();device.CreateRenderTargetView(resource,null,handle);
        device.CreateShaderResourceView(input.Resource,null,srv.GetCPUDescriptorHandleForHeapStart());
        var output=runtime.Transients.Track(new D3D12GpuTarget("$straight-alpha",resource,handle,input.Width,input.Height,Format.R8G8B8A8_UNorm,false));
        commands.UnsetRenderTargets();context.Transition(input,ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);commands.SetGraphicsRootSignature(root);commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootDescriptorTable(0,srv.GetGPUDescriptorHandleForHeapStart());
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0,0,input.Width,input.Height,0,1);
        commands.RSSetScissorRect(new RawRect(0,0,input.Width,input.Height));
        commands.OMSetRenderTargets(handle);commands.DrawInstanced(3,1,0,0);
        commands.UnsetRenderTargets();output.Initialized=true;return output;
    }

    private (byte[] Vertex,byte[] Pixel) GetShaders()
    {
        if(shaders is {} compiled)return compiled;
        const ShaderFlags flags=ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness;
        compiled=(Compiler.Compile(Source,"VSMain","alpha-resolve.hlsl","vs_5_1",flags).ToArray(),
            Compiler.Compile(Source,"PSMain","alpha-resolve.hlsl","ps_5_1",flags).ToArray());
        shaders=compiled;runtime.ShaderCompilations+=2;return compiled;
    }

    private const string Source="""
        Texture2D<float4> InputColor:register(t0);
        float4 VSMain(uint id:SV_VertexID):SV_Position {
            float2 uv=float2((id<<1)&2,id&2);
            return float4(uv*float2(2,-2)+float2(-1,1),0,1);
        }
        float4 PSMain(float4 position:SV_Position):SV_Target {
            float4 color=InputColor.Load(int3(int2(position.xy),0));
            // Quantize coverage explicitly before the zero-RGB decision. Relying on
            // the render-target conversion for A after comparing an unquantized
            // float can leave RGB behind on devices whose UNorm rounding reaches
            // zero near the first code boundary. HLSL round uses nearest-even.
            float coverage=round(saturate(color.a)*255)/255;
            return coverage>0&&color.a>0?float4(saturate(color.rgb/color.a),coverage):float4(0,0,0,0);
        }
        """;
}
