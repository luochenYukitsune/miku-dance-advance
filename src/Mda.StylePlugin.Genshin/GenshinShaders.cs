using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.Genshin;

internal static class GenshinShaders
{
    public const string DefaultModuleId = "default";
    public const string FaceSdfModuleId = "face-sdf";

    private const string DefaultHlsl = """
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            float halfLambert = saturate(dot(n, l) * .5 + .5);
            float band;
            if (IncomingLight.w <= 0) band = halfLambert >= ShadowOptions.w ? 1 : 0;
            else {
                float lower = ShadowOptions.w - IncomingLight.w * .5;
                float t = saturate((halfLambert - lower) / IncomingLight.w);
                band = t * t * (3 - 2 * t);
            }
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        """;

    private const string FaceSdfHlsl = """
        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 FaceSdfForward;
            float4 FaceSdfRight;
            float4 FaceSdfParameters;
        };
        Texture2D<float4> FaceSdfMap : register(t0, space2);
        Texture2D<float4> FaceCombinedMap : register(t1, space2);

        float4 FaceCubicWeights(float t) {
            float t2 = t * t, t3 = t2 * t, u = 1 - t;
            return float4(u * u * u, 3 * t3 - 6 * t2 + 4, -3 * t3 + 3 * t2 + 3 * t + 1, t3) / 6;
        }
        float4 FaceCubicRepeat(Texture2D<float4> image, float2 uv) {
            uint width, height; image.GetDimensions(width, height);
            float2 size = float2(width, height);
            float2 position = uv * size - .5;
            float2 cell = floor(position), fraction = position - cell;
            float4 wx = FaceCubicWeights(fraction.x), wy = FaceCubicWeights(fraction.y);
            float2 first = float2(wx.x + wx.y, wy.x + wy.y);
            float2 second = float2(wx.z + wx.w, wy.z + wy.w);
            float2 low = (cell - .5 + float2(wx.y, wy.y) / first) / size;
            float2 high = (cell + 1.5 + float2(wx.w, wy.w) / second) / size;
            float4 a = image.SampleLevel(MdaLinearWrapSampler, low, 0);
            float4 b = image.SampleLevel(MdaLinearWrapSampler, float2(high.x, low.y), 0);
            float4 c = image.SampleLevel(MdaLinearWrapSampler, float2(low.x, high.y), 0);
            float4 d = image.SampleLevel(MdaLinearWrapSampler, high, 0);
            return a * first.x * first.y + b * second.x * first.y + c * first.x * second.y + d * second.x * second.y;
        }
        float FaceShapePower(float value, float exponent) {
            if (exponent == 0) return 1;
            if (value == 0) return 0;
            if (value > 0) return pow(value, exponent);
            float fractional = frac(abs(exponent));
            if (fractional >= .001 && fractional <= .999) return 0;
            float integerPower = floor(exponent + .5);
            float magnitude = pow(-value, integerPower);
            return fmod(abs(integerPower), 2) == 0 ? magnitude : -magnitude;
        }
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            float2 horizontal = l.xz;
            float lengthSquared = dot(horizontal, horizontal);
            horizontal = lengthSquared == 0 ? float2(0, 0) : horizontal * rsqrt(lengthSquared);
            float forward = dot(FaceSdfForward.xz, horizontal);
            float side = dot(FaceSdfRight.xz, horizontal);
            float2 faceUv = v.Uv;
            if (side <= 0) faceUv.x = -faceUv.x;
            float lightmap = dot(FaceCubicRepeat(FaceSdfMap, faceUv).rgb, float3(.2126, .7152, .0722));
            float4 combined = FaceCubicRepeat(FaceCombinedMap, v.Uv);
            float baseValue = min((forward + 1) * .5, .999);
            float exponentBase = 1.5 - FaceSdfParameters.y;
            float exponent = exponentBase * exponentBase * exponentBase;
            float threshold = FaceShapePower(baseValue, exponent);
            float lower = threshold - FaceSdfParameters.x, upper = threshold + FaceSdfParameters.x;
            float t = upper == lower ? 0 : saturate((lightmap - lower) / (upper - lower));
            float soft = t * t * (3 - 2 * t);
            float alphaGate = combined.a > .55 ? 1 : 0;
            float extra = saturate(((1 - threshold) - .71) / (.8 - .71)) * saturate((combined.a - alphaGate) * 5);
            float band = saturate(soft * combined.g + extra);
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        """;

