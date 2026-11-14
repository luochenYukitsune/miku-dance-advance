using Mmd.Modern.Core;
namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12SurfaceStyleMap
{
    private SceneTexture?[]? validatedGenshinMetallicLightmaps;
    private SceneTexture?[]? validatedGenshinMetallicColors;
    private SceneTexture?[]? validatedGenshinMetallicSpecularRamps;
    internal bool HasConsumedGenshinMetallicSpecular=>Enumerable.Range(0,Count).Any(i=>EffectiveGenshinMetallic(i)?.Specular is not null);
    internal bool HasConsumedGenshinMetallic=>Enumerable.Range(0,Count).Any(i=>EffectiveGenshinMetallic(i) is not null);
    internal D3D12GenshinMetallicBinding? EffectiveGenshinMetallic(int index)
        =>Materials[index]==D3D12SurfaceStyle.GenshinPmx && CharacterMaterials[index].GenshinMetallic is { EnableMetallics: not 0 } value ? value:null;
    private void ValidateGenshinMetallic(int index,OriginalFrameScene frame)
    {
        if(CharacterMaterials[index].GenshinMetallic is not {} value)return;
        if(Materials[index]!=D3D12SurfaceStyle.GenshinPmx)
            throw new NotSupportedException("Genshin metallic requires its explicitly selected Genshin surface family.");
        if(value.Specular?.RampCombinedTextureIndex is {} ramp)
        {
            if((uint)ramp>=(uint)frame.Surface.Snapshot.Textures.Count)
                throw new ArgumentOutOfRangeException(nameof(CharacterMaterials),"Metallic specular ramp is outside the current texture table.");
            if(frame.Surface.Snapshot.Textures[ramp].Kind!=SceneTextureKind.Image)
                throw new ArgumentException("Metallic specular ramp must be an explicitly selected numerical image.",nameof(CharacterMaterials));
        }
        int count=frame.Surface.Snapshot.Textures.Count;
        if(value.LightmapCombinedTextureIndex>=count || value.MatcapCombinedTextureIndex>=count)
            throw new ArgumentOutOfRangeException(nameof(CharacterMaterials),"Metallic lightmap/color image is outside the current texture table.");
        if(frame.Surface.Snapshot.Textures[value.LightmapCombinedTextureIndex].Kind!=SceneTextureKind.Image
            || frame.Surface.Snapshot.Textures[value.MatcapCombinedTextureIndex].Kind!=SceneTextureKind.Image)
            throw new ArgumentException("Metallic lightmap and MatCap must be explicitly selected image resources.",nameof(CharacterMaterials));
    }
    private void CaptureGenshinMetallic(OriginalFrameScene frame)
    {
        if(!HasConsumedGenshinMetallic)return;
        validatedGenshinMetallicLightmaps=new SceneTexture?[Count];
        validatedGenshinMetallicColors=new SceneTexture?[Count];
        validatedGenshinMetallicSpecularRamps=new SceneTexture?[Count];
        for(int i=0;i<Count;i++)if(EffectiveGenshinMetallic(i) is {} b)
        {
            validatedGenshinMetallicLightmaps[i]=frame.Surface.Snapshot.Textures[b.LightmapCombinedTextureIndex];
            validatedGenshinMetallicColors[i]=frame.Surface.Snapshot.Textures[b.MatcapCombinedTextureIndex];
            if(b.Specular is {ConsumesRamp:true,RampCombinedTextureIndex:{} ramp})
                validatedGenshinMetallicSpecularRamps[i]=frame.Surface.Snapshot.Textures[ramp];
        }
    }
    private static bool SameGenshinMetallicInputs(D3D12SurfaceStyleMap a,D3D12SurfaceStyleMap b)
    {
        if(a.validatedGenshinMetallicLightmaps is null && b.validatedGenshinMetallicLightmaps is null)return true;
        for(int i=0;i<a.Count;i++)
            if(!SameChannels(a.validatedGenshinMetallicLightmaps?[i],b.validatedGenshinMetallicLightmaps?[i],a.EffectiveGenshinMetallic(i)?.Specular is not null || b.EffectiveGenshinMetallic(i)?.Specular is not null?5:1)
                || !SameChannels(a.validatedGenshinMetallicColors?[i],b.validatedGenshinMetallicColors?[i],7)
                || !SameChannels(a.validatedGenshinMetallicSpecularRamps?[i],b.validatedGenshinMetallicSpecularRamps?[i],7))return false;
        return true;
    }
}
