using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored numerical implementation of the audited gray SDF graphs.
    // The dedicated data must be supplied explicitly; no third-party masks are embedded.
    // Vertical light uses the accepted ordinary Face response, and the exact left/right
    // tie retains the original U. Both are documented safety policies, not source claims.
    internal static string FaceSdfSurfaceSource(D3D12SurfaceStyle family, bool native, bool gi, bool textureMorph, bool subTexture)
    {
        if (family == D3D12SurfaceStyle.GenshinPmx)
            return GenshinFaceSdfSurfaceSource(native, gi, textureMorph, subTexture);
        if (family == D3D12SurfaceStyle.EndfieldPmx)
            return EndfieldFaceSdfSurfaceSource(native, gi, textureMorph, subTexture);
        string prefix, source, halfWidth, ramp;
        switch (family)
        {
            case D3D12SurfaceStyle.StarRailPmx:
                prefix = "StarRail"; source = StarRailSurfaceSource(native, gi, textureMorph, subTexture);
                halfWidth = ".15/FaceSdfParameters.x"; ramp = "saturate((q0-.6)/.3)"; break;
            case D3D12SurfaceStyle.WutheringPmx:
                prefix = "Wuthering"; source = WutheringSurfaceSource(native, gi, textureMorph, subTexture);
                halfWidth = "FaceSdfParameters.x"; ramp = "saturate((q0-.35)/.15)"; break;
            default: throw new NotSupportedException("Dedicated FaceSDF is implemented only for Star Rail and Wuthering.");
        }
        string helper = $$"""
            cbuffer FaceSdfConstants:register(b4) {
                float4 FaceSdfForward; float4 FaceSdfRight; float4 FaceSdfParameters;
            };
            Texture2D<float4> FaceSdfMap:register(t7);
            SamplerState FaceSdfSampler:register(s2);
            float FaceSdfLightResponse(SurfaceOutput v,float3 towardLight,float visibility) {
                float f=dot(FaceSdfForward.xyz,towardLight);
                float r=dot(FaceSdfRight.xyz,towardLight);
                float planeLengthSquared=f*f+r*r;
                if(planeLengthSquared<=1e-8)
                    return {{prefix}}LightResponse(dot(normalize(v.Normal),towardLight),visibility);
                float inverseLength=rsqrt(planeLengthSquared);
                f*=inverseLength; r*=inverseLength;
                float2 faceUv=v.Uv;
                // Preserve U at an exact frontal/backward tie, rather than collapsing it to zero.
                if(r>0) faceUv.x=-faceUv.x;
                float sdf=FaceSdfMap.Sample(FaceSdfSampler,faceUv).r;
                float center=FaceSdfParameters.y-(f*.5+.5);
                float width={{halfWidth}};
                float q0=saturate((sdf-center+width)/(2*width));
                float band={{ramp}};
                if(EyePosition.w<1) band=lerp(1,band,EyePosition.w);
                if(GroundDirection.w<1) visibility=lerp(1,visibility,GroundDirection.w);
                return band*visibility;
            }
            """;
        static string ReplaceRequired(string text, string anchor, string replacement)
        {
            if (!text.Contains(anchor, StringComparison.Ordinal))
                throw new InvalidOperationException("FaceSDF shader anchor missing: " + anchor);
            return text.Replace(anchor, replacement, StringComparison.Ordinal);
        }
        source = ReplaceRequired(source, "SurfaceOutput VSSurface(Input v) {", helper + "\nSurfaceOutput VSSurface(Input v) {");
        foreach (string visibility in new[] { "1", "visibility" })
        {
            source = source.Replace($"{prefix}LightResponse(dot(n,l),{visibility})", $"FaceSdfLightResponse(v,l,{visibility})", StringComparison.Ordinal);
            source = source.Replace($"{prefix}LightResponse(dot(normalize(v.Normal),IncomingLight.xyz),{visibility})", $"FaceSdfLightResponse(v,IncomingLight.xyz,{visibility})", StringComparison.Ordinal);
        }
        return source;
    }

    internal static byte[] CompileFaceSdfSurface(D3D12SurfaceStyle family, string entry, bool native, bool gi, bool textureMorph, bool subTexture)
        => Compiler.Compile(FaceSdfSurfaceSource(family, native, gi, textureMorph, subTexture), entry,
            "ExplicitFaceSdf.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray();

    // Independent implementation of the inspected packed graph's numerical shape only.
    // Source cm_M and RGB data are explicit caller inputs; no reference images/code
    // or source palette/eye-refraction operations are bundled.
    private static string EndfieldFaceSdfSurfaceSource(bool native, bool gi, bool textureMorph, bool subTexture)
    {
        const string helper = """
            cbuffer FaceSdfConstants:register(b4) {
                float4 FaceSdfForward; float4 FaceSdfRight; float4 FaceSdfParameters;
            };
            Texture2D<float4> FaceSdfMap:register(t7);
            Texture2D<float4> FaceCombinedMap:register(t8);
            SamplerState FaceSdfSampler:register(s2);
            float FaceSdfLightResponse(SurfaceOutput v,float3 towardLight,float visibility) {
                float f=dot(FaceSdfForward.xyz,towardLight);
                float r=dot(FaceSdfRight.xyz,towardLight);
                float planeLengthSquared=f*f+r*r;
                if(planeLengthSquared<=1e-8)
                    return EndfieldLightResponse(dot(normalize(v.Normal),towardLight),visibility);
                float inverseLength=rsqrt(planeLengthSquared);
                f*=inverseLength; r*=inverseLength;
                float2 faceUv=v.Uv;
                // Endfield's exact side tie uses mirrored U; cm_M remains unmirrored.
                if(r<=0) faceUv.x=1-faceUv.x;
                float3 sdf=FaceSdfMap.Sample(FaceSdfSampler,faceUv).rgb;
                float2 combined=FaceCombinedMap.Sample(FaceSdfSampler,v.Uv).rg;
                float value=saturate(saturate(sdf.r+sdf.b)*sdf.g-combined.g);
                float scale=combined.r>0?combined.r:1;
                float center=FaceSdfParameters.y-(f*.5+.5);
                float lower=saturate(scale*(center-FaceSdfParameters.x));
                float upper=center+FaceSdfParameters.x;
                // Blender linear MapRange returns zero for exactly equal endpoints;
                // tiny and reversed nonzero ranges retain their signed arithmetic.
                float band=upper==lower?0:saturate((value-lower)/(upper-lower));
                if(EyePosition.w<1) band=lerp(1,band,EyePosition.w);
                if(GroundDirection.w<1) visibility=lerp(1,visibility,GroundDirection.w);
                return band*visibility;
            }
            """;
        string source = EndfieldSurfaceSource(native, gi, textureMorph, subTexture);
        const string anchor = "SurfaceOutput VSSurface(Input v) {";
        if (!source.Contains(anchor, StringComparison.Ordinal))
            throw new InvalidOperationException("Endfield packed FaceSDF shader anchor missing.");
        source = source.Replace(anchor, helper + "\n" + anchor, StringComparison.Ordinal);
        foreach (string visibility in new[] { "1", "visibility" })
        {
            source = source.Replace($"EndfieldLightResponse(dot(n,l),{visibility})", $"FaceSdfLightResponse(v,l,{visibility})", StringComparison.Ordinal);
            source = source.Replace($"EndfieldLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),{visibility})", $"FaceSdfLightResponse(v,IncomingLight.xyz,{visibility})", StringComparison.Ordinal);
        }
        return source;
    }
}