    private const string DedicatedUberHlsl = """
        cbuffer PluginStyleConstants : register(b0, space2) {
        #ifdef GENSHIN_HAS_RB
            float4 GenshinHairParameters;
        #endif
        #ifdef GENSHIN_HAS_ATLAS
            float4 GenshinAtlasLayout0;
            float4 GenshinAtlasLayout1;
            float4 GenshinAtlasLayout2;
        #endif
        #ifdef GENSHIN_HAS_METALLIC
            float4 GenshinMetallicParameters; // enable, tileScale, brightness, litMode (0=PrimaryResponse, 1=Uniform)
            float4 GenshinMetallicDarkLit;    // darkColor.rgb, uniformLitFactor
            float4 GenshinMetallicLight;      // lightColor.rgb, swizzleMatcapBgr (1=BGR, 0=RGB)
            float4 GenshinMetallicShadow;     // shadowMultiplyColor.rgb, 0
        #endif
        #ifdef GENSHIN_HAS_METALLIC_SPECULAR
            float4 GenshinMetallicSpecularColorShininess; // color.rgb, shininess
            float4 GenshinMetallicSpecularSharpScale;     // sharpColor.rgb, scale
            float4 GenshinMetallicSpecularOptions;        // attenuation, sharpOffset, rampMix, hasRamp
        #endif
        };

        #ifdef GENSHIN_HAS_RB
        Texture2D<float4> GenshinHairMap : register(t0, space2);
        #endif
        #ifdef GENSHIN_HAS_ATLAS
        Texture2D<float4> GenshinAtlasLightmap : register(t1, space2);
        Texture2D<float4> GenshinAtlasColor : register(t2, space2);
        #endif
        #ifdef GENSHIN_HAS_METALLIC
        Texture2D<float4> GenshinMetallicLightmap : register(t3, space2);
        Texture2D<float4> GenshinMetallicMatcap : register(t4, space2);
        #endif
        #ifdef GENSHIN_HAS_METALLIC_SPECULAR
        Texture2D<float4> GenshinMetallicSpecularRamp : register(t5, space2);
        #endif

        float GenshinLightResponse(float nDotL, float visibility) {
            float halfLambert = saturate(nDotL * .5 + .5);
            float band;
            if (IncomingLight.w <= 0) band = halfLambert >= ShadowOptions.w ? 1 : 0;
            else {
                float lower = ShadowOptions.w - IncomingLight.w * .5;
                float t = saturate((halfLambert - lower) / IncomingLight.w);
                band = t * t * (3 - 2 * t);
            }
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }

        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return GenshinLightResponse(dot(n, l), visibility);
        }

        float4 GenshinCubicWeights(float t) {
            float t2 = t * t, t3 = t2 * t, u = 1 - t;
            return float4(u * u * u, 3 * t3 - 6 * t2 + 4, -3 * t3 + 3 * t2 + 3 * t + 1, t3) / 6;
        }

        float4 GenshinCubicSample(Texture2D<float4> tex, float2 uv) {
            uint width, height; tex.GetDimensions(width, height);
            float2 size = float2(width, height), position = uv * size - .5;
            float2 cell = floor(position), fraction = position - cell;
            float4 wx = GenshinCubicWeights(fraction.x), wy = GenshinCubicWeights(fraction.y);
            float2 first = float2(wx.x + wx.y, wy.x + wy.y), second = float2(wx.z + wx.w, wy.z + wy.w);
            float2 low = (cell - .5 + float2(wx.y, wy.y) / first) / size;
            float2 high = (cell + 1.5 + float2(wx.w, wy.w) / second) / size;
            float4 a = tex.SampleLevel(MdaLinearWrapSampler, low, 0);
            float4 b = tex.SampleLevel(MdaLinearWrapSampler, float2(high.x, low.y), 0);
            float4 c = tex.SampleLevel(MdaLinearWrapSampler, float2(low.x, high.y), 0);
            float4 d = tex.SampleLevel(MdaLinearWrapSampler, high, 0);
            return a * first.x * first.y + b * second.x * first.y + c * first.x * second.y + d * second.x * second.y;
        }

        #ifdef GENSHIN_HAS_RB
        float3 GenshinHairHighlight(SurfaceOutput v, float3 n, float3 h) {
            float4 packed = GenshinCubicSample(GenshinHairMap, v.Uv);
            float halfResponse = max(dot(n, h) + GenshinHairParameters.x - 1, .001);
            float gate = (1 - packed.b) < pow(halfResponse, GenshinHairParameters.y) ? 1 : 0;
            float factor = packed.r * gate * GenshinHairParameters.z * GenshinHairParameters.w;
            return saturate(SpecularPower.rgb * factor);
        }
        #endif

        #ifdef GENSHIN_HAS_ATLAS
        float GenshinAtlasSdiv(float a, float b) { return b == 0 ? 0 : a / b; }
        float3 GenshinAtlasDecode(float3 rgb) {
            if (GenshinAtlasLayout2.x == 0) return rgb;
            float3 low = rgb / 12.92, high = pow((rgb + .055) / 1.055, 2.4);
            return float3(rgb.x <= .04045 ? low.x : high.x, rgb.y <= .04045 ? low.y : high.y, rgb.z <= .04045 ? low.z : high.z);
        }
        float3 GenshinAtlasClosestExtend(float2 uv) {
            uint width, height; GenshinAtlasColor.GetDimensions(width, height);
            int2 pixel = min((int2)floor(saturate(uv) * float2(width, height)), int2(width - 1, height - 1));
            return GenshinAtlasDecode(GenshinAtlasColor.Load(int3(pixel, 0)).rgb);
        }
        float3 GenshinAtlasPalette(SurfaceOutput v, float nDotL, float visibility) {
            float4 lm = GenshinCubicSample(GenshinAtlasLightmap, v.Uv);
            float ao = lerp(.5, lm.g, GenshinAtlasLayout1.z) * lerp(1, GenshinAtlasLayout1.y, GenshinAtlasLayout1.w);
            float t = saturate((ao - .01) / (.4 - .01));
            float light = (t * t * (3 - 2 * t)) * (.5 * nDotL + .5);
            float width = (GenshinAtlasLayout1.x * 2) * GenshinAtlasLayout0.w;
            float q = 1 - GenshinAtlasSdiv(GenshinAtlasSdiv(GenshinAtlasLayout0.z - light, GenshinAtlasLayout0.z), width);
            float nightY = ((6 - GenshinAtlasLayout0.x) - 1) * .1 + .05;
            float gate = saturate((light < GenshinAtlasLayout0.z ? 1 : 0) - (lm.g > .95 ? 1 : 0));
            float3 day = GenshinAtlasClosestExtend(float2(q, nightY + .5));
            float3 night = GenshinAtlasClosestExtend(float2(q, nightY));
            float3 palette = lerp(float3(1, 1, 1), lerp(day, night, GenshinAtlasLayout0.y), gate);
            return lerp(ToonShadowColor.rgb, palette, visibility);
        }
        #endif

        #ifdef GENSHIN_HAS_METALLIC
        float3 GenshinMetallicSafeNormalize(float3 value) {
            float scale = max(abs(value.x), max(abs(value.y), abs(value.z)));
            if (scale <= 0 || !isfinite(scale)) return float3(0, 0, 0);
            return normalize(value / scale);
        }
        void GenshinExtractCameraBasis(out float3 camUp, out float3 camBackward, out float isOrtho) {
            float3 col1 = float3(ViewProjection._12, ViewProjection._22, ViewProjection._32);
            float3 col2 = float3(ViewProjection._13, ViewProjection._23, ViewProjection._33);
            float3 col3 = float3(ViewProjection._14, ViewProjection._24, ViewProjection._34);
            if (dot(col3, col3) >= 1e-6) {
                isOrtho = 0;
                camBackward = -GenshinMetallicSafeNormalize(col3);
            } else {
                isOrtho = 1;
                camBackward = -GenshinMetallicSafeNormalize(col2);
            }
            camUp = GenshinMetallicSafeNormalize(col1 - camBackward * dot(col1, camBackward));
        }
        float GenshinMetallicRaw(SurfaceOutput v) {
            if (GenshinMetallicParameters.x == 0) return 0;
            return GenshinMetallicParameters.x * (GenshinCubicSample(GenshinMetallicLightmap, v.Uv).r > .9 ? 1 : 0);
        }
        float3 GenshinMetallicGamma(float3 color) {
            float3 positive = pow(max(color, 0), 2.2);
            return float3(color.x > 0 ? positive.x : color.x, color.y > 0 ? positive.y : color.y, color.z > 0 ? positive.z : color.z);
        }
        float2 GenshinMetallicSourceUv(SurfaceOutput v) {
            float3 camUp, camBackward; float isOrtho;
            GenshinExtractCameraBasis(camUp, camBackward, isOrtho);
            float3 incoming = isOrtho > 0 ? camBackward : normalize(EyePosition.xyz - v.WorldPosition);
            float3 negativeUp = -camUp;
            float3 tangent = cross(incoming, negativeUp);
            float3 bitangent = cross(incoming, tangent);
            float3 normal = normalize(v.Normal);
            return float2(.5 + .5 * GenshinMetallicParameters.y * dot(normal, tangent), .5 + .5 * dot(normal, bitangent));
        }
        float3 GenshinMetallicPaletteRaw(SurfaceOutput v, float3 nonmetal, float primaryLit, float raw) {
            if (raw <= 0) return nonmetal;
            float2 sourceUv = GenshinMetallicSourceUv(v);
            float3 image = GenshinCubicSample(GenshinMetallicMatcap, float2(sourceUv.x, 1 - sourceUv.y)).rgb;
            if (GenshinMetallicLight.w > 0) image = image.bgr;
            float q = saturate(dot(image * GenshinMetallicParameters.z, float3(.2126, .7152, .0722)));
            float3 palette = lerp(GenshinMetallicGamma(GenshinMetallicDarkLit.rgb), GenshinMetallicGamma(GenshinMetallicLight.rgb), q);
            float lit = GenshinMetallicParameters.w == 1 ? GenshinMetallicDarkLit.w : primaryLit;
            float shadow = saturate(lit - .8);
            float3 tint = palette * lerp(float3(1, 1, 1), GenshinMetallicGamma(GenshinMetallicShadow.rgb), shadow);
            return lerp(float3(1, 1, 1), nonmetal, saturate(1 - raw)) * lerp(float3(1, 1, 1), tint, saturate(raw));
        }
        float3 GenshinMetallicPalette(SurfaceOutput v, float3 nonmetal, float primaryLit) {
            return GenshinMetallicPaletteRaw(v, nonmetal, primaryLit, GenshinMetallicRaw(v));
        }
        #endif

        #ifdef GENSHIN_HAS_METALLIC_SPECULAR
        float3 GenshinMetallicSpecularSafeNormalize(float3 value) {
            float squared = dot(value, value);
            if (squared <= 0) return float3(0, 0, 0);
            return value * rsqrt(squared);
        }
        float GenshinMetallicSpecularNdotH(SurfaceOutput v) {
            float3 normal = GenshinMetallicSpecularSafeNormalize(v.Normal);
            float3 incoming = GenshinMetallicSpecularSafeNormalize(EyePosition.xyz - v.WorldPosition);
            float3 light = GenshinMetallicSpecularSafeNormalize(IncomingLight.xyz);
            float3 halfDirection = GenshinMetallicSpecularSafeNormalize(incoming + light);
            return dot(normal, halfDirection);
        }
        float GenshinMetallicPrimaryLit(SurfaceOutput v) {
            float3 q = v.LightClip.xyz / v.LightClip.w;
            float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
            float visibility = 1;
            if (ShadowOptions.z > 0 && uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1) {
                precise float delta = max(q.z - ShadowMap.Sample(ShadowSampler, uv), 0);
                precise float weighted = delta * (ShadowOptions.y > 0 ? 8000 : 1500);
                if (ShadowOptions.y > 0) weighted = weighted * uv.y;
                visibility = 1 - saturate(weighted - .3);
            }
            return GenshinLightResponse(dot(normalize(v.Normal), IncomingLight.xyz), visibility);
        }
        float3 GenshinMetallicSpecularLocal(float nh, float blue, float lit) {
            precise float shifted = nh + (1 - 1);
            float h = max(shifted, .001);
            float p = saturate(GenshinMetallicSpecularSharpScale.w * pow(h, GenshinMetallicSpecularColorShininess.w));
            float3 authored = GenshinMetallicGamma(GenshinMetallicSpecularColorShininess.rgb);
            float3 q = authored * p;
            if (GenshinMetallicSpecularOptions.z > 0 && GenshinMetallicSpecularOptions.w > 0) {
                float3 ramp = GenshinMetallicSpecularRamp.SampleLevel(MdaLinearWrapSampler, float2(p, .5), 0).rgb;
                q = lerp(q, authored * ramp, saturate(GenshinMetallicSpecularOptions.z));
            }
            float3 ordinary = blue * q;
            float3 selected = GenshinMetallicSpecularOptions.y < p ? GenshinMetallicGamma(GenshinMetallicSpecularSharpScale.rgb) : ordinary;
            return selected * lerp(1, GenshinMetallicSpecularOptions.x, saturate(1 - lit));
        }
        float4 MdaStylePrimarySurfacePost(SurfaceOutput v, float4 color) {
            float raw = GenshinMetallicRaw(v);
            if (raw > 0) {
                float blue = GenshinCubicSample(GenshinMetallicLightmap, v.Uv).b;
                float lit = GenshinMetallicParameters.w == 1 ? GenshinMetallicDarkLit.w : GenshinMetallicPrimaryLit(v);
                color.rgb += saturate(raw) * GenshinMetallicSpecularLocal(GenshinMetallicSpecularNdotH(v), blue, lit);
            }
            return color;
        }
        #endif

        #if defined(GENSHIN_HAS_ATLAS) || defined(GENSHIN_HAS_METALLIC)
        float3 MdaStylePaletteFactor(SurfaceOutput v, float blend, float nDotL, float visibility) {
        #ifdef GENSHIN_HAS_ATLAS
            float3 nonmetal = GenshinAtlasPalette(v, nDotL, visibility);
        #else
            float3 nonmetal = MdaPaletteFactorDefault(blend);
        #endif
        #ifdef GENSHIN_HAS_METALLIC
            return GenshinMetallicPalette(v, nonmetal, blend);
        #else
            return nonmetal;
        #endif
        }
        #endif

        #if defined(GENSHIN_HAS_RB) || (defined(GENSHIN_HAS_METALLIC) && defined(GENSHIN_HAS_METALLIC_SPECULAR))
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
        #ifdef GENSHIN_HAS_RB
            float3 highlight = GenshinHairHighlight(v, n, h);
        #else
            float3 highlight = saturate(pow(max(0, dot(n, h)), SpecularPower.w) * SpecularPower.rgb);
        #endif
        #ifdef GENSHIN_HAS_METALLIC
            float metallicRaw = GenshinMetallicRaw(v);
        #ifdef GENSHIN_HAS_RB
            highlight *= saturate(1 - metallicRaw);
        #else
            if (metallicRaw > 0) highlight *= saturate(1 - metallicRaw);
        #endif
        #endif
            return highlight;
        }
        #endif
        """;

