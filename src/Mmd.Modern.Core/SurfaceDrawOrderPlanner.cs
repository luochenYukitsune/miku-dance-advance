using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace Mmd.Modern.Core;

/// <summary>Stable surface-only ordering; material identities, geometry and auxiliary pass order stay unchanged.</summary>
public static class SurfaceDrawOrderPlanner
{
    private sealed record AlphaInfo(bool HasTransparency);
    private static readonly ConditionalWeakTable<SceneTexture, AlphaInfo> TextureAlpha = new();
    private static readonly ConditionalWeakTable<OriginalFrameScene, ReadOnlyCollection<int>> Schedules = new();

    /// <summary>Returns material indices eligible for the surface loop, in draw order.</summary>
    public static IReadOnlyList<int> Plan(OriginalFrameScene frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Schedules.GetValue(frame, Build);
    }

    private static ReadOnlyCollection<int> Build(OriginalFrameScene frame)
    {
        var options = frame.Options;
        var materials = frame.Surface.Snapshot.Materials;
        var opaque = new List<int>(materials.Count);
        var transparent = new List<int>();
        bool partition = options?.SurfaceOrder == SurfaceDrawOrder.OpaqueFirst
            && !options.Transp && options.BlendMode == OriginalAlphaBlendMode.Normal;
        for (int i = 0; i < materials.Count; i++)
        {
            if (materials[i].IndexCount == 0 || (options is not null && !frame.Materials[i].SurfaceEnabled)) continue;
            if (partition && !IsOpaque(frame, i)) transparent.Add(i);
            else opaque.Add(i);
        }
        opaque.AddRange(transparent);
        return opaque.AsReadOnly();
    }

    private static bool IsOpaque(OriginalFrameScene frame, int index)
    {
        var scene = frame.Surface.Snapshot;
        var material = scene.Materials[index];
        var binding = frame.Surface.Materials[index];
        if (material.Diffuse.W < 1) return false; // Host clamps only the upper bound.
        if (material.DiffuseTexture >= 0 && HasAlpha(scene.Textures[material.DiffuseTexture])) return false;
        if (binding.SphereMode != OriginalSphereMode.None && HasAlpha(scene.Textures[binding.SphereTexture])) return false;
        // The UV1 two-map technique also multiplies alpha by the shadow/toon term.
        if (binding.SphereMode == OriginalSphereMode.SubTexture && material.DiffuseTexture >= 0)
        {
            float toonAlpha = binding.TextureFactors.Toon.Multiply.W + binding.TextureFactors.Toon.Add.W;
            if (!float.IsFinite(toonAlpha) || toonAlpha < 1) return false;
        }
        return true;
    }

    private static bool HasAlpha(SceneTexture texture) => TextureAlpha.GetValue(texture, static t =>
    {
        var rgba = t.Rgba;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return new(true);
        return new(false);
    }).HasTransparency;
}
