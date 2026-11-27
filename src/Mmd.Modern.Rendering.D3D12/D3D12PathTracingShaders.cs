namespace Mmd.Modern.Rendering.D3D12;

/// <summary>HLSL compute, denoiser, and resolve shaders for full multi-bounce path tracing.
/// Reuses <see cref="D3D12HardwareRayGi.TraceSource"/> (DXR RayQuery 1.1) and
/// <see cref="D3D12SoftwareRayGi.TraceSource"/> (Software BVH Compute) so both backends share
/// identical camera rays, alpha cutout traversal, NEE direct lights, Russian Roulette,
/// firefly clamping, progressive accumulation, and ToonHybrid / Physical shading contracts.</summary>
internal static class D3D12PathTracingShaders
{
    internal static DxcShaderWarmupRequest GetTracePrewarmRequest(bool hardware, bool textureMorph, bool subTexture)
    {
        var defines = hardware ? new Dictionary<string, string?> { ["PT_HARDWARE"] = "1" } : null;
        return new(TraceShaderSource(hardware, textureMorph, subTexture), "CSPathTrace", hardware ? "cs_6_5" : "cs_6_0", defines);
    }

    internal static DxcShaderWarmupRequest GetDenoisePrewarmRequest()
        => new(DenoiserSource, "CSDenoise", "cs_6_0");

    internal static IEnumerable<D3D12FxcShaderRequest> GetResolvePrewarmRequests()
    {
        const Vortice.D3DCompiler.ShaderFlags flags = Vortice.D3DCompiler.ShaderFlags.EnableStrictness | Vortice.D3DCompiler.ShaderFlags.IeeeStrictness;
        yield return new(ResolveSource, "VSResolve", "pathtrace-resolve.hlsl", "vs_5_1", flags);
        yield return new(ResolveSource, "PSResolve", "pathtrace-resolve.hlsl", "ps_5_1", flags);
    }

    internal static string TraceShaderSource(bool hardware, bool textureMorph, bool subTexture)
    {
        string shared = SharedHeader;
        if (textureMorph)
            shared = shared.Replace("// PT_DIFFUSE_ALBEDO_HOOK", TextureMorphHook, StringComparison.Ordinal);
        else
            shared = shared.Replace("// PT_DIFFUSE_ALBEDO_HOOK", DefaultDiffuseHook, StringComparison.Ordinal);

        if (subTexture)
            shared = shared.Replace("// PT_SUBTEXTURE_HOOK", SubTextureUvHook, StringComparison.Ordinal);
        else
            shared = shared.Replace("// PT_SUBTEXTURE_HOOK", DefaultSubTextureUvHook, StringComparison.Ordinal);

        return shared
            + (hardware ? D3D12HardwareRayGi.TraceSource : D3D12SoftwareRayGi.TraceSource)
            + NativeLightModel.Hlsl
            + PathTraceIntegrator;
    }

