using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored source-local R-gated MatCap palette. Complete metallic
    // specular/ShaderInfo/normal maps remain separate; no reference graph/code/pixels bundled.
    internal static string GenshinMetallicSurfaceSource(bool native, bool gi, bool morph, bool sub, bool rb, bool atlas)
    {
        string source = atlas ? GenshinHairAtlasSurfaceSource(native, gi, morph, sub, rb)
            : rb ? GenshinHairSurfaceSource(native, gi, morph, sub) : GenshinSurfaceSource(native, gi, morph, sub);
        const string helper = """
            cbuffer GenshinMetallicConstants : register(b11) {
                float4 GenshinMetallicParameters; // enable, tileX, brightness, lit mode
                float4 GenshinMetallicDarkLit; // source numeric Dark RGB, uniform lit factor
                float4 GenshinMetallicLight;
                float4 GenshinMetallicShadow;
                float4 GenshinMetallicCameraUp;
                float4 GenshinMetallicCameraBackward; // normalized eye-target; W=orthographic
            };
            Texture2D<float4> GenshinMetallicLightmap : register(t19);
            // RGB resource is already linearized BEFORE bilinear/cubic filtering.
            // CHANNEL_PACKED Alpha is deliberately unconsumed and never affects coverage.
            Texture2D<float4> GenshinMetallicMatcap : register(t20);
            SamplerState GenshinMetallicLinearRepeat : register(s8);
            float4 GenshinMetallicWeights(float t) {
                float t2=t*t,t3=t2*t,u=1-t;
                return float4(u*u*u,3*t3-6*t2+4,-3*t3+3*t2+3*t+1,t3)/6;
            }
            CUBIC_FUNCTIONS
            float GenshinMetallicRaw(SurfaceOutput v) {
                if(GenshinMetallicParameters.x==0) return 0;
                return GenshinMetallicParameters.x*(GenshinMetallicCubicLightmap(v.Uv).r>.9?1:0);
            }
            float3 GenshinMetallicGamma(float3 color) {
                float3 positive=pow(max(color,0),2.2);
                return float3(color.x>0?positive.x:color.x,color.y>0?positive.y:color.y,color.z>0?positive.z:color.z);
            }
            float2 GenshinMetallicSourceUv(SurfaceOutput v) {
                float3 incoming=GenshinMetallicCameraBackward.w>0?GenshinMetallicCameraBackward.xyz:normalize(EyePosition.xyz-v.WorldPosition);
                float3 negativeUp=-GenshinMetallicCameraUp.xyz;
                // Source cross order and lengths matter. Parallel Incoming/Up collapses to center.
                float3 tangent=cross(incoming,negativeUp);
                float3 bitangent=cross(incoming,tangent);
                // Explicit MDA GeometryNormal input: existing normalized current world normal.
                // No extra front-face flip, rest/head stream or normal-map reconstruction.
                float3 normal=normalize(v.Normal);
                return float2(.5+.5*GenshinMetallicParameters.y*dot(normal,tangent),.5+.5*dot(normal,bitangent));
            }
            float3 GenshinMetallicPaletteRaw(SurfaceOutput v,float3 nonmetal,float primaryLit,float raw) {
                // Preserve accepted no-metal arithmetic and skip all color-image work.
                if(raw<=0) return nonmetal;
                float2 sourceUv=GenshinMetallicSourceUv(v);
                float3 image=GenshinMetallicCubicMatcap(float2(sourceUv.x,1-sourceUv.y)).rgb;
                float q=saturate(dot(image*GenshinMetallicParameters.z,float3(.2126,.7152,.0722)));
                float3 palette=lerp(GenshinMetallicGamma(GenshinMetallicDarkLit.rgb),GenshinMetallicGamma(GenshinMetallicLight.rgb),q);
                float lit=GenshinMetallicParameters.w==1?GenshinMetallicDarkLit.w:primaryLit;
                float shadow=saturate(lit-.8);
                float3 tint=palette*lerp(float3(1,1,1),GenshinMetallicGamma(GenshinMetallicShadow.rgb),shadow);
                return lerp(float3(1,1,1),nonmetal,saturate(1-raw))*lerp(float3(1,1,1),tint,saturate(raw));
            }
            float3 GenshinMetallicPalette(SurfaceOutput v,float3 nonmetal,float primaryLit) {
                return GenshinMetallicPaletteRaw(v,nonmetal,primaryLit,GenshinMetallicRaw(v));
            }
            float3 GenshinMetallicOrdinaryDiffuse(SurfaceOutput v,float3 baseColor,float c) {
                float raw=GenshinMetallicRaw(v);
                if(raw<=0) return baseColor*c+baseColor*ToonShadowColor.rgb*(1-c);
                return baseColor*GenshinMetallicPaletteRaw(v,GenshinPaletteFactor(c),c,raw);
            }
            float GenshinMetallicPrimaryLit(SurfaceOutput v) {
                float3 q=v.LightClip.xyz/v.LightClip.w;
                float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
                float visibility=1;
                if(uv.x>=0&&uv.x<=1&&uv.y>=0&&uv.y<=1) {
                    precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
                    precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
                    if(ShadowOptions.y>0) weighted=weighted*uv.y;
                    visibility=1-saturate(weighted-.3);
                }
                return GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),visibility);
            }
            """;
        const string cubic = """
            float4 GenshinMetallicCubicNAME(float2 uv) {
                uint width,height; IMAGE.GetDimensions(width,height);
                float2 size=float2(width,height),position=uv*size-.5;
                float2 cell=floor(position),fraction=position-cell;
                float4 wx=GenshinMetallicWeights(fraction.x),wy=GenshinMetallicWeights(fraction.y);
                float2 first=float2(wx.x+wx.y,wy.x+wy.y),second=float2(wx.z+wx.w,wy.z+wy.w);
                float2 low=(cell-.5+float2(wx.y,wy.y)/first)/size;
                float2 high=(cell+1.5+float2(wx.w,wy.w)/second)/size;
                float4 a=IMAGE.SampleLevel(GenshinMetallicLinearRepeat,low,0);
                float4 b=IMAGE.SampleLevel(GenshinMetallicLinearRepeat,float2(high.x,low.y),0);
                float4 c=IMAGE.SampleLevel(GenshinMetallicLinearRepeat,float2(low.x,high.y),0);
                float4 d=IMAGE.SampleLevel(GenshinMetallicLinearRepeat,high,0);
                return a*first.x*first.y+b*second.x*first.y+c*first.x*second.y+d*second.x*second.y;
            }
            """;
        string cubics = cubic.Replace("NAME", "Lightmap", StringComparison.Ordinal).Replace("IMAGE", "GenshinMetallicLightmap", StringComparison.Ordinal)
            + "\n" + cubic.Replace("NAME", "Matcap", StringComparison.Ordinal).Replace("IMAGE", "GenshinMetallicMatcap", StringComparison.Ordinal);
        string generated = helper.Replace("CUBIC_FUNCTIONS", cubics, StringComparison.Ordinal);
        const string anchor = "SurfaceOutput VSSurface(Input v) {";
        source = MetallicOnce(source, anchor, generated + "\n" + anchor);

        if (atlas)
        {
            // Atlas controls the nonmetal endpoint only. Both source Mix factors use raw R gate.
            source = MetallicReplace(source, "baseColor*GenshinAtlasPalette(v,dot(n,l),visibility)",
                "baseColor*GenshinMetallicPalette(v,GenshinAtlasPalette(v,dot(n,l),visibility),c)", 2);
            source = MetallicOnce(source, "baseColor*GenshinAtlasPalette(v,dot(n,l),1)",
                "baseColor*GenshinMetallicPalette(v,GenshinAtlasPalette(v,dot(n,l),1),GenshinLightResponse(dot(n,l),1))");
        }
        else
        {
            source = MetallicOnce(source, "if(Options.z>0) color=baseColor*(c+ToonShadowColor.rgb*(1-c))+highlight*originalC;",
                "if(Options.z>0) color=baseColor*GenshinMetallicPalette(v,GenshinPaletteFactor(c),c)+highlight*originalC;");
            source = MetallicOnce(source, "else color=baseColor*c+baseColor*ToonShadowColor.rgb*(1-c)+highlight*originalC;",
                "else color=GenshinMetallicOrdinaryDiffuse(v,baseColor,c)+highlight*originalC;");
            source = MetallicOnce(source, "baseColor*GenshinPaletteFactor(GenshinLightResponse(dot(n,l),1))",
                "baseColor*GenshinMetallicPalette(v,GenshinPaletteFactor(GenshinLightResponse(dot(n,l),1)),GenshinLightResponse(dot(n,l),1))");
        }
        if (rb)
            source = MetallicOnce(source, "float3 highlight=GenshinHairHighlight(v,n,h);",
                "float3 highlight=GenshinHairHighlight(v,n,h)*saturate(1-GenshinMetallicRaw(v));");
        if (native || gi)
        {
            if (atlas)
            {
                source = MetallicOnce(source, "return fill*GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),1);",
                    "return fill*GenshinMetallicPalette(v,GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),1),GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),1));");
                source = MetallicReplace(source, "return fill*GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility);",
                    "return fill*GenshinMetallicPalette(v,GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility),c);", 2);
                source = MetallicOnce(source, "precise float3 toonFactor=GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility);",
                    "precise float3 toonFactor=GenshinMetallicPalette(v,GenshinAtlasPalette(v,dot(normalize(v.Normal),IncomingLight.xyz),visibility),c);");
            }
            else
            {
                source = MetallicOnce(source, "return fill*GenshinPaletteFactor(GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),1));",
                    "return fill*GenshinMetallicPalette(v,GenshinPaletteFactor(GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),1)),GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),1));");
                source = MetallicOnce(source, "if(Options.z>0) return fill*(c+ToonShadowColor.rgb*(1-c));",
                    "if(Options.z>0) return fill*GenshinMetallicPalette(v,GenshinPaletteFactor(c),c);");
                source = MetallicOnce(source, "return fill*c+fill*ToonShadowColor.rgb*(1-c);",
                    "return GenshinMetallicOrdinaryDiffuse(v,fill,c);");
                source = MetallicOnce(source, "if(c==1) return false;", "");
                source = MetallicOnce(source, "precise float3 toonFactor=c+ToonShadowColor.rgb*(1-c);",
                    "precise float3 toonFactor=GenshinMetallicPalette(v,GenshinPaletteFactor(c),c);");
            }
        }
        if (native)
        {
            string old = atlas ? "GenshinAtlasPrimaryPalette(v)" : "GenshinPaletteFactor(GenshinPrimaryBlend(v))";
            source = MetallicReplace(source, old, "GenshinMetallicPalette(v," + old + ",GenshinMetallicPrimaryLit(v))", 2);
        }
        return source;
    }

    private static string MetallicOnce(string source, string anchor, string replacement)
        => MetallicReplace(source, anchor, replacement, 1);

    private static string MetallicReplace(string source, string anchor, string replacement, int expected)
    {
        int count = 0, at = 0;
        while ((at = source.IndexOf(anchor, at, StringComparison.Ordinal)) >= 0) { count++; at += anchor.Length; }
        if (count != expected) throw new InvalidOperationException($"Genshin metallic shader anchor expected {expected}, found {count}: {anchor}");
        return source.Replace(anchor, replacement, StringComparison.Ordinal);
    }

    internal static byte[] CompileGenshinMetallicSurface(string entry, bool native, bool gi, bool morph, bool sub, bool rb, bool atlas)
        => Compiler.Compile(GenshinMetallicSurfaceSource(native, gi, morph, sub, rb, atlas), entry,
            "OriginalGenshinMetallic.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray();
}
