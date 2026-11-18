using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independent finite radial-scalar + explicitly supplied palette adapter.
    // Not full HA/HB, MatCap, rim or a GI-emissive material. No source graph/assets.
    internal static string EndfieldHairSurfaceSource(bool native,bool gi,bool morph,bool sub)
    {
        string source=EndfieldSurfaceSource(native,gi,morph,sub).ReplaceLineEndings("\n");
        static string Required(string s,string old,string value)
        {
            if(!s.Contains(old,StringComparison.Ordinal))throw new InvalidOperationException("Endfield hair anchor missing: "+old);
            return s.Replace(old,value,StringComparison.Ordinal);
        }
        source=Required(source,"struct Input {", "struct Input { float3 RawSeed:RAW_RADIAL;");
        source=Required(source,"float4 Position:SV_POSITION; float3 Normal:NORMAL;", "float4 Position:SV_POSITION; float3 Normal:NORMAL; float3 RawSeed:TEXCOORD5;");
        source=Required(source,"o.Uv=v.Uv;", "o.RawSeed=mul(float4(v.RawSeed,0),World).xyz; o.Uv=v.Uv;");
        const string helper="""
            cbuffer EndfieldHairConstants:register(b7) {
                float4 EndfieldPaletteBrightness;
                float4 EndfieldViewDirection;
            };
            Texture2D<float4> EndfieldGreenMap:register(t12);
            SamplerState EndfieldHairSampler:register(s5);
            float3 EndfieldRadialIncrement(SurfaceOutput v) {
                // MDA's explicit zero-contribution policy for degenerate vectors.
                float normalScale=max(abs(v.Normal.x),max(abs(v.Normal.y),abs(v.Normal.z)));
                float seedScale=max(abs(v.RawSeed.x),max(abs(v.RawSeed.y),abs(v.RawSeed.z)));
                if(normalScale==0 || seedScale==0) return 0;
                float3 n=normalize(v.Normal/normalScale);
                float3 projectedCross=cross(v.RawSeed/seedScale,n);
                float crossScale=max(abs(projectedCross.x),max(abs(projectedCross.y),abs(projectedCross.z)));
                if(crossScale==0) return 0;
                float3 tangent=cross(n,normalize(projectedCross/crossScale));
                float3 binormal=normalize(cross(tangent,n));
                float3 incoming=EndfieldViewDirection.w>0?EndfieldViewDirection.xyz:EyePosition.xyz-v.WorldPosition;
                float incomingScale=max(abs(incoming.x),max(abs(incoming.y),abs(incoming.z)));
                if(incomingScale==0) return 0;
                incoming=normalize(incoming/incomingScale);
                float3 u=incoming+float3(0,0,.5000999569892883);
                float uScale=max(abs(u.x),max(abs(u.y),abs(u.z)));
                if(uScale==0) return 0;
                u=normalize(u/uScale);
                float q=saturate((abs(dot(u,binormal))-.10000000149011612)/(0-.10000000149011612));
                float green=EndfieldGreenMap.SampleLevel(EndfieldHairSampler,v.Uv,0).g;
                float p=min(q,.75)*green*(EndfieldPaletteBrightness.w+1.5001001358032227);
                return EndfieldPaletteBrightness.rgb*p;
            }
            """;
        source=Required(source,"SurfaceOutput VSSurface(Input v) {",helper+"\nSurfaceOutput VSSurface(Input v) {");
        // Wrap exactly the primary surface. All native/GI entrypoints (including
        // shared-budget copies) call it once; their diffuse budgets exclude P.
        source=Required(source,"float4 PSSurface(SurfaceOutput v):SV_Target", "float4 PSSurfaceEndfieldBase(SurfaceOutput v):SV_Target");
        source=Required(source,"struct DepthOutput {", """
            float4 PSSurface(SurfaceOutput v):SV_Target {
                float4 color=PSSurfaceEndfieldBase(v);
                color.rgb+=EndfieldRadialIncrement(v);
                return color;
            }
            struct DepthOutput {
            """);
        return source;
    }

    internal static byte[] CompileEndfieldHairSurface(string entry,bool vertex,bool native,bool gi,bool morph,bool sub)
        =>D3D12FxcShaderCache.Compile(EndfieldHairSurfaceSource(native,gi,morph,sub),entry,"OriginalEndfieldHair.hlsl",
            vertex?"vs_5_1":"ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness);
}