    private const string SharedHeader = """
        struct GiTriangle { float4 P0; float4 P1; float4 P2; float4 N0U0; float4 N1U1; float4 N2U2; float4 V012; float4 SphereUv01; float4 SphereUv2; uint4 Meta; };
        struct GiMaterial { float4 DiffuseFactor; float4 Emission; uint4 DiffuseTexture; uint4 SphereTexture; float4 Parameters; uint4 Flags; };
        struct GiBvhNode { float4 BoundsMin; float4 BoundsMax; uint4 Data; };
        struct GiRay { float3 Origin; float TMin; float3 Direction; float TMax; };
        struct GiHit { uint TriangleIndex; float T; float2 Barycentrics; uint FrontFace; };
        struct PtMaterialExtra {
            float4 EgColor;
            float4 ToonShadowColor;
            float4 SpecularPower;
            float4 Flags;             // x=useSceneLighting, y=sphereMode, z=receiveSelfShadow, w=hasDiffuseTex
            float4 RoughnessMetallic; // x=roughness, y=metallic, z=reserved, w=reserved
        };
        struct PtNativeLight { float4 PositionKind; float4 DirectionRange; float4 ColorIntensity; float4 ConeShadow; float4 AreaRightTile; };
        struct PtDiffuseTextureFactor { float4 Multiply; float4 Add; };
        struct PtSubTextureUv { float4 Uv01; float4 Uv2; };

        cbuffer PtConstants : register(b0) {
            uint4 PtDispatch;        // x=width, y=height, z=samplesPerFrame, w=frameSeed
            uint4 GiCounts;          // x=triangleCount, y=materialCount, z=bvhNodeCount, w=nativeLightCount
            uint4 PtConfig;          // x=maxBounces, y=shadingMode(0=ToonHybrid,1=Physical), z=accumulatedIndex, w=flags
            float4 PtLightDirDist;   // xyz=lightDir (toward light), w=maxRayDistance
            float4 PtLightColorEnv;  // xyz=primaryLightColor, w=environmentStrength
            float4 PtTuning;         // x=rayEpsilon, y=fireflyClamp, z=sunConeSin, w=maxAccumulatedFrames
            float4 PtBackground;     // rgba
            float4 PtCameraEye;      // xyz=eye, w=isOrthographic
            row_major float4x4 PtInvViewProj;
            row_major float4x4 PtViewProj;
            row_major float4x4 GiCamera;
        };

        StructuredBuffer<GiTriangle> GiTriangles : register(t0);
        StructuredBuffer<GiMaterial> GiMaterials : register(t1);
        StructuredBuffer<uint> GiTexels : register(t2);
        StructuredBuffer<GiBvhNode> GiBvhNodes : register(t3);
        StructuredBuffer<uint> GiTriangleOrder : register(t4);
        StructuredBuffer<PtMaterialExtra> PtMaterials : register(t5);
        StructuredBuffer<PtNativeLight> PtNativeLights : register(t6);
        StructuredBuffer<PtDiffuseTextureFactor> PtDiffuseFactors : register(t7);
        StructuredBuffer<PtSubTextureUv> PtSubTextureUvs : register(t8);
        #ifdef PT_HARDWARE
        RaytracingAccelerationStructure GiHardwareScene : register(t9);
        #endif

        RWStructuredBuffer<float4> PtAccumulation : register(u0);
        RWStructuredBuffer<float4> PtGuidePosDepth : register(u1);
        RWStructuredBuffer<float4> PtGuideNormalMat : register(u2);
        RWStructuredBuffer<float4> PtGuideAlbedo : register(u3);
        RWStructuredBuffer<uint> PtDiagnostics : register(u4);

        static const float PT_PI = 3.14159265359;
        static const float PT_INV_PI = 0.31830988618;

        void ReportTraversalOverflow() { InterlockedAdd(PtDiagnostics[0], 1); }

        float4 UnpackPtTexel(uint value) { return float4(value & 255u, (value >> 8u) & 255u, (value >> 16u) & 255u, value >> 24u) / 255.0; }
        int PtCoord(int value, int size, bool wrap) { return wrap ? ((value % size) + size) % size : clamp(value, 0, size - 1); }
        float4 PtReadTexel(uint4 tex, int2 p, bool wrap) {
            p = int2(PtCoord(p.x, (int)tex.y, wrap), PtCoord(p.y, (int)tex.z, wrap));
            return UnpackPtTexel(GiTexels[tex.x + p.y * tex.y + p.x]);
        }
        float4 PtSampleTexture(uint4 tex, float2 uv, uint sampling) {
            bool wrap = sampling == 2u || sampling == 3u;
            uv = wrap ? frac(uv) : saturate(uv);
            if (sampling == 0u || sampling == 2u) return PtReadTexel(tex, (int2)floor(uv * tex.yz), wrap);
            float2 coord = uv * tex.yz - 0.5;
            int2 p = (int2)floor(coord);
            float2 f = frac(coord);
            return lerp(
                lerp(PtReadTexel(tex, p, wrap), PtReadTexel(tex, p + int2(1, 0), wrap), f.x),
                lerp(PtReadTexel(tex, p + int2(0, 1), wrap), PtReadTexel(tex, p + int2(1, 1), wrap), f.x),
                f.y);
        }
        float3 PtWeights(float2 bary) { return float3(1.0 - bary.x - bary.y, bary.x, bary.y); }
        float2 PtDiffuseUv(GiTriangle tri, float2 bary) {
            float3 w = PtWeights(bary);
            return float2(dot(w, float3(tri.N0U0.w, tri.N1U1.w, tri.N2U2.w)), dot(w, tri.V012.xyz));
        }
        float2 PtSphereUv(GiTriangle tri, float2 bary) {
            float3 w = PtWeights(bary);
            if (tri.Meta.z == 1u) {
                float3 n0 = tri.SphereUv01.xyz, n1 = float3(tri.SphereUv01.w, tri.SphereUv2.xy), n2 = float3(tri.SphereUv2.zw, tri.V012.w);
                float2 a = mul(float4(n0, 0), GiCamera).xy, b = mul(float4(n1, 0), GiCamera).xy, c = mul(float4(n2, 0), GiCamera).xy;
                float2 proj = a * w.x + b * w.y + c * w.z;
                return float2(0.5 * proj.x + 0.5, -0.5 * proj.y + 0.5);
            }
            return tri.SphereUv01.xy * w.x + tri.SphereUv01.zw * w.y + tri.SphereUv2.xy * w.z;
        }
        // PT_SUBTEXTURE_HOOK
        // PT_DIFFUSE_ALBEDO_HOOK
        bool AcceptCandidate(uint triangleIndex, float2 bary, float3 rayDirection) {
            GiTriangle tri = GiTriangles[triangleIndex];
            GiMaterial material = GiMaterials[tri.Meta.x];
            if (material.Parameters.z == 0.0) return false;
            bool front = dot(cross(tri.P1.xyz - tri.P0.xyz, tri.P2.xyz - tri.P0.xyz), rayDirection) < 0.0;
            if (material.Parameters.y == 0.0 && !front) return false;
            float2 uv = PtDiffuseUv(tri, bary);
            uint cutoffBits = asuint(material.Parameters.x);
            float alpha;
            if ((material.Flags.x == 1u || material.Flags.x == 2u) && cutoffBits >= 0x00800000u && cutoffBits < 0x7f800000u && all(isfinite(uv))) {
                float constantAlpha = UnpackPtTexel(GiTexels[material.DiffuseTexture.x]).a;
                alpha = min(material.DiffuseFactor.a, 1.0) * constantAlpha;
            } else {
                alpha = min(material.DiffuseFactor.a, 1.0) * PtSampleTexture(material.DiffuseTexture, uv, material.DiffuseTexture.w).a;
            }
            if (material.SphereTexture.w != 0u) {
                float2 sUv = material.SphereTexture.w == 3u ? PtSubTextureUvAt(triangleIndex, bary) : PtSphereUv(tri, bary);
                alpha *= PtSampleTexture(material.SphereTexture, sUv, material.DiffuseTexture.w).a;
            }
            return alpha >= material.Parameters.x;
        }
        float3 PtGeometricNormal(GiTriangle tri) {
            float3 g = cross(tri.P1.xyz - tri.P0.xyz, tri.P2.xyz - tri.P0.xyz);
            float lenSq = dot(g, g);
            return lenSq > 1e-20 ? g * rsqrt(lenSq) : float3(0, 1, 0);
        }
        float3 PtShadingNormal(GiTriangle tri, float2 bary, float3 incoming) {
            float3 w = PtWeights(bary);
            float3 n = tri.N0U0.xyz * w.x + tri.N1U1.xyz * w.y + tri.N2U2.xyz * w.z;
            float lenSq = dot(n, n);
            n = lenSq > 1e-20 ? n * rsqrt(lenSq) : PtGeometricNormal(tri);
            if (dot(n, incoming) > 0.0) n = -n;
            return n;
        }
        float3 PtOffset(float3 p, float3 geometric, float3 direction) {
            return p + geometric * (dot(geometric, direction) >= 0.0 ? PtTuning.x : -PtTuning.x);
        }
        """;

