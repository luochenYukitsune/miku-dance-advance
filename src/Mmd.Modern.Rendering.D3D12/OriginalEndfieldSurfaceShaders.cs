using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independent ordinary-PMX adaptation of the audited multi-stage palette graph.
    // The two fixed knot offsets and neutral midpoint below are declared fallbacks,
    // not recovered Endfield ramp values. No reference code, node assets, PMap,
    // MatCap, hair/eye data or dedicated face SDF is incorporated.
    private const string EndfieldSurfaceHelper="""
        float EndfieldLightResponse(float nDotL,float visibility) {
            float halfLambert=saturate(nDotL*.5+.5);
            float band;
            if(IncomingLight.w<=0) band=halfLambert>=ShadowOptions.w?1:0;
            else {
                // Fixed dark/mid/lit knots stay on the PMX-authored endpoint segment.
                // Each transition is half the configured diffuse width, leaving a
                // genuine mid plateau instead of another single continuous ramp.
                float lowerThreshold=ShadowOptions.w-IncomingLight.w;
                float upperThreshold=ShadowOptions.w+IncomingLight.w;
                float halfTransition=IncomingLight.w*.25;
                float lowerBand=smoothstep(lowerThreshold-halfTransition,lowerThreshold+halfTransition,halfLambert);
                float upperBand=smoothstep(upperThreshold-halfTransition,upperThreshold+halfTransition,halfLambert);
                band=.5*lowerBand+.5*upperBand;
            }
            if(EyePosition.w<1) band=lerp(1,band,EyePosition.w);
            if(GroundDirection.w<1) visibility=lerp(1,visibility,GroundDirection.w);
            return band*visibility;
        }
        float3 EndfieldPaletteFactor(float blend) {
            return blend+ToonShadowColor.rgb*(1-blend);
        }
        """;

    internal static string EndfieldSurfaceSource(bool native,bool gi,bool textureMorph,bool subTexture)
    {
        // Reuse the verified raw coverage, originalC highlight attenuation, PMX
        // palette budget and GI/native plumbing. Only the diffuse family changes;
        // the authored Blinn highlight remains independent and unchanged.
        string source=GenshinSurfaceSource(native,gi,textureMorph,subTexture);
        if(!source.Contains(GenshinSurfaceHelper,StringComparison.Ordinal))
            throw new InvalidOperationException("Endfield PMX shader helper anchor missing.");
        source=source.Replace(GenshinSurfaceHelper,EndfieldSurfaceHelper,StringComparison.Ordinal);
        return source.Replace("GenshinLightResponse","EndfieldLightResponse",StringComparison.Ordinal)
            .Replace("GenshinPaletteFactor","EndfieldPaletteFactor",StringComparison.Ordinal)
            .Replace("GenshinPrimaryBlend","EndfieldPrimaryBlend",StringComparison.Ordinal);
    }

    internal static byte[] CompileEndfieldSurface(string entry,bool native,bool gi,bool textureMorph,bool subTexture)
        =>Compiler.Compile(EndfieldSurfaceSource(native,gi,textureMorph,subTexture),entry,
            "OriginalEndfieldPmxBody.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();
}
