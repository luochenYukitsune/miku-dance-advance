using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.StarRail;

internal static class StarRailShaders
{
    public const string DefaultModuleId = "default";
    public const string FaceSdfModuleId = "face-sdf";
    public const string HairModuleId = "hair";
    public const string BodyModuleId = "body";

    private const string StarRailLightHelper = """
        float StarRailLightResponse(float nDotL, float visibility) {
            float halfLambert = saturate(nDotL * .5 + .5);
            float band;
            if (IncomingLight.w <= 0) band = halfLambert >= ShadowOptions.w ? 1 : 0;
            else {
                float lower = ShadowOptions.w - IncomingLight.w;
                band = saturate((halfLambert - lower) / IncomingLight.w);
            }
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        """;

    private const string StarRailSurfaceHelper = StarRailLightHelper + "\n" + """
        float3 StarRailAuthoredSpecular(float3 n, float3 h) {
            float authoredLobe = pow(max(0, dot(n, h)), SpecularPower.w);
            float highlightBand = smoothstep(.45, .55, authoredLobe);
            return saturate(authoredLobe * SpecularPower.rgb) * highlightBand;
        }
        """;

    private const string DefaultHlsl = StarRailSurfaceHelper + "\n" + """
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return StarRailLightResponse(dot(n, l), visibility);
        }
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            return StarRailAuthoredSpecular(n, h);
        }
        """;

    private const string FaceSdfHlsl = StarRailSurfaceHelper + "\n" + """
        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 FaceSdfForward;
            float4 FaceSdfRight;
            float4 FaceSdfParameters;
        };
        Texture2D<float4> FaceSdfMap : register(t0, space2);

        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            float f = dot(FaceSdfForward.xyz, l);
            float r = dot(FaceSdfRight.xyz, l);
            float planeLengthSquared = f * f + r * r;
            if (planeLengthSquared <= 1e-8)
                return StarRailLightResponse(dot(n, l), visibility);
            float inverseLength = rsqrt(planeLengthSquared);
            f *= inverseLength;
            r *= inverseLength;
            float2 faceUv = v.Uv;
            if (r > 0) faceUv.x = -faceUv.x;
            float sdf = FaceSdfMap.SampleLevel(MdaLinearWrapSampler, faceUv, 0).r;
            float center = FaceSdfParameters.y - (f * .5 + .5);
            float width = .15 / FaceSdfParameters.x;
            float q0 = saturate((sdf - center + width) / (2 * width));
            float band = saturate((q0 - .6) / .3);
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            return StarRailAuthoredSpecular(n, h);
        }
        """;

    private const string HairHlsl = StarRailLightHelper + "\n" + """
        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 StarMcHairParameters;
            float4 StarMcHairSelection;
        };
        Texture2D<float4> StarMcHighlightMap : register(t0, space2);
        Texture2D<float4> StarMcShadowMap : register(t1, space2);

        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return StarRailLightResponse(dot(n, l), visibility);
        }
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            float4 packed = StarMcHighlightMap.SampleLevel(MdaLinearWrapSampler, v.Uv, 0);
            float r = StarMcShadowMap.SampleLevel(MdaLinearWrapSampler, v.Uv, 0).r;
            float highlightChannel = StarMcHairSelection.y > 0 ? packed.a : packed.b;
            float k = saturate(highlightChannel + StarMcHairParameters.x);
            float edge = 1 - saturate(r * StarMcHairParameters.z);
            float x = dot(n, l) * .5 + .5;
            float u = saturate((x - (edge - StarMcHairParameters.w)) / StarMcHairParameters.w);
            float q = .05000000074505806 + (1 - .05000000074505806) * u;
            float f = saturate(q * 3.333333969116211 - 2.000000476837158);
            float response = dot(float3(f, f, f), float3(.2126, .7152, .0722));
            float gate = saturate(response * k);
            float addedBrightness = gate * (k + StarMcHairParameters.y);
            return saturate(SpecularPower.rgb * addedBrightness * StarMcHairSelection.x);
        }
        """;

