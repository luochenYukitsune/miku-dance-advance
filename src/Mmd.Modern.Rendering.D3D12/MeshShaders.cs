using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static class MeshShaders
{
    private const string Source = """
        cbuffer MeshConstants : register(b0)
        {
            row_major float4x4 World;
            row_major float4x4 NormalWorld;
            row_major float4x4 ViewProjection;
            float4 Diffuse;
            float4 AmbientShininess;
            float4 Specular;
            float4 LightDirection;
            float4 ToonUvTransform;
            float4 AlphaCutoff;
        };
        Texture2D<float4> DiffuseMap : register(t0);
        Texture2D<float4> ToonMap : register(t1);
        SamplerState NearestClamp : register(s0);
        struct Input { float3 Position : POSITION; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; };
        struct Output { float4 Position : SV_POSITION; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; };
        Output VSMain(Input v)
        {
            Output o;
            o.Position = mul(mul(float4(v.Position, 1), World), ViewProjection);
            o.Normal = mul(float4(v.Normal, 0), NormalWorld).xyz;
            o.Uv = v.Uv;
            return o;
        }
        float4 PSMain(Output v) : SV_Target
        {
            float ndotl = saturate(dot(normalize(v.Normal), normalize(LightDirection.xyz)));
            float2 toonUv = ndotl * ToonUvTransform.xy + ToonUvTransform.zw;
            float3 toon = ToonMap.Sample(NearestClamp, toonUv).rgb;
            float4 texel = DiffuseMap.Sample(NearestClamp, v.Uv);
            clip(Diffuse.a * texel.a - AlphaCutoff.x);
            // Modern basic material model. This is not an MMD pixel-compatibility shader.
            float3 color = texel.rgb * (Diffuse.rgb * toon + AmbientShininess.rgb * 0.15)
                + Specular.rgb * pow(ndotl, max(1, AmbientShininess.w)) * 0.15;
            return float4(saturate(color), Diffuse.a * texel.a);
        }
        """;

    public static (byte[] Vertex, byte[] Pixel) Compile() => (
        Compiler.Compile(Source, "VSMain", "D3D12Mesh.hlsl", "vs_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray(),
        Compiler.Compile(Source, "PSMain", "D3D12Mesh.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray());
}
