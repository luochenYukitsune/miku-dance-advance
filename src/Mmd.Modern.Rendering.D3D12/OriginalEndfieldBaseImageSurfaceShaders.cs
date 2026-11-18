using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Bounded independently authored equations. The old no-child source is untouched.
    internal static string EndfieldBaseImageSurfaceSource(string entry,bool native,bool gi,
        bool morph,bool sub,bool hair=false,bool rim=false)
    {
        string source=EndfieldPaletteSurfaceSource(entry,native,gi,morph,sub,hair,rim);
        // Parent consumers share one captured interpolation / TBN / packed sample.
        source=ReplaceOnce(source,
            "float3 EndfieldMatcapComposeDelta(SurfaceOutput v,uint primitive,bool front,float3 changed,float2 uv,float3 geometric,float3 perturbed,float k,float rangeQ,float3 currentColor)",
            BaseImageHelper+"\nfloat3 EndfieldMatcapComposeDelta(SurfaceOutput v,uint primitive,bool front,float3 changed,float2 uv,float3 geometric,float3 perturbed,float k,float rangeQ,float3 currentColor,float3 effectiveCpre)");
        source=ReplaceOnce(source,"float3 initial=MatcapBaseBaseline.rgb;","float3 initial=effectiveCpre;");
        // The wrappers below are the old uniform-Cpre implementation. Cancel their
        // actual contribution, rather than a hypothetical per-pixel replacement.
        source=ReplaceOnce(source,"float3 oldHair=initial*p;","float3 oldHair=MatcapBaseBaseline.rgb*p;");
        source=ReplaceOnce(source,
            "float3 oldRim=r==0?float3(0,0,0):EndfieldMatcapOverlay(initial,tint,saturate(r))*(MatcapBaseBaseline.w+r)-initial*MatcapBaseBaseline.w;",
            "float3 oldRim=r==0?float3(0,0,0):EndfieldMatcapOverlay(MatcapBaseBaseline.rgb,tint,saturate(r))*(MatcapBaseBaseline.w+r)-MatcapBaseBaseline.rgb*MatcapBaseBaseline.w;");
        source=ReplaceOnce(source,"float3 changed=MatcapBaseBaseline.rgb*((1-gate)+gate*screen);",
            "float3 effectiveCpre=EndfieldBaseImageCpre(v,primitive,front,uv,baseGeometric,basePerturbed,rangeQ);\n    float3 changed=effectiveCpre*((1-gate)+gate*screen);");
        return ReplaceOnce(source,"return EndfieldMatcapComposeDelta(v,primitive,front,changed,uv,baseGeometric,basePerturbed,k,rangeQ,currentColor);",
            "return EndfieldMatcapComposeDelta(v,primitive,front,changed,uv,baseGeometric,basePerturbed,k,rangeQ,currentColor,effectiveCpre);");
    }

    private static string ReplaceOnce(string source,string old,string replacement)
    {
        int index=source.IndexOf(old,StringComparison.Ordinal);
        if(index<0 || source.IndexOf(old,index+old.Length,StringComparison.Ordinal)>=0)
            throw new InvalidOperationException("Endfield base-image shader anchor missing or ambiguous: "+old);
        return source[..index]+replacement+source[(index+old.Length)..];
    }

    private const string BaseImageHelper="""
        cbuffer EndfieldBaseImageConstants:register(b14) {
            float4 BaseImageBaseContrast;
            float4 BaseImageModelShadow;
            float4 BaseImageShadowTint;
            float4 BaseImageMultiplierSaturation;
            float4 BaseImageOptions;
            float4 BaseImageModes;
            float4 BaseImageSourceDirectionWidth;
            float4 BaseImageSourceOptions;
            float4 BaseImageSourceView;
        };
        Texture2D<float4> BaseImageColorMap:register(t24);
        Texture2D<float4> BaseImageDetailMap:register(t25);
        Texture2D<float4> BaseImageShadowMap:register(t26);
        StructuredBuffer<float4> BaseImageShadowLut:register(t27);
        float3 EndfieldBaseImageLut(uint row,float q) {
            // Clamp before floor/cast: finite out-of-range q extends the endpoints.
            float x=saturate(q)*257-.5;
            int j=(int)floor(x);
            float f=x-j;
            float3 a=BaseImageShadowLut[row*257+(uint)clamp(j,0,256)].rgb;
            float3 b=BaseImageShadowLut[row*257+(uint)clamp(j+1,0,256)].rgb;
            return (1-f)*a+f*b;
        }
        float3 EndfieldBaseImageShadow(float q) {
            float k=saturate(BaseImageShadowTint.w);
            float3 gray=EndfieldBaseImageLut(0,q),warm=EndfieldBaseImageLut(1,q);
            float3 a=(1-k)*gray+k*warm;
            float3 b=EndfieldMatcapOverlay(a,BaseImageShadowTint.rgb,1);
            float3 c=(1-saturate(q))*BaseImageModelShadow.rgb+saturate(q);
            float3 d=(1-k)*c+k*c*EndfieldBaseImageLut(2,q);
            precise float luminance=BaseImageShadowTint.r*.2126+BaseImageShadowTint.g*.7152+BaseImageShadowTint.b*.0722;
            return luminance<1?b:d;
        }
        float3 EndfieldBaseImageHsv(float3 rgb,float multiplier) {
            float hi=max(rgb.r,max(rgb.g,rgb.b)),lo=min(rgb.r,min(rgb.g,rgb.b));
            float chroma=hi-lo,saturation=hi==0?0:chroma/hi,hue=0;
            if(saturation!=0) {
                if(rgb.r==hi) hue=(rgb.g-rgb.b)/chroma;
                else if(rgb.g==hi) hue=2+(rgb.b-rgb.r)/chroma;
                else hue=4+(rgb.r-rgb.g)/chroma;
                hue/=6;if(hue<0) hue+=1;
            }
            hue=frac(hue+1);saturation=saturate(saturation*multiplier);
            if(saturation==0) return hi.xxx;
            float sector=hue*6,whole=floor(sector),fraction=sector-whole;
            float low=hi*(1-saturation),falling=hi*(1-saturation*fraction),rising=hi*(1-saturation*(1-fraction));
            if(whole==0) return float3(hi,rising,low);
            if(whole==1) return float3(falling,hi,low);
            if(whole==2) return float3(low,hi,rising);
            if(whole==3) return float3(low,falling,hi);
            if(whole==4) return float3(rising,low,hi);
            return float3(hi,low,falling);
        }
        float3 EndfieldBaseImageSourceNormalize(float3 value) {
            float scale=max(abs(value.x),max(abs(value.y),abs(value.z)));
            return scale==0?float3(0,0,0):normalize(value/scale);
        }
        float3 EndfieldBaseImageSourceNormal(float3 geometric,float3 perturbed,float strength) {
            return EndfieldBaseImageSourceNormalize(geometric+(perturbed-geometric)*strength);
        }
        float EndfieldBaseImageFacing(SurfaceOutput v,float3 normal) {
            float3 view=BaseImageSourceView.w>0?BaseImageSourceView.xyz:BaseImageSourceView.xyz-v.WorldPosition;
            float facing=saturate(abs(dot(normal,EndfieldBaseImageSourceNormalize(view))));
            float blend=BaseImageSourceOptions.y;
            if(blend!=.5) {
                blend=clamp(blend,0,.99999);
                float exponent=blend<.5?2*blend:.5/(1-blend);
                // Defined neutral Blend0, including pow(0,0), on every device.
                facing=exponent==0?1:pow(facing,exponent);
            }
            return 1-facing;
        }
        float EndfieldBaseImageDirectionalQ(float3 normal) {
            precise float u=dot(normal,BaseImageSourceDirectionWidth.xyz)*.5+.5;
            precise float a=BaseImageSourceDirectionWidth.w-.5;
            precise float maximum=1-a;
            precise float minimum=maximum*BaseImageSourceDirectionWidth.w;
            precise float q=maximum==minimum?0:((u-minimum)/(maximum-minimum))*1.5;
            return clamp(q,0,1.5);
        }
        float3 EndfieldBaseImageCpre(SurfaceOutput v,uint primitive,bool front,float2 uv,float3 geometric,float3 perturbed,float rangeQ) {
            float3 x=BaseImageOptions.z>0?BaseImageColorMap.SampleLevel(MatcapSampler,uv,0).rgb:BaseImageBaseContrast.rgb;
            if(BaseImageSourceOptions.x>0) {
                float f=EndfieldBaseImageFacing(v,EndfieldBaseImageSourceNormal(geometric,perturbed,1));
                x=(1-f)*x+f*x*x;
            }
            if(!front) x=x*x;
            x=(1-BaseImageBaseContrast.w)*x+BaseImageBaseContrast.w*x*x;
            x=EndfieldBaseImageHsv(x,BaseImageMultiplierSaturation.w)*BaseImageMultiplierSaturation.rgb;
            float detail=BaseImageOptions.w>0?BaseImageDetailMap.SampleLevel(MatcapSampler,uv,0).b:BaseImageOptions.x;
            x=.5*x+.5*x*detail;
            float h=saturate(BaseImageOptions.y);
            if(h>0) {
                float q=BaseImageModes.x>0?BaseImageShadowMap.SampleLevel(MatcapSampler,uv,0).r:BaseImageModelShadow.w;
                if(BaseImageSourceOptions.z>0) {
                    if(MatcapReserved.x>0 && all(BaseImageSourceDirectionWidth==MatcapRangeDirectionWidth) && BaseImageSourceOptions.w==MatcapReserved.w) q=rangeQ;
                    else q=EndfieldBaseImageDirectionalQ(EndfieldBaseImageSourceNormal(geometric,perturbed,BaseImageSourceOptions.w));
                }
                float3 shadow=EndfieldBaseImageShadow(q);
                x=(1-h)*x+h*x*shadow;
            }
            return x;
        }
        """;

    internal static byte[] CompileEndfieldBaseImageSurface(string entry,bool native,bool gi,
        bool morph,bool sub,bool hair=false,bool rim=false)
        => D3D12FxcShaderCache.Compile(EndfieldBaseImageSurfaceSource(entry,native,gi,morph,sub,hair,rim),entry,
            "OriginalEndfieldBaseImageAdapter.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness);
}