    private const string DefaultDiffuseHook = """
        float3 PtDiffuseTexel(uint matIndex, GiTriangle tri, float2 bary) {
            GiMaterial m = GiMaterials[matIndex];
            return PtSampleTexture(m.DiffuseTexture, PtDiffuseUv(tri, bary), m.DiffuseTexture.w).rgb;
        }
        """;

    private const string TextureMorphHook = """
        float3 PtDiffuseTexel(uint matIndex, GiTriangle tri, float2 bary) {
            GiMaterial m = GiMaterials[matIndex];
            PtDiffuseTextureFactor factor = PtDiffuseFactors[matIndex];
            float3 raw = PtSampleTexture(m.DiffuseTexture, PtDiffuseUv(tri, bary), m.DiffuseTexture.w).rgb;
            precise float weight = factor.Multiply.w + factor.Add.w;
            precise float3 effective = (raw * factor.Multiply.rgb + factor.Add.rgb) * weight + (1.0 - weight);
            return effective;
        }
        """;

    private const string DefaultSubTextureUvHook = """
        float2 PtSubTextureUvAt(uint triangleIndex, float2 bary) { return PtSphereUv(GiTriangles[triangleIndex], bary); }
        """;

    private const string SubTextureUvHook = """
        float2 PtSubTextureUvAt(uint triangleIndex, float2 bary) {
            PtSubTextureUv row = PtSubTextureUvs[triangleIndex];
            float3 w = PtWeights(bary);
            return row.Uv01.xy * w.x + row.Uv01.zw * w.y + row.Uv2.xy * w.z;
        }
        """;

