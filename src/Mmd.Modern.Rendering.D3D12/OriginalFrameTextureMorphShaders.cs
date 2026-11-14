using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Optional factor variant. Entirely identity frames compile the unchanged accepted source.
/// Equations follow retained render_recovery.md 85-110 and toon host 0x1400b8e67..0x1400b8ea8.
/// Raw sampled alpha remains independent of texture-colour accumulator W.</summary>
internal static class OriginalTextureMorphShader
{
    internal const string Constants = """
        cbuffer TextureMorphConstants:register(b3) {
            float4 TextureMultiply; float4 TextureAdd;
            float4 SphereMultiply; float4 SphereAdd;
            float4 ToonMultiply; float4 ToonAdd;
        };
        float3 MorphDiffuse(float3 sampleColor) {
            if(Options.z==0) return sampleColor;
            precise float alpha=TextureMultiply.w+TextureAdd.w;
            precise float3 scaled=sampleColor*TextureMultiply.rgb;
            precise float3 biased=scaled+TextureAdd.rgb;
            precise float3 weighted=biased*alpha;
            return weighted+(1-alpha);
        }
        float3 MorphSphere(float3 sampleColor,bool additive) {
            precise float alpha=SphereMultiply.w+SphereAdd.w;
            precise float3 scaled=sampleColor*SphereMultiply.rgb;
            precise float3 biased=scaled+SphereAdd.rgb;
            precise float3 weighted=biased*alpha;
            if(additive) return weighted;
            return weighted+(1-alpha);
        }
        """;

    internal static string Apply(string source)
    {
        // Replace only colour uses. Alpha, cutoff, projection/Transp, shadow and depth entrypoints stay raw.
        source=source.Replace("EgColor.rgb*texel.rgb", "EgColor.rgb*MorphDiffuse(texel.rgb)",StringComparison.Ordinal)
            .Replace("baseColor=baseColor*sphere.rgb", "baseColor=baseColor*MorphSphere(sphere.rgb,false)",StringComparison.Ordinal)
            .Replace("baseColor=baseColor+sphere.rgb", "baseColor=baseColor+MorphSphere(sphere.rgb,true)",StringComparison.Ordinal)
            .Replace("NativeDiffuse.rgb*texel", "NativeDiffuse.rgb*MorphDiffuse(texel)",StringComparison.Ordinal)
            .Replace("EgColor.rgb*GiAmbientShare.rgb*texel", "EgColor.rgb*GiAmbientShare.rgb*MorphDiffuse(texel)",StringComparison.Ordinal)
            .Replace("EgColor.rgb*DiffuseMap.Sample(SelectedSampler,v.Uv).rgb",
                "EgColor.rgb*MorphDiffuse(DiffuseMap.Sample(SelectedSampler,v.Uv).rgb)",StringComparison.Ordinal)
            .Replace("fill=fill*SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb",
                "fill=fill*MorphSphere(SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb,false)",StringComparison.Ordinal)
            .Replace("diffuseLit=diffuseLit*SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb",
                "diffuseLit=diffuseLit*MorphSphere(SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb,false)",StringComparison.Ordinal)
            .Replace("diffuse*=SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb",
                "diffuse*=MorphSphere(SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb,false)",StringComparison.Ordinal);
        // HLSL functions can be declared after the cbuffer they read, before the original entrypoints.
        int insertion=source.IndexOf("struct Input",StringComparison.Ordinal);
        if(insertion<0)throw new InvalidOperationException("Texture-factor source has no expected input declaration.");
        return source.Insert(insertion,Constants+"\n");
    }
}

internal static partial class OriginalFrameShaders
{
    internal static byte[] CompileTextureMorph(string entry,bool native,bool gi=false)
        =>Compiler.Compile(OriginalTextureMorphShader.Apply(Source+(native||gi?GiSource:"")+(native?NativeSource:"")),entry,
            native?"OriginalFrameNativeTextureMorph.hlsl":"OriginalFrameTextureMorph.hlsl","ps_5_1",
            ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();
}

internal static partial class OriginalStyleShaders
{
    internal static (byte[] Vertex,byte[] Pixel) CompileTextureMorph()
        =>(Compiler.Compile(Source,"VSMain","OriginalSurface.hlsl","vs_5_1",
                ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray(),
            Compiler.Compile(OriginalTextureMorphShader.Apply(Source),"PSMain","OriginalSurfaceTextureMorph.hlsl","ps_5_1",
                ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray());
}

public sealed partial class D3D12OriginalStyleRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TextureMorphConstants(Vector4 TextureMultiply,Vector4 TextureAdd,
        Vector4 SphereMultiply,Vector4 SphereAdd,Vector4 ToonMultiply,Vector4 ToonAdd)
    {
        internal TextureMorphConstants(OriginalMaterialTextureFactors value):this(value.Diffuse.Multiply,value.Diffuse.Add,
            value.Sphere.Multiply,value.Sphere.Add,value.Toon.Multiply,value.Toon.Add){}
    }
    private (byte[] Vertex,byte[] Pixel)? textureMorphSurfaceShaders;
    private readonly Dictionary<(string Entry,bool Native,bool Gi),byte[]> textureMorphFrameShaders=[];
    private (byte[] Vertex,byte[] Pixel) GetTextureMorphSurfaceShaders()
    {
        if(textureMorphSurfaceShaders is {} found)return found;
        var compiled=OriginalStyleShaders.CompileTextureMorph();runtime.ShaderCompilations+=2;
        textureMorphSurfaceShaders=compiled;return compiled;
    }
    private byte[] GetTextureMorphFrameShader(string entry,bool native,bool gi=false)
    {
        if(textureMorphFrameShaders.TryGetValue((entry,native,gi),out var found))return found;
        var compiled=OriginalFrameShaders.CompileTextureMorph(entry,native,gi);runtime.ShaderCompilations++;
        textureMorphFrameShaders.Add((entry,native,gi),compiled);return compiled;
    }
    internal static Vector3 ResolveTextureMorphToon(OriginalStyleMaterialBinding binding)
    {
        var factor=binding.TextureFactors.Toon;
        if(factor.IsIdentity)return binding.ToonShadowColor;
        var color=binding.ToonShadowColor;
        return new(Add(Multiply(color.X,factor.Multiply.X),factor.Add.X),
            Add(Multiply(color.Y,factor.Multiply.Y),factor.Add.Y),Add(Multiply(color.Z,factor.Multiply.Z),factor.Add.Z));
    }
    // The original host uses separate scalar mulss/addss. Avoid a JIT fused multiply-add changing this contract.
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Multiply(float a,float b)=>a*b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Add(float a,float b)=>a+b;
}
