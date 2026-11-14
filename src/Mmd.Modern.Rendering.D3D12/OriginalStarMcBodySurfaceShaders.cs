using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independent bounded Body adaptation, informed by the audited connected source
    // branches. Shares coverage, evaluated tangents and remaining-light budgeting;
    // neither third-party node data nor original MatCap images are distributed.
    internal static string StarMcBodySurfaceSource(D3D12SurfaceStyle family, string entry,
        bool native, bool gi, bool morph, bool sub)
    {
        if (entry is not ("PSSurface" or "PSSurfaceNative" or "PSSurfaceGi" or "PSSurfaceGiNative"))
            throw new ArgumentOutOfRangeException(nameof(entry));
        bool mc = family == D3D12SurfaceStyle.WutheringPmx;
        string prefix = family switch
        {
            D3D12SurfaceStyle.WutheringPmx => "Wuthering",
            D3D12SurfaceStyle.StarRailPmx => "StarRail",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        string source = (mc ? WutheringSurfaceSource(native, gi, morph, sub)
            : StarRailSurfaceSource(native, gi, morph, sub)).ReplaceLineEndings("\n");
        // All downstream primary/native/GI functions receive one mapped shading normal
        // and share private per-invocation scratch values initialized by the entry.
        // SurfaceOutput remains byte-for-byte the existing VS/PS semantic interface.
        string oldHelper = mc ? WutheringSurfaceHelper : StarRailSurfaceHelper;
        source = ReplaceBodyRequired(source, oldHelper.ReplaceLineEndings("\n"), "");
        source = AddBodyArgument(source, prefix + "LightResponse");
        source = AddBodyArgument(source, prefix + "PaletteFactor");
        source = source.Replace(prefix + "LightResponse", "StarMcBodyLightResponse", StringComparison.Ordinal)
            .Replace(prefix + "PaletteFactor", "StarMcBodyPaletteFactor", StringComparison.Ordinal);
        source = source.Replace("baseColor*c+baseColor*ToonShadowColor.rgb*(1-c)", "baseColor*StarMcBodyPaletteFactor(c,v)", StringComparison.Ordinal)
            .Replace("fill*c+fill*ToonShadowColor.rgb*(1-c)", "fill*StarMcBodyPaletteFactor(c,v)", StringComparison.Ordinal)
            .Replace("c+ToonShadowColor.rgb*(1-c)", "StarMcBodyPaletteFactor(c,v)", StringComparison.Ordinal);
        foreach (string assignment in new[] { "precise float3 baseColor=", "precise float3 diffuseLit=", "float3 diffuse=" })
            source = TreatBodyAssignments(source, assignment);
        // AO fill must use the same spatial detail as the visible endpoint, without
        // applying detail to the independently allocated ambient-share vector.
        const string fillAnchor = "precise float3 fill=EgColor.rgb*GiAmbientShare.rgb*";
        int fill = source.IndexOf(fillAnchor, StringComparison.Ordinal);
        if (fill >= 0)
        {
            int end = source.IndexOf(';', fill);
            string texel = source[(fill + fillAnchor.Length)..end];
            source = source.Insert(end + 1, " fill=StarMcBodyAlbedo(EgColor.rgb*" + texel + ",v)*GiAmbientShare.rgb;");
        }
        // Capture the already generated morph/SubTexture/sphere expression. The
        // generated Body path is never a second hand-maintained texture decoder.
        int surface = source.IndexOf("float4 PSSurface(SurfaceOutput v):SV_Target", StringComparison.Ordinal);
        int open = source.IndexOf('{', surface);
        int normal = source.IndexOf("float3 n=normalize(v.Normal);", open, StringComparison.Ordinal);
        if (surface < 0 || normal < 0) throw new InvalidOperationException("Body diffuse endpoint is missing.");
        string albedo = "float3 StarMcBodySourceAlbedo(SurfaceOutput v) {\n" + source[(open + 1)..normal] + " return baseColor;\n}\n";
        string helpers = MatcapNormalHelper + "\n" + StarMcBodyHelper
            .Replace("BODY_MC", mc ? "1" : "0", StringComparison.Ordinal)
            .Replace("BODY_VALUE", mc ? "1.5" : "2.0", StringComparison.Ordinal);
        // The light-response helpers are needed before every native/GI function.
        const string insert = "SurfaceOutput VSSurface(Input v) {";
        source = ReplaceBodyRequired(source, insert, helpers + "\n" + insert);
        source = ReplaceBodyRequired(source, "float4 PSSurface(SurfaceOutput v):SV_Target", albedo + "\nfloat4 PSSurface(SurfaceOutput v):SV_Target");
        source = source.Replace(entry + "(", entry + "StarMcBodyBase(", StringComparison.Ordinal);
        string primitive = gi ? ",primitive" : "";
        source = source.Replace("v.BodyUv", "StarMcBodyUv", StringComparison.Ordinal)
            .Replace("v.BodyAlbedo", "StarMcBodyAlbedoValue", StringComparison.Ordinal)
            .Replace("v.BodyGeometric", "StarMcBodyGeometric", StringComparison.Ordinal);
        return (source + $"\nfloat4 {entry}(SurfaceOutput v,uint primitive:SV_PrimitiveID,bool front:SV_IsFrontFace):SV_Target {{\n"
            + "float3 mapped,geometric,perturbed; float2 uv; v.BodyGeometric=normalize(v.Normal)*(front?1:-1); v.BodyUv=v.Uv;\n"
            + "if(EndfieldMatcapMappedNormal(v,primitive,front,mapped,uv,geometric,perturbed)) { v.BodyUv=uv; v.Normal=mapped; }\n"
            + "v.BodyAlbedo=StarMcBodySourceAlbedo(v);\n"
            + $"float4 color={entry}StarMcBodyBase(v{primitive});\n"
            + "color.rgb+=StarMcBodyMatcapDelta(v); color.rgb=StarMcBodyRim(v,color.rgb); return color;\n}\n")
            .Replace("v.BodyUv", "StarMcBodyUv", StringComparison.Ordinal)
            .Replace("v.BodyAlbedo", "StarMcBodyAlbedoValue", StringComparison.Ordinal)
            .Replace("v.BodyGeometric", "StarMcBodyGeometric", StringComparison.Ordinal);
    }

    private static string ReplaceBodyRequired(string source, string anchor, string replacement)
    {
        if (!source.Contains(anchor, StringComparison.Ordinal)) throw new InvalidOperationException("Star/MC Body shader anchor missing: " + anchor);
        return source.Replace(anchor, replacement, StringComparison.Ordinal);
    }
    private static string AddBodyArgument(string source, string function)
    {
        int at = source.IndexOf(function + "(", StringComparison.Ordinal);
        while (at >= 0)
        {
            int cursor = at + function.Length + 1, depth = 1;
            for (; cursor < source.Length && depth > 0; cursor++)
            { if (source[cursor] == '(') depth++; else if (source[cursor] == ')') depth--; }
            if (depth != 0) throw new InvalidOperationException("Body function call incomplete.");
            source = source.Insert(cursor - 1, ",v");
            at = source.IndexOf(function + "(", cursor + 2, StringComparison.Ordinal);
        }
        return source;
    }
    private static string TreatBodyAssignments(string source, string assignment)
    {
        int at = source.IndexOf(assignment, StringComparison.Ordinal);
        while (at >= 0)
        {
            int start = at + assignment.Length, end = source.IndexOf(';', start);
            string expression = source[start..end];
            string replacement = "StarMcBodyAlbedo(" + expression + ",v)";
            source = source[..start] + replacement + source[end..];
            at = source.IndexOf(assignment, start + replacement.Length + 1, StringComparison.Ordinal);
        }
        return source;
    }

    private const string StarMcBodyHelper = """
        // Static globals are private pixel invocation scratch, never resources or
        // interpolants. Every live read follows the final entry's initialization.
        static float2 StarMcBodyUv=0;
        static float3 StarMcBodyAlbedoValue=0;
        static float3 StarMcBodyGeometric=0;
        cbuffer StarMcBodyConstants:register(b15) {
            float4 BodyDetailMetalHighlight; // detail, saturation, coefficient, strength
            float4 BodyShadowWarm; // coefficient, softness, color strength, warm strength
            float4 BodyShadowColor;
            float4 BodyRimColorStrength;
            float4 BodyRimShape; // size, direction, orthographic, unused
            float4 BodyCameraBackward;
        };
        Texture2D<float4> BodyDetailMap:register(t28);
        float3 StarMcBodyAlbedo(float3 c,SurfaceOutput v) {
            if(BODY_MC==0 || BodyDetailMetalHighlight.x==0) return c;
            float g=BodyDetailMap.SampleLevel(MatcapSampler,v.BodyUv,0).g;
            return c+(.5*BodyDetailMetalHighlight.x*(2*g-1))*c*(1-c);
        }
        float StarMcBodyLightResponse(float nDotL,float visibility,SurfaceOutput v) {
            float r=MatcapPMap.SampleLevel(MatcapSampler,v.BodyUv,0).r;
            float edge=1-BodyShadowWarm.x*r;
            float q=.05+.95*saturate((.5+.5*nDotL-(edge-BodyShadowWarm.y))/BodyShadowWarm.y);
            float band=BODY_MC==1?q:saturate(q*3.333333969116211-2.000000476837158);
            return band*visibility;
        }
        float3 StarMcBodyPaletteFactor(float blend,SurfaceOutput v) {
            float3 shadow=BodyShadowColor.rgb;
            if(BODY_MC==0) {
                float3 c=max(v.BodyAlbedo,0);
                float value=saturate(max(c.x,max(c.y,c.z)));
                float3 squared=lerp(c,c*c,saturate(value+.5));
                float donorValue=max(squared.x,max(squared.y,squared.z));
                // Independent Color-blend adaptation: grayscale donor recomposes
                // hue/saturation of the grayscale donor while retaining HSV value.
                float3 recomposed=lerp(squared,donorValue.xxx,value*(1-value)+.5);
                float3 ratio=float3(c.x>0?recomposed.x/c.x:0,c.y>0?recomposed.y/c.y:0,c.z>0?recomposed.z/c.z:0);
                shadow*=lerp(1,saturate(ratio),.35);
            }
            float3 factor=lerp(shadow,1,blend);
            if(BODY_MC==0) {
                float t=saturate(blend);
                float3 bandA=t<.5?lerp(1,float3(1,.9,.75),t*2):lerp(float3(1,.9,.75),1,(t-.5)*2);
                float3 bandB=t<.803021?float3(.6,.6,.6):t<.9?lerp(.6,float3(1,.85318,.681425),(t-.803021)/(.9-.803021)):lerp(float3(1,.85318,.681425),1,(t-.9)/.1);
                factor*=lerp(1,bandA*bandB,BodyShadowWarm.w);
            }
            return lerp(1,saturate(factor),BodyShadowWarm.z);
        }
        float StarMcBodyVisibility(SurfaceOutput v) {
            float3 q=v.LightClip.xyz/v.LightClip.w; float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
            if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) return 1;
            precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
            precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
            if(ShadowOptions.y>0) weighted*=uv.y;
            return 1-saturate(weighted-.3);
        }
        float3 StarMcBodyMatcapDelta(SurfaceOutput v) {
            if(BodyDetailMetalHighlight.w==0) return 0;
            float3 n=normalize(v.Normal);
            float3 nc=float3(dot(n,MatcapCameraRight.xyz),dot(n,MatcapCameraUp.xyz),-dot(n,MatcapCameraForward.xyz));
            float sn,cs; sincos(MatcapRotationGate.x,sn,cs);
            float2 uv=float2(.5+.5*(cs*nc.x+sn*nc.z),.5-.5*nc.y);
            float3 cap=MatcapColorMap.SampleLevel(MatcapSampler,uv,0).rgb;
            float3 c=v.BodyAlbedo;
            float value=max(c.x,max(c.y,c.z)); float chroma=value-min(c.x,min(c.y,c.z));
            float scale=chroma>0?min(BodyDetailMetalHighlight.y,value/chroma):1;
            float3 metal=value+(c-value)*scale;
            float3 changed=lerp(metal,metal*BODY_VALUE,cap);
            float k=saturate(BodyDetailMetalHighlight.z+MatcapPMap.SampleLevel(MatcapSampler,v.BodyUv,0).b);
            float gate=saturate(StarMcBodyLightResponse(dot(n,IncomingLight.xyz),StarMcBodyVisibility(v),v)*k)*BodyDetailMetalHighlight.w;
            return (changed-c)*gate;
        }
        float3 StarMcBodyRim(SurfaceOutput v,float3 c) {
            if(BodyRimColorStrength.w==0) return c;
            float3 n=normalize(v.BodyGeometric);
            float3 eye=BodyRimShape.z>0?BodyCameraBackward.xyz:normalize(EyePosition.xyz-v.WorldPosition);
            float cosine=saturate(abs(dot(n,eye)));
            // Unpolarized dielectric Fresnel, IOR=2. Directional gradients stay
            // inside original coverage; this adds no outline or outside halo.
            float transmitted=sqrt(max(1-(1-cosine*cosine)*.25,0));
            float rs=(cosine-2*transmitted)/(cosine+2*transmitted);
            float rp=(2*cosine-transmitted)/(2*cosine+transmitted);
            float fresnel=.5*(rs*rs+rp*rp);
            float2 nc=float2(dot(n,MatcapCameraRight.xyz),dot(n,MatcapCameraUp.xyz));
            float sn,cs; sincos(BodyRimShape.y,sn,cs); nc=float2(cs*nc.x-sn*nc.y,sn*nc.x+cs*nc.y)*.5-.5;
            float gradient=BODY_MC==1?frac(atan2(nc.y,nc.x)/6.28318530718+.5):max(1-dot(nc,nc),0);
            float directional=saturate(gradient/.2);
            float r=smoothstep(.1,.2,(directional+BodyRimShape.x-1)*fresnel)*BodyRimColorStrength.w;
            return lerp(c,BodyRimColorStrength.rgb,saturate(r));
        }
        """;

    internal static byte[] CompileStarMcBodySurface(D3D12SurfaceStyle family, string entry, bool native, bool gi, bool morph, bool sub)
        => Compiler.Compile(StarMcBodySurfaceSource(family, entry, native, gi, morph, sub), entry,
            "OriginalStarMcBody.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray();
}
