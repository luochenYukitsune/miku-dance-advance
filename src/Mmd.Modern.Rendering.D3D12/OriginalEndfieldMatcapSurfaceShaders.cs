using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored equations for the bounded packed-normal/source-gate
    // MatCap palette adapter. No plugin graph, images or implementation are included.
    internal static string EndfieldMatcapSurfaceSource(string entry, bool native, bool gi,
        bool morph, bool sub, bool hair = false, bool rim = false)
    {
        if (entry is not ("PSSurface" or "PSSurfaceNative" or "PSSurfaceGi" or "PSSurfaceGiNative"))
            throw new ArgumentOutOfRangeException(nameof(entry));
        string source = (rim ? EndfieldRimSurfaceSource(entry, native, gi, morph, sub, hair)
            : hair ? EndfieldHairSurfaceSource(native, gi, morph, sub)
            : EndfieldSurfaceSource(native, gi, morph, sub)).ReplaceLineEndings("\n");
        if (!source.Contains($"float4 {entry}(SurfaceOutput v", StringComparison.Ordinal))
            throw new InvalidOperationException("Endfield MatCap selected entry anchor missing.");

        // Only this variant obtains the scalar helper. Every old no-MatCap source
        // and compiled shader remains untouched, including accepted radial outputs.
        string radial = hair ? MatcapRadialScalarHelper(source) : "";
        source = source.Replace(entry + "(", entry + "EndfieldMatcapBase(", StringComparison.Ordinal);
        bool basePrimitive = entry is "PSSurfaceGi" or "PSSurfaceGiNative";
        string call = entry + "EndfieldMatcapBase(v" + (basePrimitive ? ",primitive" : "") + (rim ? ",front" : "") + ")";
        string composition = "float3 EndfieldMatcapComposeDelta(SurfaceOutput v,uint primitive,bool front,float3 changed,float2 uv,float3 geometric,float3 perturbed,float k,float rangeQ,float3 currentColor) {\n"
            + "    float3 initial=MatcapBaseBaseline.rgb;\n"
            + "    float rangeStrength=EndfieldMatcapRangeStrength(k,rangeQ);\n"
            + "    if(MatcapRotationGate.w==0) {\n"
            + "        if(MatcapReserved.x==0) return (changed-initial)*MatcapBaseBaseline.w;\n"
            + "        return changed*rangeStrength-initial*MatcapBaseBaseline.w;\n    }\n"
            + "    float r=" + (rim ? "EndfieldRimMask(v,front)" : "0") + ";\n"
            + "    float p=" + (hair ? "EndfieldMatcapRadialScalar(v)" : "0") + ";\n"
            + "    float strength=MatcapBaseBaseline.w+r+p;\n"
            + (rim ? "    float3 result=(EndfieldMatcapOverlay(changed,RimColorRadial.rgb,saturate(r))-EndfieldMatcapOverlay(initial,RimColorRadial.rgb,saturate(r)))*strength;\n"
                + "    if(MatcapReserved.x>0) result+=EndfieldMatcapOverlay(changed,RimColorRadial.rgb,saturate(r))*(rangeStrength-MatcapBaseBaseline.w);\n"
                : "    float3 result=(changed-initial)*strength;\n"
                + "    if(MatcapReserved.x>0) result+=changed*(rangeStrength-MatcapBaseBaseline.w);\n")
            + "    return result;\n}\n";
        return source + "\n" + MatcapNormalHelper + "\n" + radial + "\n" + composition + "\n" + MatcapDeltaHelper
            + $"\nfloat4 {entry}(SurfaceOutput v,uint primitive:SV_PrimitiveID,bool front:SV_IsFrontFace):SV_Target {{\n"
            + $"    float4 color={call};\n"
            + "    color.rgb+=EndfieldMatcapDelta(v,primitive,front,color.rgb); return color;\n}\n";
    }

    private static string MatcapRadialScalarHelper(string source)
    {
        const string signature = "float3 EndfieldRadialIncrement(SurfaceOutput v)";
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Endfield MatCap radial scalar anchor missing.");
        int open = source.IndexOf('{', start), depth = 1, end = open + 1;
        for (; end < source.Length && depth != 0; end++)
        {
            if (source[end] == '{') depth++;
            else if (source[end] == '}') depth--;
        }
        string helper = source[start..end];
        const string result = "return EndfieldPaletteBrightness.rgb*p;";
        if (depth != 0 || !helper.Contains(result, StringComparison.Ordinal))
            throw new InvalidOperationException("Endfield MatCap radial result anchor missing.");
        return helper.Replace(signature, "float EndfieldMatcapRadialScalar(SurfaceOutput v)", StringComparison.Ordinal)
            .Replace(result, "return p;", StringComparison.Ordinal);
    }

    // b10 is exactly nine float4s. The host supplies an orthonormal camera basis
    // and normalized explicit world light-control direction, defaulting to world Z.
    // Corner tangent.w contains the Mikk sign; UvParity.z carries object determinant
    // separately, applied exactly once below. UVs use the tangent generator's convention.
    private const string MatcapNormalHelper = """
        cbuffer EndfieldMatcapConstants:register(b10) {
            float4 MatcapBaseBaseline;
            float4 MatcapHighlightNormal;
            float4 MatcapRotationGate;
            float4 MatcapLightControl;
            float4 MatcapCameraRight;
            float4 MatcapCameraUp;
            float4 MatcapCameraForward;
            float4 MatcapReserved;
            float4 MatcapRangeDirectionWidth;
        };
        Texture2D<float4> MatcapNormalMap:register(t15);
        Texture2D<float4> MatcapPMap:register(t16);
        Texture2D<float4> MatcapColorMap:register(t17);
        SamplerState MatcapSampler:register(s7);
        struct MatcapCorner { float4 WorldPosition;float4 WorldNormal;float4 Tangent;float4 UvParity; };
        StructuredBuffer<MatcapCorner> MatcapCorners:register(t18);

        bool EndfieldMatcapMappedNormal(SurfaceOutput v,uint primitive,bool front,out float3 mapped,out float2 uv,out float3 baseGeometric,out float3 basePerturbed) {
            mapped=0;uv=0;baseGeometric=0;basePerturbed=0;
            MatcapCorner a=MatcapCorners[primitive*3],b=MatcapCorners[primitive*3+1],c=MatcapCorners[primitive*3+2];
            float3 e0=b.WorldPosition.xyz-a.WorldPosition.xyz,e1=c.WorldPosition.xyz-a.WorldPosition.xyz;
            float3 offset=v.WorldPosition-a.WorldPosition.xyz;
            float aa=dot(e0,e0),ab=dot(e0,e1),bb=dot(e1,e1);
            float denominator=aa*bb-ab*ab;
            if(denominator==0 || !isfinite(denominator)) return false;
            float d=dot(offset,e0),e=dot(offset,e1);
            float w1=(bb*d-ab*e)/denominator,w2=(aa*e-ab*d)/denominator,w0=1-w1-w2;
            float3 ni=(a.WorldNormal.xyz*w0+b.WorldNormal.xyz*w1+c.WorldNormal.xyz*w2)*(front?1:-1);
            float4 tangent=a.Tangent*w0+b.Tangent*w1+c.Tangent*w2;
            uv=a.UvParity.xy*w0+b.UvParity.xy*w1+c.UvParity.xy*w2;
            float nScale=max(abs(ni.x),max(abs(ni.y),abs(ni.z)));
            if(nScale==0 || !all(isfinite(ni)) || !all(isfinite(tangent)) || !all(isfinite(uv))) return false;
            float3 geometric=normalize(ni/nScale);
            float2 rg=MatcapNormalMap.SampleLevel(MatcapSampler,uv,0).rg;
            float2 xy=rg*2-1;
            float z=sqrt(max(1-dot(xy,xy),0));
            float3 perturbed;
            if(all(tangent==float4(0,0,0,1))) perturbed=ni;
            else {
                tangent*=front?1:-1;
                float3 bitangent=tangent.w*cross(ni,tangent.xyz)*a.UvParity.z;
                perturbed=xy.x*tangent.xyz+xy.y*bitangent+z*ni;
                float mappedScale=max(abs(perturbed.x),max(abs(perturbed.y),abs(perturbed.z)));
                if(mappedScale==0 || !all(isfinite(perturbed))) return false;
                perturbed=normalize(perturbed/mappedScale);
            }
            float3 mixed=geometric+(perturbed-geometric)*max(MatcapHighlightNormal.w,0);
            float mixedScale=max(abs(mixed.x),max(abs(mixed.y),abs(mixed.z)));
            if(mixedScale==0 || !all(isfinite(mixed))) return false;
            mapped=normalize(mixed/mixedScale);baseGeometric=geometric;basePerturbed=perturbed;return true;
        }
        float3 EndfieldSharedNormal(float3 geometric,float3 perturbed,float strength) {
            float3 mixed=geometric+(perturbed-geometric)*strength;
            float scale=max(abs(mixed.x),max(abs(mixed.y),abs(mixed.z)));
            return scale==0?float3(0,0,0):normalize(mixed/scale);
        }
        float EndfieldSharedDirectionalQ(float3 normal,float4 directionWidth) {
            precise float u=dot(normal,directionWidth.xyz)*.5+.5;
            precise float a=directionWidth.w-.5;
            precise float maximum=1-a;
            precise float minimum=maximum*directionWidth.w;
            precise float q=maximum==minimum?0:((u-minimum)/(maximum-minimum))*1.5;
            return clamp(q,0,1.5);
        }
        float EndfieldMatcapRangeStrength(float k,float q) {
            if(MatcapReserved.x==0) return MatcapBaseBaseline.w;
            return MatcapReserved.y+(MatcapReserved.z*k)*saturate(q*k);
        }
        float3 EndfieldMatcapOverlay(float3 a,float3 b,float factor) {
            float3 lower=a*((1-factor)+2*factor*b);
            float3 upper=1-((1-factor)+2*factor*(1-b))*(1-a);
            return float3(a.x<.5?lower.x:upper.x,a.y<.5?lower.y:upper.y,a.z<.5?lower.z:upper.z);
        }
        """;

    private const string MatcapDeltaHelper = """
        float3 EndfieldMatcapDelta(SurfaceOutput v,uint primitive,bool front,float3 currentColor) {
            float3 mapped;float2 uv;float3 baseGeometric,basePerturbed;
            if(!EndfieldMatcapMappedNormal(v,primitive,front,mapped,uv,baseGeometric,basePerturbed)) return 0;
            float3 nc=float3(dot(mapped,MatcapCameraRight.xyz),dot(mapped,MatcapCameraUp.xyz),-dot(mapped,MatcapCameraForward.xyz));
            float sine,cosine;sincos(MatcapRotationGate.x,sine,cosine);
            float2 colorUv=float2(.5+.5*(cosine*nc.x+sine*nc.z),.5-.5*nc.y);
            // Color resource/SRV decodes RGB before bilinear filtering; alpha stays linear.
            float4 sampled=MatcapColorMap.SampleLevel(MatcapSampler,colorUv,0);
            float3 imageColor=sampled.rgb*sampled.a;
            precise float a=1-(1-MatcapRotationGate.z);
            float fromMin=a*MatcapRotationGate.z,fromMax=a;
            float value=.5*dot(mapped,MatcapLightControl.xyz)+.5;
            float s=fromMax==fromMin?0:1.5*saturate((value-fromMin)/(fromMax-fromMin));
            float4 p=MatcapPMap.SampleLevel(MatcapSampler,uv,0);
            float k=saturate(MatcapRotationGate.y+saturate(p.r+p.a));
            float rangeQ=MatcapReserved.x>0?EndfieldSharedDirectionalQ(EndfieldSharedNormal(baseGeometric,basePerturbed,MatcapReserved.w),MatcapRangeDirectionWidth):0;
            float gate=saturate(s*k);
            float3 screen=1-(1-imageColor)*(1-MatcapHighlightNormal.rgb);
            float3 changed=MatcapBaseBaseline.rgb*((1-gate)+gate*screen);
            return EndfieldMatcapComposeDelta(v,primitive,front,changed,uv,baseGeometric,basePerturbed,k,rangeQ,currentColor);
        }
        """;

    internal static byte[] CompileEndfieldMatcapSurface(string entry, bool native, bool gi,
        bool morph, bool sub, bool hair = false, bool rim = false)
        => D3D12FxcShaderCache.Compile(EndfieldMatcapSurfaceSource(entry, native, gi, morph, sub, hair, rim), entry,
            "OriginalEndfieldMatcapAdapter.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness);
}
