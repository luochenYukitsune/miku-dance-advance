using System.Numerics;

namespace Mmd.Modern.Core;

public enum SceneAppearanceMode { MaterialMapped, ModernSoft }

/// <summary>Modern appearance policy applied after format adaptation, without changing source data.</summary>
public static class SceneAppearance
{
    /// <summary>Authored smoothstep grayscale ramp; independent of the ten material-indexed presets.</summary>
    public static SceneTexture ModernSoftToon { get; } = BuildModernSoftToon();

    public static SceneSnapshot Apply(SceneSnapshot scene, SceneAppearanceMode mode = SceneAppearanceMode.ModernSoft)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == SceneAppearanceMode.MaterialMapped) return scene;

        bool Replace(SceneMaterial material) => material.ToonTexture < 0
            || scene.Textures[material.ToonTexture].Kind == SceneTextureKind.IndexedProceduralToon;
        if (!scene.Materials.Any(Replace)) return scene;

        var textures = scene.Textures.ToList();
        int shared = textures.FindIndex(t => ReferenceEquals(t, ModernSoftToon));
        if (shared < 0) { shared = textures.Count; textures.Add(ModernSoftToon); }
        var materials = scene.Materials.Select(m => Replace(m)
            ? m with { ToonTexture = shared, ToonUvTransform = new Vector4(1, 0, 0, .5f) }
            : m);
        return new SceneSnapshot(scene.Vertices, scene.Indices, materials, textures,
            scene.World, scene.Camera, scene.LightDirection, scene.AdditionalUvs, scene.RawRadialSeeds);
    }

    private static SceneTexture BuildModernSoftToon()
    {
        var rgba = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            float smooth = t * t * (3f - 2f * t);
            byte level = (byte)(int)(255f * (.55f + .45f * smooth) + .5f);
            int offset = i * 4;
            rgba[offset] = level; rgba[offset + 1] = level; rgba[offset + 2] = level; rgba[offset + 3] = 255;
        }
        return new SceneTexture("modern-soft-toon", 256, 1, rgba, SceneTextureKind.ModernSoftToon);
    }
}
