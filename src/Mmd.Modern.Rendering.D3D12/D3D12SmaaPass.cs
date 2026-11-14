using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Mmd.Modern.Core;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Upstream SMAA 1x: luma edges, selected preset weights, and gamma-space neighborhood blending.
/// LUTs and native state are device cached; targets and descriptors belong to the recording fence.</summary>
internal sealed class D3D12SmaaPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    internal sealed record Result(D3D12GpuTarget Output, D3D12GpuTarget Edges, D3D12GpuTarget Weights);
    private readonly Dictionary<(SmaaQuality Quality, string Pass, bool AlphaCoverage), (byte[] Vertex, byte[] Pixel, StaticSamplerDescription[] Samplers)> shaders = [];
    private static readonly Lazy<string> Algorithm = new(() => ReadResource("SMAA.hlsl"));
    private static readonly Lazy<byte[]> Area = new(() => ReadLookup("AreaTex.h", 160 * 560 * 2));
    private static readonly Lazy<byte[]> Search = new(() => ReadLookup("SearchTex.h", 64 * 16));

    internal Result Record(D3D12RecordingContext context, D3D12GpuTarget input, SmaaQuality quality = SmaaQuality.High, bool alphaCoverage = false)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        if (input.Depth || input.Format is not (Format.R8G8B8A8_UNorm or Format.R16G16B16A16_Float) || !input.Initialized)
            throw new InvalidOperationException("SMAA requires initialized RGBA8 or RGBA16F frame color.");
        var device = context.Device;
        var commands = context.Commands;
        var area = cache.Get("smaa:area", () => UploadLookup(context, 160, 560, 2, Format.R8G8_UNorm, Area.Value));
        var search = cache.Get("smaa:search", () => UploadLookup(context, 64, 16, 1, Format.R8_UNorm, Search.Value));
        var rtvHeap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 3)));
        var srvHeap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            5, DescriptorHeapFlags.ShaderVisible)));
        int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 5);
        D3D12GpuTarget Target(string name, int index)
        {
            var format=index==2?input.Format:Format.R8G8B8A8_UNorm;
            var resource = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(format, (uint)input.Width, (uint)input.Height,
                    mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget));
            var handle = rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset(index, rtvStride);
            device.CreateRenderTargetView(resource, null, handle);
            return new(name, resource, handle, input.Width, input.Height, format, false);
        }
        var edges = Target("$smaa-edges", 0);
        var weights = Target("$smaa-weights", 1);
        var output = Target("$smaa-output", 2);
        ID3D12Resource[] textures = [input.Resource, edges.Resource, area, search, weights.Resource];
        for (int i = 0; i < textures.Length; i++)
            device.CreateShaderResourceView(textures[i], null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(i, srvStride));

        commands.UnsetRenderTargets();
        context.Transition(input, ResourceStates.PixelShaderResource);
        // Edge detection discards nonedges; all channels, including weight alpha, must start at zero.
        commands.ClearRenderTargetView(edges.Handle, new Color4(0, 0, 0, 0));
        commands.ClearRenderTargetView(weights.Handle, new Color4(0, 0, 0, 0));
        commands.SetDescriptorHeaps(srvHeap);
        var constants = context.UploadConstants(new Vector4(1f / input.Width, 1f / input.Height, input.Width, input.Height));
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, input.Width, input.Height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, input.Width, input.Height));
        void Draw(string name, D3D12GpuTarget target)
        {
            var compiled = GetShaders(name, quality, alphaCoverage);
            string variant = quality + ":" + name + (alphaCoverage?":coverage":"") + (target.Format==Format.R8G8B8A8_UNorm?"":":fp16");
            var root = cache.Get("smaa:root:" + variant, () => device.CreateRootSignature<ID3D12RootSignature>(
                new RootSignatureDescription(RootSignatureFlags.None,
                    [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                     new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 5, 0)), ShaderVisibility.Pixel)],
                    compiled.Samplers), RootSignatureVersion.Version10));
            var pipeline = cache.Get("smaa:pso:" + variant, () =>
            {
                var created = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
                {
                    RootSignature = root, VertexShader = compiled.Vertex, PixelShader = compiled.Pixel,
                    BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue,
                    RasterizerState = RasterizerDescription.CullNone, DepthStencilState = DepthStencilDescription.None,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RenderTargetFormats = [target.Format],
                    SampleDescription = new(1, 0)
                });
                runtime.PipelineCreations++;
                return created;
            });
            commands.OMSetRenderTargets(target.Handle);
            commands.SetGraphicsRootSignature(root);
            commands.SetGraphicsRootConstantBufferView(0, constants);
            commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart());
            commands.SetPipelineState(pipeline);
            commands.DrawInstanced(3, 1, 0, 0);
            target.Initialized = true;
            commands.UnsetRenderTargets();
        }
        Draw("Edges", edges);
        context.Transition(edges, ResourceStates.PixelShaderResource);
        Draw("Weights", weights);
        context.Transition(weights, ResourceStates.PixelShaderResource);
        Draw("Neighborhood", output);
        return new(output, edges, weights);
    }

    private (byte[] Vertex, byte[] Pixel, StaticSamplerDescription[] Samplers) GetShaders(string pass, SmaaQuality quality, bool alphaCoverage)
    {
        var key = (quality, pass, alphaCoverage);
        if (shaders.TryGetValue(key, out var found)) return found;
        string preset = quality switch
        {
            SmaaQuality.Low => "SMAA_PRESET_LOW",
            SmaaQuality.Medium => "SMAA_PRESET_MEDIUM",
            SmaaQuality.High => "SMAA_PRESET_HIGH",
            SmaaQuality.Ultra => "SMAA_PRESET_ULTRA",
            _ => throw new ArgumentOutOfRangeException(nameof(quality))
        };
        // Keep the High source unchanged so its default image remains the existing baseline.
        string entries=alphaCoverage?Entries.Replace("struct EdgeVertex",CoverageEdges+"\nstruct EdgeVertex",StringComparison.Ordinal)
            .Replace("SMAALumaEdgeDetectionPS(i.uv, i.offset, SmaaColor)","SmaaCoverageEdges(i.uv, i.offset)",StringComparison.Ordinal):Entries;
        string source = Prefix.Replace("#define SMAA_PRESET_HIGH", "#define " + preset, StringComparison.Ordinal) + Algorithm.Value + entries;
        var flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        var vertex = Compiler.Compile(source, "VS" + pass, "smaa-wrapper.hlsl", "vs_5_1", flags).ToArray();
        var pixel = Compiler.Compile(source, "PS" + pass, "smaa-wrapper.hlsl", "ps_5_1", flags).ToArray();
        // The unmodified upstream HLSL4 declares its linear/point samplers without explicit registers.
        // FXC can compact an unused sampler out and give PointSampler s0. Bind each compiled
        // pass's actual registers instead of assuming declaration order survives optimization.
        var samplers = D3D12ShaderReflection.Read(pixel).Resources.Where(r => r.Kind == 3)
            .Select(s => Sampler(s.Slot, s.Name switch
            {
                "LinearSampler" => Filter.MinMagLinearMipPoint,
                "PointSampler" => Filter.MinMagMipPoint,
                _ => throw new InvalidOperationException("Unexpected native SMAA sampler: " + s.Name)
            })).ToArray();
        shaders.Add(key, (vertex, pixel, samplers));
        runtime.ShaderCompilations += 2;
        return (vertex, pixel, samplers);
    }

    private static StaticSamplerDescription Sampler(int slot, Filter filter) => new(ShaderVisibility.Pixel, slot, 0)
    { Filter = filter, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp };

    private ID3D12Resource UploadLookup(D3D12RecordingContext context, int width, int height, int channels, Format format, byte[] data)
    {
        var desc = ResourceDescription.Texture2D(format, (uint)width, (uint)height, mipLevels: 1);
        var resource = context.Device.CreateCommittedResource(HeapType.Default, desc, ResourceStates.CopyDest);
        try
        {
            var footprint = new PlacedSubresourceFootPrint[1];
            context.Device.GetCopyableFootprints(desc, 0, 1, 0, footprint, new int[1], new ulong[1], out ulong size);
            var staging = context.Own(context.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(size), ResourceStates.GenericRead));
            var mapped = staging.Map<byte>(0, checked((int)size));
            try
            {
                int rowBytes = checked(width * channels);
                for (int y = 0; y < height; y++) data.AsSpan(y * rowBytes, rowBytes).CopyTo(mapped.Slice(y * footprint[0].Footprint.RowPitch, rowBytes));
            }
            finally { staging.Unmap(0); }
            context.Commands.CopyTextureRegion(new TextureCopyLocation(resource, 0), 0, 0, 0, new TextureCopyLocation(staging, footprint[0]));
            context.Commands.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            runtime.TextureUploads++;
            return resource;
        }
        catch { resource.Dispose(); throw; }
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(D3D12SmaaPass).Assembly.GetManifestResourceStream("Smaa." + name)
            ?? throw new InvalidOperationException("Missing embedded SMAA resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] ReadLookup(string name, int expectedBytes)
    {
        string header = ReadResource(name);
        int start = header.IndexOf("Bytes[] = {", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Invalid SMAA lookup header: " + name);
        int end = header.IndexOf("};", start, StringComparison.Ordinal);
        var data = Regex.Matches(header[start..end], "0x[0-9a-fA-F]{2}")
            .Select(m => byte.Parse(m.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        if (data.Length != expectedBytes) throw new InvalidOperationException("Invalid SMAA lookup size: " + name);
        return data;
    }

    private const string Prefix = """
        cbuffer SmaaConstants : register(b0) { float4 SmaaMetrics; };
        #define SMAA_RT_METRICS SmaaMetrics
        #define SMAA_HLSL_4
        #define SMAA_PRESET_HIGH
        #define SMAA_REPROJECTION 0
        #define SMAA_PREDICATION 0

        """;

    // Optional coverage adaptation of upstream SMAA's luma threshold/local-contrast stage.
    // The original MIT header and LUTs remain unmodified; see third_party/smaa/LICENSE.txt.
    // Use the maximum luma/alpha difference so a black silhouette on transparent black is detectable.
    // All four premultiplied channels then share the unchanged neighborhood weights.
    private const string CoverageEdges="""
        float2 SmaaLumaAlpha(float2 uv) {
            float4 color=SMAASamplePoint(SmaaColor,uv);
            return float2(dot(color.rgb,float3(.2126,.7152,.0722)),color.a);
        }
        float SmaaCoverageDelta(float2 a,float2 b) { float2 d=abs(a-b); return max(d.x,d.y); }
        float2 SmaaCoverageEdges(float2 uv,float4 offset[3]) {
            float2 current=SmaaLumaAlpha(uv);
            float2 left=SmaaLumaAlpha(offset[0].xy),top=SmaaLumaAlpha(offset[0].zw);
            float4 delta;
            delta.xy=float2(SmaaCoverageDelta(current,left),SmaaCoverageDelta(current,top));
            float2 edges=step(float2(SMAA_THRESHOLD,SMAA_THRESHOLD),delta.xy);
            if(dot(edges,float2(1,1))==0)discard;
            delta.zw=float2(SmaaCoverageDelta(current,SmaaLumaAlpha(offset[1].xy)),
                SmaaCoverageDelta(current,SmaaLumaAlpha(offset[1].zw)));
            float2 maxDelta=max(delta.xy,delta.zw);
            delta.zw=float2(SmaaCoverageDelta(left,SmaaLumaAlpha(offset[2].xy)),
                SmaaCoverageDelta(top,SmaaLumaAlpha(offset[2].zw)));
            maxDelta=max(maxDelta,delta.zw);
            float finalDelta=max(maxDelta.x,maxDelta.y);
            return edges*step(finalDelta,SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR*delta.xy);
        }
        """;

    // Entry wrappers only. The included algorithm and lookup headers are unmodified upstream files.
    private const string Entries = """

        Texture2D SmaaColor : register(t0);
        Texture2D SmaaEdges : register(t1);
        Texture2D SmaaArea : register(t2);
        Texture2D SmaaSearch : register(t3);
        Texture2D SmaaWeights : register(t4);
        struct EdgeVertex { float4 position : SV_Position; float2 uv : TEXCOORD0; float4 offset[3] : TEXCOORD1; };
        struct WeightVertex { float4 position : SV_Position; float2 uv : TEXCOORD0; float2 pixel : TEXCOORD1; float4 offset[3] : TEXCOORD2; };
        struct NeighborhoodVertex { float4 position : SV_Position; float2 uv : TEXCOORD0; float4 offset : TEXCOORD1; };
        float2 TriangleUv(uint id) { return float2((id << 1) & 2, id & 2); }
        float4 TrianglePosition(float2 uv) { return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); }
        EdgeVertex VSEdges(uint id : SV_VertexID) {
            EdgeVertex o; o.uv = TriangleUv(id); o.position = TrianglePosition(o.uv);
            SMAAEdgeDetectionVS(o.uv, o.offset); return o;
        }
        float4 PSEdges(EdgeVertex i) : SV_Target {
            return float4(SMAALumaEdgeDetectionPS(i.uv, i.offset, SmaaColor), 0, 0);
        }
        WeightVertex VSWeights(uint id : SV_VertexID) {
            WeightVertex o; o.uv = TriangleUv(id); o.position = TrianglePosition(o.uv);
            SMAABlendingWeightCalculationVS(o.uv, o.pixel, o.offset); return o;
        }
        float4 PSWeights(WeightVertex i) : SV_Target {
            return SMAABlendingWeightCalculationPS(i.uv, i.pixel, i.offset, SmaaEdges, SmaaArea, SmaaSearch, float4(0, 0, 0, 0));
        }
        NeighborhoodVertex VSNeighborhood(uint id : SV_VertexID) {
            NeighborhoodVertex o; o.uv = TriangleUv(id); o.position = TrianglePosition(o.uv);
            SMAANeighborhoodBlendingVS(o.uv, o.offset); return o;
        }
        float4 PSNeighborhood(NeighborhoodVertex i) : SV_Target {
            return SMAANeighborhoodBlendingPS(i.uv, i.offset, SmaaColor, SmaaWeights);
        }
        """;
}
