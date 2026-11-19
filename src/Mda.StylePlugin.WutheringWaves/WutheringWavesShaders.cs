using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.WutheringWaves;

internal static class WutheringWavesShaders
{
    internal const string DefaultModuleId = "default";
    internal const string FaceSdfModuleId = "face-sdf";
    internal const string HairModuleId = "hair";
    internal const string BodyModuleId = "body";

    private const string WutheringLightHelper = """
        float WutheringRawLightResponse(float nDotL, float visibility) {
            // Preserve the [0,1] threshold control while evaluating directly in raw [-1,1] NdotL.
            float threshold = ShadowOptions.w * 2 - 1;
            float band;
            if (IncomingLight.w <= 0) band = nDotL >= threshold ? 1 : 0;
            else {
                float lower = threshold - IncomingLight.w * .5;
                float t = saturate((nDotL - lower) / IncomingLight.w);
                band = t * t * (3 - 2 * t);
            }
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        """;

    private const string WutheringCommonHlsl = WutheringLightHelper + "\n" + """
        float3 WutheringBoundedSpecular(float3 n, float3 h) {
            float authoredLobe = pow(max(0, dot(n, h)), SpecularPower.w);
            float highlightBand = authoredLobe * smoothstep(.10, .30, authoredLobe * authoredLobe);
            return saturate(authoredLobe * SpecularPower.rgb) * highlightBand;
        }
        """;

    private const string DefaultHlsl = WutheringCommonHlsl + "\n" + """
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return WutheringRawLightResponse(dot(n, l), visibility);
        }
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            return WutheringBoundedSpecular(n, h);
        }
        """;

    private const string FaceSdfHlsl = WutheringCommonHlsl + "\n" + """
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
                return WutheringRawLightResponse(dot(n, l), visibility);
            float inverseLength = rsqrt(planeLengthSquared);
            f *= inverseLength;
            r *= inverseLength;
            float2 faceUv = v.Uv;
            if (r > 0) faceUv.x = -faceUv.x;
            float sdf = FaceSdfMap.SampleLevel(MdaLinearWrapSampler, faceUv, 0).r;
            float center = FaceSdfParameters.y - (f * .5 + .5);
            float width = FaceSdfParameters.x;
            float q0 = saturate((sdf - center + width) / (2 * width));
            float band = saturate((q0 - .35) / .15);
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            return WutheringBoundedSpecular(n, h);
        }
        """;

    private const string HairHlsl = WutheringLightHelper + "\n" + """
        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 StarMcHairParameters;
            float4 StarMcHairSelection;
        };
        Texture2D<float4> StarMcHighlightMap : register(t0, space2);
        Texture2D<float4> StarMcShadowMap : register(t1, space2);
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return WutheringRawLightResponse(dot(n, l), visibility);
        }
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            float4 packed = StarMcHighlightMap.SampleLevel(MdaLinearWrapSampler, v.Uv, 0);
            float r = StarMcShadowMap.SampleLevel(MdaLinearWrapSampler, v.Uv, 0).r;
            float ch = StarMcHairSelection.y > 0 ? packed.a : packed.b;
            float k = saturate(ch + StarMcHairParameters.x);
            float edge = 1 - saturate(r * StarMcHairParameters.z);
            float x = dot(n, l) * .5 + .5;
            float u = saturate((x - (edge - StarMcHairParameters.w)) / StarMcHairParameters.w);
            float q = .05000000074505806 + (1 - .05000000074505806) * u;
            float response = q;
            float gate = saturate(response * k);
            float addedBrightness = gate * (k + StarMcHairParameters.y);
            return saturate(SpecularPower.rgb * addedBrightness * StarMcHairSelection.x);
        }
        """;

