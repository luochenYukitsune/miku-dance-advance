using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // MDA 自有的风格化明暗（不需要额外贴图）。只用于四个画风家族的普通 PMX 材质；
    // 带专用贴图绑定的变体不经过这里。思路参考用户提供的三渲二插件的分析：
    // 阴影色由底色自身色相加深、交界暖色带、受光侧硬边缘光、饱和／对比／亮度调色。
    // 不包含任何插件的节点、参数表或贴图。
    private const string StylizedShadingHelper = """
        float3 StylizedAlbedo(SurfaceOutput v) {
        ALBEDO
            return baseColor;
        }
        float3 StylizedGrade(float3 rgb) {
            float luma=dot(rgb,float3(.2126,.7152,.0722));
            rgb=max(luma+(rgb-luma)*StylizedGradeParams.x,0);
            // Contrast and brightness keep 0 and 1 fixed so lit skin never clips to white.
            float3 unit=saturate(rgb);
            if(StylizedGradeParams.y!=0) unit=lerp(unit,unit*unit*(3-2*unit),StylizedGradeParams.y);
            float gain=StylizedGradeParams.z;
            if(gain!=1) unit=unit*gain/(1+(gain-1)*unit);
            return rgb>1?rgb:unit;
        }
        // Additional lights take the same band as the main light; a flattened face only gains brightness
        // from lights in front of it, without a new terminator. Unselected materials and eyes keep the family band.
        float StylizedNativeResponse(float nDotL) {
            float band=PREFIXLightResponse(nDotL,1);
            if(StylizedGradeParams.z<=0 || StylizedShadeParams.x<=0) return band;
            return lerp(band,saturate((nDotL+.35)*4),StylizedShadeParams.w);
        }
        float4 PSSurface(SurfaceOutput v):SV_Target {
            float4 color=PSSurfaceUnstylized(v);
            // All-zero constants: an unselected material keeps the exact prior output.
            if(StylizedGradeParams.z<=0) return color;
            // Eye: keep the authored look (sphere sparkle, highlight); grade only.
            if(StylizedShadeParams.x<=0) return float4(StylizedGrade(color.rgb),color.a);
            float3 baseColor=StylizedAlbedo(v);
            if(any(baseColor<0) || any(!isfinite(baseColor))) return color;
            // Cel look: the colour is rebuilt from albedo and the normal band only.
            // Cast shadows, view-dependent sphere maps and the PMX specular lobe are
            // deliberately absent; they read as realistic 3D shading.
            float3 n=normalize(v.Normal); float3 l=IncomingLight.xyz;
            float c=PREFIXLightResponse(dot(n,l),1);
            float lit=lerp(c,1,StylizedShadeParams.w);
            // Shadow: albedo deepened along its own hue, then value and tint.
            float peak=max(baseColor.r,max(baseColor.g,baseColor.b));
            float3 chroma=peak>0?baseColor/peak:1;
            float3 shadow=baseColor*lerp(1,chroma,StylizedShadeParams.z)*StylizedShadeParams.y*StylizedTintParams.rgb;
            float3 rgb=lerp(shadow,baseColor,lit);
            // Warm terminator band centred on the half-Lambert threshold.
            float halfLambert=saturate(dot(n,l)*.5+.5);
            float band=(1-smoothstep(0,.07,abs(halfLambert-ShadowOptions.w)))*(1-StylizedShadeParams.w);
            rgb*=lerp(1,StylizedWarmParams.rgb,StylizedTintParams.w*band);
            rgb=lerp(baseColor*PREFIXPaletteFactor(c),rgb,StylizedShadeParams.x);
            if(StylizedGradeParams.w>0) {
                // Hard lit-side rim, tinted by albedo.
                float3 view=normalize(EyePosition.xyz-v.WorldPosition);
                float fresnel=1-saturate(dot(n,view));
                float rim=smoothstep(StylizedWarmParams.w,StylizedWarmParams.w+.04,fresnel)*c;
                rgb+=baseColor*rim*StylizedGradeParams.w;
            }
            return float4(StylizedGrade(rgb),color.a);
        }
        """;

    /// <summary>给四个画风家族的普通表面加上风格化明暗。prefix 是家族函数名前缀
    /// （Genshin、StarRail、Wuthering、Endfield）。</summary>
    internal static string ApplyStylizedShading(string source, string prefix)
    {
        static string Once(string text, string anchor, string replacement)
        {
            int at = text.IndexOf(anchor, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("Stylized shading anchor must occur exactly once: " + anchor);
            return text.Replace(anchor, replacement, StringComparison.Ordinal);
        }
        source = Once(source, "float4 PmxMaterialTreatmentParams;",
            "float4 PmxMaterialTreatmentParams; float4 StylizedShadeParams; float4 StylizedTintParams; float4 StylizedWarmParams; float4 StylizedGradeParams;");
        const string signature = "float4 PSSurface(SurfaceOutput v):SV_Target";
        int surface = source.IndexOf(signature, StringComparison.Ordinal);
        int open = surface < 0 ? -1 : source.IndexOf('{', surface);
        int normal = open < 0 ? -1 : source.IndexOf("float3 n=normalize(v.Normal);", open, StringComparison.Ordinal);
        if (normal < 0) throw new InvalidOperationException("Stylized shading diffuse endpoint is missing.");
        // 复制已生成的底色表达式（含贴图变形、SubTexture 和材质调色），不另写一份。
        // 去掉随视角变化的乘法／加法球面贴图（sph/spa），SubTexture 的 UV 贴图保留。
        string albedo = source[(open + 1)..normal];
        albedo = Once(albedo, "if(Options.y==1) baseColor=", "if(false) baseColor=");
        albedo = Once(albedo, "if(Options.y==2) baseColor=", "if(false) baseColor=");
        source = Once(source, signature, "float4 PSSurfaceUnstylized(SurfaceOutput v):SV_Target");
        string helper = StylizedShadingHelper.Replace("ALBEDO", albedo, StringComparison.Ordinal)
            .Replace("PREFIX", prefix, StringComparison.Ordinal);
        source = Once(source, "struct DepthOutput {", helper + "\nstruct DepthOutput {");
        // Native variants: route each additional light through the stylized response (after any family define).
        const string native = "float3 NativeDirect(SurfaceOutput v) {";
        return source.Contains(native, StringComparison.Ordinal) ? Once(source, native,
            "#undef NATIVE_LIGHT_RESPONSE\n#define NATIVE_LIGHT_RESPONSE(v,n,l) StylizedNativeResponse(dot(n,l))\n" + native) : source;
    }

    internal static byte[] CompileStylizedFamilySurface(D3D12SurfaceStyle family, string entry, bool native, bool gi, bool morph, bool sub)
    {
        var (source, prefix, name) = family switch
        {
            D3D12SurfaceStyle.GenshinPmx => (GenshinSurfaceSource(native, gi, morph, sub), "Genshin", "OriginalGenshinPmxBody.hlsl"),
            D3D12SurfaceStyle.StarRailPmx => (StarRailSurfaceSource(native, gi, morph, sub), "StarRail", "OriginalStarRailPmxBody.hlsl"),
            D3D12SurfaceStyle.WutheringPmx => (WutheringSurfaceSource(native, gi, morph, sub), "Wuthering", "OriginalWutheringPmxBody.hlsl"),
            D3D12SurfaceStyle.EndfieldPmx => (EndfieldSurfaceSource(native, gi, morph, sub), "Endfield", "OriginalEndfieldPmxBody.hlsl"),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        string stylized = ApplyStylizedShading(source, prefix);
        if (Environment.GetEnvironmentVariable("MDA_DUMP_STYLIZED_SHADER") is { Length: > 0 } dump)
            File.WriteAllText(Path.Combine(dump, name), stylized);
        return D3D12FxcShaderCache.Compile(stylized, entry, name, "ps_5_1",
            ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness);
    }
}