    private const string PathTraceIntegrator = """
        uint PtPcg(inout uint state) {
            state = state * 747796405u + 2891336453u;
            uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
            return (word >> 22u) ^ word;
        }
        float PtRand(inout uint state) { return (PtPcg(state) >> 8u) * (1.0 / 16777216.0); }

        void PtBuildBasis(float3 n, out float3 t, out float3 b) {
            float3 up = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(0, 1, 0);
            t = normalize(cross(up, n));
            b = cross(n, t);
        }

        float3 PtSampleCosineHemisphere(float3 n, float u1, float u2) {
            float r = sqrt(saturate(u1));
            float phi = 6.28318530718 * u2;
            float3 t, b;
            PtBuildBasis(n, t, b);
            return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(1.0 - u1, 0.0)));
        }

        float3 PtSampleConeDirection(float3 axis, float sinHalfAngle, float u1, float u2) {
            if (sinHalfAngle <= 1e-5) return axis;
            float r = sqrt(saturate(u1)) * sinHalfAngle;
            float phi = 6.28318530718 * u2;
            float3 t, b;
            PtBuildBasis(axis, t, b);
            float z = sqrt(max(1.0 - r * r, 0.0));
            return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + axis * z);
        }

        float3 PtFresnelSchlick(float cosTheta, float3 f0) {
            float m = saturate(1.0 - cosTheta);
            float m2 = m * m;
            return f0 + (1.0 - f0) * (m2 * m2 * m);
        }

        float PtD_GGX(float nDotH, float alpha) {
            float a2 = alpha * alpha;
            float d = (nDotH * nDotH) * (a2 - 1.0) + 1.0;
            return a2 / max(PT_PI * d * d, 1e-6);
        }

        float PtG1_Schlick(float nDotX, float k) {
            return nDotX / max(nDotX * (1.0 - k) + k, 1e-6);
        }

        float3 PtEvalPbrBrdf(float3 n, float3 v, float3 l, float3 albedo, float roughness, float3 f0) {
            float nDotL = max(dot(n, l), 0.0);
            float nDotV = max(dot(n, v), 0.0);
            if (nDotL <= 0.0 || nDotV <= 0.0) return 0.0;
            float3 h = normalize(v + l);
            float nDotH = max(dot(n, h), 0.0);
            float vDotH = max(dot(v, h), 0.0);
            float alpha = max(roughness * roughness, 0.0025);
            float D = PtD_GGX(nDotH, alpha);
            float k = (roughness + 1.0);
            k = (k * k) * 0.125;
            float G = PtG1_Schlick(nDotL, k) * PtG1_Schlick(nDotV, k);
            float3 F = PtFresnelSchlick(vDotH, f0);
            float3 spec = (D * G * F) / max(4.0 * nDotL * nDotV, 1e-5);
            float3 diff = (1.0 - F) * albedo * PT_INV_PI;
            return diff + spec;
        }

        float3 PtSampleGgxHalfVector(float3 n, float roughness, float u1, float u2) {
            float alpha = max(roughness * roughness, 0.0025);
            float a2 = alpha * alpha;
            float phi = 6.28318530718 * u1;
            float cosTheta = sqrt(saturate((1.0 - u2) / max(1.0 + (a2 - 1.0) * u2, 1e-6)));
            float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
            float3 t, b;
            PtBuildBasis(n, t, b);
            return normalize(t * (sinTheta * cos(phi)) + b * (sinTheta * sin(phi)) + n * cosTheta);
        }

        float3 PtSkyRadiance(float3 dir) {
            float env = PtLightColorEnv.w;
            if (env <= 0.0) return 0.0;
            float up = saturate(dir.y * 0.5 + 0.5);
            float3 horizon = lerp(float3(0.45, 0.48, 0.55), PtBackground.rgb, 0.5);
            float3 zenith = lerp(float3(0.65, 0.78, 0.98), PtLightColorEnv.rgb, 0.25);
            return lerp(horizon, zenith, up) * env;
        }

        float3 PtSoftAdd(float3 color, float3 addition) {
            float3 room = max(1.0 - color, 0.0);
            if (!all(isfinite(room)) || !all(isfinite(addition)) || any(addition < 0.0)) return color + addition;
            float t = 0.0;
            if (room.x > 0.0) t = max(t, addition.x / room.x);
            if (room.y > 0.0) t = max(t, addition.y / room.y);
            if (room.z > 0.0) t = max(t, addition.z / room.z);
            if (t <= 0.6 || !isfinite(t)) return color + addition;
            float u = (t - 0.6) / 0.4;
            float shoulder = 0.6 + 0.4 * u / (1.0 + u);
            return color + (shoulder / t) * addition;
        }

        float3 PtEvaluateNativeDirect(
            uint matIndex,
            float3 p,
            float3 gN,
            float3 n,
            float3 v,
            float3 diffuseAlbedo,
            float roughness,
            float3 f0,
            bool toonMode,
            bool receiveShadow,
            inout uint rng)
        {
            uint lightCount = GiCounts.w;
            float3 result = 0.0;
            if (PtLightColorEnv.w > 0.0) result += diffuseAlbedo * (PtLightColorEnv.w * (toonMode ? 1.0 : PT_INV_PI));
            if (lightCount == 0u) return result;
            uint receiverMask = 0xffffffffu;
            if ((PtConfig.w & 4u) != 0u)
                receiverMask = asuint(PtNativeLights[lightCount + matIndex].PositionKind.x);

            [loop] for (uint i = 0u; i < lightCount; i++) {
                if ((receiverMask & (1u << i)) == 0u) continue;
                PtNativeLight light = PtNativeLights[i];
                float3 lightPos = light.PositionKind.xyz;
                float kind = light.PositionKind.w;
                if (kind == 3.0) {
                    // Stochastic sample on the area light rectangle for true soft shadows.
                    float su = PtRand(rng) * 2.0 - 1.0;
                    float sv = PtRand(rng) * 2.0 - 1.0;
                    float3 areaUp = cross(light.AreaRightTile.xyz, light.DirectionRange.xyz);
                    lightPos += light.AreaRightTile.xyz * (su * light.ConeShadow.x) + areaUp * (sv * light.ConeShadow.y);
                }
                float3 delta = lightPos - p;
                float rawDistSq = dot(delta, delta);
                if (kind != 0.0 && rawDistSq < 1e-12) continue;
                float distSq = max(rawDistSq, 0.0001);
                float dist = sqrt(rawDistSq);
                float3 l = kind == 0.0 ? -light.DirectionRange.xyz : delta / dist;
                float nDotL = max(dot(n, l), 0.0);
                if (nDotL <= 0.0) continue;
                float attenuation = NativeLightAttenuation(light.PositionKind, light.DirectionRange, light.ConeShadow, distSq, l);
                if (attenuation <= 0.0) continue;

                if (receiveShadow && light.ConeShadow.z > 0.0) {
                    GiRay shadowRay;
                    shadowRay.Origin = PtOffset(p, gN, l);
                    shadowRay.Direction = l;
                    shadowRay.TMin = PtTuning.x;
                    shadowRay.TMax = kind == 0.0 ? PtLightDirDist.w : max(dist - PtTuning.x, PtTuning.x);
                    if (TraceAny(shadowRay)) continue;
                }

                float3 irradiance = light.ColorIntensity.rgb * (light.ColorIntensity.w * attenuation);
                if (toonMode) {
                    float toon = saturate(nDotL * 3.0);
                    result += diffuseAlbedo * irradiance * toon;
                } else {
                    result += PtEvalPbrBrdf(n, v, l, diffuseAlbedo, roughness, f0) * irradiance * nDotL;
                }
            }
            return result;
        }

        [numthreads(8, 8, 1)]
        void CSPathTrace(uint3 thread : SV_DispatchThreadID) {
            if (any(thread.xy >= PtDispatch.xy)) return;
            uint pixelIndex = thread.y * PtDispatch.x + thread.x;

            uint spp = max(PtDispatch.z, 1u);
            uint maxBounces = max(PtConfig.x, 1u);
            bool physicalMode = PtConfig.y == 1u;
            bool progressive = (PtConfig.w & 1u) != 0u;
            bool resetHistory = (PtConfig.w & 2u) != 0u;
            bool hasPrimaryLight = (PtConfig.w & 32u) != 0u;

            uint baseSeed = thread.x * 1973u + thread.y * 9277u + PtDispatch.w * 26699u + 1u;
            uint rng = baseSeed;
            PtPcg(rng);

            // Per-pixel Cranley-Patterson offset for R2 low-discrepancy subpixel AA.
            float cpX = PtRand(rng);
            float cpY = PtRand(rng);

            float3 radianceSum = 0.0;
            float alphaSum = 0.0;
            float4 guidePosDepth = float4(0, 0, 0, 1.0);
            float4 guideNormalMat = float4(0, 0, 0, 0);
            float4 guideAlbedo = float4(0, 0, 0, 0);
            bool hasGuide = false;

            [loop] for (uint s = 0u; s < spp; s++) {
                uint totalSampleIdx = PtConfig.z * spp + s;
                float jx = frac(cpX + 0.7548776662 * (float)(totalSampleIdx + 1u));
                float jy = frac(cpY + 0.5698402910 * (float)(totalSampleIdx + 1u));

                float ndcX = ((thread.x + jx) / (float)PtDispatch.x) * 2.0 - 1.0;
                float ndcY = 1.0 - ((thread.y + jy) / (float)PtDispatch.y) * 2.0;

                float4 nearClip = mul(float4(ndcX, ndcY, 0.0, 1.0), PtInvViewProj);
                float4 farClip = mul(float4(ndcX, ndcY, 1.0, 1.0), PtInvViewProj);
                float3 nearWorld = nearClip.xyz / nearClip.w;
                float3 farWorld = farClip.xyz / farClip.w;
                float3 rayVec = farWorld - nearWorld;
                float rayLen = length(rayVec);

                GiRay ray;
                ray.Origin = nearWorld;
                ray.Direction = rayLen > 1e-6 ? (rayVec / rayLen) : float3(0, 0, 1);
                ray.TMin = 0.0;
                ray.TMax = min(rayLen, PtLightDirDist.w);

                GiHit primaryHit;
                if (!TraceClosest(ray, primaryHit)) {
                    bool straightBg = (PtConfig.w & 8u) != 0u;
                    radianceSum += straightBg ? (PtBackground.rgb * PtBackground.a) : PtBackground.rgb;
                    alphaSum += PtBackground.a;
                    continue;
                }

                GiTriangle tri0 = GiTriangles[primaryHit.TriangleIndex];
                uint matIndex0 = tri0.Meta.x;
                GiMaterial mat0 = GiMaterials[matIndex0];
                PtMaterialExtra extra0 = PtMaterials[matIndex0];

                float3 p0 = ray.Origin + ray.Direction * primaryHit.T;
                float3 gN0 = PtGeometricNormal(tri0);
                float3 n0 = PtShadingNormal(tri0, primaryHit.Barycentrics, ray.Direction);
                float3 v0 = -ray.Direction;

                float3 texel0 = PtDiffuseTexel(matIndex0, tri0, primaryHit.Barycentrics);
                float3 albedo0 = saturate(mat0.DiffuseFactor.rgb * texel0);

                if (!hasGuide) {
                    float4 clip0 = mul(float4(p0, 1.0), PtViewProj);
                    float clipZ0 = clip0.w > 1e-6 ? saturate(clip0.z / clip0.w) : 1.0;
                    guidePosDepth = float4(p0, clipZ0);
                    guideNormalMat = float4(n0, (float)(matIndex0 + 1u));
                    guideAlbedo = float4(albedo0, 1.0);
                    hasGuide = true;
                }

                float3 sampleRadiance = 0.0;
                if (!physicalMode) {
                    // --- ToonHybrid Mode ---
                    // Preserves MMD original palette, toon shading and specular highlights,
                    // upgraded with ray-traced soft shadows and multi-bounce indirect diffuse GI.
                    float3 baseColor = extra0.EgColor.rgb * texel0;
                    float sphereMode = extra0.Flags.y;
                    if (sphereMode > 0.5) {
                        float2 sUv = sphereMode >= 2.5 ? PtSubTextureUvAt(primaryHit.TriangleIndex, primaryHit.Barycentrics) : PtSphereUv(tri0, primaryHit.Barycentrics);
                        float3 sp = PtSampleTexture(mat0.SphereTexture, sUv, mat0.DiffuseTexture.w).rgb;
                        if (sphereMode == 1.0 || (sphereMode >= 3.0 && (sphereMode != 4.0 || extra0.Flags.w < 0.5)))
                            baseColor *= sp;
                        else
                            baseColor += sp;
                    }

                    float3 lMain = normalize(PtLightDirDist.xyz);
                    float3 hMain = normalize(lMain + v0);
                    float3 highlight = saturate(pow(max(0.0, dot(n0, hMain)), max(extra0.SpecularPower.w, 1.0)) * extra0.SpecularPower.rgb);
                    bool receiveShadow0 = extra0.Flags.z > 0.5;

                    float shadowVis = 1.0;
                    if (hasPrimaryLight && receiveShadow0 && dot(n0, lMain) > 0.0) {
                        float3 jitteredSun = PtSampleConeDirection(lMain, PtTuning.z, PtRand(rng), PtRand(rng));
                        GiRay sunRay;
                        sunRay.Origin = PtOffset(p0, gN0, jitteredSun);
                        sunRay.Direction = jitteredSun;
                        sunRay.TMin = PtTuning.x;
                        sunRay.TMax = PtLightDirDist.w;
                        if (TraceAny(sunRay)) shadowVis = 0.0;
                    }

                    float c = hasPrimaryLight ? min(saturate(dot(n0, lMain) * 3.0), shadowVis) : 0.0;
                    float3 toonFactor = c + extra0.ToonShadowColor.rgb * (1.0 - c);
                    float3 directToon = baseColor * toonFactor + highlight * c + mat0.Emission.rgb;

                    float3 nativeDirect = PtEvaluateNativeDirect(
                        matIndex0, p0, gN0, n0, v0, albedo0, extra0.RoughnessMetallic.x, float3(0.04, 0.04, 0.04),
                        true, receiveShadow0, rng);
                    sampleRadiance = PtSoftAdd(directToon, nativeDirect);

                    // Multi-bounce indirect diffuse transport (bounces 1..MaxBounces-1)
                    if (maxBounces > 1u) {
                        float3 indirect = 0.0;
                        float3 throughput = 1.0;
                        float3 bounceDir = PtSampleCosineHemisphere(n0, PtRand(rng), PtRand(rng));
                        GiRay bounceRay;
                        bounceRay.Origin = PtOffset(p0, gN0, bounceDir);
                        bounceRay.Direction = bounceDir;
                        bounceRay.TMin = PtTuning.x;
                        bounceRay.TMax = PtLightDirDist.w;

                        [loop] for (uint b = 1u; b < maxBounces; b++) {
                            GiHit hit;
                            if (!TraceClosest(bounceRay, hit)) {
                                indirect += throughput * PtSkyRadiance(bounceRay.Direction);
                                break;
                            }
                            GiTriangle tri = GiTriangles[hit.TriangleIndex];
                            uint mIdx = tri.Meta.x;
                            GiMaterial mat = GiMaterials[mIdx];
                            PtMaterialExtra extra = PtMaterials[mIdx];
                            float3 hp = bounceRay.Origin + bounceRay.Direction * hit.T;
                            float3 gN = PtGeometricNormal(tri);
                            float3 hn = PtShadingNormal(tri, hit.Barycentrics, bounceRay.Direction);
                            float3 halbedo = saturate(mat.DiffuseFactor.rgb * PtDiffuseTexel(mIdx, tri, hit.Barycentrics));

                            indirect += throughput * mat.Emission.rgb;

                            // NEE at secondary bounce: primary MMD sun + native lights
                            if (hasPrimaryLight) {
                                float nDotL = max(dot(hn, lMain), 0.0);
                                if (nDotL > 0.0) {
                                    float3 jSun = PtSampleConeDirection(lMain, PtTuning.z, PtRand(rng), PtRand(rng));
                                    GiRay sr;
                                    sr.Origin = PtOffset(hp, gN, jSun);
                                    sr.Direction = jSun;
                                    sr.TMin = PtTuning.x;
                                    sr.TMax = PtLightDirDist.w;
                                    if (!TraceAny(sr))
                                        indirect += throughput * halbedo * PtLightColorEnv.rgb * (nDotL * PT_INV_PI);
                                }
                            }
                            float3 natSec = PtEvaluateNativeDirect(
                                mIdx, hp, gN, hn, -bounceRay.Direction, halbedo, 0.9, float3(0.04, 0.04, 0.04),
                                false, true, rng);
                            indirect += throughput * natSec;

                            throughput *= halbedo;
                            if (b >= 2u) {
                                float pSurvive = clamp(max(throughput.x, max(throughput.y, throughput.z)), 0.05, 0.95);
                                if (PtRand(rng) > pSurvive) break;
                                throughput /= pSurvive;
                            }
                            float3 nextDir = PtSampleCosineHemisphere(hn, PtRand(rng), PtRand(rng));
                            bounceRay.Origin = PtOffset(hp, gN, nextDir);
                            bounceRay.Direction = nextDir;
                            bounceRay.TMin = PtTuning.x;
                            bounceRay.TMax = PtLightDirDist.w;
                        }

                        // Compose indirect diffuse bounce respecting MMD's toon palette budget + gentle color bleed.
                        float indLum = dot(max(indirect, 0.0), float3(0.2126, 0.7152, 0.0722));
                        float3 paletteLift = baseColor * max(1.0 - toonFactor, 0.0) * saturate(indLum * 0.65);
                        float3 colorBounce = albedo0 * max(indirect, 0.0) * 0.35;
                        sampleRadiance = PtSoftAdd(sampleRadiance, paletteLift + colorBounce);
                    }
                } else {
                    // --- Physical Mode (Energy-Conserving Multi-Bounce PBR Path Tracing) ---
                    float3 throughput = 1.0;
                    GiHit hit = primaryHit;
                    GiRay curRay = ray;
                    float3 lMain = normalize(PtLightDirDist.xyz);

                    [loop] for (uint b = 0u; b < maxBounces; b++) {
                        if (b > 0u) {
                            if (!TraceClosest(curRay, hit)) {
                                sampleRadiance += throughput * PtSkyRadiance(curRay.Direction);
                                break;
                            }
                        }
                        GiTriangle tri = GiTriangles[hit.TriangleIndex];
                        uint mIdx = tri.Meta.x;
                        GiMaterial mat = GiMaterials[mIdx];
                        PtMaterialExtra extra = PtMaterials[mIdx];
                        float3 hp = curRay.Origin + curRay.Direction * hit.T;
                        float3 gN = PtGeometricNormal(tri);
                        float3 hn = PtShadingNormal(tri, hit.Barycentrics, curRay.Direction);
                        float3 hv = -curRay.Direction;
                        float3 tex = PtDiffuseTexel(mIdx, tri, hit.Barycentrics);
                        float3 halbedo = saturate(mat.DiffuseFactor.rgb * tex);
                        float roughness = clamp(extra.RoughnessMetallic.x, 0.05, 1.0);
                        float metallic = clamp(extra.RoughnessMetallic.y, 0.0, 1.0);
                        float3 specHint = saturate(extra.SpecularPower.rgb * 0.08 + 0.04);
                        float3 f0 = lerp(specHint, halbedo, metallic);

                        sampleRadiance += throughput * mat.Emission.rgb;

                        // NEE: MMD primary directional light
                        if (hasPrimaryLight) {
                            float3 jSun = PtSampleConeDirection(lMain, PtTuning.z, PtRand(rng), PtRand(rng));
                            float nDotL = max(dot(hn, jSun), 0.0);
                            if (nDotL > 0.0) {
                                bool occluded = false;
                                if (extra.Flags.z > 0.5 || b > 0u) {
                                    GiRay sr;
                                    sr.Origin = PtOffset(hp, gN, jSun);
                                    sr.Direction = jSun;
                                    sr.TMin = PtTuning.x;
                                    sr.TMax = PtLightDirDist.w;
                                    occluded = TraceAny(sr);
                                }
                                if (!occluded) {
                                    float3 brdf = PtEvalPbrBrdf(hn, hv, jSun, halbedo, roughness, f0);
                                    sampleRadiance += throughput * brdf * (PtLightColorEnv.rgb * 2.5) * nDotL;
                                }
                            }
                        }

                        // NEE: Native scene lights
                        sampleRadiance += throughput * PtEvaluateNativeDirect(
                            mIdx, hp, gN, hn, hv, halbedo, roughness, f0,
                            false, extra.Flags.z > 0.5 || b > 0u, rng);

                        if (b + 1u >= maxBounces) break;

                        // Russian Roulette path termination starting at bounce 2
                        if (b >= 2u) {
                            float pSurvive = clamp(max(throughput.x, max(throughput.y, throughput.z)), 0.05, 0.95);
                            if (PtRand(rng) > pSurvive) break;
                            throughput /= pSurvive;
                        }

                        // Importance-sample GGX specular or cosine-weighted diffuse lobe
                        float3 F = PtFresnelSchlick(max(dot(hn, hv), 0.0), f0);
                        float pSpec = roughness < 0.95 ? clamp(max(F.x, max(F.y, F.z)), 0.05, 0.95) : 0.0;
                        float3 nextDir;
                        if (PtRand(rng) < pSpec) {
                            float3 h = PtSampleGgxHalfVector(hn, roughness, PtRand(rng), PtRand(rng));
                            nextDir = reflect(-hv, h);
                            float nDotL = dot(hn, nextDir);
                            float nDotV = max(dot(hn, hv), 1e-4);
                            float nDotH = max(dot(hn, h), 1e-4);
                            float vDotH = max(dot(hv, h), 1e-4);
                            if (nDotL <= 0.0) break;
                            float k = (roughness + 1.0);
                            k = (k * k) * 0.125;
                            float G = PtG1_Schlick(nDotL, k) * PtG1_Schlick(nDotV, k);
                            float3 Fh = PtFresnelSchlick(vDotH, f0);
                            throughput *= (Fh * G * vDotH) / max(nDotV * nDotH * pSpec, 1e-4);
                        } else {
                            nextDir = PtSampleCosineHemisphere(hn, PtRand(rng), PtRand(rng));
                            float3 kD = (1.0 - F) * (1.0 - metallic);
                            throughput *= (kD * halbedo) / max(1.0 - pSpec, 1e-4);
                        }

                        curRay.Origin = PtOffset(hp, gN, nextDir);
                        curRay.Direction = nextDir;
                        curRay.TMin = PtTuning.x;
                        curRay.TMax = PtLightDirDist.w;
                    }
                }

                // Guard against NaN/Inf and apply hue-preserving firefly clamping
                if (!all(isfinite(sampleRadiance))) sampleRadiance = 0.0;
                sampleRadiance = max(sampleRadiance, 0.0);
                float lum = dot(sampleRadiance, float3(0.2126, 0.7152, 0.0722));
                if (lum > PtTuning.y) sampleRadiance *= (PtTuning.y / lum);

                radianceSum += sampleRadiance;
                alphaSum += 1.0;
            }

            float4 frameMean = float4(radianceSum / (float)spp, saturate(alphaSum / (float)spp));
            if (resetHistory || !progressive || PtConfig.z == 0u) {
                PtAccumulation[pixelIndex] = frameMean;
            } else {
                float4 prev = PtAccumulation[pixelIndex];
                float blend = 1.0 / (float)(PtConfig.z + 1u);
                PtAccumulation[pixelIndex] = lerp(prev, frameMean, blend);
            }

            PtGuidePosDepth[pixelIndex] = guidePosDepth;
            PtGuideNormalMat[pixelIndex] = guideNormalMat;
            PtGuideAlbedo[pixelIndex] = guideAlbedo;
        }
        """;

