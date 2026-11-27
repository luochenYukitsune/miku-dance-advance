using System.Text.RegularExpressions;
using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in PCF soft primary self-shadow shader transform.
/// Keeps OriginalFrameShaders.Source / GiSource / NativeSource byte-for-byte identical when Point/Linear is active.</summary>
internal static partial class OriginalPcfShadowShader
{
    private const string Helper = """
        float PrimaryShadowVisibilityPcf(float2 uv, float receiverDepth, float2 screenPos) {
            precise float delta=max(receiverDepth-ShadowMap.Sample(ShadowSampler,uv),0);
            precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
            if(ShadowOptions.y>0) weighted=weighted*uv.y;
            precise float centerVis=1-saturate(weighted-.3);
            if(ShadowOptions.x<=0) return centerVis;
            uint sw,sh; ShadowMap.GetDimensions(sw,sh);
            float2 radius=ShadowOptions.x*1.5/max(float2(sw,sh),float2(1,1));
            float angle=frac(52.9829189*frac(dot(screenPos,float2(0.06711056,0.00583715))))*6.2831853;
            float s=sin(angle), cRot=cos(angle);
            static const float2 kPcf[12]={
                float2(-0.326212,-0.405810), float2(-0.840144,-0.073580),
                float2(-0.695914, 0.457137), float2(-0.203345, 0.620716),
                float2( 0.962340,-0.194983), float2( 0.473434,-0.480026),
                float2( 0.519456, 0.767022), float2( 0.185461,-0.893124),
                float2( 0.507431, 0.064425), float2( 0.896420, 0.412458),
                float2(-0.321940,-0.932615), float2(-0.791559,-0.597710)
            };
            float sum=centerVis;
            [unroll] for(int t=0;t<12;t++) {
                float2 r=float2(kPcf[t].x*cRot-kPcf[t].y*s, kPcf[t].x*s+kPcf[t].y*cRot);
                float2 suv=saturate(uv+r*radius);
                float sd=ShadowMap.SampleLevel(ShadowSampler,suv,0);
                float d=max(receiverDepth-sd,0);
                float w=d*(ShadowOptions.y>0?8000:1500);
                if(ShadowOptions.y>0) w*=suv.y;
                sum+=1-saturate(w-.3);
            }
            return sum/13.0;
        }
        """;

    [GeneratedRegex(
        @"precise float delta=max\(q\.z-ShadowMap\.Sample\(ShadowSampler,uv\),0\);\s*"
        + @"precise float weighted=delta\*\(ShadowOptions\.y>0\?8000:1500\);\s*"
        + @"if\(ShadowOptions\.y>0\) weighted=weighted\*uv\.y;\s*"
        + @"precise float visibility=1-saturate\(weighted-\.3\);",
        RegexOptions.CultureInvariant)]
    private static partial Regex LegacyVisibilityPattern();

    private const string PcfVisibilityLine = "precise float visibility=PrimaryShadowVisibilityPcf(uv,q.z,v.Position.xy);";

    internal static string Apply(string source)
    {
        string normalized = source.ReplaceLineEndings("\n");
        var pattern = LegacyVisibilityPattern();
        if (!pattern.IsMatch(normalized))
            throw new InvalidOperationException("PCF shadow shader anchor missing.");
        normalized = pattern.Replace(normalized, PcfVisibilityLine);
        int insertion = normalized.IndexOf("struct Input", StringComparison.Ordinal);
        if (insertion < 0)
            throw new InvalidOperationException("PCF shadow shader input declaration is missing.");
        return normalized.Insert(insertion, Helper + "\n");
    }
}

internal static partial class OriginalFrameShaders
{
    internal static byte[] CompilePcfShadow(string entry, bool native, bool gi, bool textureMorph, bool subTexture,
        bool characterToon, bool characterDirectPalette)
    {
        string source;
        if (characterDirectPalette)
            source = CharacterDirectPaletteSource(gi, textureMorph, subTexture, characterToon);
        else if (characterToon)
            source = CharacterToonSource(native, gi, textureMorph, subTexture);
        else
        {
            source = Source + (native || gi ? GiSource : "") + (native ? NativeSource : "");
            if (subTexture) source = OriginalSubTextureShader.Apply(source, true, native, gi);
            else if (textureMorph) source = OriginalTextureMorphShader.Apply(source);
        }
        return D3D12FxcShaderCache.Compile(
            OriginalPcfShadowShader.Apply(source),
            entry,
            "OriginalFramePcfShadow.hlsl",
            "ps_5_1",
            ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness);
    }
}

public sealed partial class D3D12OriginalStyleRenderer
{
    private readonly Dictionary<(string Entry, bool Native, bool Gi, bool Morph, bool SubTexture, bool CharacterToon, bool CharacterDirectPalette), byte[]> pcfShadowFrameShaders = [];

    private byte[] GetPcfShadowFrameShader(string entry, bool native, bool gi, bool textureMorph, bool subTexture,
        bool characterToon, bool characterDirectPalette)
    {
        var key = (entry, native, gi, textureMorph, subTexture, characterToon, characterDirectPalette);
        if (pcfShadowFrameShaders.TryGetValue(key, out var found)) return found;
        var compiled = OriginalFrameShaders.CompilePcfShadow(entry, native, gi, textureMorph, subTexture, characterToon, characterDirectPalette);
        runtime.ShaderCompilations++;
        pcfShadowFrameShaders.Add(key, compiled);
        return compiled;
    }
}
