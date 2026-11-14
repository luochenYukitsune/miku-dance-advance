using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    /// <summary>Modern hull fragment coverage uses the same surface alpha implementation,
    /// including the raw sphere alpha, UV1 technique, shadow-dependent toon alpha and Transp.
    /// Only the final line RGB is replaced. The accepted original source and entry points stay intact.</summary>
    internal static (byte[] Vertex, byte[] Pixel) CompileCharacterOutline(bool subTexture)
    {
        string source = subTexture ? OriginalSubTextureShader.Apply(Source, true) : OriginalTextureMorphShader.Apply(Source);
        source += """

            cbuffer CharacterOutlineConstants:register(b4) {
                row_major float4x4 OutlineWorldViewProjection;
                float4 CharacterOutlineColor;
                float4 CharacterOutlineOptions; // automatic material/texture palette RGB
            };
            struct CharacterOutlineInput {
                float3 Expanded:POSITION0; float3 Position:POSITION1; float3 Normal:NORMAL;
                float2 Uv:TEXCOORD0; float2 AdditionalUv:TEXCOORD1; float4 WidthSource:TEXCOORD2;
            };
            SurfaceOutput VSCharacterOutline(CharacterOutlineInput v) {
                Input original;
                original.Position=v.Position; original.Normal=v.Normal; original.Uv=v.Uv;
            """;
        if (subTexture) source += "original.AdditionalUv=float4(v.AdditionalUv,0,0);\n";
        source += """
                SurfaceOutput o=VSSurface(original);
                // Lighting/coverage attributes belong to the original surface; only raster geometry expands.
                float3 expanded=v.Expanded;
                if(CharacterOutlineOptions.y!=1 && v.WidthSource.w>0) {
                    precise float3 scaled=v.WidthSource.xyz+(expanded-v.WidthSource.xyz)*CharacterOutlineOptions.y;
                    expanded=scaled;
                }
                precise float4 projected=mul(float4(expanded,1),OutlineWorldViewProjection);
                o.Position=projected;
                return o;
            }
            float4 PSCharacterOutline(SurfaceOutput v):SV_Target {
                float alpha=PSSurface(v).a;
                float3 color=CharacterOutlineColor.rgb;
                if(CharacterOutlineOptions.x>0) {
                    float3 diffuse=DiffuseMap.Sample(SelectedSampler,v.Uv).rgb;
                    color=saturate(EgColor.rgb*MorphDiffuse(diffuse))*.55+.025;
                }
                return float4(color,alpha*CharacterOutlineColor.a);
            }
            """;
        return (
            Compiler.Compile(source, "VSCharacterOutline", "ModernCharacterOutline.hlsl", "vs_5_1",
                ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray(),
            Compiler.Compile(source, "PSCharacterOutline", "ModernCharacterOutline.hlsl", "ps_5_1",
                ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray());
    }
}
