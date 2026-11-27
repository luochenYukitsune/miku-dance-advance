using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Optional independently authored UV1 technique. Accepted ordinary HLSL literals stay unchanged.</summary>
internal static class OriginalSubTextureShader
{
    private static string Replace(string source,string oldValue,string newValue)
    {
        if(!source.Contains(oldValue,StringComparison.Ordinal))throw new InvalidOperationException("SubTexture shader anchor missing: "+oldValue);
        return source.Replace(oldValue,newValue,StringComparison.Ordinal);
    }

    internal static string Apply(string source,bool frame,bool native=false,bool gi=false)
    {
        source=OriginalTextureMorphShader.Apply(source);
        source=Replace(source,"float2 Uv:TEXCOORD0; };","float2 Uv:TEXCOORD0; float4 AdditionalUv:TEXCOORD1; };");
        source=Replace(source,"float2 SphereProjection:TEXCOORD2;","float2 SphereProjection:TEXCOORD2; float2 AdditionalUv:TEXCOORD4;");
        source=Replace(source,"o.Uv=v.Uv;","o.Uv=v.Uv; o.AdditionalUv=v.AdditionalUv.xy;");
        if(frame)
            source=Replace(source,"SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5))",
                "SphereMap.Sample(SelectedSampler,Options.y>=3?v.AdditionalUv:float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5))");
        else
            source=Replace(source,"SphereMap.Sample(SelectedSampler,sphereUv)","SphereMap.Sample(SelectedSampler,Options.y>=3?v.AdditionalUv:sphereUv)");
        const string add="if(Options.y==2) baseColor=baseColor+MorphSphere(sphere.rgb,true);";
        source=Replace(source,add,add+"\nif(Options.y>=3) { if(Options.y==4 && Options.z>0) baseColor+=MorphSphere(sphere.rgb,true); else baseColor*=MorphSphere(sphere.rgb,false); }");
        // The original two-map technique's unassigned else expression does not reset alpha.
        // Its no-diffuse sibling explicitly restores raw alpha and always multiplies colour.
        if(frame)
            source=Replace(source,"if(Options.w>0) alpha=.5; clip(alpha-Options.x);",
                "if(Options.y>=3 && Options.z>0 && c<1) alpha*=c+ToonShadowColor.w*(1-c); if(Options.w>0) alpha=.5; clip(alpha-Options.x);");
        else
        {
            source=Replace(source,"clip(alpha-Options.x);","");
            source=Replace(source,"return float4(result,alpha);",
                "if(Options.y>=3 && Options.z>0 && c<1) alpha*=c+ToonShadowColor.w*(1-c); clip(alpha-Options.x); return float4(result,alpha);");
        }
        if(native)
        {
            // Additive two-map decoration is already in the base pass. A no-diffuse
            // SubTexture technique always multiplies, including native irradiance.
            source=Replace(source,"if(Options.y==1) diffuse*=MorphSphere(",
                "if(Options.y==1 || (Options.y>=3 && (Options.y!=4 || Options.z==0))) diffuse*=MorphSphere(");
        }
        if(frame&&(native||gi))
        {
            // Receiver eligibility is exactly the visible primary technique's effective
            // alpha, including projection bounds, current shadow visibility and Toon W.
            source=Replace(source,"clip(alpha-Options.x);clip(alpha-.999);",
                "alpha=PSSurface(v).a; clip(alpha-Options.x);clip(alpha-.999);");
            // Additive decoration is outside the occludable/diffuse palette budget.
            // The original no-diffuse mode3 technique always multiplies.
            source=Replace(source,"if(Options.y==1) fill=fill*MorphSphere(",
                "if(Options.y==1 || (Options.y>=3 && (Options.y!=4 || Options.z==0))) fill=fill*MorphSphere(");
            source=Replace(source,"if(Options.y==1) diffuseLit=diffuseLit*MorphSphere(",
                "if(Options.y==1 || (Options.y>=3 && (Options.y!=4 || Options.z==0))) diffuseLit=diffuseLit*MorphSphere(");
        }
        return source;
    }
}

internal static partial class OriginalFrameShaders
{
    // Reuse the colour technique verbatim. Only register/name isolation is needed when
    // linked with the independent temporal geometry entrypoints.
    internal static string TemporalSubTextureCoverageSource() => OriginalSubTextureShader.Apply(Source,true)
        .Replace("register(b0)","register(b1)",StringComparison.Ordinal)
        .Replace("struct Input","struct CoverageInput",StringComparison.Ordinal)
        .Replace("VSSurface(Input v)","VSSurface(CoverageInput v)",StringComparison.Ordinal)
        .Replace("SphereRotation","SurfaceSphereRotation",StringComparison.Ordinal)
        .Replace("LightViewProjection","SurfaceLightViewProjection",StringComparison.Ordinal)
        .Replace("GroundDirection","SurfaceGroundDirection",StringComparison.Ordinal);

