namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    internal const string StylizedShadingHlsl = """
        float3 StylizedAlbedo(SurfaceOutput v) {
        #ifdef MDA_DISABLE_STYLIZED_SPHERE_MAPS
            return MdaComputeBaseColor(v, false);
        #else
            return MdaComputeBaseColor(v, true);
        #endif
        }
        float3 StylizedGrade(float3 rgb) {
            float luma=dot(rgb,float3(.2126,.7152,.0722));
            rgb=max(luma+(rgb-luma)*StylizedGradeParams.x,0);
            float3 unit=saturate(rgb);
            if(StylizedGradeParams.y!=0) unit=lerp(unit,unit*unit*(3-2*unit),StylizedGradeParams.y);
            float gain=StylizedGradeParams.z;
            if(gain!=1) unit=unit*gain/(1+(gain-1)*unit);
            return rgb>1?rgb:unit;
        }
        #ifdef MDA_TOON_PRESERVE_CHROMA
        // Grade the value once, then restore the pigment ratios. Independent channel curves
        // can bleach bright reds/cyans or clip two different texture colors to the same white.
        float3 StylizedPigmentGrade(float3 rgb) {
            float value=max(rgb.r,max(rgb.g,rgb.b));
            if(value<=0) return rgb;
            float3 pigment=rgb/value;
            float chroma=1-min(pigment.r,min(pigment.g,pigment.b));
            float saturation=lerp(StylizedGradeParams.x,1,StylizedShadeParams.w);
            if(chroma>0 && saturation!=1)
                pigment=1+(pigment-1)*min(saturation,1/chroma);
            // Keep authored over-range values available to later lighting and exposure passes.
            if(value<1) {
                value=lerp(value,value*value*(3-2*value),StylizedGradeParams.y);
                float gain=StylizedGradeParams.z;
                value=value*gain/(1+(gain-1)*value);
            }
            return pigment*value;
        }
        #endif
        float StylizedNativeResponse(SurfaceOutput v,float3 n,float3 l) {
            float band=MdaStyleLightResponse(v, n, l, 1);
            if(StylizedGradeParams.z<=0 || StylizedShadeParams.x<=0) return band;
            return lerp(band,saturate((dot(n,l)+.35)*4),StylizedShadeParams.w);
        }
        float4 MdaPSSurfaceStylized(SurfaceOutput v) {
            float hairBand;
            float4 color=MdaPSSurfaceCore(v,hairBand);
            if(StylizedGradeParams.z<=0) return color;
            if(StylizedShadeParams.x<=0) return float4(StylizedGrade(color.rgb),color.a);
            float3 baseColor=StylizedAlbedo(v);
            if(any(baseColor<0) || any(!isfinite(baseColor))) return color;
            float3 n=normalize(v.Normal); float3 l=IncomingLight.xyz;
            float c=MdaStyleLightResponse(v,n,l,1);
            float lit=lerp(c,1,StylizedShadeParams.w);
            float peak=max(baseColor.r,max(baseColor.g,baseColor.b));
            float3 chroma=peak>0?baseColor/peak:1;
            float3 shadow=baseColor*lerp(1,chroma,StylizedShadeParams.z)*StylizedShadeParams.y*StylizedTintParams.rgb;
            float3 rgb=lerp(shadow,baseColor,lit);
            float halfLambert=saturate(dot(n,l)*.5+.5);
            float band=(1-smoothstep(0,.07,abs(halfLambert-ShadowOptions.w)))*(1-StylizedShadeParams.w);
            rgb*=lerp(1,StylizedWarmParams.rgb,StylizedTintParams.w*band);
            rgb=lerp(baseColor*MdaStylePaletteFactor(v,c,dot(n,l),1),rgb,StylizedShadeParams.x);
            if(StylizedGradeParams.w>0) {
                float3 view=normalize(EyePosition.xyz-v.WorldPosition);
                float fresnel=1-saturate(dot(n,view));
                float rim=smoothstep(StylizedWarmParams.w,StylizedWarmParams.w+.04,fresnel)*c;
            #ifdef MDA_TOON_PRESERVE_CHROMA
                // A lit rim lifts the existing tone. Material that is already bright has
                // less headroom, so a rim cannot flatten neighboring texture highlights.
                float value=max(rgb.r,max(rgb.g,rgb.b));
                float headroom=saturate(1-value);
                rgb*=1+rim*StylizedGradeParams.w*headroom;
            #else
                rgb+=baseColor*rim*StylizedGradeParams.w;
            #endif
            }
        #ifdef MDA_TOON_PRESERVE_CHROMA
            // The toon color is reconstructed from albedo, so restore the visible UV-oriented hair band
            // using the shaded pigment and its remaining headroom.
            if(hairBand>0)
                rgb*=1+hairBand*PmxMaterialTreatmentParams.z*saturate(1-max(rgb.r,max(rgb.g,rgb.b)));
            return float4(StylizedPigmentGrade(rgb),color.a);
        #else
            return float4(StylizedGrade(rgb),color.a);
        #endif
        }
        """;
}
