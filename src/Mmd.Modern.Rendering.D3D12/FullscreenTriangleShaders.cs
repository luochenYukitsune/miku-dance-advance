using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>
/// Compiles the bootstrap shaders at runtime with D3DCompiler (SM 5.1, no DXC dependency).
/// The pixel shader is deliberately just "return the constant colour"; its purpose is to prove
/// that a real PSO, root signature, root CBV and draw call execute on the device.
/// </summary>
internal static class FullscreenTriangleShaders
{
    private const string Source = """
        cbuffer PassConstants : register(b0)
        {
            float4 Color;
        };

        struct VertexOutput
        {
            float4 Position : SV_POSITION;
        };

        VertexOutput VSMain(uint vertexId : SV_VertexID)
        {
            float2 positions[3] =
            {
                float2(-1.0f, -1.0f),
                float2( 3.0f, -1.0f),
                float2(-1.0f,  3.0f)
            };

            VertexOutput output;
            output.Position = float4(positions[vertexId], 0.0f, 1.0f);
            return output;
        }

        float4 PSMain(float4 position : SV_POSITION) : SV_Target
        {
            return Color;
        }
        """;

    public static (byte[] Vertex, byte[] Pixel) Compile()
    {
        byte[] vertex = D3D12FxcShaderCache.Compile(
            Source,
            "VSMain",
            "D3D12FullscreenTriangle.hlsl",
            "vs_5_1",
            ShaderFlags.OptimizationLevel3);

        byte[] pixel = D3D12FxcShaderCache.Compile(
            Source,
            "PSMain",
            "D3D12FullscreenTriangle.hlsl",
            "ps_5_1",
            ShaderFlags.OptimizationLevel3);

        return (vertex, pixel);
    }
}
