using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently expressed recovered scalar equations, not copied graph/source.
    // Explicit C0/range/P delta is an MDA adapter; not complete HA/HB material.
    internal static string EndfieldRimSurfaceSource(string entry,bool native,bool gi,bool morph,bool sub,bool hair=false)
    {
        string source=(hair?EndfieldHairSurfaceSource(native,gi,morph,sub):EndfieldSurfaceSource(native,gi,morph,sub))
            .ReplaceLineEndings("\n");
        bool primitive=entry is "PSSurfaceGi" or "PSSurfaceGiNative";
        if(entry is not ("PSSurface" or "PSSurfaceNative" or "PSSurfaceGi" or "PSSurfaceGiNative"))
            throw new ArgumentOutOfRangeException(nameof(entry));
        string signature=$"float4 {entry}(SurfaceOutput v";
        if(!source.Contains(signature,StringComparison.Ordinal))
            throw new InvalidOperationException("Endfield rim selected entry anchor missing.");
        source=source.Replace(entry+"(",entry+"EndfieldRimBase(",StringComparison.Ordinal);
        // Wrap only the selected final entry. Internal primary/native/GI budget calls
        // keep their original functions and never spend, duplicate or emit this delta.
        return source+"\n"+RimHelper+$"\nfloat4 {entry}(SurfaceOutput v"
            +(primitive?",uint primitive:SV_PrimitiveID":"")+",bool front:SV_IsFrontFace):SV_Target {\n"
            +$"    float4 color={entry}EndfieldRimBase(v"+(primitive?",primitive":"")+");\n"
            +"    color.rgb+=EndfieldRimDelta(v,front); return color;\n}\n";
    }

    private const string RimHelper="""
        cbuffer EndfieldRimConstants:register(b8) {
            float4 RimBaseRange;
            float4 RimColorRadial;
            float4 RimParameters;
            float4 RimCameraRight;
            float4 RimCameraUp;
            float4 RimCameraForward;
            float4 RimIncoming; // w: orthographic
        };
        float EndfieldExactFresnel(float cosine,bool front) {
            float eta=front?1.9999998807907104:rcp(1.9999998807907104);
            float transmittedSquared=(1-cosine*cosine)/(eta*eta);
            if(transmittedSquared>=1) return 1;
            float ct=sqrt(max(1-transmittedSquared,0));
            float rs=(cosine-eta*ct)/(cosine+eta*ct);
            float rp=(eta*cosine-ct)/(eta*cosine+ct);
            return (rs*rs+rp*rp)*.5;
        }
        float EndfieldRimMask(SurfaceOutput v,bool front) {
            float normalScale=max(abs(v.Normal.x),max(abs(v.Normal.y),abs(v.Normal.z)));
            float3 incoming=RimIncoming.w>0?RimIncoming.xyz:EyePosition.xyz-v.WorldPosition;
            float incomingScale=max(abs(incoming.x),max(abs(incoming.y),abs(incoming.z)));
            // Explicit MDA zero-normal / zero-incoming policy.
            if(normalScale==0 || incomingScale==0) return 0;
            float3 n=normalize(v.Normal/normalScale)*(front?1:-1);
            float3 view=normalize(incoming/incomingScale);
            float3 nc=float3(dot(n,RimCameraRight.xyz),dot(n,RimCameraUp.xyz),-dot(n,RimCameraForward.xyz));
            float sine,cosine;sincos(RimParameters.z,sine,cosine);
            float3 mapped=float3(cosine*.5*nc.x-sine*.5*nc.y-.5,
                sine*.5*nc.x+cosine*.5*nc.y-.5,nc.z);
            float radial=max(0.9999989867210388-length(mapped),0);
            float gradient=saturate(saturate(radial*radial)*5);
            // Clamp only finite-normal roundoff, not the recovered strength.
            float fresnel=EndfieldExactFresnel(saturate(abs(dot(view,n))),front);
            float u=saturate((gradient+(RimParameters.x-0.9999998807907104))*fresnel*10-1);
            return u*u*(3-2*u)*RimParameters.y;
        }
        float3 EndfieldRimDelta(SurfaceOutput v,bool front) {
            float rim=EndfieldRimMask(v,front);
            if(rim==0) return 0;
            float factor=saturate(rim);
            float3 a=RimBaseRange.rgb,b=RimColorRadial.rgb;
            float3 lower=a*((1-factor)+2*factor*b);
            float3 upper=1-((1-factor)+2*factor*(1-b))*(1-a);
            float3 overlay=float3(a.x<.5?lower.x:upper.x,a.y<.5?lower.y:upper.y,a.z<.5?lower.z:upper.z);
            float baselineStrength=RimBaseRange.w+RimColorRadial.w;
            return overlay*(baselineStrength+rim)-a*baselineStrength;
        }
        """;

    internal static byte[] CompileEndfieldRimSurface(string entry,bool native,bool gi,bool morph,bool sub,bool hair=false)
        =>D3D12FxcShaderCache.Compile(EndfieldRimSurfaceSource(entry,native,gi,morph,sub,hair),entry,
            "OriginalEndfieldRimAdapter.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness);
}
