using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed record XAccessoryAdaptation(OriginalFrameScene Frame, IReadOnlyList<string> Diagnostics);

/// <summary>Static .x to existing renderer-neutral frame. Raw DirectX handedness, UV and units are retained;
/// explicit positive unitScale can be selected by the caller. No original toon assets are distributed.</summary>
public static class XAccessoryAdapter
{
    public static XAccessoryAdaptation FromDocument(XTextDocument document, Func<string, SceneTexture?>? loadTexture = null, float unitScale = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!float.IsFinite(unitScale) || unitScale <= 0) throw new ArgumentOutOfRangeException(nameof(unitScale));
        var diagnostics = document.Diagnostics.ToList(); var textures = new List<SceneTexture>();
        var loaded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var materials = document.Materials.Select(range =>
        {
            var material = range.Material; int texture = -1;
            if (!string.IsNullOrWhiteSpace(material.Texture))
            {
                if (!loaded.TryGetValue(material.Texture, out texture))
                {
                    if (loadTexture?.Invoke(material.Texture) is { } image) { texture = textures.Count; textures.Add(image); }
                    else { texture = -1; diagnostics.Add("Accessory texture unavailable; explicit untextured fallback: " + material.Texture); }
                    loaded.Add(material.Texture, texture);
                }
            }
            return new SceneMaterial(material.Name, range.StartIndex, range.IndexCount, material.Diffuse, material.Specular,
                material.Emissive, material.SpecularPower, texture);
        }).ToArray();
        var vertices = document.Vertices.Select(v => v with { Position = v.Position * unitScale }).ToArray();
        var snapshot = new SceneSnapshot(vertices, document.Indices.ToArray(), materials, textures);
        var bindings = materials.Select(_ => new OriginalStyleMaterialBinding(Vector3.One)).ToArray();
        var selected = OriginalOutlineMath.SelectVertexMaterialsAtLoad(vertices.Length, snapshot.Indices,
            materials.Select(m => m.IndexCount).ToArray(), new float[materials.Length]);
        var edges = vertices.Select((vertex, index) => new OriginalOutlineVertex(vertex.Position, vertex.Normal,
            OriginalOutlineVertexMode.Expand, 0, selected[index])).ToArray();
        var outline = new OriginalOutlineData(edges, materials.Select(_ => new OriginalOutlineMaterial(0, Vector4.Zero, false)));
        var frame = new OriginalFrameScene(new(snapshot, bindings), outline,
            new(Vector4.One, Matrix4x4.Identity, snapshot.Camera.Eye, 30, 0, false),
            materials: materials.Select(_ => new OriginalFrameMaterialFlags(true, true, true)));
        diagnostics.Add("Accessory surface uses neutral material/texture inputs. Original MMD accessory shader and unit parity require their separate renderer validation.");
        return new(frame, diagnostics.AsReadOnly());
    }
}
