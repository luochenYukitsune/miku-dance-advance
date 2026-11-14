using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12SurfaceStyleMap
{
    private SceneTexture?[][]? validatedStarMcBodyTextures;
    private string?[]? validatedStarMcBodyGeometry;
    internal bool HasConsumedStarMcBody => Enumerable.Range(0, Count).Any(i => EffectiveStarMcBody(i) is not null);
    internal bool HasConsumedSurfaceMatcap => HasConsumedEndfieldMatcap || HasConsumedStarMcBody;
    internal D3D12StarMcBodyBinding? EffectiveStarMcBody(int i) =>
        Materials[i] is D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx
            && CharacterMaterials[i].Role is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair ? CharacterMaterials[i].StarMcBody : null;
    internal D3D12EndfieldMatcapBinding? EffectiveSurfaceMatcap(int i) =>
        EffectiveStarMcBody(i)?.GeometryAdapter(Materials[i] == D3D12SurfaceStyle.WutheringPmx) ?? EffectiveEndfieldMatcap(i);

    private void ValidateStarMcBody(int i, OriginalFrameScene frame)
    {
        if (CharacterMaterials[i].StarMcBody is not { } b) return;
        if (Materials[i] is not (D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx))
            throw new NotSupportedException("Explicit Star/MC Body requires the selected Star Rail or Wuthering surface family.");
        var images = frame.Surface.Snapshot.Textures;
        foreach (int slot in new[] { b.NormalCombinedTextureIndex, b.DetailCombinedTextureIndex, b.MaskCombinedTextureIndex, b.MatcapCombinedTextureIndex })
        {
            if (slot >= images.Count) throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Star/MC Body texture index is outside the actual frame texture table.");
            if (images[slot].Rgba.Length != checked(images[slot].Width * images[slot].Height * 4))
                throw new ArgumentException("Star/MC Body requires actual image data, not a procedural toon resource.", nameof(frame));
        }
        _ = EndfieldMatcapGeometry(i, frame);
    }
    private void CaptureStarMcBody(OriginalFrameScene frame)
    {
        if (!HasConsumedStarMcBody) return;
        validatedStarMcBodyTextures = new SceneTexture?[Count][];
        validatedStarMcBodyGeometry = new string?[Count];
        for (int i = 0; i < Count; i++)
        {
            if (EffectiveStarMcBody(i) is not { } b) continue;
            var t = frame.Surface.Snapshot.Textures;
            validatedStarMcBodyTextures[i] = [t[b.NormalCombinedTextureIndex], t[b.DetailCombinedTextureIndex], t[b.MaskCombinedTextureIndex], t[b.MatcapCombinedTextureIndex]];
            validatedStarMcBodyGeometry[i] = EndfieldMatcapGeometry(i, frame).AppearanceFingerprint;
        }
    }
    private static bool SameStarMcBodyInputs(D3D12SurfaceStyleMap a, D3D12SurfaceStyleMap b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            bool mc = a.Materials[i] == D3D12SurfaceStyle.WutheringPmx;
            if (!D3D12StarMcBodyBinding.ConsumedEquivalent(a.EffectiveStarMcBody(i), b.EffectiveStarMcBody(i), mc,
                a.validatedStarMcBodyTextures is null || b.validatedStarMcBodyTextures is null)) return false;
            var x = a.validatedStarMcBodyTextures?[i]; var y = b.validatedStarMcBodyTextures?[i];
            if (mc && (!SameChannels(x?[0], y?[0], 3) || !SameChannels(x?[1], y?[1], 2))
                || !SameChannels(x?[2], y?[2], 5) || !SameChannels(x?[3], y?[3], 7)
                || a.validatedStarMcBodyGeometry?[i] != b.validatedStarMcBodyGeometry?[i]) return false;
        }
        return true;
    }
}
