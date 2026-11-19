using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>Only allocation-driving layout/asset identity, never per-frame geometry or pose contents.
    /// New textures, topology, native shadow atlas layout or plugin shader/resource layouts reattempt
    /// the requested count; any later memory pressure can still reject and lower the cached count.</summary>
    private sealed class ResourceContext
    {
        private readonly ResourceShape shape;
        private readonly IReadOnlyList<SceneTexture> textures;
        private readonly MaterialSamplingKey[] materialResources;

        internal ResourceContext(OriginalFrameScene scene, D3D12SurfaceStyleMap? styles)
        {
            shape = ResourceShape.Create(scene);
            textures = scene.Surface.Snapshot.Textures;
            materialResources = new MaterialSamplingKey[scene.Materials.Count];
            for (int index = 0; index < materialResources.Length; index++)
                materialResources[index] = CreateMaterialKey(styles, index);
        }

        internal bool Matches(OriginalFrameScene scene, D3D12SurfaceStyleMap? styles)
        {
            if (shape != ResourceShape.Create(scene)) return false;
            var nextTextures = scene.Surface.Snapshot.Textures;
            for (int index = 0; index < textures.Count; index++)
                if (!ReferenceEquals(textures[index], nextTextures[index])) return false;
            for (int index = 0; index < materialResources.Length; index++)
                if (materialResources[index] != CreateMaterialKey(styles, index)) return false;
            return true;
        }

        private static MaterialSamplingKey CreateMaterialKey(D3D12SurfaceStyleMap? styles, int index)
        {
            if (styles is null || styles.IsDefault || styles.IsDefaultStyle(index))
                return default;

            string styleId = styles.StyleIds[index];
            var material = styles.CharacterMaterials[index];
            var draw = styles.GetPluginDraw(index);

            string? moduleId = draw?.ShaderModuleId;
            string? moduleHash = null;
            D3D12StyleShaderHookMask hooks = draw?.HookMaskOverride ?? D3D12StyleShaderHookMask.None;
            D3D12StyleGeometryRequirements geom = D3D12StyleGeometryRequirements.None;

            if (draw is null || (styles.RegisteredStyles.TryGetValue(styleId, out var reg)
                && reg.ShaderModules is not null && reg.ShaderModules.ContainsKey(draw.ShaderModuleId)))
            {
                var module = styles.ResolveShaderModule(index);
                if (module is not null)
                {
                    moduleId = module.ModuleId;
                    moduleHash = module.EffectiveContentHash;
                    hooks = styles.EffectiveHookMask(index);
                    geom = styles.EffectiveGeometryRequirements(index);
                }
            }

            string drawLayoutFingerprint = ComputeDrawLayoutFingerprint(draw);
            bool hasTreatment = styles.EffectivePmxMaterialTreatment(index) is not null;
            bool hasStylized = styles.EffectiveStylizedShading(index) is not null;
            bool hasToon = styles.EffectiveToonResponse(index, null) is not null;
            bool preserveDirect = styles.EffectivePreserveDirectLightPalette(index, false);

            return new MaterialSamplingKey(
                Active: true,
                StyleId: styleId,
                Role: material.Role,
                ModuleId: moduleId,
                ModuleHash: moduleHash,
                Hooks: hooks,
                Geometry: geom,
                DrawLayoutFingerprint: drawLayoutFingerprint,
                HasTreatment: hasTreatment,
                HasStylized: hasStylized,
                HasToon: hasToon,
                PreserveDirectPalette: preserveDirect);
        }

        private static string ComputeDrawLayoutFingerprint(D3D12PluginDrawData? draw)
        {
            if (draw is null)
                return string.Empty;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            static void FeedInt(IncrementalHash h, int value)
            {
                Span<byte> buf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buf, value);
                h.AppendData(buf);
            }
            static void FeedString(IncrementalHash h, string? s)
            {
                byte[] bytes = s is null ? [] : Encoding.UTF8.GetBytes(s);
                FeedInt(h, s is null ? -1 : bytes.Length);
                if (bytes.Length > 0) h.AppendData(bytes);
            }

            // Capture structural CBV length and binding slots/keys (not animated camera/bone floats).
            FeedInt(hash, draw.ConstantBufferData.Length);
            FeedInt(hash, draw.DisableStylizedSphereMaps.HasValue ? (draw.DisableStylizedSphereMaps.Value ? 2 : 1) : 0);
            FeedInt(hash, draw.ExtraSurfaceParams.HasValue ? 1 : 0);
            FeedInt(hash, draw.PreserveDirectLightPalette.HasValue ? (draw.PreserveDirectLightPalette.Value ? 2 : 1) : 0);

            int texCount = draw.TextureBindings?.Count ?? 0;
            FeedInt(hash, texCount);
            if (texCount > 0)
            {
                foreach (var tex in draw.TextureBindings!)
                {
                    FeedInt(hash, tex.SlotIndex);
                    FeedInt(hash, tex.SceneTextureIndex >= 0 ? 1 : 0);
                    FeedInt(hash, (int)tex.Encoding);
                    FeedInt(hash, (int)tex.MipRule);
                    if (tex.InlineTexture is { } inline)
                    {
                        FeedInt(hash, 1);
                        FeedString(hash, inline.AssetKey);
                        FeedInt(hash, inline.Width);
                        FeedInt(hash, inline.Height);
                        FeedInt(hash, (int)inline.Format);
                        FeedInt(hash, (int)inline.MipRule);
                    }
                    else
                    {
                        FeedInt(hash, 0);
                    }
                }
            }

            int bufCount = draw.BufferBindings?.Count ?? 0;
            FeedInt(hash, bufCount);
            if (bufCount > 0)
            {
                foreach (var buf in draw.BufferBindings!)
                {
                    FeedInt(hash, buf.SlotIndex);
                    FeedInt(hash, (int)buf.Kind);
                    FeedString(hash, buf.DataKey);
                    FeedInt(hash, buf.ElementStrideBytes);
                    FeedInt(hash, buf.ElementCount);
                    FeedInt(hash, buf.Data.Length);
                }
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    private readonly record struct MaterialSamplingKey(
        bool Active,
        string? StyleId,
        D3D12CharacterMaterialRole Role,
        string? ModuleId,
        string? ModuleHash,
        D3D12StyleShaderHookMask Hooks,
        D3D12StyleGeometryRequirements Geometry,
        string? DrawLayoutFingerprint,
        bool HasTreatment,
        bool HasStylized,
        bool HasToon,
        bool PreserveDirectPalette);

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