    internal const string DenoiserSource = """
        cbuffer DenoiseConstants : register(b0) {
            uint4 Dimensions; // x=width, y=height, z=accumulatedFrames, w=mode(1=Spatial,2=TemporalSpatial)
            float4 Weights;   // x=spatialSigma, y=normalPower, z=depthThreshold, w=albedoSigma
        };
        StructuredBuffer<float4> InputColor : register(t0);
        StructuredBuffer<float4> GuidePosDepth : register(t1);
        StructuredBuffer<float4> GuideNormalMat : register(t2);
        StructuredBuffer<float4> GuideAlbedo : register(t3);
        RWStructuredBuffer<float4> OutputColor : register(u0);

        float KernelWeight(int offset) {
            int a = abs(offset);
            return a == 0 ? 1.0 : (a == 1 ? 0.6666667 : 0.1666667);
        }

        [numthreads(8, 8, 1)]
        void CSDenoise(uint3 thread : SV_DispatchThreadID) {
            if (any(thread.xy >= Dimensions.xy)) return;
            uint idx = thread.y * Dimensions.x + thread.x;
            float4 center = InputColor[idx];
            float4 nMat0 = GuideNormalMat[idx];
            if (nMat0.w < 0.5 || !all(isfinite(center))) {
                OutputColor[idx] = center;
                return;
            }

            float3 p0 = GuidePosDepth[idx].xyz;
            float3 n0 = normalize(nMat0.xyz);
            float3 a0 = GuideAlbedo[idx].rgb;
            float mat0 = nMat0.w;

            // In TemporalSpatial mode, progressively relax spatial smoothing as accumulatedFrames converges.
            float temporalRelax = Dimensions.w == 2u ? rsqrt(max((float)Dimensions.z, 1.0)) : 1.0;
            float filterStrength = saturate(temporalRelax * Weights.x);
            if (filterStrength < 0.05) {
                OutputColor[idx] = center;
                return;
            }

            float3 sumRgb = center.rgb;
            float sumWeight = 1.0;
            float centerLum = dot(center.rgb, float3(0.2126, 0.7152, 0.0722));

            [loop] for (int dy = -2; dy <= 2; dy++) {
                [loop] for (int dx = -2; dx <= 2; dx++) {
                    if (dx == 0 && dy == 0) continue;
                    int2 q = int2(thread.xy) + int2(dx, dy);
                    if (any(q < 0) || any(q >= int2(Dimensions.xy))) continue;
                    uint qIdx = (uint)q.y * Dimensions.x + (uint)q.x;
                    float4 nMat = GuideNormalMat[qIdx];
                    if (nMat.w != mat0) continue;

                    float3 n1 = normalize(nMat.xyz);
                    float nAlign = saturate(dot(n0, n1));
                    if (nAlign < 0.85) continue;

                    float3 p1 = GuidePosDepth[qIdx].xyz;
                    float planeDist = abs(dot(p1 - p0, n0));
                    if (planeDist > Weights.z) continue;

                    float3 a1 = GuideAlbedo[qIdx].rgb;
                    float albedoDiff = length(a1 - a0);
                    float wAlbedo = exp(-albedoDiff * albedoDiff / max(Weights.w, 1e-4));

                    float4 sampleCol = InputColor[qIdx];
                    if (!all(isfinite(sampleCol))) continue;
                    float sLum = dot(sampleCol.rgb, float3(0.2126, 0.7152, 0.0722));
                    float lumDiff = abs(sLum - centerLum);
                    float wLum = exp(-lumDiff / max(0.35 + centerLum * 0.5, 0.1));

                    float w = KernelWeight(dx) * KernelWeight(dy)
                        * pow(nAlign, Weights.y)
                        * exp(-planeDist * planeDist / max(Weights.z * Weights.z * 0.25, 1e-6))
                        * wAlbedo * wLum * filterStrength;
                    if (w > 1e-5) {
                        sumRgb += sampleCol.rgb * w;
                        sumWeight += w;
                    }
                }
            }
            OutputColor[idx] = float4(sumRgb / sumWeight, center.a);
        }
        """;

