using System.Collections.ObjectModel;
using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.Endfield;

internal static class EndfieldShaders
{
    internal const string DefaultModuleId = "default";
    internal const string FaceSdfModuleId = "face-sdf";

    private const string EndfieldLightCommonHlsl = """
        float EndfieldLightResponseCore(float nDotL, float visibility) {
            float halfLambert = saturate(nDotL * .5 + .5);
            float band;
            if (IncomingLight.w <= 0) band = halfLambert >= ShadowOptions.w ? 1 : 0;
            else {
                float lowerThreshold = ShadowOptions.w - IncomingLight.w;
                float upperThreshold = ShadowOptions.w + IncomingLight.w;
                float halfTransition = IncomingLight.w * .25;
                float lowerBand = smoothstep(lowerThreshold - halfTransition, lowerThreshold + halfTransition, halfLambert);
                float upperBand = smoothstep(upperThreshold - halfTransition, upperThreshold + halfTransition, halfLambert);
                band = .5 * lowerBand + .5 * upperBand;
            }
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        """;

    private const string DefaultHlsl = EndfieldLightCommonHlsl + "\n" + """
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return EndfieldLightResponseCore(dot(n, l), visibility);
        }
        """;

    private const string FaceSdfHlsl = EndfieldLightCommonHlsl + "\n" + """
        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 FaceSdfForward;
            float4 FaceSdfRight;
            float4 FaceSdfParameters;
        };
        Texture2D<float4> FaceSdfMap : register(t0, space2);
        Texture2D<float4> FaceCombinedMap : register(t1, space2);
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            float f = dot(FaceSdfForward.xyz, l);
            float r = dot(FaceSdfRight.xyz, l);
            float planeLengthSquared = f * f + r * r;
            if (planeLengthSquared <= 1e-8)
                return EndfieldLightResponseCore(dot(n, l), visibility);
            float inverseLength = rsqrt(planeLengthSquared);
            f *= inverseLength;
            r *= inverseLength;
            float2 faceUv = v.Uv;
            if (r <= 0) faceUv.x = 1 - faceUv.x;
            float3 sdf = FaceSdfMap.SampleLevel(MdaLinearWrapSampler, faceUv, 0).rgb;
            float2 combined = FaceCombinedMap.SampleLevel(MdaLinearWrapSampler, v.Uv, 0).rg;
            float value = saturate(saturate(sdf.r + sdf.b) * sdf.g - combined.g);
            float scale = combined.r > 0 ? combined.r : 1;
            float center = FaceSdfParameters.y - (f * .5 + .5);
            float lower = saturate(scale * (center - FaceSdfParameters.x));
            float upper = center + FaceSdfParameters.x;
            float band = upper == lower ? 0 : saturate((value - lower) / (upper - lower));
            if (EyePosition.w < 1) band = lerp(1, band, EyePosition.w);
            if (GroundDirection.w < 1) visibility = lerp(1, visibility, GroundDirection.w);
            return band * visibility;
        }
        """;

