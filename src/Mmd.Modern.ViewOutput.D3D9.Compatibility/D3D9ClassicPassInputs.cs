using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Original fixed ground-pass material, before per-model diffuse-alpha multiplication.
/// Values are the actual D3DMATERIAL9 and light ambient, not an inverse of a resolved modern color.</summary>
public sealed record D3D9ClassicGroundMaterial(Vector4 Diffuse, Vector4 Ambient, Vector4 Specular,
    Vector4 Emissive, float Power);

/// <summary>Explicit original ground controls. Direction is outgoing and must point below the horizon when enabled.</summary>
public sealed record D3D9ClassicGroundShadowInputs(bool Enabled, Vector3 ProjectionDirection,
    D3D9ClassicGroundMaterial Material, Vector3 LightAmbient, float AccessoryAmbientGray,
    bool Translucent, bool UseStencil);

/// <summary>One original model edge selection; geometry and material values come only from the accepted frame's source inputs.</summary>
public sealed record D3D9ClassicModelPassInputs(Guid ObjectId, OriginalOutlineOptions? OutlineOptions,
    bool AdditiveBlend = false);

/// <summary>Frozen original pass selections tied by identity to one already evaluated frame.
/// The callback constructing this value must consume only that frame and previously frozen project settings;
/// no animation, physics clock or live workspace is queried by the encoder.</summary>
public sealed class D3D9ClassicPassInputs
{
    internal OfflineFrame Frame { get; }
    public bool EffectFrame { get; }
    /// <summary>The original global switch also forces opaque PMX vertex-edge alpha when mode zero selects fixed drawing.</summary>
    public bool GlobalSelfShadowEnabled { get; }
    /// <summary>Null disables map generation/sampling at this sample; the independent global switch is preserved separately.</summary>
    public OriginalSelfShadowOptions? SelfShadow { get; }
    public D3D9ClassicGroundShadowInputs GroundShadow { get; }
    public IReadOnlyDictionary<Guid, D3D9ClassicModelPassInputs> Models { get; }

    public D3D9ClassicPassInputs(OfflineFrame frame, bool effectFrame, OriginalSelfShadowOptions? selfShadow,
        D3D9ClassicGroundShadowInputs groundShadow, IEnumerable<D3D9ClassicModelPassInputs> models,
        bool globalSelfShadowEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(groundShadow);
        ArgumentNullException.ThrowIfNull(groundShadow.Material); ArgumentNullException.ThrowIfNull(models);
        var frozen = models.ToArray();
        if (frozen.Any(value => value is null || value.ObjectId == Guid.Empty)
            || frozen.Select(value => value.ObjectId).Distinct().Count() != frozen.Length)
            throw new ArgumentException("Classic model selections require unique nonempty object identities.", nameof(models));
        if (selfShadow is not null && !effectFrame)
            throw new ArgumentException("Original self-shadow maps require the effect-frame path.", nameof(selfShadow));
        Frame = frame; EffectFrame = effectFrame; GlobalSelfShadowEnabled = globalSelfShadowEnabled || effectFrame;
        SelfShadow = selfShadow; GroundShadow = groundShadow;
        Models = new ReadOnlyDictionary<Guid, D3D9ClassicModelPassInputs>(frozen.ToDictionary(value => value.ObjectId));
    }

    /// <summary>Uses the same already resolved camera as the surface encoder, including the captured parent transform.</summary>
    public static SceneCamera ResolveCamera(OfflineFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return D3D9SurfaceFrameEncoder.ResolveCamera(frame);
    }

    /// <summary>Refreshes camera-dependent edge width from this exact frame while preserving the
    /// explicitly selected reference point, distance transform, model width and eligibility.
    /// Orthographic PMM keeps its authored FOV; an orthographic neutral camera without raw PMM
    /// metadata requires the caller's explicit original FOV in options.</summary>
    public static OriginalOutlineOptions ResolveOutlineOptions(OfflineFrame frame, OriginalOutlineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var camera = ResolveCamera(frame);
        return options with { CameraPosition = camera.Eye,
            FovDegrees = camera.VerticalFovDegrees ?? frame.Camera?.Fov ?? options.FovDegrees };
    }

    /// <summary>Reuses the existing independent Core camera/light warp. All selected size, scale,
    /// sampling and comparison controls are explicit, including the original global-off decision at the caller.</summary>
    public static OriginalSelfShadowOptions BuildSelfShadow(Vector3 cameraPosition, Vector3 outgoingLightDirection,
        float shadowScale, int mode, int width, int height, bool generate,
        OriginalTextureSampling sampling, OriginalDepthComparison comparison)
    {
        var transform = OriginalShadowTransform.Create(Matrix4x4.Identity, cameraPosition, outgoingLightDirection, shadowScale, mode);
        var result = new OriginalSelfShadowOptions(transform.LightViewProjection, width, height, generate,
            transform.PartialFar, sampling, comparison);
        OriginalFrameMath.Validate(new(OriginalAlphaBlendMode.Normal, true, false, result));
        return result;
    }
}
