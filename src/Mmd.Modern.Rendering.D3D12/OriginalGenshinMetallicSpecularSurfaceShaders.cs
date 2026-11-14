using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored source-local metallic specular operators. No reference
    // graph, code or pixels bundled; full source lighting/direction parity is separate.
    internal static string GenshinMetallicSpecularSurfaceSource(bool native, bool gi, bool morph, bool sub, bool rb, bool atlas)
    {
        string source = GenshinMetallicSurfaceSource(native, gi, morph, sub, rb, atlas);
        const string helper = """
            cbuffer GenshinMetallicSpecularConstants : register(b12) {
                float4 GenshinMetallicSpecularColorShininess; // authored source numeric RGB, shininess
                float4 GenshinMetallicSpecularSharpScale; // authored sharp RGB, scale
                float4 GenshinMetallicSpecularOptions; // attenuation, sharp offset, ramp mix, has ramp
            };
            Texture2D<float4> GenshinMetallicSpecularRamp : register(t21);
            // Reuses parent s8 Linear Repeat. This is numerical RGB, without transfer,
            // luminance, palette Gamma, alpha multiplication or row flattening.
            float3 GenshinMetallicSpecularSafeNormalize(float3 value) {
                float squared=dot(value,value);
                if(squared<=0) return float3(0,0,0);
                return value*rsqrt(squared);
            }
            float3 GenshinMetallicSpecularCameraIncoming(SurfaceOutput v) {
                // Inherits the existing primary half-vector adapter, including its
                // Eye-world direction for orthographic projection. The parent's MatCap
                // camera-backward basis is a different source-local operation.
                return GenshinMetallicSpecularSafeNormalize(EyePosition.xyz-v.WorldPosition);
            }
            float GenshinMetallicSpecularNdotH(SurfaceOutput v) {
                float3 normal=GenshinMetallicSpecularSafeNormalize(v.Normal);
                float3 incoming=GenshinMetallicSpecularCameraIncoming(v);
                float3 light=GenshinMetallicSpecularSafeNormalize(IncomingLight.xyz);
                float3 halfDirection=GenshinMetallicSpecularSafeNormalize(incoming+light);
                return dot(normal,halfDirection);
            }
            float3 GenshinMetallicSpecularLocal(float nh,float blue,float lit) {
                // Body fixes GlobalScale and Intensity to one. Preserve source order;
                // floor h, then power/scale, then clamp p before ramp and sharp.
                precise float shifted=nh+(1-1);
                float h=max(shifted,.001);
                float p=saturate(GenshinMetallicSpecularSharpScale.w*pow(h,GenshinMetallicSpecularColorShininess.w));
                float3 authored=GenshinMetallicGamma(GenshinMetallicSpecularColorShininess.rgb);
                float3 q=authored*p;
                if(GenshinMetallicSpecularOptions.z>0 && GenshinMetallicSpecularOptions.w>0) {
                    float3 ramp=GenshinMetallicSpecularRamp.SampleLevel(GenshinMetallicLinearRepeat,float2(p,.5),0).rgb;
                    q=lerp(q,authored*ramp,saturate(GenshinMetallicSpecularOptions.z));
                }
                float3 ordinary=blue*q;
                // Strict sharp replacement bypasses B, ordinary color and ramp.
                float3 selected=GenshinMetallicSpecularOptions.y<p?GenshinMetallicGamma(GenshinMetallicSpecularSharpScale.rgb):ordinary;
                return selected*lerp(1,GenshinMetallicSpecularOptions.x,saturate(1-lit));
            }
            """;
        const string vertex = "SurfaceOutput VSSurface(Input v) {";
        source = MetallicOnce(source, vertex, helper + "\n" + vertex);
        if (!rb)
        {
            const string pmx = "float3 highlight=saturate(pow(max(0,dot(n,h)),SpecularPower.w)*SpecularPower.rgb);";
            // RB was already masked by the parent. Only a retained PMX highlight
            // needs replacement here, with a true no-metal branch for byte restoration.
            source = MetallicOnce(source, pmx, pmx + "\nfloat metallicSpecularRaw=GenshinMetallicRaw(v); if(metallicSpecularRaw>0) highlight*=saturate(1-metallicSpecularRaw);");
        }
        source = MetallicOnce(source, "float4 PSSurface(SurfaceOutput v):SV_Target",
            "float4 PSSurfaceMetallicSpecularBase(SurfaceOutput v):SV_Target");
        const string endpoint = """
            float4 PSSurface(SurfaceOutput v):SV_Target {
                float4 color=PSSurfaceMetallicSpecularBase(v);
                float raw=GenshinMetallicRaw(v);
                if(raw>0) {
                    float blue=GenshinMetallicCubicLightmap(v.Uv).b;
                    float lit=GenshinMetallicParameters.w==1?GenshinMetallicDarkLit.w:GenshinMetallicPrimaryLit(v);
                    // The base already applied nonmetal originalC or the unprojected
                    // endpoint. Metal owns lit attenuation and is added exactly once.
                    color.rgb+=saturate(raw)*GenshinMetallicSpecularLocal(GenshinMetallicSpecularNdotH(v),blue,lit);
                }
                return color;
            }
            """;
        // Insert before independent GI/native consumers so every visible entry calls
        // the same endpoint. Alpha-only consumers keep the accepted base coverage.
        const string following = "struct DepthOutput";
        return MetallicOnce(source, following, endpoint + "\n" + following);
    }

    internal static byte[] CompileGenshinMetallicSpecularSurface(string entry,
        bool native, bool gi, bool morph, bool sub, bool rb, bool atlas)
        => Compiler.Compile(GenshinMetallicSpecularSurfaceSource(native, gi, morph, sub, rb, atlas), entry,
            "OriginalGenshinMetallicSpecular.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray();
}