    private const string ComposableSurfaceHlsl = EndfieldLightCommonHlsl + "\n" + """
        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            return EndfieldLightResponseCore(dot(n, l), visibility);
        }

        cbuffer PluginStyleConstants : register(b0, space2) {
        #ifdef ENDFIELD_HAIR
            float4 EndfieldPaletteBrightness;
        #endif
        #ifdef ENDFIELD_RIM
            float4 RimBaseRange;
            float4 RimColorRadial;
            float4 RimParameters;
        #endif
        #ifdef ENDFIELD_MATCAP
            float4 MatcapBaseBaseline;
            float4 MatcapHighlightNormal;
            float4 MatcapRotationGate;
            float4 MatcapLightControl;
            float4 MatcapReserved;
            float4 MatcapRangeDirectionWidth;
            float4 MatcapColorOptions;
        #endif
        #ifdef ENDFIELD_PALETTE
            float4 PaletteEmission;
            float4 PaletteLight;
            float4 PaletteModes;
        #endif
        #ifdef ENDFIELD_BASE_IMAGE
            float4 BaseImageBaseContrast;
            float4 BaseImageModelShadow;
            float4 BaseImageShadowTint;
            float4 BaseImageMultiplierSaturation;
            float4 BaseImageOptions;
            float4 BaseImageModes;
            float4 BaseImageSourceDirectionWidth;
            float4 BaseImageSourceOptions;
        #endif
        };

        #ifdef ENDFIELD_HAIR
        Texture2D<float4> EndfieldGreenMap : register(t0, space2);
        #endif
        #ifdef ENDFIELD_MATCAP
        Texture2D<float4> MatcapNormalMap : register(t1, space2);
        Texture2D<float4> MatcapPMap : register(t2, space2);
        Texture2D<float4> MatcapColorMap : register(t3, space2);
        StructuredBuffer<MdaTangentCorner> MatcapCorners : register(t12, space2);
        #endif
        #ifdef ENDFIELD_PALETTE
        Texture2D<float4> PaletteEmissionMap : register(t4, space2);
        Texture2D<float4> PaletteReceivedLightMap : register(t5, space2);
        #endif
        #ifdef ENDFIELD_BASE_IMAGE
        Texture2D<float4> BaseImageColorMap : register(t6, space2);
        Texture2D<float4> BaseImageDetailMap : register(t7, space2);
        Texture2D<float4> BaseImageShadowMap : register(t8, space2);
        StructuredBuffer<float4> BaseImageShadowLut : register(t13, space2);
        #endif

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

        #ifdef ENDFIELD_HAIR
        float EndfieldRadialScalar(SurfaceOutput v, float3 camBackward, bool isOrtho) {
            float normalScale = max(abs(v.Normal.x), max(abs(v.Normal.y), abs(v.Normal.z)));
            float seedScale = max(abs(v.RawSeed.x), max(abs(v.RawSeed.y), abs(v.RawSeed.z)));
            if (normalScale == 0 || seedScale == 0) return 0;
            float3 n = normalize(v.Normal / normalScale);
            float3 projectedCross = cross(v.RawSeed / seedScale, n);
            float crossScale = max(abs(projectedCross.x), max(abs(projectedCross.y), abs(projectedCross.z)));
            if (crossScale == 0) return 0;
            float3 tangent = cross(n, normalize(projectedCross / crossScale));
            float3 binormal = normalize(cross(tangent, n));
            float3 incoming = isOrtho ? camBackward : EyePosition.xyz - v.WorldPosition;
            float incomingScale = max(abs(incoming.x), max(abs(incoming.y), abs(incoming.z)));
            if (incomingScale == 0) return 0;
            incoming = normalize(incoming / incomingScale);
            float3 u = incoming + float3(0, 0, .5000999569892883);
            float uScale = max(abs(u.x), max(abs(u.y), abs(u.z)));
            if (uScale == 0) return 0;
            u = normalize(u / uScale);
            float q = saturate((abs(dot(u, binormal)) - .10000000149011612) / (0 - .10000000149011612));
            float green = EndfieldGreenMap.SampleLevel(MdaLinearWrapSampler, v.Uv, 0).g;
            return min(q, .75) * green * (EndfieldPaletteBrightness.w + 1.5001001358032227);
        }
        float4 MdaStylePrimarySurfacePost(SurfaceOutput v, float4 color) {
            float3 camRight, camUp, camForward, camBackward;
            bool isOrtho;
            MdaExtractCameraBasis(camRight, camUp, camForward, camBackward, isOrtho);
            color.rgb += EndfieldPaletteBrightness.rgb * EndfieldRadialScalar(v, camBackward, isOrtho);
            return color;
        }
        #endif

        #ifdef ENDFIELD_RIM
        float EndfieldExactFresnel(float cosine, bool front) {
            float eta = front ? 1.9999998807907104 : rcp(1.9999998807907104);
            float transmittedSquared = (1 - cosine * cosine) / (eta * eta);
            if (transmittedSquared >= 1) return 1;
            float ct = sqrt(max(1 - transmittedSquared, 0));
            float rs = (cosine - eta * ct) / (cosine + eta * ct);
            float rp = (eta * cosine - ct) / (eta * cosine + ct);
            return (rs * rs + rp * rp) * .5;
        }
        float EndfieldRimMask(SurfaceOutput v, bool front, float3 camRight, float3 camUp, float3 camForward, float3 camBackward, bool isOrtho) {
            float normalScale = max(abs(v.Normal.x), max(abs(v.Normal.y), abs(v.Normal.z)));
            float3 incoming = isOrtho ? camBackward : EyePosition.xyz - v.WorldPosition;
            float incomingScale = max(abs(incoming.x), max(abs(incoming.y), abs(incoming.z)));
            if (normalScale == 0 || incomingScale == 0) return 0;
            float3 n = normalize(v.Normal / normalScale) * (front ? 1 : -1);
            float3 view = normalize(incoming / incomingScale);
            float3 nc = float3(dot(n, camRight), dot(n, camUp), -dot(n, camForward));
            float sine, cosine;
            sincos(RimParameters.z, sine, cosine);
            float3 mapped = float3(cosine * .5 * nc.x - sine * .5 * nc.y - .5,
                sine * .5 * nc.x + cosine * .5 * nc.y - .5, nc.z);
            float radial = max(0.9999989867210388 - length(mapped), 0);
            float gradient = saturate(saturate(radial * radial) * 5);
            float fresnel = EndfieldExactFresnel(saturate(abs(dot(view, n))), front);
            float u = saturate((gradient + (RimParameters.x - 0.9999998807907104)) * fresnel * 10 - 1);
            return u * u * (3 - 2 * u) * RimParameters.y;
        }
        float3 EndfieldRimDelta(SurfaceOutput v, bool front, float3 camRight, float3 camUp, float3 camForward, float3 camBackward, bool isOrtho) {
            float rim = EndfieldRimMask(v, front, camRight, camUp, camForward, camBackward, isOrtho);
            if (rim == 0) return 0;
            float factor = saturate(rim);
            float3 a = RimBaseRange.rgb, b = RimColorRadial.rgb;
            float3 lower = a * ((1 - factor) + 2 * factor * b);
            float3 upper = 1 - ((1 - factor) + 2 * factor * (1 - b)) * (1 - a);
            float3 overlay = float3(a.x < .5 ? lower.x : upper.x, a.y < .5 ? lower.y : upper.y, a.z < .5 ? lower.z : upper.z);
            float baselineStrength = RimBaseRange.w + RimColorRadial.w;
            return overlay * (baselineStrength + rim) - a * baselineStrength;
        }
        #endif

        #ifdef ENDFIELD_MATCAP
        bool EndfieldMatcapMappedNormal(SurfaceOutput v, uint primitive, bool front, out float3 mapped, out float2 uv, out float3 baseGeometric, out float3 basePerturbed) {
            mapped = 0; uv = 0; baseGeometric = 0; basePerturbed = 0;
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
            float3 mixed = geometric + (perturbed - geometric) * max(MatcapHighlightNormal.w, 0);
            float mixedScale = max(abs(mixed.x), max(abs(mixed.y), abs(mixed.z)));
            if (mixedScale == 0 || !all(isfinite(mixed))) return false;
            mapped = normalize(mixed / mixedScale);
            baseGeometric = geometric;
            basePerturbed = perturbed;
            return true;
        }
        float3 EndfieldSharedNormal(float3 geometric, float3 perturbed, float strength) {
            float3 mixed = geometric + (perturbed - geometric) * strength;
            float scale = max(abs(mixed.x), max(abs(mixed.y), abs(mixed.z)));
            return scale == 0 ? float3(0, 0, 0) : normalize(mixed / scale);
        }
        float EndfieldSharedDirectionalQ(float3 normal, float4 directionWidth) {
            precise float u = dot(normal, directionWidth.xyz) * .5 + .5;
            precise float a = directionWidth.w - .5;
            precise float maximum = 1 - a;
            precise float minimum = maximum * directionWidth.w;
            precise float q = maximum == minimum ? 0 : ((u - minimum) / (maximum - minimum)) * 1.5;
            return clamp(q, 0, 1.5);
        }
        float EndfieldMatcapRangeStrength(float k, float q) {
            if (MatcapReserved.x == 0) return MatcapBaseBaseline.w;
            return MatcapReserved.y + (MatcapReserved.z * k) * saturate(q * k);
        }
        float3 EndfieldMatcapOverlay(float3 a, float3 b, float factor) {
            float3 lower = a * ((1 - factor) + 2 * factor * b);
            float3 upper = 1 - ((1 - factor) + 2 * factor * (1 - b)) * (1 - a);
            return float3(a.x < .5 ? lower.x : upper.x, a.y < .5 ? lower.y : upper.y, a.z < .5 ? lower.z : upper.z);
        }

        #ifdef ENDFIELD_PALETTE
        float EndfieldPaletteWave(float u, float density) {
            precise float scale = 100 * density;
            precise float x = (u * scale + .000001) * .999999;
            return .5 + .5 * sin(20 * x - 1.5707963267948966);
        }
        float3 EndfieldPaletteHsv(float3 rgb) {
            float hi = max(rgb.r, max(rgb.g, rgb.b)), lo = min(rgb.r, min(rgb.g, rgb.b));
            float chroma = hi - lo;
            float saturation = hi == 0 ? 0 : chroma / hi;
            float hue = 0;
            if (saturation != 0) {
                if (rgb.r == hi) hue = (rgb.g - rgb.b) / chroma;
                else if (rgb.g == hi) hue = 2 + (rgb.b - rgb.r) / chroma;
                else hue = 4 + (rgb.r - rgb.g) / chroma;
                hue /= 6;
                if (hue < 0) hue += 1;
            }
            hue = frac(hue + 1);
            saturation = saturate(saturation);
            float value = hi * .7999999523162842;
            if (saturation == 0) return value.xxx;
            float sector = hue * 6, whole = floor(sector), fraction = sector - whole;
            float low = value * (1 - saturation);
            float falling = value * (1 - saturation * fraction);
            float rising = value * (1 - saturation * (1 - fraction));
            if (whole == 0) return float3(value, rising, low);
            if (whole == 1) return float3(falling, value, low);
            if (whole == 2) return float3(low, value, rising);
            if (whole == 3) return float3(low, falling, value);
            if (whole == 4) return float3(rising, low, value);
            return float3(value, low, falling);
        }
        float EndfieldPrimaryBlend(SurfaceOutput v) {
            return EndfieldLightResponseCore(dot(normalize(v.Normal), IncomingLight.xyz), MdaPrimaryShadowVisibility(v));
        }
        float3 EndfieldPaletteFactor(float blend) {
            return blend + ToonShadowColor.rgb * (1 - blend);
        }
        float EndfieldLiveScale(float3 budget, float3 addition) {
            if (all(addition <= budget)) return 1;
            float scale = 1;
            if (addition.x > 0) scale = min(scale, budget.x / addition.x);
            if (addition.y > 0) scale = min(scale, budget.y / addition.y);
            if (addition.z > 0) scale = min(scale, budget.z / addition.z);
            return saturate(scale);
        }
        float3 EndfieldLiveIncident(SurfaceOutput v, float3 normal, float3 geometric) {
            float3 result = max(PaletteModes.yzw, 0) * saturate(dot(normal, IncomingLight.xyz)) * MdaPrimaryShadowVisibility(v);
        #ifdef MDA_HAS_NATIVE_LIGHTS
            if (NativeAtlasLayout.z > 0) result += NativeAtlasLayout.z.xxx;
            [loop] for (uint i = 0; i < (uint)NativeInfo.x; i++) {
                if ((NativeInfo.y & (1u << i)) == 0) continue;
                NativeLight source = NativeLights[i];
                [loop] for (uint sample = 0; sample < (source.PositionKind.w == 3 ? 4u : 1u); sample++) {
                    NativeLight light = NativeSample(source, sample);
                    float3 delta = light.PositionKind.xyz - v.WorldPosition;
                    float distanceSquared = max(dot(delta, delta), .0001);
                    float3 l = light.PositionKind.w == 0 ? -light.DirectionRange.xyz : delta * rsqrt(distanceSquared);
                    float attenuation = NativeLightAttenuation(light.PositionKind, light.DirectionRange, light.ConeShadow, distanceSquared, l);
                    float visibility = NativeVisibility(i, light, v.WorldPosition, geometric, l, 0, 0, sample);
                    result += max(light.ColorIntensity.rgb, 0) * (light.ColorIntensity.w * attenuation * saturate(dot(normal, l)) * visibility);
                }
            }
        #endif
            return result;
        }
        float3 EndfieldLiveAvailableBudget(SurfaceOutput v, uint primitive) {
            float3 diffuseLit = MdaComputeDiffuseLit(v);
            float3 budget = max(diffuseLit * max(1 - EndfieldPaletteFactor(EndfieldPrimaryBlend(v)), 0), 0);
            float3 direct = 0, indirect = 0;
        #ifdef MDA_HAS_NATIVE_LIGHTS
            direct = NativeDirect(v);
        #endif
        #ifdef MDA_ENTRY_GI
            int3 pixel = int3((int2)v.Position.xy, 0);
            float4 receiver = SurfaceGiNormal.Load(pixel);
            if (receiver.w == (float)(GiTriangleBase + primitive + 1) && SurfaceGiPosition.Load(pixel).w == v.Position.z) {
                float4 transport = SurfaceIndirect[pixel.y * GiSurfaceWidth + pixel.x];
                if (GiAmbientShare.w >= 1) GiToonPaletteDelta(v, transport.rgb, indirect);
                else indirect = transport.rgb * GiSurfaceStrength;
            }
        #endif
        #ifdef MDA_HAS_NATIVE_LIGHTS
            float scale = 1;
        #ifdef MDA_ENTRY_GI
            scale = GiAmbientShare.w >= 1 ? EndfieldLiveScale(budget, direct + indirect) : EndfieldLiveScale(budget, direct);
            direct *= scale;
            if (GiAmbientShare.w >= 1) indirect *= scale;
        #else
            scale = EndfieldLiveScale(budget, direct);
            direct *= scale;
        #endif
        #endif
            return max(budget - max(direct + indirect, 0), 0);
        }
        #endif

        #ifdef ENDFIELD_BASE_IMAGE
        float3 EndfieldBaseImageLut(uint row, float q) {
            float x = saturate(q) * 257 - .5;
            int j = (int)floor(x);
            float f = x - j;
            float3 a = BaseImageShadowLut[row * 257 + (uint)clamp(j, 0, 256)].rgb;
            float3 b = BaseImageShadowLut[row * 257 + (uint)clamp(j + 1, 0, 256)].rgb;
            return (1 - f) * a + f * b;
        }
        float3 EndfieldBaseImageShadow(float q) {
            float k = saturate(BaseImageShadowTint.w);
            float3 gray = EndfieldBaseImageLut(0, q), warm = EndfieldBaseImageLut(1, q);
            float3 a = (1 - k) * gray + k * warm;
            float3 b = EndfieldMatcapOverlay(a, BaseImageShadowTint.rgb, 1);
            float3 c = (1 - saturate(q)) * BaseImageModelShadow.rgb + saturate(q);
            float3 d = (1 - k) * c + k * c * EndfieldBaseImageLut(2, q);
            precise float luminance = BaseImageShadowTint.r * .2126 + BaseImageShadowTint.g * .7152 + BaseImageShadowTint.b * .0722;
            return luminance < 1 ? b : d;
        }
        float3 EndfieldBaseImageHsv(float3 rgb, float multiplier) {
            float hi = max(rgb.r, max(rgb.g, rgb.b)), lo = min(rgb.r, min(rgb.g, rgb.b));
            float chroma = hi - lo, saturation = hi == 0 ? 0 : chroma / hi, hue = 0;
            if (saturation != 0) {
                if (rgb.r == hi) hue = (rgb.g - rgb.b) / chroma;
                else if (rgb.g == hi) hue = 2 + (rgb.b - rgb.r) / chroma;
                else hue = 4 + (rgb.r - rgb.g) / chroma;
                hue /= 6;
                if (hue < 0) hue += 1;
            }
            hue = frac(hue + 1);
            saturation = saturate(saturation * multiplier);
            if (saturation == 0) return hi.xxx;
            float sector = hue * 6, whole = floor(sector), fraction = sector - whole;
            float low = hi * (1 - saturation), falling = hi * (1 - saturation * fraction), rising = hi * (1 - saturation * (1 - fraction));
            if (whole == 0) return float3(hi, rising, low);
            if (whole == 1) return float3(falling, hi, low);
            if (whole == 2) return float3(low, hi, rising);
            if (whole == 3) return float3(low, falling, hi);
            if (whole == 4) return float3(rising, low, hi);
            return float3(hi, low, falling);
        }
        float3 EndfieldBaseImageSourceNormal(float3 geometric, float3 perturbed, float strength) {
            return MdaSafeNormalize(geometric + (perturbed - geometric) * strength, float3(0, 0, 0));
        }
        float EndfieldBaseImageFacing(SurfaceOutput v, float3 normal, float3 camBackward, bool isOrtho) {
            float3 view = isOrtho ? camBackward : EyePosition.xyz - v.WorldPosition;
            float facing = saturate(abs(dot(normal, MdaSafeNormalize(view, float3(0, 0, 0)))));
            float blend = BaseImageSourceOptions.y;
            if (blend != .5) {
                blend = clamp(blend, 0, .99999);
                float exponent = blend < .5 ? 2 * blend : .5 / (1 - blend);
                facing = exponent == 0 ? 1 : pow(facing, exponent);
            }
            return 1 - facing;
        }
        float EndfieldBaseImageDirectionalQ(float3 normal) {
            precise float u = dot(normal, BaseImageSourceDirectionWidth.xyz) * .5 + .5;
            precise float a = BaseImageSourceDirectionWidth.w - .5;
            precise float maximum = 1 - a;
            precise float minimum = maximum * BaseImageSourceDirectionWidth.w;
            precise float q = maximum == minimum ? 0 : ((u - minimum) / (maximum - minimum)) * 1.5;
            return clamp(q, 0, 1.5);
        }
        float3 EndfieldBaseImageCpre(SurfaceOutput v, bool front, float2 uv, float3 geometric, float3 perturbed, float rangeQ, float3 camBackward, bool isOrtho) {
            float3 texColor = BaseImageOptions.z > 0
                ? (BaseImageModes.y > 0 ? BaseImageColorMap.SampleLevel(MdaLinearWrapSampler, uv, 0).bgr : BaseImageColorMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rgb)
                : BaseImageBaseContrast.rgb;
            float3 x = texColor;
            if (BaseImageSourceOptions.x > 0) {
                float f = EndfieldBaseImageFacing(v, EndfieldBaseImageSourceNormal(geometric, perturbed, 1), camBackward, isOrtho);
                x = (1 - f) * x + f * x * x;
            }
            if (!front) x = x * x;
            x = (1 - BaseImageBaseContrast.w) * x + BaseImageBaseContrast.w * x * x;
            x = EndfieldBaseImageHsv(x, BaseImageMultiplierSaturation.w) * BaseImageMultiplierSaturation.rgb;
            float detail = BaseImageOptions.w > 0 ? BaseImageDetailMap.SampleLevel(MdaLinearWrapSampler, uv, 0).b : BaseImageOptions.x;
            x = .5 * x + .5 * x * detail;
            float h = saturate(BaseImageOptions.y);
            if (h > 0) {
                float q = BaseImageModes.x > 0 ? BaseImageShadowMap.SampleLevel(MdaLinearWrapSampler, uv, 0).r : BaseImageModelShadow.w;
                if (BaseImageSourceOptions.z > 0) {
                    if (MatcapReserved.x > 0 && all(BaseImageSourceDirectionWidth == MatcapRangeDirectionWidth) && BaseImageSourceOptions.w == MatcapReserved.w) q = rangeQ;
                    else q = EndfieldBaseImageDirectionalQ(EndfieldBaseImageSourceNormal(geometric, perturbed, BaseImageSourceOptions.w));
                }
                float3 shadow = EndfieldBaseImageShadow(q);
                x = (1 - h) * x + h * x * shadow;
            }
            return x;
        }
        #endif

        float3 EndfieldMatcapComposeDelta(
            SurfaceOutput v, uint primitive, bool front, float3 changed, float2 uv,
            float3 geometric, float3 perturbed, float k, float rangeQ,
            float3 camRight, float3 camUp, float3 camForward, float3 camBackward, bool isOrtho
        #ifdef ENDFIELD_BASE_IMAGE
            , float3 effectiveCpre
        #endif
        ) {
        #ifdef ENDFIELD_PALETTE
            #ifdef ENDFIELD_BASE_IMAGE
            float3 initial = effectiveCpre;
            #else
            float3 initial = MatcapBaseBaseline.rgb;
            #endif
            #ifdef ENDFIELD_RIM
            float r = EndfieldRimMask(v, front, camRight, camUp, camForward, camBackward, isOrtho);
            float3 tint = RimColorRadial.rgb;
            #else
            float r = 0;
            float3 tint = float3(0, 0, 0);
            #endif
            #ifdef ENDFIELD_HAIR
            float p = EndfieldRadialScalar(v, camBackward, isOrtho);
            #else
            float p = 0;
            #endif
            float3 partial = changed;
            if (PaletteEmission.x != 0) {
                float blue = PaletteEmission.w > 0 ? PaletteEmissionMap.SampleLevel(MdaLinearWrapSampler, uv, 0).b : PaletteEmission.y;
                partial += (initial * (2 * blue)) * PaletteEmission.x;
            }
            float influence = saturate(PaletteLight.w);
            if (influence > 0) {
                float3 light = PaletteModes.x == 1 ? PaletteReceivedLightMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rgb : PaletteLight.rgb;
                if (PaletteModes.x == 2) light = 0;
                partial += influence * light;
            }
            float3 litRim = EndfieldMatcapOverlay(partial, tint, saturate(r));
            float wave = EndfieldPaletteWave(uv.x, PaletteEmission.z);
            float rangeStrength = EndfieldMatcapRangeStrength(k, rangeQ);
            float3 emission = lerp(litRim, EndfieldPaletteHsv(litRim), saturate(wave)) * (rangeStrength + r + p);
            if (PaletteModes.x == 2 && influence > 0) {
                float3 normal = EndfieldSharedNormal(geometric, perturbed, 1);
                float3 livePartial = partial + influence * max(partial, 0) * EndfieldLiveIncident(v, normal, geometric);
                float3 liveRim = EndfieldMatcapOverlay(livePartial, tint, saturate(r));
                float3 candidate = lerp(liveRim, EndfieldPaletteHsv(liveRim), saturate(wave)) * (rangeStrength + r + p);
                float3 addition = candidate - emission;
                emission += addition * EndfieldLiveScale(EndfieldLiveAvailableBudget(v, primitive), max(addition, 0));
            }
            float3 oldHair = MatcapBaseBaseline.rgb * p;
            float3 oldRim = r == 0 ? float3(0, 0, 0) : EndfieldMatcapOverlay(MatcapBaseBaseline.rgb, tint, saturate(r)) * (MatcapBaseBaseline.w + r) - MatcapBaseBaseline.rgb * MatcapBaseBaseline.w;
            return emission - initial * MatcapBaseBaseline.w - oldHair - oldRim;
        #else
            float3 initial = MatcapBaseBaseline.rgb;
            float rangeStrength = EndfieldMatcapRangeStrength(k, rangeQ);
            if (MatcapRotationGate.w == 0) {
                if (MatcapReserved.x == 0) return (changed - initial) * MatcapBaseBaseline.w;
                return changed * rangeStrength - initial * MatcapBaseBaseline.w;
            }
            #ifdef ENDFIELD_RIM
            float r = EndfieldRimMask(v, front, camRight, camUp, camForward, camBackward, isOrtho);
            #else
            float r = 0;
            #endif
            #ifdef ENDFIELD_HAIR
            float p = EndfieldRadialScalar(v, camBackward, isOrtho);
            #else
            float p = 0;
            #endif
            float strength = MatcapBaseBaseline.w + r + p;
            #ifdef ENDFIELD_RIM
            float3 result = (EndfieldMatcapOverlay(changed, RimColorRadial.rgb, saturate(r)) - EndfieldMatcapOverlay(initial, RimColorRadial.rgb, saturate(r))) * strength;
            if (MatcapReserved.x > 0) result += EndfieldMatcapOverlay(changed, RimColorRadial.rgb, saturate(r)) * (rangeStrength - MatcapBaseBaseline.w);
            #else
            float3 result = (changed - initial) * strength;
            if (MatcapReserved.x > 0) result += changed * (rangeStrength - MatcapBaseBaseline.w);
            #endif
            return result;
        #endif
        }

        float3 EndfieldMatcapDelta(SurfaceOutput v, uint primitive, bool front, float3 camRight, float3 camUp, float3 camForward, float3 camBackward, bool isOrtho) {
            float3 mapped;
            float2 uv;
            float3 baseGeometric, basePerturbed;
            if (!EndfieldMatcapMappedNormal(v, primitive, front, mapped, uv, baseGeometric, basePerturbed)) return 0;
            float3 nc = float3(dot(mapped, camRight), dot(mapped, camUp), -dot(mapped, camForward));
            float sine, cosine;
            sincos(MatcapRotationGate.x, sine, cosine);
            float2 colorUv = float2(.5 + .5 * (cosine * nc.x + sine * nc.z), .5 - .5 * nc.y);
            float4 sampled = MatcapColorMap.SampleLevel(MdaLinearWrapSampler, colorUv, 0);
            float3 capRgb = MatcapColorOptions.x > 0 ? sampled.bgr : sampled.rgb;
            float3 imageColor = capRgb * sampled.a;
            precise float a = 1 - (1 - MatcapRotationGate.z);
            float fromMin = a * MatcapRotationGate.z, fromMax = a;
            float value = .5 * dot(mapped, MatcapLightControl.xyz) + .5;
            float s = fromMax == fromMin ? 0 : 1.5 * saturate((value - fromMin) / (fromMax - fromMin));
            float4 p = MatcapPMap.SampleLevel(MdaLinearWrapSampler, uv, 0);
            float k = saturate(MatcapRotationGate.y + saturate(p.r + p.a));
            float rangeQ = MatcapReserved.x > 0 ? EndfieldSharedDirectionalQ(EndfieldSharedNormal(baseGeometric, basePerturbed, MatcapReserved.w), MatcapRangeDirectionWidth) : 0;
            float gate = saturate(s * k);
            float3 screen = 1 - (1 - imageColor) * (1 - MatcapHighlightNormal.rgb);
        #ifdef ENDFIELD_BASE_IMAGE
            float3 effectiveCpre = EndfieldBaseImageCpre(v, front, uv, baseGeometric, basePerturbed, rangeQ, camBackward, isOrtho);
            float3 changed = effectiveCpre * ((1 - gate) + gate * screen);
            return EndfieldMatcapComposeDelta(v, primitive, front, changed, uv, baseGeometric, basePerturbed, k, rangeQ, camRight, camUp, camForward, camBackward, isOrtho, effectiveCpre);
        #else
            float3 changed = MatcapBaseBaseline.rgb * ((1 - gate) + gate * screen);
            return EndfieldMatcapComposeDelta(v, primitive, front, changed, uv, baseGeometric, basePerturbed, k, rangeQ, camRight, camUp, camForward, camBackward, isOrtho);
        #endif
        }
        #endif

        #if defined(ENDFIELD_RIM) || defined(ENDFIELD_MATCAP)
        float4 MdaStyleFinalEntryPost(SurfaceOutput v, uint mdaPrimitive, bool mdaFront, float4 color) {
            float3 camRight, camUp, camForward, camBackward;
            bool isOrtho;
            MdaExtractCameraBasis(camRight, camUp, camForward, camBackward, isOrtho);
        #ifdef ENDFIELD_RIM
            color.rgb += EndfieldRimDelta(v, mdaFront, camRight, camUp, camForward, camBackward, isOrtho);
        #endif
        #ifdef ENDFIELD_MATCAP
            color.rgb += EndfieldMatcapDelta(v, mdaPrimitive, mdaFront, camRight, camUp, camForward, camBackward, isOrtho);
        #endif
            return color;
        }
        #endif
        """;

