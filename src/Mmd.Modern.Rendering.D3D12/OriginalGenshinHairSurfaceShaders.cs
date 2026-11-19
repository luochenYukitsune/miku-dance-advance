using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored packed R/B mathematical adapter. PMX authored specular
    // color remains the palette; metallic, material IDs and complete hair ramps are
    // separate contracts. No reference shader, graph or texture is bundled.
    internal static string GenshinHairSurfaceSource(bool native, bool gi, bool textureMorph, bool subTexture)
    {
        const string helper = """
            cbuffer GenshinHairConstants:register(b5) {
                float4 GenshinHairParameters;
            };
            Texture2D<float4> GenshinHairMap:register(t9);
            SamplerState GenshinHairSampler:register(s3);
            float4 GenshinHairCubicWeights(float t) {
                float t2=t*t, t3=t2*t, u=1-t;
                return float4(u*u*u,3*t3-6*t2+4,-3*t3+3*t2+3*t+1,t3)/6;
            }
            float4 GenshinHairCubicRepeat(float2 uv) {
                uint width,height; GenshinHairMap.GetDimensions(width,height);
                float2 size=float2(width,height);
                float2 position=uv*size-.5;
                float2 cell=floor(position), fraction=position-cell;
                float4 wx=GenshinHairCubicWeights(fraction.x), wy=GenshinHairCubicWeights(fraction.y);
                float2 first=float2(wx.x+wx.y,wy.x+wy.y);
                float2 second=float2(wx.z+wx.w,wy.z+wy.w);
                float2 low=(cell-.5+float2(wx.y,wy.y)/first)/size;
                float2 high=(cell+1.5+float2(wx.w,wy.w)/second)/size;
                float4 a=GenshinHairMap.SampleLevel(GenshinHairSampler,low,0);
                float4 b=GenshinHairMap.SampleLevel(GenshinHairSampler,float2(high.x,low.y),0);
                float4 c=GenshinHairMap.SampleLevel(GenshinHairSampler,float2(low.x,high.y),0);
                float4 d=GenshinHairMap.SampleLevel(GenshinHairSampler,high,0);
                return a*first.x*first.y+b*second.x*first.y+c*first.x*second.y+d*second.x*second.y;
            }
            float3 GenshinHairHighlight(SurfaceOutput v,float3 n,float3 h) {
                float4 packed=GenshinHairCubicRepeat(v.Uv);
                float halfResponse=max(dot(n,h)+GenshinHairParameters.x-1,.001);
                float gate=(1-packed.b)<pow(halfResponse,GenshinHairParameters.y)?1:0;
                float factor=packed.r*gate*GenshinHairParameters.z*GenshinHairParameters.w;
                return saturate(SpecularPower.rgb*factor);
            }
            """;
        string source = GenshinSurfaceSource(native, gi, textureMorph, subTexture);
        const string helperAnchor = "SurfaceOutput VSSurface(Input v) {";
        const string highlightAnchor = "float3 highlight=saturate(pow(max(0,dot(n,h)),SpecularPower.w)*SpecularPower.rgb);";
        static string ReplaceExactlyOnce(string source, string anchor, string replacement)
        {
            int at = source.IndexOf(anchor, StringComparison.Ordinal);
            if (at < 0 || source.LastIndexOf(anchor, StringComparison.Ordinal) != at)
                throw new InvalidOperationException("Genshin packed hair shader anchor must occur exactly once: " + anchor);
            return source.Replace(anchor, replacement, StringComparison.Ordinal);
        }
        source = ReplaceExactlyOnce(source, helperAnchor, helper + "\n" + helperAnchor);
        // Replace the one primary authored lobe. Existing originalC attenuation,
        // outside-projection composition and all diffuse/native/GI paths stay intact.
        return ReplaceExactlyOnce(source, highlightAnchor, "float3 highlight=GenshinHairHighlight(v,n,h);");
    }

    internal static byte[] CompileGenshinHairSurface(string entry, bool native, bool gi, bool textureMorph, bool subTexture)
        => D3D12FxcShaderCache.Compile(GenshinHairSurfaceSource(native, gi, textureMorph, subTexture), entry,
            "OriginalGenshinPackedHair.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness);
}
