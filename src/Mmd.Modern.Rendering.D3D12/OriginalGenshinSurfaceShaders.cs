using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored generic-PMX body shading. The audited references inform
    // light-signal/palette separation; no reference code, node assets, LightMap or face
    // data are copied. Explicit face normal shading is supported; dedicated face SDF
    // and hair responses still require future authored inputs.
    private const string GenshinSurfaceHelper="""
        float GenshinLightResponse(float nDotL,float visibility) {
            float halfLambert=saturate(nDotL*.5+.5);
            float band;
            if(IncomingLight.w<=0) band=halfLambert>=ShadowOptions.w?1:0;
            else {
                float lower=ShadowOptions.w-IncomingLight.w*.5;
                float t=saturate((halfLambert-lower)/IncomingLight.w);
                band=t*t*(3-2*t);
            }
            // EyePosition.xyz remains the unchanged view position. Only this shader
            // family consumes W, packed per draw from an explicitly selected Face role.
            // Body/1 follows the accepted arithmetic exactly. The independently
            // controlled geometric visibility is applied after the normal band.
            if(EyePosition.w<1) band=lerp(1,band,EyePosition.w);
            // A separate explicit Face control adjusts computed shadow-map contrast.
            // The shadow is still generated and sampled; one retains the exact
            // accepted response, and other materials pack one here as well.
            if(GroundDirection.w<1) visibility=lerp(1,visibility,GroundDirection.w);
            // The primary geometric penumbra remains continuous. Only the normal
            // response is banded, before mixing the PMX-authored dark/lit endpoints.
            return band*visibility;
        }
        float3 GenshinPaletteFactor(float blend) {
            return blend+ToonShadowColor.rgb*(1-blend);
        }
        """;

    // Primary-light palette blend, consumed by the Endfield palette material's own live-light budget.
    private const string GenshinNativePaletteHelper="""
        float GenshinPrimaryBlend(SurfaceOutput v) {
            float3 q=v.LightClip.xyz/v.LightClip.w;
            float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
            float visibility=1;
            if(uv.x>=0 && uv.x<=1 && uv.y>=0 && uv.y<=1) {
                precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
                precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
                if(ShadowOptions.y>0) weighted=weighted*uv.y;
                visibility=1-saturate(weighted-.3);
            }
            return GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),visibility);
        }
        """;

    internal static string GenshinSurfaceSource(bool native,bool gi,bool textureMorph,bool subTexture)
    {
        // Existing transformations keep raw alpha, PMX specular, texture morphs and
        // SubTexture exactly aligned with outline/GBuffer/temporal coverage consumers.
        string source=CharacterToonSource(native,gi,textureMorph,subTexture);
        static string ReplaceRequired(string text,string anchor,string replacement)
        {
            if(!text.Contains(anchor,StringComparison.Ordinal))
                throw new InvalidOperationException("Genshin PMX shader anchor missing: "+anchor);
            return text.Replace(anchor,replacement,StringComparison.Ordinal);
        }
        source=ReplaceRequired(source,CharacterToonHelper,GenshinSurfaceHelper);
        source=ReplaceRequired(source,
            "float originalC=c; c=CharacterMainToon(c);",
            "float originalC=c; c=GenshinLightResponse(dot(n,l),visibility);");
        // Outside the projective shadow map, retain raw coverage and the established
        // unoccluded authored highlight, while still applying the new diffuse response.
        source=ReplaceRequired(source,"return float4(baseColor+highlight,alpha);",
            "return float4(baseColor*GenshinPaletteFactor(GenshinLightResponse(dot(n,l),1))+highlight,alpha);");
        if(native||gi)
        {
            source=ReplaceRequired(source,
                "float c=min(saturate(dot(normalize(v.Normal),IncomingLight.xyz)*3),visibility); c=CharacterMainToon(c);",
                "float c=GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),visibility);");
            source=ReplaceRequired(source,
                "if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) return fill;",
                "if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) return fill*GenshinPaletteFactor(GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),1));");
            source=ReplaceRequired(source,"if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) return false;","");
            // GI's remaining palette budget also has an unoccluded response outside
            // the primary map. The sampled value there is not used for visibility.
            const string visibility="precise float visibility=1-saturate(weighted-.3);";
            source=ReplaceRequired(source,visibility,visibility+" if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) visibility=1;");
        }
        // Additional lights use the same half-Lambert band as the surface (face SDF variants later swap in their
        // local-direction response through this exact call text); they are composed with the
        // shared highlight compression of every surface family (NativeAddSoft), not a family budget.
        if(native)
            source=ReplaceRequired(source,"float3 NativeDirect(SurfaceOutput v) {",
                "#undef NATIVE_LIGHT_RESPONSE\n#define NATIVE_LIGHT_RESPONSE(v,n,l) GenshinLightResponse(dot(n,l),1)\nfloat3 NativeDirect(SurfaceOutput v) {");
        return ApplyPmxMaterialTreatment(source,native,gi);
    }

    internal static byte[] CompileGenshinSurface(string entry,bool native,bool gi,bool textureMorph,bool subTexture)
        =>Compiler.Compile(GenshinSurfaceSource(native,gi,textureMorph,subTexture),entry,
            "OriginalGenshinPmxBody.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();
}