    private const string BodyHlsl = """
        static float2 StarMcBodyUv = 0;
        static float3 StarMcBodyAlbedoValue = 0;
        static float3 StarMcBodyGeometric = 0;
        static float3 StarMcCamRight = float3(1, 0, 0);
        static float3 StarMcCamUp = float3(0, 1, 0);
        static float3 StarMcCamForward = float3(0, 0, 1);
        static float3 StarMcCamBackward = float3(0, 0, -1);
        static float StarMcIsOrtho = 0;

        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 BodyDetailMetalHighlight; // detailStrength, metallicSaturation, highlightCoefficient, highlightStrength
            float4 BodyShadowWarm;           // shadowCoefficient, shadowSoftness, shadowColorStrength, warmBandStrength
            float4 BodyShadowColor;          // shadowColor.rgb, swizzleMatcapBgr (1=BGR, 0=RGB)
            float4 BodyRimColorStrength;     // rimColor.rgb, rimStrength
            float4 BodyRimShape;             // rimSize, rimDirectionRadians, rotationRadians, normalStrength (0 for Star Rail)
        };
        Texture2D<float4> MatcapNormalMap : register(t0, space2);
        Texture2D<float4> BodyDetailMap : register(t1, space2);
        Texture2D<float4> MatcapPMap : register(t2, space2);
        Texture2D<float4> MatcapColorMap : register(t3, space2);
        StructuredBuffer<MdaTangentCorner> MatcapCorners : register(t12, space2);

        float3 StarMcSafeNormalize(float3 value) {
            float scale = max(abs(value.x), max(abs(value.y), abs(value.z)));
            if (scale <= 0 || !isfinite(scale)) return float3(0, 0, 0);
            return normalize(value / scale);
        }

        void StarMcExtractCameraBasis(
            out float3 camRight,
            out float3 camUp,
            out float3 camForward,
            out float3 camBackward,
            out float isOrtho) {
            float3 col0 = float3(ViewProjection._11, ViewProjection._21, ViewProjection._31);
            float3 col1 = float3(ViewProjection._12, ViewProjection._22, ViewProjection._32);
            float3 col2 = float3(ViewProjection._13, ViewProjection._23, ViewProjection._33);
            float3 col3 = float3(ViewProjection._14, ViewProjection._24, ViewProjection._34);
            if (dot(col3, col3) >= 1e-6) {
                isOrtho = 0;
                camBackward = -StarMcSafeNormalize(col3);
            } else {
                isOrtho = 1;
                camBackward = -StarMcSafeNormalize(col2);
            }
            camRight = StarMcSafeNormalize(col0 - camBackward * dot(col0, camBackward));
            camUp = StarMcSafeNormalize(col1 - camBackward * dot(col1, camBackward));
            camForward = cross(camRight, camUp);
        }

        bool StarMcBodyMappedNormal(
            SurfaceOutput v,
            uint primitive,
            bool front,
            out float3 mapped,
            out float2 uv) {
            mapped = 0;
            uv = 0;
            MdaTangentCorner a = MatcapCorners[primitive * 3];
            MdaTangentCorner b = MatcapCorners[primitive * 3 + 1];
            MdaTangentCorner c = MatcapCorners[primitive * 3 + 2];
            float3 e0 = b.WorldPosition.xyz - a.WorldPosition.xyz;
            float3 e1 = c.WorldPosition.xyz - a.WorldPosition.xyz;
            float3 offset = v.WorldPosition - a.WorldPosition.xyz;
            float aa = dot(e0, e0), ab = dot(e0, e1), bb = dot(e1, e1);
            float denominator = aa * bb - ab * ab;
            if (denominator == 0 || !isfinite(denominator)) return false;
            float d = dot(offset, e0), e = dot(offset, e1);
            float w1 = (bb * d - ab * e) / denominator;
            float w2 = (aa * e - ab * d) / denominator;
            float w0 = 1 - w1 - w2;
            float3 ni = (a.WorldNormal.xyz * w0 + b.WorldNormal.xyz * w1 + c.WorldNormal.xyz * w2) * (front ? 1 : -1);
            float4 tangent = a.Tangent * w0 + b.Tangent * w1 + c.Tangent * w2;
            uv = a.UvParity.xy * w0 + b.UvParity.xy * w1 + c.UvParity.xy * w2;
            float nScale = max(abs(ni.x), max(abs(ni.y), abs(ni.z)));
            if (nScale == 0 || !all(isfinite(ni)) || !all(isfinite(tangent)) || !all(isfinite(uv))) return false;
            float3 geometric = normalize(ni / nScale);
            float2 rg = MatcapNormalMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rg;
            float2 xy = rg * 2 - 1;
            float z = sqrt(max(1 - dot(xy, xy), 0));
            float3 perturbed;
            if (all(tangent == float4(0, 0, 0, 1))) perturbed = ni;
            else {
                tangent *= front ? 1 : -1;
                float3 bitangent = tangent.w * cross(ni, tangent.xyz) * a.UvParity.z;
                perturbed = xy.x * tangent.xyz + xy.y * bitangent + z * ni;
                float mappedScale = max(abs(perturbed.x), max(abs(perturbed.y), abs(perturbed.z)));
                if (mappedScale == 0 || !all(isfinite(perturbed))) return false;
                perturbed = normalize(perturbed / mappedScale);
            }
            float3 mixed = geometric + (perturbed - geometric) * max(BodyRimShape.w, 0);
            float mixedScale = max(abs(mixed.x), max(abs(mixed.y), abs(mixed.z)));
            if (mixedScale == 0 || !all(isfinite(mixed))) return false;
            mapped = normalize(mixed / mixedScale);
            return true;
        }

        float StarMcBodyLightResponseScalar(float nDotL, float visibility) {
            float r = MatcapPMap.SampleLevel(MdaLinearWrapSampler, StarMcBodyUv, 0).r;
            float edge = 1 - BodyShadowWarm.x * r;
            float q = .05 + .95 * saturate((.5 + .5 * nDotL - (edge - BodyShadowWarm.y)) / BodyShadowWarm.y);
            float band = saturate(q * 3.333333969116211 - 2.000000476837158);
            return band * visibility;
        }

        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return StarMcBodyLightResponseScalar(dot(n, l), visibility);
        }

        float3 MdaStylePaletteFactor(SurfaceOutput v, float blend, float nDotL, float visibility) {
            float3 c = max(StarMcBodyAlbedoValue, 0);
            float value = saturate(max(c.x, max(c.y, c.z)));
            float3 squared = lerp(c, c * c, saturate(value + .5));
            float donorValue = max(squared.x, max(squared.y, squared.z));
            float3 recomposed = lerp(squared, donorValue.xxx, value * (1 - value) + .5);
            float3 ratio = float3(c.x > 0 ? recomposed.x / c.x : 0, c.y > 0 ? recomposed.y / c.y : 0, c.z > 0 ? recomposed.z / c.z : 0);
            float3 shadow = BodyShadowColor.rgb * lerp(1, saturate(ratio), .35);
            float3 factor = lerp(shadow, 1, blend);
            float t = saturate(blend);
            float3 bandA = t < .5 ? lerp(1, float3(1, .9, .75), t * 2) : lerp(float3(1, .9, .75), 1, (t - .5) * 2);
            float3 bandB = t < .803021 ? float3(.6, .6, .6) : t < .9 ? lerp(.6, float3(1, .85318, .681425), (t - .803021) / (.9 - .803021)) : lerp(float3(1, .85318, .681425), 1, (t - .9) / .1);
            factor *= lerp(1, bandA * bandB, BodyShadowWarm.w);
            return lerp(1, saturate(factor), BodyShadowWarm.z);
        }

        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            float authoredLobe = pow(max(0, dot(n, h)), SpecularPower.w);
            float highlightBand = smoothstep(.45, .55, authoredLobe);
            return saturate(authoredLobe * SpecularPower.rgb) * highlightBand;
        }

        float StarMcBodyVisibility(SurfaceOutput v) {
            float3 q = v.LightClip.xyz / v.LightClip.w;
            float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
            if (ShadowOptions.z <= 0 || uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) return 1;
            precise float delta = max(q.z - ShadowMap.Sample(ShadowSampler, uv), 0);
            precise float weighted = delta * (ShadowOptions.y > 0 ? 8000 : 1500);
            if (ShadowOptions.y > 0) weighted *= uv.y;
            return 1 - saturate(weighted - .3);
        }

        float3 StarMcBodyMatcapDelta(SurfaceOutput v) {
            if (BodyDetailMetalHighlight.w == 0) return 0;
            float3 n = normalize(v.Normal);
            float3 nc = float3(dot(n, StarMcCamRight), dot(n, StarMcCamUp), -dot(n, StarMcCamForward));
            float sn, cs; sincos(BodyRimShape.z, sn, cs);
            float2 uv = float2(.5 + .5 * (cs * nc.x + sn * nc.z), .5 - .5 * nc.y);
            float3 cap = MatcapColorMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rgb;
            if (BodyShadowColor.w > 0) cap = cap.bgr;
            float3 c = StarMcBodyAlbedoValue;
            float value = max(c.x, max(c.y, c.z));
            float chroma = value - min(c.x, min(c.y, c.z));
            float scale = chroma > 0 ? min(BodyDetailMetalHighlight.y, value / chroma) : 1;
            float3 metal = value + (c - value) * scale;
            float3 changed = lerp(metal, metal * 2.0, cap);
            float k = saturate(BodyDetailMetalHighlight.z + MatcapPMap.SampleLevel(MdaLinearWrapSampler, StarMcBodyUv, 0).b);
            float gate = saturate(StarMcBodyLightResponseScalar(dot(n, IncomingLight.xyz), StarMcBodyVisibility(v)) * k) * BodyDetailMetalHighlight.w;
            return (changed - c) * gate;
        }

        float3 StarMcBodyRim(SurfaceOutput v, float3 c) {
            if (BodyRimColorStrength.w == 0) return c;
            float3 n = normalize(StarMcBodyGeometric);
            float3 eye = StarMcIsOrtho > 0 ? StarMcCamBackward : normalize(EyePosition.xyz - v.WorldPosition);
            float cosine = saturate(abs(dot(n, eye)));
            float transmitted = sqrt(max(1 - (1 - cosine * cosine) * .25, 0));
            float rs = (cosine - 2 * transmitted) / (cosine + 2 * transmitted);
            float rp = (2 * cosine - transmitted) / (2 * cosine + transmitted);
            float fresnel = .5 * (rs * rs + rp * rp);
            float2 nc = float2(dot(n, StarMcCamRight), dot(n, StarMcCamUp));
            float sn, cs; sincos(BodyRimShape.y, sn, cs);
            nc = float2(cs * nc.x - sn * nc.y, sn * nc.x + cs * nc.y) * .5 - .5;
            float gradient = max(1 - dot(nc, nc), 0);
            float directional = saturate(gradient / .2);
            float r = smoothstep(.1, .2, (directional + BodyRimShape.x - 1) * fresnel) * BodyRimColorStrength.w;
            return lerp(c, BodyRimColorStrength.rgb, saturate(r));
        }

        void MdaStyleFinalEntryPre(inout SurfaceOutput v, uint primitive, bool front) {
            StarMcExtractCameraBasis(StarMcCamRight, StarMcCamUp, StarMcCamForward, StarMcCamBackward, StarMcIsOrtho);
            StarMcBodyGeometric = normalize(v.Normal) * (front ? 1 : -1);
            StarMcBodyUv = v.Uv;
            float3 mapped; float2 uv;
            if (StarMcBodyMappedNormal(v, primitive, front, mapped, uv)) {
                StarMcBodyUv = uv;
                v.Normal = mapped;
            }
            StarMcBodyAlbedoValue = MdaComputeBaseColor(v, true);
        }

        float4 MdaStyleFinalEntryPost(SurfaceOutput v, uint primitive, bool front, float4 color) {
            color.rgb += StarMcBodyMatcapDelta(v);
            color.rgb = StarMcBodyRim(v, color.rgb);
            return color;
        }
        """;

    public static IReadOnlyList<StyleShaderModule> CreateAllModules() =>
    [
        new(
            ModuleId: DefaultModuleId,
            HlslSource: DefaultHlsl,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.Specular
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
                | StyleShaderHookMask.Specular
                | StyleShaderHookMask.HasStyleShaderCode
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.None),
        new(
            ModuleId: HairModuleId,
            HlslSource: HairHlsl,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.Specular
                | StyleShaderHookMask.HasStyleShaderCode
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.None),
        new(
            ModuleId: BodyModuleId,
            HlslSource: BodyHlsl,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.PaletteFactor
                | StyleShaderHookMask.Specular
                | StyleShaderHookMask.FinalEntryPre
                | StyleShaderHookMask.FinalEntryPost
                | StyleShaderHookMask.HasStyleShaderCode
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.FrontFace
                | StyleGeometryRequirements.PrimitiveId
                | StyleGeometryRequirements.MikkTangentCorners),
    ];
}
