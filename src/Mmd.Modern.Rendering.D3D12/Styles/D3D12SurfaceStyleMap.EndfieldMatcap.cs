using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12SurfaceStyleMap
{
    private SceneTexture?[]? validatedMatcapNormalTextures;
    private SceneTexture?[]? validatedMatcapPTextures;
    private SceneTexture?[]? validatedMatcapColorTextures;
    private string?[]? validatedMatcapAppearance;

    internal bool HasConsumedEndfieldMatcap => Enumerable.Range(0, Count).Any(i => EffectiveEndfieldMatcap(i) is not null);

    internal D3D12EndfieldMatcapBinding? EffectiveEndfieldMatcap(int materialIndex)
        => Materials[materialIndex] == D3D12SurfaceStyle.EndfieldPmx
            && CharacterMaterials[materialIndex].Role is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair
                ? CharacterMaterials[materialIndex].EndfieldMatcap : null;

    private void ValidateEndfieldMatcap(int index, OriginalFrameScene frame)
    {
        if (CharacterMaterials[index].EndfieldMatcap is not { } binding) return;
        if (Materials[index] != D3D12SurfaceStyle.EndfieldPmx)
            throw new NotSupportedException("Explicit Endfield MatCap requires its explicitly selected Endfield surface family.");
        int count = frame.Surface.Snapshot.Textures.Count;
        if (binding.NormalCombinedTextureIndex >= count || binding.PCombinedTextureIndex >= count || binding.MatcapCombinedTextureIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Endfield MatCap normal/P/color texture index is outside the actual frame texture table.");
        _ = EndfieldMatcapGeometry(index, frame);
    }

    internal D3D12EndfieldMatcapGeometryData EndfieldMatcapGeometry(int index, OriginalFrameScene frame)
    {
        var binding = EffectiveSurfaceMatcap(index)
            ?? throw new ArgumentException("The selected draw has no consumed Endfield MatCap binding.", nameof(index));
        var target = frame.Surface.Snapshot;
        var geometry = binding.Geometry;
        var source = geometry?.Source ?? target;
        var data = D3D12EndfieldMatcapGeometry.Build(source, geometry?.LocalMaterialIndex ?? index, binding.UvChannel);
        var material = target.Materials[index];
        var corners = data.Corners;
        if (corners.Length != material.IndexCount)
            throw new ArgumentException("Endfield MatCap captured geometry corner count does not match the selected current draw.", nameof(frame));
        if (binding.UvChannel > 0 && (target.AdditionalUvs is null || target.AdditionalUvs.ChannelCount < binding.UvChannel))
            throw new ArgumentException("Endfield MatCap selected effective UV channel is missing from the current draw.", nameof(frame));
        if (!Matrix4x4.Invert(target.World, out var inverseWorld))
            throw new ArgumentException("MatCap draw World must be invertible.", nameof(frame));
        var normalWorld = Matrix4x4.Transpose(inverseWorld);
        var indices = target.Indices.Slice(material.StartIndex, material.IndexCount);
        for (int i = 0; i < corners.Length; i++)
        {
            int vertexIndex = checked((int)indices[i]);
            var vertex = target.GetVertex(vertexIndex);
            Vector3 expected = Vector3.Transform(vertex.Position, target.World);
            var corner = corners[i];
            if (!Near(expected.X, corner.WorldPosition.X) || !Near(expected.Y, corner.WorldPosition.Y) || !Near(expected.Z, corner.WorldPosition.Z))
                throw new ArgumentException("Endfield MatCap captured source positions do not correspond to this draw in the same evaluated pose / World transform.", nameof(frame));
            var expectedNormal = Vector3.TransformNormal(vertex.Normal, normalWorld);
            var sourceNormal = new Vector3(corner.WorldNormal.X, corner.WorldNormal.Y, corner.WorldNormal.Z);
            // Scene composition normalizes the baked normal, so compare directions rather than
            // discarded source magnitude. A normal-only morph must reject a stale source capture.
            var expectedDirection = Vector3.Normalize(expectedNormal);
            var sourceDirection = Vector3.Normalize(sourceNormal);
            if (!Near(expectedDirection.X, sourceDirection.X) || !Near(expectedDirection.Y, sourceDirection.Y)
                || !Near(expectedDirection.Z, sourceDirection.Z))
                throw new ArgumentException("Endfield MatCap captured source normals do not correspond to this draw's current shading normals.", nameof(frame));
            Vector2 uv;
            if (binding.UvChannel == 0) uv = vertex.Uv;
            else
            {
                var additional = target.AdditionalUvs!.Vertices[vertexIndex];
                Vector4 selected = binding.UvChannel switch { 1 => additional.Uv1, 2 => additional.Uv2, 3 => additional.Uv3, 4 => additional.Uv4, _ => default };
                uv = new(selected.X, selected.Y);
            }
            if (!Near(uv.X, corner.UvParity.X) || !Near(uv.Y, corner.UvParity.Y))
                throw new ArgumentException("Endfield MatCap captured effective UVs do not correspond to this draw's selected current UV channel.", nameof(frame));
        }
        return data;
    }

    private static bool Near(float a, float b) => float.IsFinite(a) && float.IsFinite(b)
        && MathF.Abs(a - b) <= 1e-5f * MathF.Max(1, MathF.Max(MathF.Abs(a), MathF.Abs(b)));

    private void CaptureEndfieldMatcap(OriginalFrameScene frame)
    {
        if (!HasConsumedEndfieldMatcap) return;
        var snapshot = frame.Surface.Snapshot;
        validatedMatcapNormalTextures = new SceneTexture?[Count];
        validatedMatcapPTextures = new SceneTexture?[Count];
        validatedMatcapColorTextures = new SceneTexture?[Count];
        validatedMatcapAppearance = new string?[Count];
        for (int i = 0; i < Count; i++)
        {
            if (EffectiveEndfieldMatcap(i) is not { } binding) continue;
            validatedMatcapNormalTextures[i] = snapshot.Textures[binding.NormalCombinedTextureIndex];
            validatedMatcapPTextures[i] = snapshot.Textures[binding.PCombinedTextureIndex];
            validatedMatcapColorTextures[i] = snapshot.Textures[binding.MatcapCombinedTextureIndex];
            validatedMatcapAppearance[i] = EndfieldMatcapGeometry(i, frame).AppearanceFingerprint;
        }
    }

    private static bool SameEndfieldMatcapInputs(D3D12SurfaceStyleMap a, D3D12SurfaceStyleMap b)
    {
        if (a.validatedMatcapNormalTextures is null && b.validatedMatcapNormalTextures is null) return true;
        for (int i = 0; i < a.Count; i++)
        {
            if (!SameChannels(a.validatedMatcapNormalTextures?[i], b.validatedMatcapNormalTextures?[i], 0x3)
                || !SameChannels(a.validatedMatcapPTextures?[i], b.validatedMatcapPTextures?[i], 0x9)
                || !SameChannels(a.validatedMatcapColorTextures?[i], b.validatedMatcapColorTextures?[i], 0xf)
                || !string.Equals(a.validatedMatcapAppearance?[i], b.validatedMatcapAppearance?[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
