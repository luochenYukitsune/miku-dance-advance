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
        float StylizedNativeResponse(SurfaceOutput v,float3 n,float3 l) {
            float band=MdaStyleLightResponse(v, n, l, 1);
            if(StylizedGradeParams.z<=0 || StylizedShadeParams.x<=0) return band;
            return lerp(band,saturate((dot(n,l)+.35)*4),StylizedShadeParams.w);
        }
        float4 MdaPSSurfaceStylized(SurfaceOutput v) {
            float4 color=MdaPSSurfaceCore(v);
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
                rgb+=baseColor*rim*StylizedGradeParams.w;
            }
            return float4(StylizedGrade(rgb),color.a);
        }
        """;
}
