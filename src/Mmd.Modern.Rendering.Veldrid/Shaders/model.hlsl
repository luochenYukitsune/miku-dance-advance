[[vk::binding(0, 0)]] cbuffer Parameters : register(b0)
{
    row_major float4x4 ViewProjection;
    float4 Diffuse;
};

struct VertexInput
{
    [[vk::location(0)]] float3 Position : POSITION;
    [[vk::location(1)]] float3 Normal : NORMAL;
};
struct VertexOutput
{
    float4 Position : SV_Position;
    [[vk::location(0)]] float3 Normal : NORMAL;
};
VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.Position = mul(float4(input.Position, 1), ViewProjection);
    output.Normal = input.Normal;
    return output;
}
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 normal = normalize(input.Normal);
    float lighting = 0.28 + 0.72 * saturate(dot(normal, normalize(float3(-0.4, 0.8, -1))));
    return float4(Diffuse.rgb * lighting, Diffuse.a);
}
