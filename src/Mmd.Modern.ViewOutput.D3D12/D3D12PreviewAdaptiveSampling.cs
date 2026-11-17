using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Preview-only admission choices. The requested quality and scene remain immutable;
/// only a renderer-owned pre-submit GI budget rejection may advance to a lower spatial sample count.</summary>
internal sealed class D3D12PreviewAdaptiveSampling
{
    private ResourceContext? resources;
    private ViewQualityOptions? quality;
    private int width, height, requestedSamples;
    private string? adjustmentReason;
    internal int SpatialSamples { get; private set; } = 1;
    internal int LastRequestedSpatialSamples { get; private set; } = 1;
    internal int LastEffectiveSpatialSamples { get; private set; } = 1;
    internal string? LastAdjustmentReason { get; private set; }

    internal void Prepare(int width, int height, ViewQualityOptions quality, OriginalFrameScene scene,
        D3D12SurfaceStyleMap? styles = null)
    {
        int requested = scene.Options?.SpatialSamples ?? 1;
        if (requested is not (1 or 2 or 4 or 8 or 16 or 32))
            throw new ArgumentOutOfRangeException(nameof(scene), "Unsupported spatial sample count.");
        // Camera/pose values are intentionally absent: animated frames with the same resource
        // layout reuse the admitted choice instead of attempting the rejected count every frame.
        if (this.width == width && this.height == height && this.quality == quality
            && requestedSamples == requested && resources?.Matches(scene, styles) == true) return;
        this.width = width; this.height = height; this.quality = quality;
        requestedSamples = requested; SpatialSamples = requested; adjustmentReason = null;
        resources = new(scene, styles);
    }

    internal bool Reduce(string reason)
    {
        if (SpatialSamples == 1) return false;
        SpatialSamples /= 2;
        adjustmentReason = reason;
        return true;
    }

    internal OriginalFrameScene ApplyTo(OriginalFrameScene scene)
    {
        if ((scene.Options?.SpatialSamples ?? 1) == SpatialSamples) return scene;
        var options = (scene.Options ?? new(OriginalAlphaBlendMode.Normal, true, false))
            with { SpatialSamples = SpatialSamples };
        return new(scene.Surface, scene.Outline, scene.OutlineOptions, options,
            scene.Materials, scene.Lighting, scene.EditorOverlay);
    }

    internal void CommitPresentation()
    {
        LastRequestedSpatialSamples = requestedSamples;
        LastEffectiveSpatialSamples = SpatialSamples;
        LastAdjustmentReason = SpatialSamples < requestedSamples ? adjustmentReason : null;
    }

    internal void Reset() => resources = null;

    /// <summary>Only allocation-driving layout/asset identity, never per-frame geometry contents.
    /// New textures, topology, native shadow atlas layout or dedicated resource families reattempt
    /// the requested count; any later memory pressure can still reject and lower the cached count.</summary>
    private sealed class ResourceContext
    {
        private readonly ResourceShape shape;
        private readonly IReadOnlyList<SceneTexture> textures;
        private readonly int[] materialResources;

        internal ResourceContext(OriginalFrameScene scene, D3D12SurfaceStyleMap? styles)
        {
            shape = ResourceShape.Create(scene);
            textures = scene.Surface.Snapshot.Textures;
            materialResources = Enumerable.Range(0, scene.Materials.Count).Select(index => ResourceKind(styles, index)).ToArray();
        }

        internal bool Matches(OriginalFrameScene scene, D3D12SurfaceStyleMap? styles)
        {
            if (shape != ResourceShape.Create(scene)) return false;
            var nextTextures = scene.Surface.Snapshot.Textures;
            for (int index = 0; index < textures.Count; index++)
                if (!ReferenceEquals(textures[index], nextTextures[index])) return false;
            for (int index = 0; index < materialResources.Length; index++)
                if (materialResources[index] != ResourceKind(styles, index)) return false;
            return true;
        }

        private static int ResourceKind(D3D12SurfaceStyleMap? styles, int index)
        {
            if (styles is null || styles.IsDefault) return 0;
            var material = styles.CharacterMaterials[index];
            return ((int)styles.Materials[index] << 16)
                | (material.FaceSdf is not null ? 1 : 0)
                | (material.FaceSdf?.Encoding is D3D12FaceSdfEncoding.EndfieldRgbCmM or D3D12FaceSdfEncoding.GenshinSoftFace ? 2 : 0)
                | (material.GenshinNonMetallicSpecular is not null ? 4 : 0)
                | (material.StarMcHair is not null ? 8 : 0)
                | (material.EndfieldHair is not null ? 16 : 0)
                | (material.GenshinHairShadowAtlas is not null ? 32 : 0)
                | (material.EndfieldMatcap is not null ? 64 : 0)
                | (material.EndfieldMatcap?.Palette?.BaseImage is not null ? 128 : 0)
                | (material.GenshinMetallic is not null ? 256 : 0)
                | (material.GenshinMetallic?.Specular is not null ? 512 : 0)
                | (material.StarMcBody is not null ? 1024 : 0);
        }
    }

    private readonly record struct ResourceShape(int Vertices, int Indices, int Materials, int Textures,
        int OutlineVertices, bool DynamicVertices, bool AdditionalUvs, bool RawRadialSeeds,
        int ShadowWidth, int ShadowHeight, int NativeShadowWidth, int NativeShadowHeight, bool Stencil, FrameAlphaOutput AlphaOutput,
        FrameAntialiasing Antialiasing)
    {
        internal static ResourceShape Create(OriginalFrameScene frame)
        {
            var scene = frame.Surface.Snapshot;
            var shadow = frame.Options?.SelfShadow;
            var nativeAtlas = NativeLightShadowAtlasLayout.Create(frame.Lighting);
            return new(scene.VertexCount, scene.Indices.Length, scene.Materials.Count, scene.Textures.Count,
                frame.Outline?.Vertices.Count ?? 0, scene.HasDynamicVertices, scene.AdditionalUvs is not null, scene.RawRadialSeeds is not null,
                shadow is { Generate: true } ? shadow.Width : 0, shadow is { Generate: true } ? shadow.Height : 0, nativeAtlas.Width, nativeAtlas.Height,
                frame.Options?.GroundShadow?.UseStencil == true, frame.Options?.AlphaOutput ?? FrameAlphaOutput.LegacyMax,
                frame.Options?.Antialiasing ?? FrameAntialiasing.Off);
        }
    }
}
