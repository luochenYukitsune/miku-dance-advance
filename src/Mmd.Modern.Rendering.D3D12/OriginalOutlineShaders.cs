using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Independently authored solid-colour outline transform. No textures, normals, toon or original FX.</summary>
internal static class OriginalOutlineShaders
{
    private const string Source="""
        cbuffer OutlineConstants : register(b0)
        {
            row_major float4x4 WorldViewProjection;
            float4 Color;
            float4 WidthScale;
        };
        float4 VSMain(float3 position:POSITION,float4 widthSource:TEXCOORD2):SV_POSITION
        {
            // Identity takes the accepted expanded stream verbatim. Inward vertices have
            // their independent fixed offset and do not consume material width.
            if(WidthScale.x!=1 && widthSource.w>0) {
                precise float3 scaled=widthSource.xyz+(position-widthSource.xyz)*WidthScale.x;
                position=scaled;
            }
            precise float4 projected=mul(float4(position,1),WorldViewProjection);
            return projected;
        }
        float4 PSMain():SV_Target { return Color; }
        """;
    public static (byte[] Vertex,byte[] Pixel) Compile()=>(
        Compiler.Compile(Source,"VSMain","OriginalOutline.hlsl","vs_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray(),
        Compiler.Compile(Source,"PSMain","OriginalOutline.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray());
}
