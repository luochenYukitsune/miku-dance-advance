using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

// Explicit-row, nonmetallic source subset. The MDA real-shadow palette adapter
// is independent of the source fork's custom ShaderInfo output.
internal static partial class OriginalFrameShaders
{
    internal static string GenshinHairAtlasSurfaceSource(bool native, bool gi, bool morph, bool sub, bool rbLobe)
    {
        string source = rbLobe ? GenshinHairSurfaceSource(native, gi, morph, sub)
            : GenshinSurfaceSource(native, gi, morph, sub);
        const string AtlasHelper = """
            // Independently authored helper; no third-party shader code or assets bundled.
            // Insert AFTER SurfaceOutput definition and BEFORE VSSurface.
            cbuffer GenshinAtlasConstants : register(b9) {
                float4 GenshinAtlasLayout0;
                float4 GenshinAtlasLayout1;
                float4 GenshinAtlasLayout2;
            };
            Texture2D<float4> GenshinAtlasLightmap : register(t13);
            Texture2D<float4> GenshinAtlasColor : register(t14);
            SamplerState GenshinAtlasLinearRepeat : register(s6);
            
            float4 GenshinAtlasWeights(float t) {
                float t2=t*t, t3=t2*t, u=1-t;
                return float4(u*u*u,3*t3-6*t2+4,-3*t3+3*t2+3*t+1,t3)/6;
            }
            float4 GenshinAtlasCubicLightmap(float2 uv) {
                uint width,height; GenshinAtlasLightmap.GetDimensions(width,height);
                float2 size=float2(width,height), position=uv*size-.5;
                float2 cell=floor(position), fraction=position-cell;
                float4 wx=GenshinAtlasWeights(fraction.x), wy=GenshinAtlasWeights(fraction.y);
                float2 first=float2(wx.x+wx.y,wy.x+wy.y), second=float2(wx.z+wx.w,wy.z+wy.w);
                float2 low=(cell-.5+float2(wx.y,wy.y)/first)/size;
                float2 high=(cell+1.5+float2(wx.w,wy.w)/second)/size;
                float4 a=GenshinAtlasLightmap.SampleLevel(GenshinAtlasLinearRepeat,low,0);
                float4 b=GenshinAtlasLightmap.SampleLevel(GenshinAtlasLinearRepeat,float2(high.x,low.y),0);
                float4 c=GenshinAtlasLightmap.SampleLevel(GenshinAtlasLinearRepeat,float2(low.x,high.y),0);
                float4 d=GenshinAtlasLightmap.SampleLevel(GenshinAtlasLinearRepeat,high,0);
                return a*first.x*first.y+b*second.x*first.y+c*first.x*second.y+d*second.x*second.y;
            }
            float GenshinAtlasSdiv(float a,float b) { return b==0?0:a/b; }
            float3 GenshinAtlasDecode(float3 rgb) {
                if(GenshinAtlasLayout2.x==0) return rgb;
                float3 low=rgb/12.92, high=pow((rgb+.055)/1.055,2.4);
                return float3(rgb.x<=.04045?low.x:high.x,rgb.y<=.04045?low.y:high.y,rgb.z<=.04045?low.z:high.z);
            }
            float3 GenshinAtlasClosestExtend(float2 uv) {
                uint width,height; GenshinAtlasColor.GetDimensions(width,height);
                // Clamp before int conversion; q may overflow to +/-inf for tiny nonzero widths.
                // Host scalars are finite, and explicit source zero divisor remains exactly zero.
                int2 pixel=min((int2)floor(saturate(uv)*float2(width,height)),int2(width-1,height-1));
                return GenshinAtlasDecode(GenshinAtlasColor.Load(int3(pixel,0)).rgb);
            }
            float3 GenshinAtlasPalette(SurfaceOutput v,float nDotL,float visibility) {
                float4 lm=GenshinAtlasCubicLightmap(v.Uv);
                float ao=lerp(.5,lm.g,GenshinAtlasLayout1.z)*lerp(1,GenshinAtlasLayout1.y,GenshinAtlasLayout1.w);
                float t=saturate((ao-.01)/(.4-.01));
                float light=(t*t*(3-2*t))*(.5*nDotL+.5);
                float width=(GenshinAtlasLayout1.x*2)*GenshinAtlasLayout0.w;
                float q=1-GenshinAtlasSdiv(GenshinAtlasSdiv(GenshinAtlasLayout0.z-light,GenshinAtlasLayout0.z),width);
                float nightY=((6-GenshinAtlasLayout0.x)-1)*.1+.05;
                float gate=saturate((light<GenshinAtlasLayout0.z?1:0)-(lm.g>.95?1:0));
                float3 day=GenshinAtlasClosestExtend(float2(q,nightY+.5));
                float3 night=GenshinAtlasClosestExtend(float2(q,nightY));
                float3 palette=lerp(float3(1,1,1),lerp(day,night,GenshinAtlasLayout0.y),gate);
                // Explicit MDA adapter: independently preserve real shadow geometry using PMX
                // authored dark color. No old normal palette is multiplied into the atlas.
                return lerp(ToonShadowColor.rgb,palette,visibility);
            }
            float3 GenshinAtlasPrimaryPalette(SurfaceOutput v) {
                float3 q=v.LightClip.xyz/v.LightClip.w;
                float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
                float visibility=1;
                if(uv.x>=0&&uv.x<=1&&uv.y>=0&&uv.y<=1) {
                    precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
                    precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
                    if(ShadowOptions.y>0)weighted=weighted*uv.y;
                    visibility=1-saturate(weighted-.3);
                }
                return GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility);
            }
            """;
        const string helperAnchor = "SurfaceOutput VSSurface(Input v) {";
        source = AtlasOnce(source, helperAnchor, AtlasHelper + "\n" + helperAnchor);
        // Replace each diffuse branch before the independent authored specular term.
        // Keeping the original scalar c and originalC preserves SubTexture alpha and
        // original/RB-highlight shadow attenuation. No coverage expression is replaced.
        source = AtlasOnce(source,
            "if(Options.z>0) color=baseColor*(c+ToonShadowColor.rgb*(1-c))+highlight*originalC;",
            "if(Options.z>0) color=baseColor*GenshinAtlasPalette(v,dot(n,l),visibility)+highlight*originalC;");
        source = AtlasOnce(source,
            "else color=baseColor*c+baseColor*ToonShadowColor.rgb*(1-c)+highlight*originalC;",
            "else color=baseColor*GenshinAtlasPalette(v,dot(n,l),visibility)+highlight*originalC;");
        source = AtlasOnce(source,
            "return float4(baseColor*GenshinPaletteFactor(GenshinLightResponse(dot(n,l),1))+highlight,alpha);",
            "return float4(baseColor*GenshinAtlasPalette(v,dot(n,l),1)+highlight,alpha);");
        if (native || gi)
        {
            source = AtlasOnce(source,
                "return fill*GenshinPaletteFactor(GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),1));",
                "return fill*GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),1);");
            source = AtlasOnce(source,
                "if(Options.z>0) return fill*(c+ToonShadowColor.rgb*(1-c));",
                "if(Options.z>0) return fill*GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility);");
            source = AtlasOnce(source,
                "return fill*c+fill*ToonShadowColor.rgb*(1-c);",
                "return fill*GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility);");
            source = AtlasOnce(source, "if(c==1) return false;", "");
            source = AtlasOnce(source,
                "precise float3 toonFactor=c+ToonShadowColor.rgb*(1-c);",
                "precise float3 toonFactor=GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility);");
        }
        return source;
    }

    private static string AtlasOnce(string text, string anchor, string replacement)
    {
        if (AtlasCount(text, anchor) != 1)
            throw new InvalidOperationException("Atlas shader anchor must occur exactly once: " + anchor);
        return text.Replace(anchor, replacement, StringComparison.Ordinal);
    }
    private static int AtlasCount(string text, string anchor)
    {
        int at = 0, result = 0;
        while ((at = text.IndexOf(anchor, at, StringComparison.Ordinal)) >= 0) { result++; at += anchor.Length; }
        return result;
    }

    internal static byte[] CompileGenshinHairAtlasSurface(string entry, bool native, bool gi, bool morph, bool sub, bool rbLobe)
        => Compiler.Compile(GenshinHairAtlasSurfaceSource(native, gi, morph, sub, rbLobe), entry,
            "OriginalGenshinHairShadowAtlas.hlsl", "ps_5_1",
            ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray();
}
