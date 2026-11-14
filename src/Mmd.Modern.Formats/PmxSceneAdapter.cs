using Mmd.Modern.Core;

namespace Mmd.Modern.Formats;

/// <summary>Static PMX rest pose. SDEF/QDEF animation, morphs and physics are not evaluated.</summary>
public static class PmxSceneAdapter
{
    public static SceneAdaptationResult FromRestPose(PmxModel model, Func<string, SceneTexture?>? loadTexture = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var vertices = new SceneVertex[model.Vertices.Count];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new(model.Vertices[i].Position, model.Vertices[i].Normal, model.Vertices[i].Uv);
        var indices = new uint[checked(model.Faces.Count * 3)];
        for (int i = 0; i < model.Faces.Count; i++)
        {
            var t = model.Faces[i];
            if (t.A < 0 || t.B < 0 || t.C < 0) throw new ArgumentException("Negative PMX vertex index.");
            indices[i * 3] = (uint)t.A; indices[i * 3 + 1] = (uint)t.B; indices[i * 3 + 2] = (uint)t.C;
        }
        var diagnostics = new List<string> { "PMX rest pose only: no SDEF/QDEF animation, morph, bone or physics evaluation. Sphere maps, edges, additional UVs and transparency sorting are not implemented." };
        var textures = new List<SceneTexture>();
        var cache = new Dictionary<int, int>();
        int Resolve(int index, string label)
        {
            if (index == -1) return -1;
            if (index < 0 || index >= model.Textures.Count) throw new ArgumentException($"Invalid PMX {label} texture identity {index}.");
            if (cache.TryGetValue(index, out int existing)) return existing;
            if (loadTexture?.Invoke(model.Textures[index]) is not { } loaded)
            {
                diagnostics.Add($"PMX {label} texture '{model.Textures[index]}' not loaded; explicit modern fallback.");
                cache[index] = -1; return -1;
            }
            int mapped = textures.Count; textures.Add(loaded); cache[index] = mapped; return mapped;
        }
        int next = 0;
        var materials = new SceneMaterial[model.Materials.Count];
        for (int i = 0; i < materials.Length; i++)
        {
            var m = model.Materials[i];
            int diffuse = Resolve(m.TextureIndex, "diffuse");
            int toon;
            if (m.ToonMode == PmxToonMode.Common)
            {
                if (m.ToonTextureIndex is < 0 or > 9) throw new ArgumentException("PMX common toon index outside 0..9.");
                toon = textures.Count; textures.Add(SceneTexture.ProceduralToon(m.ToonTextureIndex));
                diagnostics.Add($"Material {i}: authored procedural common toon {m.ToonTextureIndex}; not an original MMD bitmap.");
            }
            else if (m.ToonMode == PmxToonMode.Separate) toon = Resolve(m.ToonTextureIndex, "toon");
            else throw new ArgumentException("Invalid PMX toon mode.");
            if (m.FaceVertexCount < 0 || m.FaceVertexCount > indices.Length - next) throw new ArgumentException("PMX material range outside indices.");
            materials[i] = new(m.Name, next, m.FaceVertexCount, m.Diffuse, m.Specular, m.Ambient, m.SpecularPower, diffuse, toon,
                ToonUvTransform: m.ToonMode == PmxToonMode.Separate && toon >= 0 ? new(0, -1, 0, 1) : null);
            next += m.FaceVertexCount;
        }
        return new(new(vertices, indices, materials, textures), diagnostics.AsReadOnly());
    }
}
