using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored bounded equations. No source graph, plugin code or images.
    internal static string EndfieldPaletteSurfaceSource(string entry, bool native, bool gi,
        bool morph, bool sub, bool hair = false, bool rim = false)
    {
        string source = EndfieldMatcapSurfaceSource(entry, native, gi, morph, sub, hair, rim);
        const string signature = "float3 EndfieldMatcapComposeDelta(SurfaceOutput v,uint primitive,bool front,float3 changed,float2 uv,float3 geometric,float3 perturbed,float k,float rangeQ,float3 currentColor)";
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Endfield palette compose anchor missing.");
        int open = source.IndexOf('{', start), depth = 1, end = open + 1;
        for (; end < source.Length && depth != 0; end++)
        {
            if (source[end] == '{') depth++;
            else if (source[end] == '}') depth--;
        }
        if (depth != 0) throw new InvalidOperationException("Endfield palette compose scope is incomplete.");
        string compose = signature + " {\n"
            + "    float3 initial=MatcapBaseBaseline.rgb;\n"
            + "    float r=" + (rim ? "EndfieldRimMask(v,front)" : "0") + ";\n"
            + "    float p=" + (hair ? "EndfieldMatcapRadialScalar(v)" : "0") + ";\n"
            + "    float3 tint=" + (rim ? "RimColorRadial.rgb" : "float3(0,0,0)") + ";\n"
            + "    float3 partial=changed;\n"
            + "    if(PaletteEmission.x!=0) {\n"
            + "        float blue=PaletteEmission.w>0?PaletteEmissionMap.SampleLevel(MatcapSampler,uv,0).b:PaletteEmission.y;\n"
            + "        partial+=(initial*(2*blue))*PaletteEmission.x;\n    }\n"
            + "    float influence=saturate(PaletteLight.w);\n"
            + "    if(influence>0) {\n"
            + "        float3 light=PaletteModes.x==1?PaletteReceivedLightMap.SampleLevel(MatcapSampler,uv,0).rgb:PaletteLight.rgb;\n"
            + "        if(PaletteModes.x==2) light=0;\n"
            + "        partial+=influence*light;\n    }\n"
            + "    float3 litRim=EndfieldMatcapOverlay(partial,tint,saturate(r));\n"
            + "    float wave=EndfieldPaletteWave(uv.x,PaletteEmission.z);\n"
            + "    float rangeStrength=EndfieldMatcapRangeStrength(k,rangeQ);\n"
            + "    float3 emission=lerp(litRim,EndfieldPaletteHsv(litRim),saturate(wave))*(rangeStrength+r+p);\n"
            + "    if(PaletteModes.x==2 && influence>0) {\n"
            + "        float3 normal=EndfieldSharedNormal(geometric,perturbed,1);\n"
            + "        float3 livePartial=partial+influence*max(partial,0)*EndfieldLiveIncident(v,normal,geometric);\n"
            + "        float3 liveRim=EndfieldMatcapOverlay(livePartial,tint,saturate(r));\n"
            + "        float3 candidate=lerp(liveRim,EndfieldPaletteHsv(liveRim),saturate(wave))*(rangeStrength+r+p);\n"
            + "        float3 addition=candidate-emission;\n"
            + "        emission+=addition*EndfieldLiveScale(EndfieldLiveAvailableBudget(v,primitive),max(addition,0));\n    }\n"
            // The base wrappers already added these actual independent increments.
            // Their sum is not generally Overlay(Cpre)*(B+r+p).
            + "    float3 oldHair=initial*p;\n"
            + "    float3 oldRim=r==0?float3(0,0,0):EndfieldMatcapOverlay(initial,tint,saturate(r))*(MatcapBaseBaseline.w+r)-initial*MatcapBaseBaseline.w;\n"
            + "    return emission-initial*MatcapBaseBaseline.w-oldHair-oldRim;\n}\n";
        return source[..start] + PaletteHelper + "\n" + EndfieldLiveLightHelper(native,gi,morph,sub)
            + "\n" + compose + source[end..];
    }

    private static string EndfieldLiveLightHelper(bool native,bool gi,bool morph,bool sub)
    {
        // Reuse the generated, post-morph lit endpoint expression. It excludes add
        // sphere/specular and follows the existing UV1/SubTexture multiply policy.
        string budgetSource=EndfieldSurfaceSource(true,false,morph,sub);
        int start=budgetSource.IndexOf("precise float3 diffuseLit=",StringComparison.Ordinal);
        int end=start<0?-1:budgetSource.IndexOf("// ReferenceRadiance is fixed at 1",start,StringComparison.Ordinal);
        if(start<0 || end<0) throw new InvalidOperationException("Endfield live endpoint anchor missing.");
        string endpoint=budgetSource[start..end];
        string primary=native?"":GenshinNativePaletteHelper.Replace("Genshin","Endfield",StringComparison.Ordinal);
        string result=primary+"\n"+EndfieldLivePrimaryHelper+"\n"
            + "float3 EndfieldLiveIncident(SurfaceOutput v,float3 normal,float3 geometric) {\n"
            + "    float3 result=max(PaletteModes.yzw,0)*saturate(dot(normal,IncomingLight.xyz))*EndfieldLivePrimaryVisibility(v);\n"
            + (native?EndfieldLiveNativeLoop:"")+"    return result;\n}\n"
            + "float3 EndfieldLiveAvailableBudget(SurfaceOutput v,uint primitive) {\n"+endpoint
            + "    float3 budget=max(diffuseLit*max(1-EndfieldPaletteFactor(EndfieldPrimaryBlend(v)),0),0);\n"
            + "    float3 direct=0,indirect=0;\n"
            + (native?"    direct=NativeDirect(v);\n":"")
            + (gi?"""
                    int3 pixel=int3((int2)v.Position.xy,0);
                    float4 receiver=SurfaceGiNormal.Load(pixel);
                    if(receiver.w==(float)(GiTriangleBase+primitive+1) && SurfaceGiPosition.Load(pixel).w==v.Position.z) {
                        float4 transport=SurfaceIndirect[pixel.y*GiSurfaceWidth+pixel.x];
                        if(GiAmbientShare.w>=1) GiToonPaletteDelta(v,transport.rgb,indirect);
                        else indirect=transport.rgb*GiSurfaceStrength;
                    }
                """:"")
            + (native?"    float scale="+(gi?"GiAmbientShare.w>=1?StylizedAddedLightScale(budget,direct,indirect):StylizedAddedLightScale(budget,direct,0)":"StylizedAddedLightScale(budget,direct,0)")+";\n    direct*=scale;\n"+(gi?"    if(GiAmbientShare.w>=1) indirect*=scale;\n":""):"")
            + "    return max(budget-max(direct+indirect,0),0);\n}\n";
        return result;
    }

    private const string EndfieldLivePrimaryHelper="""
        float EndfieldLivePrimaryVisibility(SurfaceOutput v) {
            if(ShadowOptions.z<=0) return 1;
            float3 q=v.LightClip.xyz/v.LightClip.w;
            float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
            if(any(uv<0)||any(uv>1)) return 1;
            precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
            precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
            if(ShadowOptions.y>0) weighted*=uv.y;
            return 1-saturate(weighted-.3);
        }
        float EndfieldLiveScale(float3 budget,float3 addition) {
            if(all(addition<=budget)) return 1;
            float scale=1;
            if(addition.x>0) scale=min(scale,budget.x/addition.x);
            if(addition.y>0) scale=min(scale,budget.y/addition.y);
            if(addition.z>0) scale=min(scale,budget.z/addition.z);
            return saturate(scale);
        }
        """;

    private const string EndfieldLiveNativeLoop="""
            if(NativeAtlasLayout.z>0) result+=NativeAtlasLayout.z.xxx;
            [loop] for(uint i=0;i<(uint)NativeInfo.x;i++) {
                if((NativeInfo.y & (1u<<i))==0) continue;
                NativeLight source=NativeLights[i];
                [loop] for(uint sample=0;sample<(source.PositionKind.w==3?4u:1u);sample++) {
                NativeLight light=NativeSample(source,sample);
                float3 delta=light.PositionKind.xyz-v.WorldPosition;
                float distanceSquared=max(dot(delta,delta),.0001);
                float3 l=light.PositionKind.w==0?-light.DirectionRange.xyz:delta*rsqrt(distanceSquared);
                float attenuation=1;
                if(light.PositionKind.w!=0) {
                    float ratioSquared=distanceSquared/(light.DirectionRange.w*light.DirectionRange.w);
                    attenuation=saturate(1-ratioSquared*ratioSquared)/distanceSquared;
                }
                if(light.PositionKind.w==2) {
                    float angular=saturate((dot(-l,light.DirectionRange.xyz)-light.ConeShadow.y)/max(light.ConeShadow.x-light.ConeShadow.y,.00001));
                    attenuation*=angular*angular;
                }
                if(light.PositionKind.w==3) attenuation*=saturate(dot(-l,light.DirectionRange.xyz));
                // Bias uses the evaluated geometric normal. Mapped normal only
                // affects irradiance, never the atlas's surface-position offset.
                float visibility=NativeVisibility(i,light,v.WorldPosition,geometric,l,0,0,sample);
                result+=max(light.ColorIntensity.rgb,0)*(light.ColorIntensity.w*attenuation*saturate(dot(normal,l))*visibility);
                }
            }
        """;

    private const string PaletteHelper = """
        cbuffer EndfieldPaletteConstants:register(b13) {
            float4 PaletteEmission; // strength, uniform numerical Blue, density, emission input mode
            float4 PaletteLight; // authored linear received-light RGB, influence
            float4 PaletteModes; // received-light input mode, renderer primary RGB
        };
        Texture2D<float4> PaletteEmissionMap:register(t22);
        Texture2D<float4> PaletteReceivedLightMap:register(t23);
        float EndfieldPaletteWave(float u,float density) {
            precise float scale=100*density;
            precise float x=(u*scale+.000001)*.999999;
            return .5+.5*sin(20*x-1.5707963267948966);
        }
        float3 EndfieldPaletteHsv(float3 rgb) {
            // Explicit Blender 3.6.23 HSV semantics, independently expressed.
            // Signed channels can exceed saturation one before its node clamp.
            float hi=max(rgb.r,max(rgb.g,rgb.b)),lo=min(rgb.r,min(rgb.g,rgb.b));
            float chroma=hi-lo;
            float saturation=hi==0?0:chroma/hi;
            float hue=0;
            if(saturation!=0) {
                if(rgb.r==hi) hue=(rgb.g-rgb.b)/chroma;
                else if(rgb.g==hi) hue=2+(rgb.b-rgb.r)/chroma;
                else hue=4+(rgb.r-rgb.g)/chroma;
                hue/=6;
                if(hue<0) hue+=1;
            }
            hue=frac(hue+1);
            saturation=saturate(saturation);
            float value=hi*.7999999523162842;
            if(saturation==0) return value.xxx;
            float sector=hue*6,whole=floor(sector),fraction=sector-whole;
            float low=value*(1-saturation);
            float falling=value*(1-saturation*fraction);
            float rising=value*(1-saturation*(1-fraction));
            if(whole==0) return float3(value,rising,low);
            if(whole==1) return float3(falling,value,low);
            if(whole==2) return float3(low,value,rising);
            if(whole==3) return float3(low,falling,value);
            if(whole==4) return float3(rising,low,value);
            return float3(value,low,falling);
        }
        """;

    internal static byte[] CompileEndfieldPaletteSurface(string entry, bool native, bool gi,
        bool morph, bool sub, bool hair = false, bool rim = false)
        => Compiler.Compile(EndfieldPaletteSurfaceSource(entry, native, gi, morph, sub, hair, rim), entry,
            "OriginalEndfieldPaletteAdapter.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray();
}
