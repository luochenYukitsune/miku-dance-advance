using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Ordinary character materials opt in independently of GI and primary toon style.
    // The accepted no-native source and the four specialised families stay separate.
    internal static byte[] CompileCharacterDirectPalette(string entry, bool gi, bool textureMorph, bool subTexture, bool characterToon)
        => Compiler.Compile(CharacterDirectPaletteSource(gi, textureMorph, subTexture, characterToon), entry,
            "OriginalCharacterDirectPalette.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray();

    internal static string CharacterDirectPaletteSource(bool gi, bool textureMorph, bool subTexture, bool characterToon)
    {
        string source = characterToon ? CharacterToonSource(true, gi, textureMorph, subTexture) : Source + GiSource + NativeSource;
        if (!characterToon)
        {
            if (subTexture) source = OriginalSubTextureShader.Apply(source, true, true, gi);
            else if (textureMorph) source = OriginalTextureMorphShader.Apply(source);
        }

        static string Replace(string text, string anchor, string replacement)
        {
            if (!text.Contains(anchor, StringComparison.Ordinal))
                throw new InvalidOperationException("Character direct palette shader anchor missing: " + anchor);
            return text.Replace(anchor, replacement, StringComparison.Ordinal);
        }

        // Reuse the actual generated GI diffuse endpoint and primary response, after
        // morph/SubTexture/explicit toon transformations. Native variants already link
        // this function even when GI is off; none of its GI settings enter the budget.
        int function = source.IndexOf("bool GiToonPaletteDelta(", StringComparison.Ordinal);
        int controlStart = function < 0 ? -1 : source.IndexOf("float3 q=", function, StringComparison.Ordinal);
        int endpointStart = controlStart < 0 ? -1 : source.IndexOf("precise float3 diffuseLit=", controlStart, StringComparison.Ordinal);
        int endpointEnd = endpointStart < 0 ? -1 : source.IndexOf("// ReferenceRadiance", endpointStart, StringComparison.Ordinal);
        if (function < 0 || controlStart < 0 || endpointStart < 0 || endpointEnd < 0)
            throw new InvalidOperationException("Character direct palette diffuse endpoint is missing.");
        string endpoint = source[endpointStart..endpointEnd].TrimEnd();
        string control = source[controlStart..endpointStart].Replace("return false;", "return 0;", StringComparison.Ordinal).TrimEnd();
        string helper = """
            float3 CharacterRemainingLightBudget(SurfaceOutput v) {
                ENDPOINT
                // Signed/nonfinite and other previously accepted special domains
                // keep legacy arithmetic. This protection introduces no new material
                // rejection and never turns a negative budget into a negative scale.
                if(!all(isfinite(diffuseLit)) || any(diffuseLit<0)
                    || !all(isfinite(ToonShadowColor.rgb)) || any(ToonShadowColor.rgb<0) || any(ToonShadowColor.rgb>1)) return -1;
                CONTROL
                return diffuseLit*max(1-toonFactor,0);
            }
            float CharacterAddedLightScale(float3 budget,float3 directIncrement,float3 giIncrement) {
                precise float3 total=directIncrement+giIncrement;
                // Preserve the old arithmetic if an already accepted input overflows
                // or has signed diffuse light. In particular, never evaluate Inf*0.
                if(any(budget<0) || !all(isfinite(budget)) || any(directIncrement<0) || any(giIncrement<0)
                    || !all(isfinite(total))) return 1;
                if(all(total<=budget)) return 1;
                float scale=1;
                if(total.x>0) scale=min(scale,budget.x/total.x);
                if(total.y>0) scale=min(scale,budget.y/total.y);
                if(total.z>0) scale=min(scale,budget.z/total.z);
                return scale;
            }
            """.Replace("ENDPOINT", endpoint, StringComparison.Ordinal).Replace("CONTROL", control, StringComparison.Ordinal);

        if (gi)
        {
            // Split the accepted GI function exactly as the stylized shared-budget
            // path does. AO, authored highlights and additive sphere stay in color;
            // only the new diffuse increments spend the remaining palette together.
            const string signature = "float4 PSSurfaceGi(SurfaceOutput v,uint primitive:SV_PrimitiveID):SV_Target";
            int functionStart = source.IndexOf(signature, StringComparison.Ordinal);
            int brace = functionStart < 0 ? -1 : source.IndexOf('{', functionStart);
            if (brace < 0) throw new InvalidOperationException("Character direct palette GI function is missing.");
            int depth = 1, position = brace + 1;
            for (; position < source.Length && depth > 0; position++)
            {
                if (source[position] == '{') depth++;
                else if (source[position] == '}') depth--;
            }
            if (depth != 0) throw new InvalidOperationException("Character direct palette GI function is incomplete.");
            string giBase = source[functionStart..position];
            giBase = Replace(giBase, signature,
                "float4 CharacterGiSurfaceBase(SurfaceOutput v,uint primitive,out float3 giIncrement,out bool hasGiIncrement)");
            giBase = Replace(giBase, "float4 color=PSSurface(v);",
                "giIncrement=0; hasGiIncrement=false; float4 color=PSSurface(v);");
            giBase = Replace(giBase, "color.rgb+=increment;return color;",
                "giIncrement=increment; hasGiIncrement=true; return color;");
            giBase = Replace(giBase, "color.rgb+=transport.rgb*GiSurfaceStrength;",
                "giIncrement=transport.rgb*GiSurfaceStrength; hasGiIncrement=true;");
            helper += "\n" + giBase;
            source = Replace(source,
                "float4 color=PSSurfaceGi(v,primitive); color.rgb+=NativeDirect(v); return color;",
                """
                if(NativeDiffuse.a<=0) {
                    float4 color=PSSurfaceGi(v,primitive); color.rgb+=NativeDirect(v); return color;
                }
                if(GiAmbientShare.w<1) {
                    float4 color=PSSurfaceGi(v,primitive);
                    float3 directIncrement=NativeDirect(v);
                    float scale=CharacterAddedLightScale(CharacterRemainingLightBudget(v),directIncrement,0);
                    if(scale==1) color.rgb+=directIncrement;
                    else color.rgb+=scale*directIncrement;
                    return color;
                }
                float3 giIncrement; bool hasGiIncrement;
                float4 color=CharacterGiSurfaceBase(v,primitive,giIncrement,hasGiIncrement);
                float3 directIncrement=NativeDirect(v);
                float scale=CharacterAddedLightScale(CharacterRemainingLightBudget(v),directIncrement,giIncrement);
                if(scale==1) {
                    if(hasGiIncrement) color.rgb+=giIncrement;
                    color.rgb+=directIncrement;
                } else color.rgb+=scale*(directIncrement+giIncrement);
                return color;
                """);
        }
        source = Replace(source, "float4 color=PSSurface(v); color.rgb+=NativeDirect(v); return color;",
            """
            float4 color=PSSurface(v);
            float3 directIncrement=NativeDirect(v);
            if(NativeDiffuse.a<=0) { color.rgb+=directIncrement; return color; }
            float scale=CharacterAddedLightScale(CharacterRemainingLightBudget(v),directIncrement,0);
            if(scale==1) color.rgb+=directIncrement;
            else color.rgb+=scale*directIncrement;
            return color;
            """);
        return Replace(source, "float4 PSSurfaceNative(SurfaceOutput v):SV_Target", helper + "\nfloat4 PSSurfaceNative(SurfaceOutput v):SV_Target");
    }
}

public sealed partial class D3D12OriginalStyleRenderer
{
    private readonly Dictionary<(string Entry, bool Gi, bool Morph, bool SubTexture, bool Toon), byte[]> characterDirectPaletteShaders = [];

    private byte[] GetCharacterDirectPaletteShader(string entry, bool gi, bool textureMorph, bool subTexture, bool characterToon)
    {
        var key = (entry, gi, textureMorph, subTexture, characterToon);
        if (characterDirectPaletteShaders.TryGetValue(key, out var found)) return found;
        var compiled = OriginalFrameShaders.CompileCharacterDirectPalette(entry, gi, textureMorph, subTexture, characterToon);
        runtime.ShaderCompilations++;
        characterDirectPaletteShaders.Add(key, compiled);
        return compiled;
    }
}
