using Mmd.Modern.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

internal sealed partial class D3D12TemporalInputPass
{
    private readonly record struct CharacterPipelineKey(OriginalTextureSampling Sampling, Format DepthFormat, CullMode Cull,
        bool WriteDepth, bool Reactive, bool Ground, bool Stencil, bool Simple, bool ModernOutline,
        bool SubTexture, OriginalTextureSampling ShadowSampling, bool FrontCounterClockwise, bool EditorVisibility = false,
        bool SharedEditorGeometry = false);
    private sealed record CharacterPipeline(ID3D12RootSignature Root, ID3D12PipelineState State);
    private readonly Dictionary<CharacterPipelineKey, CharacterPipeline> characterPipelines = [];
    private readonly Dictionary<string, byte[]> characterShaders = [];

    private CharacterPipeline GetCharacterPipeline(CharacterPipelineKey key)
    {
        if (characterPipelines.TryGetValue(key, out var existing)) return existing;
        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = key.Sampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.LinearWrap
                ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
            AddressU = key.Sampling is OriginalTextureSampling.LinearWrap or OriginalTextureSampling.PointWrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
            AddressV = key.Sampling is OriginalTextureSampling.LinearWrap or OriginalTextureSampling.PointWrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp
        };
        RootParameter[] parameters = [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel),
                new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 1)), ShaderVisibility.Pixel)];
        var samplers = new[] { sampler };
        if (key.SubTexture)
        {
            parameters = [..parameters,
                new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 2)), ShaderVisibility.Pixel),
                new(RootParameterType.ConstantBufferView, new RootDescriptor(1, 0), ShaderVisibility.All),
                new(RootParameterType.ConstantBufferView, new RootDescriptor(3, 0), ShaderVisibility.Pixel)];
            var shadowSampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) {
                Filter = key.ShadowSampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.LinearWrap
                    ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
                AddressU = key.ShadowSampling is OriginalTextureSampling.LinearWrap or OriginalTextureSampling.PointWrap
                    ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
                AddressV = key.ShadowSampling is OriginalTextureSampling.LinearWrap or OriginalTextureSampling.PointWrap
                    ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp };
            samplers = [sampler, shadowSampler];
        }
        var root = runtime.CreateRootSignature(new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers),
            RootSignatureVersion.Version10);
        try
        {
            byte[] Shader(string entry, bool vertex)
            {
                string identity = key.SubTexture ? "subtexture:" + entry : entry;
                if (characterShaders.TryGetValue(identity, out var bytes)) return bytes;
                bytes = D3D12FxcShaderCache.Compile(CharacterShaderSource(key.SubTexture), entry, "CharacterTemporal.hlsl", vertex ? "vs_5_1" : "ps_5_1",
                    ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness);
                runtime.ShaderCompilations++; characterShaders.Add(identity, bytes); return bytes;
            }
            var raster = RasterizerDescription.CullNone; raster.CullMode = key.Cull; raster.FrontCounterClockwise = key.FrontCounterClockwise;
            if (key.ModernOutline) raster = CharacterOutlineRasterization.Biased(raster);
            var depth = DepthStencilDescription.Default;
            depth.DepthFunc = key.Ground || key.Simple ? ComparisonFunction.Less : ComparisonFunction.LessEqual;
            depth.DepthWriteMask = key.WriteDepth ? DepthWriteMask.All : DepthWriteMask.Zero;
            if (key.Stencil)
            {
                depth.StencilEnable = true; depth.StencilReadMask = 255; depth.StencilWriteMask = 255;
                depth.FrontFace.StencilFunc = key.Ground ? ComparisonFunction.Greater : ComparisonFunction.Always;
                depth.FrontFace.StencilPassOp = StencilOperation.Replace; depth.BackFace = depth.FrontFace;
            }
            var blend = BlendDescription.Opaque;
            blend.RenderTarget = (RenderTargetBlendDescription[])blend.RenderTarget.Clone();
            if (key.Reactive)
            {
                var target = blend.RenderTarget[0]; target.BlendEnable = true;
                target.SrcBlend = Blend.One; target.DestBlend = Blend.One; target.BlendOp = BlendOperation.Max;
                target.SrcBlendAlpha = Blend.One; target.DestBlendAlpha = Blend.One; target.BlendOpAlpha = BlendOperation.Max;
                blend.RenderTarget[0] = target;
            }
            else
            {
                // Both MRTs use the PS's exact 0/1 source alpha as a write selector. Unlike discard,
                // alpha=0 preserves the previous opaque inputs while still updating the real DSV/stencil.
                // With independent blending disabled this descriptor applies to both depth and motion MRTs.
                var target = blend.RenderTarget[0]; target.BlendEnable = true;
                target.SrcBlend = Blend.SourceAlpha; target.DestBlend = Blend.InverseSourceAlpha; target.BlendOp = BlendOperation.Add;
                target.SrcBlendAlpha = Blend.One; target.DestBlendAlpha = Blend.Zero; target.BlendOpAlpha = BlendOperation.Add;
                blend.RenderTarget[0] = target;
            }
            InputElementDescription[] layout = [
                new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("POSITION", 1, Format.R32G32B32_Float, 12, 0),
                new("NORMAL", 0, Format.R32G32B32_Float, 24, 0), new("TEXCOORD", 0, Format.R32G32_Float, 36, 0),
                new("POSITION", 2, Format.R32G32B32_Float, 44, 0)];
            if(key.SharedEditorGeometry)
                layout=[new("POSITION",0,Format.R32G32B32_Float,0,0),new("POSITION",1,Format.R32G32B32_Float,0,0),
                    new("NORMAL",0,Format.R32G32B32_Float,12,1),new("TEXCOORD",0,Format.R32G32_Float,24,1),
                    new("POSITION",2,Format.R32G32B32_Float,0,1)];
            if (key.SubTexture) layout = [..layout,key.SharedEditorGeometry
                ?new("TEXCOORD",1,Format.R32G32B32A32_Float,0,2):new("TEXCOORD",1,Format.R32G32B32A32_Float,0,1)];
            layout = [..layout, new("TEXCOORD",2,Format.R32G32B32A32_Float,0,3),
                new("TEXCOORD",3,Format.R32G32B32A32_Float,16,3)];
            var state = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root, VertexShader = Shader("VSCharacter", true), PixelShader = Shader(key.EditorVisibility ? "PSGuideVisibility" : key.Reactive ? "PSReactive" : "PSOpaque", false),
                InputLayout = new InputLayoutDescription(layout),
                BlendState = blend, SampleMask = uint.MaxValue, RasterizerState = raster, DepthStencilState = depth,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = key.Reactive ? [Format.R8_UNorm] : [Format.R32_Float, Format.R32G32_Float],
                DepthStencilFormat = key.DepthFormat, SampleDescription = new(1, 0)
            });
            runtime.PipelineCreations++;
            var created = new CharacterPipeline(root, state); characterPipelines.Add(key, created); return created;
        }
        catch { root.Dispose(); throw; }
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetCharacterPrewarmRequests(bool subTexture, bool includeReactive, bool includeEditorVisibility)
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        string source = CharacterShaderSource(subTexture);
        yield return new(source, "VSCharacter", "CharacterTemporal.hlsl", "vs_5_1", flags);
        yield return new(source, "PSOpaque", "CharacterTemporal.hlsl", "ps_5_1", flags);
        if (includeReactive) yield return new(source, "PSReactive", "CharacterTemporal.hlsl", "ps_5_1", flags);
        if (includeEditorVisibility) yield return new(source, "PSGuideVisibility", "CharacterTemporal.hlsl", "ps_5_1", flags);
    }

    internal static string CharacterShaderSource(bool subTexture)
    {
        if (!subTexture) return CharacterSource;
        string source = CharacterSource
            .Replace("Texture2D<float4> DiffuseMap : register(t0);", "", StringComparison.Ordinal)
            .Replace("Texture2D<float4> SphereMap : register(t1);", "", StringComparison.Ordinal)
            .Replace("SamplerState SelectedSampler : register(s0);", "", StringComparison.Ordinal)
            .Replace("float3 SourcePosition : POSITION2;", "float3 SourcePosition : POSITION2; float4 AdditionalUv : TEXCOORD1;", StringComparison.Ordinal)
            .Replace("float4 LightClip : TEXCOORD4;", "float4 LightClip : TEXCOORD4; float3 CoverageNormal : TEXCOORD5; float3 CoverageWorldPosition : TEXCOORD6; float2 AdditionalUv : TEXCOORD7;", StringComparison.Ordinal)
            .Replace("o.LightClip = Controls.z > 0 ? mul(float4(Extent.z == 3 ? v.SourcePosition : v.Current,1),LightViewProjection) : float4(0,0,0,1);", """
                CoverageInput original;
                original.Position = Extent.z == 3 ? v.SourcePosition : v.Current;
                original.Normal = v.Normal; original.Uv = v.Uv; original.AdditionalUv = v.AdditionalUv;
                SurfaceOutput surface = VSSurface(original);
                o.CoverageNormal = surface.Normal; o.CoverageWorldPosition = surface.WorldPosition;
                o.AdditionalUv = surface.AdditionalUv; o.LightClip = surface.LightClip;
            """, StringComparison.Ordinal);
        int start = source.IndexOf("float ActualAlpha(Vertex v)", StringComparison.Ordinal);
        int end = source.IndexOf("struct Targets", start, StringComparison.Ordinal);
        source = source[..start] + """
            float ActualAlpha(Vertex v) {
                if (Extent.z != 0 && Extent.z != 3) return Coverage.x;
                SurfaceOutput surface;
                surface.Position = v.Position; surface.Normal = v.CoverageNormal;
                surface.Uv = v.Uv; surface.WorldPosition = v.CoverageWorldPosition;
                surface.SphereProjection = v.SphereProjection; surface.LightClip = v.LightClip;
                surface.AdditionalUv = v.AdditionalUv;
                float alpha = PSSurface(surface).a;
                return Extent.z == 3 ? alpha * Controls.w : alpha;
            }
            """ + source[end..];
        return OriginalFrameShaders.TemporalSubTextureCoverageSource() + "\n" + source;
    }

    // Independently authored geometry/alpha contract, matching OriginalFrameShaders and OriginalOutlineMath.
    // Reactive policy follows AMD FidelityFX SDK 2.3 docs/techniques/super-resolution-upscaler.md:
    // sampled compositing alpha, clamped to .9. Max blend keeps overlapping layers conservative.
    // Official cached reference/license: references/rendering-upscale-20261022/fidelityfx-current/docs/license.md.
    // No upstream shader implementation is copied into this pass.
    private const string CharacterSource = """
        cbuffer CharacterConstants : register(b0) {
            row_major float4x4 CurrentWorld;
            row_major float4x4 PreviousWorld;
            row_major float4x4 RasterViewProjection;
            row_major float4x4 CurrentViewProjection;
            row_major float4x4 PreviousViewProjection;
            row_major float4x4 SphereRotation;
            row_major float4x4 LightViewProjection;
            float4 Extent; // width, height, geometry mode (surface/original-outline/ground/modern-outline), reserved
            float4 Coverage; // resolved material alpha, alpha cutoff, sphere mode, Transp
            float4 Controls; // opaque blend state, additive, self shadow projection enabled, modern outline alpha
            float4 GroundDirection;
            float4 PreviousGroundDirection;
            float4 OutlineScales; // independent current/previous per-draw factors
        };
        Texture2D<float4> DiffuseMap : register(t0);
        Texture2D<float4> SphereMap : register(t1);
        SamplerState SelectedSampler : register(s0);
        struct Input { float3 Current : POSITION0; float3 Previous : POSITION1; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; float3 SourcePosition : POSITION2;
            float4 WidthSource : TEXCOORD2; float4 PreviousWidthSource : TEXCOORD3; };
        struct Vertex {
            float4 Position : SV_POSITION; float4 Current : TEXCOORD0; float4 Previous : TEXCOORD1;
            float2 Uv : TEXCOORD2; float2 SphereProjection : TEXCOORD3; float4 LightClip : TEXCOORD4;
        };
        Vertex VSCharacter(Input v) {
            Vertex o;
            float3 current = v.Current, previous = v.Previous;
            if (Extent.z == 2) {
                current = float3(current.x-current.y*GroundDirection.x/GroundDirection.y,.1,current.z-current.y*GroundDirection.z/GroundDirection.y);
                previous = float3(previous.x-previous.y*PreviousGroundDirection.x/PreviousGroundDirection.y,.1,previous.z-previous.y*PreviousGroundDirection.z/PreviousGroundDirection.y);
            }
            if (Extent.z == 1 || Extent.z == 3) {
                if (OutlineScales.x != 1 && v.WidthSource.w > 0) {
                    precise float3 scaled = v.WidthSource.xyz + (current-v.WidthSource.xyz)*OutlineScales.x;
                    current = scaled;
                }
                if (OutlineScales.y != 1 && v.PreviousWidthSource.w > 0) {
                    precise float3 scaled = v.PreviousWidthSource.xyz + (previous-v.PreviousWidthSource.xyz)*OutlineScales.y;
                    previous = scaled;
                }
                // The colour outline uses the precombined World*ViewProjection, unlike the surface.
                precise float4 position = mul(float4(current,1),RasterViewProjection);
                o.Position = position;
                o.Current = mul(float4(current,1),CurrentViewProjection);
                o.Previous = mul(float4(previous,1),PreviousViewProjection);
            } else {
                float4 world = mul(float4(current,1),CurrentWorld);
                o.Position = mul(world,RasterViewProjection);
                o.Current = mul(world,CurrentViewProjection);
                o.Previous = mul(mul(float4(previous,1),PreviousWorld),PreviousViewProjection);
            }
            o.Uv = v.Uv;
            o.SphereProjection = mul(float4(v.Normal,0),SphereRotation).xy;
            o.LightClip = Controls.z > 0 ? mul(float4(Extent.z == 3 ? v.SourcePosition : v.Current,1),LightViewProjection) : float4(0,0,0,1);
            return o;
        }
        float ActualAlpha(Vertex v) {
            if (Extent.z != 0 && Extent.z != 3) return Coverage.x; // Original outline and ground remain solid.
            float alpha = Coverage.x * DiffuseMap.Sample(SelectedSampler,v.Uv).a;
            if (Coverage.z > 0)
                alpha *= SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).a;
            float3 q = v.LightClip.xyz/v.LightClip.w;
            float2 uv = float2(.5+.5*q.x,.5-.5*q.y);
            // Match the original early outside-shadow return: Transp is only applied inside projection.
            if (Coverage.w > 0 && !(uv.x<0 || uv.x>1 || uv.y<0 || uv.y>1)) alpha = .5;
            clip(alpha-Coverage.y);
            return Extent.z == 3 ? alpha * Controls.w : alpha;
        }
        struct Targets { float4 Depth : SV_Target0; float4 Motion : SV_Target1; };
        Targets PSOpaque(Vertex v) {
            float alpha = ActualAlpha(v);
            // Every alpha-tested colour fragment updates the DSV, even fully transparent fragments at cutoff=0.
            // A blended composite has multiple motions; MRT source alpha=0 preserves its existing opaque layer.
            float opaque = Controls.x>0 || (Controls.y==0 && alpha>=1) ? 1 : 0;
            Targets o; o.Depth = float4(v.Position.zzz,opaque);
            float previousW = abs(v.Previous.w)<1e-6 ? (v.Previous.w<0 ? -1e-6 : 1e-6) : v.Previous.w;
            o.Motion = float4((v.Previous.xy/previousW-v.Current.xy/v.Current.w)*float2(.5,-.5)*Extent.xy,0,opaque);
            return o;
        }
        float PSReactive(Vertex v) : SV_Target {
            float alpha = ActualAlpha(v);
            return Controls.x>0 || (Controls.y==0 && alpha>=1) ? 0 : min(saturate(alpha),.9);
        }
        float PSGuideVisibility(Vertex v) : SV_Target {
            // A zero-alpha texel can still write the legacy colour DSV at cutoff=0.
            // It has no visible surface, so it must leave the editor guide visible.
            float alpha = ActualAlpha(v);
            clip(alpha - 0.000001);
            return 0;
        }
        """;
}
