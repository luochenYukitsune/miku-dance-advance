using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored Star A / MC scalar adapter. UV0, linear repeat and
    // mip0 UNorm are explicit MDA policies, not inferred source image settings.
    // Resolved PMX specular is the palette; MatCap, emission and packed normals
    // are separate contracts. No reference code, graph or images are bundled.
    internal static string StarMcHairSurfaceSource(D3D12SurfaceStyle family, bool native, bool gi, bool textureMorph, bool subTexture)
    {
        const string helper = """
            cbuffer StarMcHairConstants:register(b6) {
                float4 StarMcHairParameters;
                float4 StarMcHairSelection;
            };
            Texture2D<float4> StarMcHighlightMap:register(t10);
            Texture2D<float4> StarMcShadowMap:register(t11);
            SamplerState StarMcHairSampler:register(s4);
            float3 StarMcHairHighlight(SurfaceOutput v,float3 n,float3 l) {
                float4 packed=StarMcHighlightMap.SampleLevel(StarMcHairSampler,v.Uv,0);
                float r=StarMcShadowMap.SampleLevel(StarMcHairSampler,v.Uv,0).r;
                float h=StarMcHairSelection.y>0?packed.a:packed.b;
                float k=saturate(h+StarMcHairParameters.x);
                float edge=1-saturate(r*StarMcHairParameters.z);
                float x=dot(n,l)*.5+.5;
                float u=saturate((x-(edge-StarMcHairParameters.w))/StarMcHairParameters.w);
                float q=.05000000074505806+(1-.05000000074505806)*u;
                STAR_MC_RESPONSE
                float gate=saturate(response*k);
                // Brightness can be negative or exceed one. Only the explicitly
                // named PMX palette adapter clamps the final authored highlight.
                float addedBrightness=gate*(k+StarMcHairParameters.y);
                return saturate(SpecularPower.rgb*addedBrightness*StarMcHairSelection.x);
            }
            """;
        const string starHighlight = """
            float authoredLobe=pow(max(0,dot(n,h)),SpecularPower.w);
            float highlightBand=smoothstep(.45,.55,authoredLobe);
            // Multiplying the unchanged authored lobe by [0,1] cannot introduce
            // highlights on a zero-specular PMX material or exceed its original bound.
            float3 highlight=saturate(authoredLobe*SpecularPower.rgb)*highlightBand;
            """;
        const string mcHighlight = """
            float authoredLobe=pow(max(0,dot(n,h)),SpecularPower.w);
            float highlightBand=authoredLobe*smoothstep(.10,.30,authoredLobe*authoredLobe);
            // Both factors lie in [0,1]: the authored specular cannot be boosted or
            // introduced when PMX RGB is zero. Base alpha remains coverage only.
            float3 highlight=saturate(authoredLobe*SpecularPower.rgb)*highlightBand;
            """;
        string source, highlight, response;
        switch (family)
        {
            case D3D12SurfaceStyle.StarRailPmx:
                source = StarRailSurfaceSource(native, gi, textureMorph, subTexture);
                highlight = starHighlight;
                // Exact float32 multiplier/bias of the audited .6/.9 Star A ramp,
                // followed by its RGB-to-float conversion. This is not the MC gate.
                response = "float f=saturate(q*3.333333969116211-2.000000476837158); float response=dot(float3(f,f,f),float3(.2126,.7152,.0722));";
                break;
            case D3D12SurfaceStyle.WutheringPmx:
                source = WutheringSurfaceSource(native, gi, textureMorph, subTexture);
                highlight = mcHighlight;
                response = "float response=q;";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family), "The selected hair scalar requires Star Rail or Wuthering.");
        }
        // Existing family literals may originate from a CRLF checkout. Normalize
        // only this derived variant before matching the complete primary block.
        source = source.ReplaceLineEndings("\n");
        static string ReplaceExactlyOnce(string source, string anchor, string replacement)
        {
            int at = source.IndexOf(anchor, StringComparison.Ordinal);
            if (at < 0 || source.LastIndexOf(anchor, StringComparison.Ordinal) != at)
                throw new InvalidOperationException("Star/MC hair shader anchor must occur exactly once: " + anchor);
            return source.Replace(anchor, replacement, StringComparison.Ordinal);
        }
        const string helperAnchor = "SurfaceOutput VSSurface(Input v) {";
        source = ReplaceExactlyOnce(source, helperAnchor,
            helper.Replace("STAR_MC_RESPONSE", response, StringComparison.Ordinal) + "\n" + helperAnchor);
        // Replace the primary lobe once, retaining originalC, outside projection,
        // diffuse/native/GI remaining budgets and every existing alpha consumer.
        return ReplaceExactlyOnce(source, highlight.ReplaceLineEndings("\n"), "float3 highlight=StarMcHairHighlight(v,n,l);");
    }

    internal static byte[] CompileStarMcHairSurface(D3D12SurfaceStyle family, string entry, bool native, bool gi, bool textureMorph, bool subTexture)
        => D3D12FxcShaderCache.Compile(StarMcHairSurfaceSource(family, native, gi, textureMorph, subTexture), entry,
            "OriginalStarMcHair.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness);
}