    internal const string ResolveSource = """
        cbuffer ResolveConstants : register(b0) {
            uint4 Dimensions; // x=width, y=height, z=shadingMode(0=ToonHybrid,1=Physical), w=straightAlpha
        };
        StructuredBuffer<float4> FinalRadiance : register(t0);
        StructuredBuffer<float4> GuidePosDepth : register(t1);

        float4 VSResolve(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
        }

        float3 AcesFilm(float3 x) {
            float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
            return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
        }

        struct ResolveOutput {
            float4 Color : SV_Target;
            float Depth : SV_Depth;
        };

        ResolveOutput PSResolve(float4 pos : SV_Position) {
            uint2 p = min((uint2)pos.xy, Dimensions.xy - 1u);
            uint idx = p.y * Dimensions.x + p.x;
            float4 col = FinalRadiance[idx];
            if (!all(isfinite(col))) col = float4(0, 0, 0, 1);
            float alpha = saturate(col.a);
            float3 rgb = max(col.rgb, 0.0);
            if (Dimensions.w != 0u) {
                // Unpremultiply before tone mapping / clamping, then re-premultiply for FP16 straight-alpha resolve.
                float3 unpremul = alpha > 1e-4 ? (rgb / alpha) : float3(0, 0, 0);
                unpremul = Dimensions.z == 1u ? AcesFilm(unpremul) : saturate(unpremul);
                rgb = unpremul * alpha;
            } else {
                rgb = Dimensions.z == 1u ? AcesFilm(rgb) : saturate(rgb);
            }
            ResolveOutput o;
            o.Color = float4(rgb, alpha);
            o.Depth = saturate(GuidePosDepth[idx].w);
            return o;
        }
        """;
}
