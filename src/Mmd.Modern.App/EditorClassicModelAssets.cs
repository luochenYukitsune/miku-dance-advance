using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>Classic-only image retention after a caller has loaded the neutral model. Shared/default
/// toon resources are explicit caller inputs; this adapter neither locates nor distributes MMD assets.</summary>
internal static class EditorClassicModelAssets
{
    internal static SceneAnimationModelAsset RetainToonTextures(SceneAnimationModelAsset asset, string path,
        CancellationToken token = default, Func<int, SceneTexture?>? loadCommonToon = null,
        Func<SceneTexture?>? loadDefaultToon = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();
        var source = asset.Source;
        var baseline = source.Surface.Snapshot;
        if (baseline.Materials.Count != asset.Model.Materials.Count)
            throw new InvalidDataException("Classic toon retention requires one source material per PMX material.");
        if (baseline.Materials.All(material => material.IndexCount == 0 || material.ToonTexture >= 0
            && baseline.Textures[material.ToonTexture].Kind == SceneTextureKind.Image)) return asset;

        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var adapted = PmxOriginalStyleAdapter.FromRestPose(asset.Model,
            EditorTextureLoader.WithModelBudget(relative => EditorTextureLoader.Load(root, relative, token)), loadCommonToon,
            source.Surface.Profile, retainToonTextures: true, loadDefaultToon: loadDefaultToon).Scene;
        // The caller's geometry, camera, materials and runtime flags stay authoritative. Append only
        // the selected image bindings, keeping all pre-existing diffuse/sphere identities intact.
        var textures = baseline.Textures.ToList();
        var remap = new Dictionary<int, int>();
        var materials = baseline.Materials.ToArray();
        var bindings = source.Surface.Materials.ToArray();
        for (int index = 0; index < materials.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            int selected = adapted.Snapshot.Materials[index].ToonTexture;
            if (selected < 0) continue;
            if (!remap.TryGetValue(selected, out int retained))
            {
                retained = textures.Count; textures.Add(adapted.Snapshot.Textures[selected]); remap.Add(selected, retained);
            }
            materials[index] = materials[index] with { ToonTexture = retained };
            bindings[index] = bindings[index] with { ToonShadowColor = adapted.Materials[index].ToonShadowColor };
        }
        var snapshot = new SceneSnapshot(baseline.Vertices, baseline.Indices, materials, textures,
            baseline.World, baseline.Camera, baseline.LightDirection,
            baseline.AdditionalUvs, baseline.RawRadialSeeds);
        if (baseline.HasDynamicVertices) snapshot = snapshot.WithDeformedVertices(baseline.Vertices.ToArray());
        var surface = new OriginalStyleScene(snapshot, bindings, source.Surface.Profile);
        return asset with { Source = new(surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials, source.Lighting, source.EditorOverlay) };
    }
}
