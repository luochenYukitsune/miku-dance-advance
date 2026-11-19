namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    internal const string PmxMaterialTreatmentHlsl = """
        float3 PmxMaterialSaturation(float3 rgb) {
            float saturation=PmxMaterialTreatmentParams.x;
            float contrast=PmxMaterialTreatmentParams.y;
            if(saturation==1 && contrast==0) return rgb;
            if(any(rgb<0)) return rgb;
            float value=max(rgb.x,max(rgb.y,rgb.z));
            float chroma=value-min(rgb.x,min(rgb.y,rgb.z));
            if(saturation!=1 && chroma!=0) {
                float factor=min(saturation,value/chroma);
                rgb=value+(rgb-value)*factor;
            }
            if(contrast!=0 && all(rgb>=0) && value>0 && value<=1)
                rgb*=((1-contrast)+contrast*value);
            return rgb;
        }
        float3 PmxGenericHair(SurfaceOutput v,float3 n,float3 h,float3 l,float3 baseColor) {
            float3 dpdx=ddx(v.WorldPosition), dpdy=ddy(v.WorldPosition);
            float2 duvdx=ddx(v.Uv), duvdy=ddy(v.Uv);
            float uvScale=max(max(abs(duvdx.x),abs(duvdx.y)),max(abs(duvdy.x),abs(duvdy.y)));
            if(uvScale<=0) return 0;
            float2 ux=duvdx/uvScale, uy=duvdy/uvScale;
            float determinant=ux.x*uy.y-ux.y*uy.x;
            if(abs(determinant)<1e-8) return 0;
            float3 tangent=dpdy*ux.x-dpdx*uy.x;
            float tangentScale=max(abs(tangent.x),max(abs(tangent.y),abs(tangent.z)));
            if(tangentScale<=0) return 0;
            tangent/=tangentScale;
            tangent-=n*dot(n,tangent);
            float length2=dot(tangent,tangent);
            if(length2<1e-12) return 0;
            tangent*=rsqrt(length2);
            float tangentHalf=dot(tangent,h);
            float lobe=1-tangentHalf*tangentHalf;
            float band=smoothstep(1-PmxMaterialTreatmentParams.w,1,lobe)*saturate(dot(n,l))*step(0,dot(n,h));
            float3 tint=max(baseColor,0)/max(1,max(baseColor.r,max(baseColor.g,baseColor.b)));
            return tint*PmxMaterialTreatmentParams.z*band;
        }
        """;
}
