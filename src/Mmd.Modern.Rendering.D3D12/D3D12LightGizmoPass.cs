using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>GPU editor guides after scene filters. World segments test unjittered scene visibility;
/// selection helpers remain on top. Neither group enters shadows, GI, history or exported captures.</summary>
internal sealed class D3D12LightGizmoPass(D3D12GpuRuntime runtime,D3D12GpuCache cache)
{
    private (byte[] Vertex,byte[] Pixel)? shaders;
    internal int Record(D3D12RecordingContext context,D3D12GpuTarget output,ViewportOverlaySnapshot overlay,
        D3D12GpuTarget? visibilityDepth=null)
    {
        if(overlay.Lines.Count==0)return 0;
        if(overlay.Lines.Any(line=>line.DeviceDepth is not null)&&visibilityDepth is null)
            throw new ArgumentException("World guides require scene visibility depth.",nameof(visibilityDepth));
        if(shaders is null)
        {
            shaders=(Compiler.Compile(Source,"VSMain","EditorGizmo.hlsl","vs_5_1",ShaderFlags.OptimizationLevel3).ToArray(),
                Compiler.Compile(Source,"PSMain","EditorGizmo.hlsl","ps_5_1",ShaderFlags.OptimizationLevel3).ToArray());
            runtime.ShaderCompilations+=2;
        }
        var device=runtime.Device;var commands=runtime.Commands;
        var root=cache.Get("editor-gizmo:root",()=>device.CreateRootSignature<ID3D12RootSignature>(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All)]),RootSignatureVersion.Version10));
        int draws=0;
        Draw(overlay.Lines.Where(line=>line.DeviceDepth is not null).ToArray(),visibilityDepth);
        Draw(overlay.Lines.Where(line=>line.DeviceDepth is null).ToArray(),null);
        return draws;

        void Draw(ViewportOverlayLine[] lines,D3D12GpuTarget? depth)
        {
        if(lines.Length==0)return;
        bool depthTest=depth is not null;
        var pipeline=cache.Get("editor-gizmo:pso:"+(depth?.Format.ToString()??"overlay"),()=>
        {
            var depthState=depthTest?DepthStencilDescription.Default:DepthStencilDescription.None;
            if(depthTest){depthState.DepthFunc=ComparisonFunction.LessEqual;depthState.DepthWriteMask=DepthWriteMask.Zero;}
            var pso=device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature=root,VertexShader=shaders.Value.Vertex,PixelShader=shaders.Value.Pixel,
                BlendState=BlendDescription.Opaque,SampleMask=uint.MaxValue,
                RasterizerState=RasterizerDescription.CullNone,DepthStencilState=depthState,
                DepthStencilFormat=depth?.Format??Format.Unknown,
                PrimitiveTopologyType=PrimitiveTopologyType.Triangle,RenderTargetFormats=[Format.R8G8B8A8_UNorm],SampleDescription=new(1,0)
            });runtime.PipelineCreations++;return pso;
        });
        var values=new Vector4[1+ViewportOverlaySnapshot.MaxLines*3];values[0]=new(output.Width,output.Height,depthTest?1:0,0);
        for(int i=0;i<lines.Length;i++)
        {
            var l=lines[i];values[1+i*3]=new(l.Start.X,l.Start.Y,l.End.X,l.End.Y);
            values[2+i*3]=l.Color;values[3+i*3]=new(l.Width,(float)l.Shape,l.Radius,0);
            if(l.DeviceDepth is {} z)values[3+i*3]=new(l.Width,(float)l.Shape,z.X,z.Y);
        }
        context.Transition(output,ResourceStates.RenderTarget);
        if(depth is not null){context.Transition(depth,ResourceStates.DepthWrite);commands.OMSetRenderTargets(output.Handle,depth.Handle);}
        else commands.OMSetRenderTargets(output.Handle);
        commands.SetGraphicsRootSignature(root);commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootConstantBufferView(0,context.UploadConstants(MemoryMarshal.AsBytes(values.AsSpan())));
        commands.RSSetViewport(0,0,output.Width,output.Height);commands.RSSetScissorRect(new RawRect(0,0,output.Width,output.Height));
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);commands.DrawInstanced(6,lines.Length,0,0);
        draws++;
        }
    }
    private static readonly string Source=$$"""
        cbuffer Gizmo : register(b0) { float4 Viewport; float4 Lines[{{ViewportOverlaySnapshot.MaxLines*3}}]; };
        struct V {
            float4 Position:SV_POSITION; float4 Color:COLOR0;
            nointerpolation float4 Ends:TEXCOORD0; nointerpolation float4 Style:TEXCOORD1;
        };
        V VSMain(uint corner:SV_VertexID,uint segmentIndex:SV_InstanceID) {
            float4 ends=Lines[segmentIndex*3],style=Lines[segmentIndex*3+2];
            float2 a=ends.xy*Viewport.xy,b=ends.zw*Viewport.xy,d=b-a;
            float2 side=float2(-d.y,d.x)/max(length(d),0.001)*style.x/2;
            float2 corners[6]={a-side,b-side,b+side,a-side,b+side,a+side};
            if(style.y==1||style.y==2) {
                float2 r=float2(style.z,style.z);
                corners[0]=a-r;corners[1]=a+float2(r.x,-r.y);corners[2]=a+r;
                corners[3]=a-r;corners[4]=a+r;corners[5]=a+float2(-r.x,r.y);
            } else if(style.y==3) {
                float2 forward=length(d)>0.001?d/length(d):float2(1,0);
                float2 perpendicular=float2(-forward.y,forward.x);
                float2 extent=perpendicular*(style.z+style.x/2),cap=forward*style.x/2;
                corners[0]=a-cap-extent;corners[1]=b+cap-extent;corners[2]=b+cap+extent;
                corners[3]=a-cap-extent;corners[4]=b+cap+extent;corners[5]=a-cap+extent;
            }
            float2 p=corners[corner]/Viewport.xy;
            float z=Viewport.z>0?((corner==0||corner==3||corner==5)?style.z:style.w):0;
            V o;o.Position=float4(p.x*2-1,1-p.y*2,z,1);
            o.Color=Lines[segmentIndex*3+1];o.Ends=float4(a,b);o.Style=style;return o;
        }
        float DistanceToSegment(float2 p,float2 a,float2 b) {
            float2 d=b-a;float t=saturate(dot(p-a,d)/max(dot(d,d),0.000001));
            return length(p-a-t*d);
        }
        float4 PSMain(V input):SV_TARGET {
            float shape=input.Style.y,width=input.Style.x,radius=input.Style.z;
            if(shape==1||shape==2) {
                float2 delta=abs(input.Position.xy-input.Ends.xy);
                float distance=shape==1?length(delta):max(delta.x,delta.y);
                clip(radius-distance);clip(distance-(radius-width));
            } else if(shape==3) {
                float2 a=input.Ends.xy,b=input.Ends.zw,d=b-a;
                float2 forward=length(d)>0.001?d/length(d):float2(1,0);
                float2 side=float2(-forward.y,forward.x)*radius;
                float distance=min(DistanceToSegment(input.Position.xy,a-side,b),DistanceToSegment(input.Position.xy,a+side,b));
                clip(width/2-distance);
            }
            return input.Color;
        }
        """;
}
