namespace Mmd.Modern.Rendering.D3D12;

/// <summary>The one native-light distance/cone model shared by direct light (every surface family) and
/// secondary GI. It mirrors <see cref="Mmd.Modern.Core.SceneLightFalloff"/>: intensity arrives already
/// aim-normalized from the CPU, so the shader only applies the unnormalized range window.</summary>
internal static class NativeLightModel
{
    internal const string Hlsl = """
        // Smooth range window (1-(d/R)^2)^2: one at the light, zero at the authored range.
        float NativeLightFalloff(float distanceSquared,float range) {
            float t=saturate(1-distanceSquared/(range*range));
            return t*t;
        }
        // Kind in positionKind.w: 0 directional, 1 point, 2 spot, 3 area sample. towardLight is unit length.
        float NativeLightAttenuation(float4 positionKind,float4 directionRange,float4 coneShadow,float distanceSquared,float3 towardLight) {
            if(positionKind.w==0) return 1;
            float attenuation=NativeLightFalloff(distanceSquared,directionRange.w);
            if(positionKind.w==2) {
                float angular=saturate((dot(-towardLight,directionRange.xyz)-coneShadow.y)/max(coneShadow.x-coneShadow.y,.00001));
                attenuation*=angular*angular;
            }
            if(positionKind.w==3) attenuation*=saturate(dot(-towardLight,directionRange.xyz));
            return attenuation;
        }
        """;
}
