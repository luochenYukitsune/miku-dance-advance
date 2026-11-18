using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independent finite ordinary-PMX adaptation informed by the audited raw-normal
    // front response and powered highlight separation. No reference code, assets,
    // encoded-normal, LightMap/AO, MatCap, hair mask or face SDF is incorporated.
    // The first squared-lobe highlight gate is fixed, not a configurable preset field.
    private const string WutheringSurfaceHelper="""
        float WutheringLightResponse(float nDotL,float visibility) {
            // Preserve the existing [0,1] threshold control while evaluating the
            // front band directly in raw [-1,1] NdotL, not half-Lambert space.
            float threshold=ShadowOptions.w*2-1;
            float band;
            if(IncomingLight.w<=0) band=nDotL>=threshold?1:0;
            else {
                float lower=threshold-IncomingLight.w*.5;
                float t=saturate((nDotL-lower)/IncomingLight.w);
                band=t*t*(3-2*t);
            }
            if(EyePosition.w<1) band=lerp(1,band,EyePosition.w);
            if(GroundDirection.w<1) visibility=lerp(1,visibility,GroundDirection.w);
            return band*visibility;
        }
        float3 WutheringPaletteFactor(float blend) {
            return blend+ToonShadowColor.rgb*(1-blend);
        }
        """;

    internal static string WutheringSurfaceSource(bool native,bool gi,bool textureMorph,bool subTexture)
    {
        // Coverage, source-order transparency, PMX palette endpoints and every
        // primary/GI/native remaining-contrast consumer share the verified chain.
        string source=GenshinSurfaceSource(native,gi,textureMorph,subTexture);
        static string ReplaceRequired(string text,string anchor,string replacement)
        {
            if(!text.Contains(anchor,StringComparison.Ordinal))
                throw new InvalidOperationException("Wuthering PMX shader anchor missing: "+anchor);
            return text.Replace(anchor,replacement,StringComparison.Ordinal);
        }
        source=ReplaceRequired(source,GenshinSurfaceHelper,WutheringSurfaceHelper);
        source=source.Replace("GenshinLightResponse","WutheringLightResponse",StringComparison.Ordinal)
            .Replace("GenshinPaletteFactor","WutheringPaletteFactor",StringComparison.Ordinal)
            .Replace("GenshinPrimaryBlend","WutheringPrimaryBlend",StringComparison.Ordinal);
        source=ReplaceRequired(source,
            "float3 highlight=saturate(pow(max(0,dot(n,h)),SpecularPower.w)*SpecularPower.rgb);",
            """
            float authoredLobe=pow(max(0,dot(n,h)),SpecularPower.w);
            float highlightBand=authoredLobe*smoothstep(.10,.30,authoredLobe*authoredLobe);
            // Both factors lie in [0,1]: the authored specular cannot be boosted or
            // introduced when PMX RGB is zero. Base alpha remains coverage only.
            float3 highlight=saturate(authoredLobe*SpecularPower.rgb)*highlightBand;
            """);
        return source;
    }

    internal static byte[] CompileWutheringSurface(string entry,bool native,bool gi,bool textureMorph,bool subTexture)
        =>D3D12FxcShaderCache.Compile(WutheringSurfaceSource(native,gi,textureMorph,subTexture),entry,
            "OriginalWutheringPmxBody.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness);
}