    internal static string ResolveModuleId(bool hair, bool rim, bool matcap, bool palette, bool baseImage)
    {
        if (!hair && !rim && !matcap)
            return DefaultModuleId;

        var parts = new List<string>(5);
        if (hair) parts.Add("hair");
        if (rim) parts.Add("rim");
        if (matcap) parts.Add("matcap");
        if (palette) parts.Add("palette");
        if (baseImage) parts.Add("base");
        return string.Join('-', parts);
    }

    internal static IReadOnlyList<StyleShaderModule> CreateModules()
    {
        var modules = new List<StyleShaderModule>
        {
            new(
                ModuleId: DefaultModuleId,
                HlslSource: DefaultHlsl,
                Hooks: StyleShaderHookMask.LightResponse
                    | StyleShaderHookMask.AllowStylizedShadingWrapper
                    | StyleShaderHookMask.DisableStylizedSphereMaps
                    | StyleShaderHookMask.RequiresOutlineColorScale
                    | StyleShaderHookMask.RequiresSkinShadeBlend,
                GeometryRequirements: StyleGeometryRequirements.None,
                DisableStylizedSphereMaps: true),
            new(
                ModuleId: FaceSdfModuleId,
                HlslSource: FaceSdfHlsl,
                Hooks: StyleShaderHookMask.LightResponse
                    | StyleShaderHookMask.RequiresSkinShadeBlend,
                GeometryRequirements: StyleGeometryRequirements.None)
        };

        (bool Matcap, bool Palette, bool BaseImage)[] matcapStages =
        [
            (false, false, false),
            (true, false, false),
            (true, true, false),
            (true, true, true)
        ];

        foreach (bool hair in new[] { false, true })
        foreach (bool rim in new[] { false, true })
        foreach (var (matcap, palette, baseImage) in matcapStages)
        {
            if (!hair && !rim && !matcap)
                continue;

            string moduleId = ResolveModuleId(hair, rim, matcap, palette, baseImage);
            var defines = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hair) defines["ENDFIELD_HAIR"] = "1";
            if (rim) defines["ENDFIELD_RIM"] = "1";
            if (matcap) defines["ENDFIELD_MATCAP"] = "1";
            if (palette) defines["ENDFIELD_PALETTE"] = "1";
            if (baseImage) defines["ENDFIELD_BASE_IMAGE"] = "1";

            var hooks = StyleShaderHookMask.LightResponse | StyleShaderHookMask.RequiresSkinShadeBlend;
            if (hair) hooks |= StyleShaderHookMask.PrimarySurfacePost;
            if (rim || matcap) hooks |= StyleShaderHookMask.FinalEntryPost;

            var geom = StyleGeometryRequirements.None;
            if (hair) geom |= StyleGeometryRequirements.RawRadialSeeds;
            if (rim) geom |= StyleGeometryRequirements.FrontFace;
            if (matcap) geom |= StyleGeometryRequirements.FrontFace | StyleGeometryRequirements.PrimitiveId | StyleGeometryRequirements.MikkTangentCorners;

            modules.Add(new StyleShaderModule(
                ModuleId: moduleId,
                HlslSource: ComposableSurfaceHlsl,
                Hooks: hooks,
                GeometryRequirements: geom,
                Defines: new ReadOnlyDictionary<string, string>(defines)));
        }

        return modules;
    }
}