    internal static byte[] CompileSubTexture(string entry,bool vertex,bool native=false,bool gi=false,bool clustered=false)
    {
        string source=OriginalSubTextureShader.Apply(Source+(native||gi?GiSource:"")+(native?NativeSource:""),true,native,gi);
        if(clustered&&native&&!vertex) source=OriginalClusteredLightShader.Apply(source);
        return D3D12FxcShaderCache.Compile(source,entry,
            native?(clustered?"OriginalFrameSubTextureNativeClustered.hlsl":"OriginalFrameSubTextureNative.hlsl"):"OriginalFrameSubTexture.hlsl",
            vertex?"vs_5_1":"ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness);
    }
}

internal static partial class OriginalStyleShaders
{
    internal static (byte[] Vertex,byte[] Pixel) CompileSubTexture()
    {
        string source=OriginalSubTextureShader.Apply(Source,false);
        return (D3D12FxcShaderCache.Compile(source,"VSMain","OriginalSurfaceSubTexture.hlsl","vs_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness),
            D3D12FxcShaderCache.Compile(source,"PSMain","OriginalSurfaceSubTexture.hlsl","ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness));
    }
}

public sealed partial class D3D12OriginalStyleRenderer
{
    private (byte[] Vertex,byte[] Pixel)? subTextureSurfaceShaders;
    private readonly Dictionary<(string,bool,bool,bool,bool),byte[]> subTextureFrameShaders=[];
    internal static bool HasSubTexture(OriginalFrameScene frame)=>frame.Surface.Materials.Any(m=>m.SphereMode==OriginalSphereMode.SubTexture);
    internal static void ValidateSubTexture(OriginalFrameScene? frame,RayGiOptions? gi,D3D12TemporalUpscalingOptions? temporal)
    {
        if(frame is null||!HasSubTexture(frame))return;
        if(frame.Surface.Snapshot.AdditionalUvs is null)throw new ArgumentException("SubTexture requires an explicit UV1 stream.");
    }
    private static float SurfaceMode(OriginalStyleMaterialBinding binding)
        =>binding.SphereMode==OriginalSphereMode.SubTexture&&binding.SubTextureAdditive?4:(float)binding.SphereMode;
    private static Vector4 SurfaceToon(OriginalStyleMaterialBinding binding,bool useSceneLighting=false)
        =>new(useSceneLighting?Vector3.Zero:ResolveTextureMorphToon(binding),binding.SphereMode==OriginalSphereMode.SubTexture
            ?binding.TextureFactors.Toon.Multiply.W+binding.TextureFactors.Toon.Add.W:1);

    internal static ulong UploadTemporalCoverageConstants(D3D12RecordingContext context,OriginalFrameScene frame,
        int width,int height,int materialIndex)
    {
        var scene=frame.Surface.Snapshot;var options=frame.Options;var camera=options?.CameraOverride??scene.Camera;
        var material=scene.Materials[materialIndex];var binding=frame.Surface.Materials[materialIndex];
        bool sceneLighting=frame.Materials[materialIndex].UseSceneLighting;
        var host=OriginalStyleMath.ResolveHostColors(material,frame.Surface.Profile,sceneLighting);
        Matrix4x4.Invert(scene.World,out var inverse);
        return context.UploadConstants(new FrameConstants(scene.World,Matrix4x4.Transpose(inverse),camera.ViewProjection(width,height),
            options?.SphereRotationOverride??frame.Surface.Profile.SphereRotation,options?.SelfShadow?.LightViewProjection??Matrix4x4.Identity,
            host.EgColor,host.SpecularPower,new(Vector3.Normalize(scene.LightDirection),0),new(camera.Eye,1),SurfaceToon(binding,sceneLighting),
            new(material.AlphaCutoff,SurfaceMode(binding),material.DiffuseTexture>=0?1:0,options?.Transp==true?1:0),
            new(0,options?.SelfShadow?.PartialFar==true?1:0,options?.SelfShadow is null?0:1,0),Vector4.Zero));
    }

    internal static ulong UploadTemporalTextureFactors(D3D12RecordingContext context,OriginalStyleMaterialBinding binding)
        =>context.UploadConstants(new TextureMorphConstants(binding.TextureFactors));
    private static InputElementDescription[] SurfaceLayout(bool subTexture)
    {
        InputElementDescription[] ordinary=[new("POSITION",0,Format.R32G32B32_Float,0,0),new("NORMAL",0,Format.R32G32B32_Float,12,0),new("TEXCOORD",0,Format.R32G32_Float,24,0)];
        return subTexture?[..ordinary,new("TEXCOORD",1,Format.R32G32B32A32_Float,0,1)]:ordinary;
    }
    private static void BindAdditionalUvs(D3D12RecordingContext context,D3D12GpuCache cache,SceneSnapshot scene)
    {
        var stream=scene.AdditionalUvs??throw new ArgumentException("SubTexture requires UV1.");
        var buffer=cache.BufferByIdentity("original-additional-uv",MemoryMarshal.AsBytes(stream.Vertices),ResourceStates.VertexAndConstantBuffer,stream);
        context.Commands.IASetVertexBuffers(1,new VertexBufferView(buffer.GPUVirtualAddress,checked(scene.VertexCount*64),64));
    }
    private (byte[] Vertex,byte[] Pixel) GetSubTextureSurfaceShaders()
    {
        if(subTextureSurfaceShaders is {} found)return found;
        var compiled=OriginalStyleShaders.CompileSubTexture();runtime.ShaderCompilations+=2;
        subTextureSurfaceShaders=compiled;return compiled;
    }
    private byte[] GetSubTextureFrameShader(string entry,bool vertex,bool native=false,bool gi=false,bool clustered=false)
    {
        if(subTextureFrameShaders.TryGetValue((entry,vertex,native,gi,clustered),out var found))return found;
        var compiled=OriginalFrameShaders.CompileSubTexture(entry,vertex,native,gi,clustered);runtime.ShaderCompilations++;
        subTextureFrameShaders.Add((entry,vertex,native,gi,clustered),compiled);return compiled;
    }
}
