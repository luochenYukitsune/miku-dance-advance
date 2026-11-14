using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Independently authored SM5.1 character surface slice from the documented mathematical contract.</summary>
internal static partial class OriginalStyleShaders
{
    private const string Source="""
        cbuffer SurfaceConstants : register(b0)
        {
            row_major float4x4 World;
            row_major float4x4 NormalWorld;
            row_major float4x4 ViewProjection;
            row_major float4x4 SphereRotation;
            float4 EgColor;
            float4 SpecularPower;
            float4 IncomingLight;
            float4 EyePosition;
            float4 ToonShadowColor;
            float4 Options; // cutoff, sphere mode, has diffuse, reserved
        };
        Texture2D<float4> DiffuseMap : register(t0);
        Texture2D<float4> SphereMap : register(t1);
        SamplerState SelectedSampler : register(s0);
        struct Input { float3 Position:POSITION; float3 Normal:NORMAL; float2 Uv:TEXCOORD0; };
        struct Output {
            float4 Position:SV_POSITION; float3 Normal:NORMAL; float2 Uv:TEXCOORD0;
            float3 WorldPosition:TEXCOORD1; float2 SphereProjection:TEXCOORD2;
        };
        Output VSMain(Input v)
        {
            Output o;
            float4 worldPosition=mul(float4(v.Position,1),World);
            o.Position=mul(worldPosition,ViewProjection);
            o.WorldPosition=worldPosition.xyz;
            o.Normal=mul(float4(v.Normal,0),NormalWorld).xyz;
            o.Uv=v.Uv;
            // Preserve the unnormalised input-normal projection in the vertex stage.
            o.SphereProjection=mul(float4(v.Normal,0),SphereRotation).xy;
            return o;
        }
        float4 PSMain(Output v):SV_Target
        {
            float4 texel=DiffuseMap.Sample(SelectedSampler,v.Uv);
            float2 sphereUv=float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5);
            float4 sphere=Options.y>0 ? SphereMap.Sample(SelectedSampler,sphereUv) : float4(1,1,1,1);
            float alpha=EgColor.a*texel.a*sphere.a;
            clip(alpha-Options.x);
            precise float3 baseColor=EgColor.rgb*texel.rgb;
            if(Options.y==1) baseColor=baseColor*sphere.rgb;
            if(Options.y==2) baseColor=baseColor+sphere.rgb;
            float3 n=normalize(v.Normal);
            float3 l=IncomingLight.xyz;
            float3 halfDirection=normalize(l+normalize(EyePosition.xyz-v.WorldPosition));
            float3 highlight=saturate(pow(max(0,dot(n,halfDirection)),SpecularPower.w)*SpecularPower.rgb);
            // This slice only covers in-projection shadow visibility=1. It is not the outside-shadow branch.
            float c=saturate(dot(n,l)*3);
            precise float3 result;
            if(Options.z>0) result=baseColor*(c+ToonShadowColor.rgb*(1-c))+highlight*c;
            else result=baseColor*c+baseColor*ToonShadowColor.rgb*(1-c)+highlight*c;
            return float4(result,alpha);
        }
        """;
    public static (byte[] Vertex,byte[] Pixel) Compile()=>(
        Compiler.Compile(Source,"VSMain","OriginalSurface.hlsl","vs_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray(),
        Compiler.Compile(Source,"PSMain","OriginalSurface.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray());
}
