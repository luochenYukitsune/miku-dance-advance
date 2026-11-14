using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>Adapts the existing CPU two-weight skinning output; no PMD object reaches a renderer.</summary>
public static class PmdSceneAdapter
{
    public static SceneAdaptationResult FromSkinned(PmdModel model, ReadOnlySpan<ModelVertex> skinnedVertices,
        float yawRadians = 0, Func<string, SceneTexture?>? loadTexture = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (skinnedVertices.Length != model.Vertices.Length) throw new ArgumentException("Skinned vertex count must match PMD.");
        var vertices = new SceneVertex[skinnedVertices.Length];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new(skinnedVertices[i].Position, skinnedVertices[i].Normal, skinnedVertices[i].Uv);
        var diagnostics = new List<string> { "PMD metadata is retained; this legacy scene adapter uses procedural toon. Sphere maps, edges and transparency sorting are not implemented by this adapter." };
        var textures = new List<SceneTexture>();
        var materials = model.Materials.Select((m, i) =>
        {
            int texture = -1;
            if (!string.IsNullOrWhiteSpace(m.Texture))
            {
                // PMD may combine diffuse and sphere names with '*'. Only the diffuse part is offered to the caller.
                string path = m.Texture.Split('*')[0];
                if (path.EndsWith(".sph", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".spa", StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add($"Material {i}: sphere texture '{path}' is unsupported.");
                else if (loadTexture?.Invoke(path) is { } loaded) { texture = textures.Count; textures.Add(loaded); }
                else diagnostics.Add($"Material {i}: diffuse texture '{path}' not loaded; explicit white fallback.");
            }
            return new SceneMaterial($"PMD material {i}", m.StartIndex, m.IndexCount, m.Diffuse, m.Specular, m.Ambient, m.Shininess, texture);
        }).ToArray();
        var indices = new uint[model.Indices.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = model.Indices[i];
        return new(new(vertices, indices, materials, textures, Matrix4x4.CreateRotationY(yawRadians)), diagnostics.AsReadOnly());
    }
}