    public static string ResolveDedicatedModuleId(bool hasRb, bool hasAtlas, bool hasMetallic, bool hasMetallicSpecular) =>
        (hasMetallicSpecular, hasMetallic, hasAtlas, hasRb) switch
        {
            (true, true, true, true) => "metallic-specular-atlas-rb",
            (true, true, true, false) => "metallic-specular-atlas",
            (true, true, false, true) => "metallic-specular-rb",
            (true, true, false, false) => "metallic-specular",
            (false, true, true, true) => "metallic-atlas-rb",
            (false, true, true, false) => "metallic-atlas",
            (false, true, false, true) => "metallic-rb",
            (false, true, false, false) => "metallic",
            (_, false, true, true) => "hair-atlas-rb",
            (_, false, true, false) => "hair-atlas",
            (_, false, false, true) => "hair",
            _ => DefaultModuleId,
        };

    public static IReadOnlyList<StyleShaderModule> CreateAllModules()
    {
        var modules = new List<StyleShaderModule>(13)
        {
            new(
                ModuleId: DefaultModuleId,
                HlslSource: DefaultHlsl,
                Hooks: StyleShaderHookMask.LightResponse
                    | StyleShaderHookMask.AllowStylizedShadingWrapper
                    | StyleShaderHookMask.DisableStylizedSphereMaps
                    | StyleShaderHookMask.HasStyleShaderCode
                    | StyleShaderHookMask.RequiresOutlineColorScale
                    | StyleShaderHookMask.RequiresSkinShadeBlend,
                GeometryRequirements: StyleGeometryRequirements.None,
                DisableStylizedSphereMaps: true),
            new(
                ModuleId: FaceSdfModuleId,
                HlslSource: FaceSdfHlsl,
                Hooks: StyleShaderHookMask.LightResponse
                    | StyleShaderHookMask.HasStyleShaderCode
                    | StyleShaderHookMask.RequiresSkinShadeBlend,
                GeometryRequirements: StyleGeometryRequirements.None),
        };

        for (int mask = 1; mask < 16; mask++)
        {
            bool hasRb = (mask & 1) != 0;
            bool hasAtlas = (mask & 2) != 0;
            bool hasMetallic = (mask & 4) != 0;
            bool hasMetallicSpecular = (mask & 8) != 0;
            if (hasMetallicSpecular && !hasMetallic)
                continue;

            var hooks = StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.HasStyleShaderCode
                | StyleShaderHookMask.RequiresSkinShadeBlend;
            if (hasAtlas || hasMetallic)
                hooks |= StyleShaderHookMask.PaletteFactor;
            if (hasRb || hasMetallicSpecular)
                hooks |= StyleShaderHookMask.Specular;
            if (hasMetallicSpecular)
                hooks |= StyleShaderHookMask.PrimarySurfacePost;

            var defines = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hasRb) defines["GENSHIN_HAS_RB"] = "1";
            if (hasAtlas) defines["GENSHIN_HAS_ATLAS"] = "1";
            if (hasMetallic) defines["GENSHIN_HAS_METALLIC"] = "1";
            if (hasMetallicSpecular) defines["GENSHIN_HAS_METALLIC_SPECULAR"] = "1";

            modules.Add(new StyleShaderModule(
                ModuleId: ResolveDedicatedModuleId(hasRb, hasAtlas, hasMetallic, hasMetallicSpecular),
                HlslSource: DedicatedUberHlsl,
                Hooks: hooks,
                GeometryRequirements: StyleGeometryRequirements.None,
                Defines: defines.AsReadOnly()));
        }

        return Array.AsReadOnly(modules.ToArray());
    }
}