    private const string BodyHlsl = WutheringCommonHlsl + "\n" + """
        static float2 StarMcBodyUv = 0;
        static float3 StarMcBodyAlbedoValue = 0;
        static float3 StarMcBodyGeometric = 0;
        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 BodyDetailMetalHighlight; // detail, saturation, coefficient, strength
            float4 BodyShadowWarm;           // coefficient, softness, color strength, warm strength
            float4 BodyShadowColor;          // xyz shadow color
            float4 BodyRimColorStrength;     // xyz rim color, rim strength
            float4 BodyRimShape;             // size, direction, normalStrength, rotationRadians
            float4 BodyColorOptions;         // x: matcapIsBgr
        };
        Texture2D<float4> BodyNormalMap : register(t0, space2);
        Texture2D<float4> BodyDetailMap : register(t1, space2);
        Texture2D<float4> BodyMaskMap : register(t2, space2);
        Texture2D<float4> BodyMatcapMap : register(t3, space2);
        StructuredBuffer<MdaTangentCorner> BodyCorners : register(t12, space2);

        float3 MdaSafeNormalize(float3 value, float3 fallback) {
            float scale = max(abs(value.x), max(abs(value.y), abs(value.z)));
            return scale > 0 ? normalize(value / scale) : fallback;
        }
        void MdaExtractCameraBasis(out float3 camRight, out float3 camUp, out float3 camForward, out float3 camBackward, out bool isOrtho) {
            float3 c0 = float3(ViewProjection[0].x, ViewProjection[1].x, ViewProjection[2].x);
            float3 c1 = float3(ViewProjection[0].y, ViewProjection[1].y, ViewProjection[2].y);
            float3 c2 = float3(ViewProjection[0].z, ViewProjection[1].z, ViewProjection[2].z);
            float3 c3 = float3(ViewProjection[0].w, ViewProjection[1].w, ViewProjection[2].w);
            isOrtho = dot(c3, c3) < 1e-6;
            if (isOrtho) {
                camRight = MdaSafeNormalize(c0, float3(1, 0, 0));
                camUp = MdaSafeNormalize(c1, float3(0, 1, 0));
                camForward = cross(camRight, camUp);
                camBackward = MdaSafeNormalize(-c2, -camForward);
            } else {
                float3 viewDir = MdaSafeNormalize(c3, float3(0, 0, 1));
                float3 r = c0 - viewDir * dot(c0, viewDir);
                float3 u = c1 - viewDir * dot(c1, viewDir);
                camRight = MdaSafeNormalize(r, float3(1, 0, 0));
                camUp = MdaSafeNormalize(u, float3(0, 1, 0));
                camForward = cross(camRight, camUp);
                camBackward = -viewDir;
            }
        }
        bool BodyMappedNormal(SurfaceOutput v, uint primitive, bool front, out float3 mapped, out float2 uv) {
            mapped = 0;
            uv = 0;
            MdaTangentCorner a = BodyCorners[primitive * 3];
            MdaTangentCorner b = BodyCorners[primitive * 3 + 1];
            MdaTangentCorner c = BodyCorners[primitive * 3 + 2];
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
            float2 rg = BodyNormalMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rg;
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
            float3 mixed = geometric + (perturbed - geometric) * max(BodyRimShape.z, 0);
            float mixedScale = max(abs(mixed.x), max(abs(mixed.y), abs(mixed.z)));
            if (mixedScale == 0 || !all(isfinite(mixed))) return false;
            mapped = normalize(mixed / mixedScale);
            return true;
        }
        float3 MdaStyleAlbedoTransform(SurfaceOutput v, float3 baseColor) {
            if (BodyDetailMetalHighlight.x == 0) return baseColor;
            float g = BodyDetailMap.SampleLevel(MdaLinearWrapSampler, StarMcBodyUv, 0).g;
            return baseColor + (.5 * BodyDetailMetalHighlight.x * (2 * g - 1)) * baseColor * (1 - baseColor);
        }
        float BodyLightResponseCore(float nDotL, float visibility) {
            float r = BodyMaskMap.SampleLevel(MdaLinearWrapSampler, StarMcBodyUv, 0).r;
            float edge = 1 - BodyShadowWarm.x * r;
            float q = .05 + .95 * saturate((.5 + .5 * nDotL - (edge - BodyShadowWarm.y)) / BodyShadowWarm.y);
            return q * visibility;
        }
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return BodyLightResponseCore(dot(n, l), visibility);
        }
        float3 MdaStylePaletteFactor(SurfaceOutput v, float blend, float nDotL, float visibility) {
            float3 factor = lerp(BodyShadowColor.rgb, 1, blend);
            return lerp(1, saturate(factor), BodyShadowWarm.z);
        }
        float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor) {
            return WutheringBoundedSpecular(n, h);
        }
        float3 BodyMatcapDelta(SurfaceOutput v, float3 camRight, float3 camUp, float3 camForward) {
            if (BodyDetailMetalHighlight.w == 0) return 0;
            float3 n = normalize(v.Normal);
            float3 nc = float3(dot(n, camRight), dot(n, camUp), -dot(n, camForward));
            float sn, cs;
            sincos(BodyRimShape.w, sn, cs);
            float2 uv = float2(.5 + .5 * (cs * nc.x + sn * nc.z), .5 - .5 * nc.y);
            float4 sampled = BodyMatcapMap.SampleLevel(MdaLinearWrapSampler, uv, 0);
            float3 cap = BodyColorOptions.x > 0 ? sampled.bgr : sampled.rgb;
            float3 c = StarMcBodyAlbedoValue;
            float value = max(c.x, max(c.y, c.z));
            float chroma = value - min(c.x, min(c.y, c.z));
            float scale = chroma > 0 ? min(BodyDetailMetalHighlight.y, value / chroma) : 1;
            float3 metal = value + (c - value) * scale;
            float3 changed = lerp(metal, metal * 1.5, cap);
            float k = saturate(BodyDetailMetalHighlight.z + BodyMaskMap.SampleLevel(MdaLinearWrapSampler, StarMcBodyUv, 0).b);
            float gate = saturate(BodyLightResponseCore(dot(n, IncomingLight.xyz), MdaPrimaryShadowVisibility(v)) * k) * BodyDetailMetalHighlight.w;
            return (changed - c) * gate;
        }
        float3 BodyRim(SurfaceOutput v, float3 c, float3 camRight, float3 camUp, float3 camBackward, bool isOrtho) {
            if (BodyRimColorStrength.w == 0) return c;
            float3 n = normalize(StarMcBodyGeometric);
            float3 eye = isOrtho ? camBackward : normalize(EyePosition.xyz - v.WorldPosition);
            float cosine = saturate(abs(dot(n, eye)));
            float transmitted = sqrt(max(1 - (1 - cosine * cosine) * .25, 0));
            float rs = (cosine - 2 * transmitted) / (cosine + 2 * transmitted);
            float rp = (2 * cosine - transmitted) / (2 * cosine + transmitted);
            float fresnel = .5 * (rs * rs + rp * rp);
            float2 nc = float2(dot(n, camRight), dot(n, camUp));
            float sn, cs;
            sincos(BodyRimShape.y, sn, cs);
            nc = float2(cs * nc.x - sn * nc.y, sn * nc.x + cs * nc.y) * .5 - .5;
            float gradient = frac(atan2(nc.y, nc.x) / 6.28318530718 + .5);
            float directional = saturate(gradient / .2);
            float r = smoothstep(.1, .2, (directional + BodyRimShape.x - 1) * fresnel) * BodyRimColorStrength.w;
            return lerp(c, BodyRimColorStrength.rgb, saturate(r));
        }
        void MdaStyleFinalEntryPre(inout SurfaceOutput v, uint mdaPrimitive, bool mdaFront) {
            StarMcBodyGeometric = normalize(v.Normal) * (mdaFront ? 1 : -1);
            StarMcBodyUv = v.Uv;
            float3 mapped;
            float2 uv;
            if (BodyMappedNormal(v, mdaPrimitive, mdaFront, mapped, uv)) {
                StarMcBodyUv = uv;
                v.Normal = mapped;
            }
            StarMcBodyAlbedoValue = MdaComputeAlbedo(v);
        }
        float4 MdaStyleFinalEntryPost(SurfaceOutput v, uint mdaPrimitive, bool mdaFront, float4 color) {
            float3 camRight, camUp, camForward, camBackward;
            bool isOrtho;
            MdaExtractCameraBasis(camRight, camUp, camForward, camBackward, isOrtho);
            color.rgb += BodyMatcapDelta(v, camRight, camUp, camForward);
            color.rgb = BodyRim(v, color.rgb, camRight, camUp, camBackward, isOrtho);
            return color;
        }
        """;

    internal static IReadOnlyList<StyleShaderModule> CreateModules() =>
    [
        new StyleShaderModule(
            ModuleId: DefaultModuleId,
            HlslSource: DefaultHlsl,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.Specular
                | StyleShaderHookMask.AllowStylizedShadingWrapper
                | StyleShaderHookMask.DisableStylizedSphereMaps
                | StyleShaderHookMask.RequiresOutlineColorScale
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.None,
            DisableStylizedSphereMaps: true),
        new StyleShaderModule(
            ModuleId: FaceSdfModuleId,
            HlslSource: FaceSdfHlsl,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.Specular
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.None),
        new StyleShaderModule(
            ModuleId: HairModuleId,
            HlslSource: HairHlsl,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.Specular
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.None),
        new StyleShaderModule(
            ModuleId: BodyModuleId,
            HlslSource: BodyHlsl,
            Hooks: StyleShaderHookMask.AlbedoTransform
                | StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.PaletteFactor
                | StyleShaderHookMask.Specular
                | StyleShaderHookMask.FinalEntryPre
                | StyleShaderHookMask.FinalEntryPost
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.FrontFace
                | StyleGeometryRequirements.PrimitiveId
                | StyleGeometryRequirements.MikkTangentCorners)
    ];
}
