using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independent ordinary-PMX adaptation of the audited light-signal/palette
    // separation. No Unity/GPL source, node asset, LightMap, MatCap or face SDF is
    // incorporated. The first highlight gate is fixed, not a configurable preset field.
    private const string StarRailSurfaceHelper="""
        float StarRailLightResponse(float nDotL,float visibility) {
            float halfLambert=saturate(nDotL*.5+.5);
            float band;
            if(IncomingLight.w<=0) band=halfLambert>=ShadowOptions.w?1:0;
            else {
                // Unlike the centred smooth Genshin band, this linear ramp finishes
                // at the threshold and occupies only its shadow-facing side.
                float lower=ShadowOptions.w-IncomingLight.w;
                band=saturate((halfLambert-lower)/IncomingLight.w);
            }
            if(EyePosition.w<1) band=lerp(1,band,EyePosition.w);
            if(GroundDirection.w<1) visibility=lerp(1,visibility,GroundDirection.w);
            return band*visibility;
        }
        float3 StarRailPaletteFactor(float blend) {
            return blend+ToonShadowColor.rgb*(1-blend);
        }
        """;

    internal static string StarRailSurfaceSource(bool native,bool gi,bool textureMorph,bool subTexture)
    {
        // Reuse the verified coverage, originalC attenuation, PMX palette budget and
        // native/GI plumbing. Only this family's diffuse and authored highlight change.
        string source=GenshinSurfaceSource(native,gi,textureMorph,subTexture);
        static string ReplaceRequired(string text,string anchor,string replacement)
        {
            if(!text.Contains(anchor,StringComparison.Ordinal))
                throw new InvalidOperationException("Star Rail PMX shader anchor missing: "+anchor);
            return text.Replace(anchor,replacement,StringComparison.Ordinal);
        }
        source=ReplaceRequired(source,GenshinSurfaceHelper,StarRailSurfaceHelper);
        source=source.Replace("GenshinLightResponse","StarRailLightResponse",StringComparison.Ordinal)
            .Replace("GenshinPaletteFactor","StarRailPaletteFactor",StringComparison.Ordinal)
            .Replace("GenshinPrimaryBlend","StarRailPrimaryBlend",StringComparison.Ordinal);
        source=ReplaceRequired(source,
            "float3 highlight=saturate(pow(max(0,dot(n,h)),SpecularPower.w)*SpecularPower.rgb);",
            """
            float authoredLobe=pow(max(0,dot(n,h)),SpecularPower.w);
            float highlightBand=smoothstep(.45,.55,authoredLobe);
            // Multiplying the unchanged authored lobe by [0,1] cannot introduce
            // highlights on a zero-specular PMX material or exceed its original bound.
            float3 highlight=saturate(authoredLobe*SpecularPower.rgb)*highlightBand;
            """);
        return source;
    }

    internal static byte[] CompileStarRailSurface(string entry,bool native,bool gi,bool textureMorph,bool subTexture)
        =>Compiler.Compile(StarRailSurfaceSource(native,gi,textureMorph,subTexture),entry,
            "OriginalStarRailPmxBody.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();
}
