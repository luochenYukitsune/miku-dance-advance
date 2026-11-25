using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

internal static class D3D9SurfacePreflight
{
    internal static ViewRenderRequest FreezeRequest(ViewRenderRequest request, Vector4 background, D3D9SurfaceIsolation isolation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (isolation != D3D9SurfaceIsolation.NoConfiguredMmeOrEmm)
            throw new ArgumentOutOfRangeException(nameof(isolation), "Explicitly declare no configured MME/EMM effects.");
        return FreezeSurfaceRequest(request, background);
    }

    internal static (ViewRenderRequest Request, D3D9SurfaceFrameEncoder Encoder) PrepareRenderer(
        ViewRenderRequest request, Vector4 background, D3D9SurfaceIsolation isolation,
        IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9ObjectFxConfiguration? objectFx,
        D3D9SceneFxConfiguration? sceneFx, D3D9EmmRuntimeConfiguration? emm,
        Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs, D3D9ClassicScreenCaptureMode? screenCaptureMode)
    {
        // Reject the request before enumerating bindings or opening the native session.
        var frozen = emm is null && sceneFx is null && objectFx is null
            ? FreezeRequest(request, background, isolation)
            : FreezeSurfaceRequest(request, background);
        var encoder = emm is not null ? new D3D9SurfaceFrameEncoder(bindings, emm, screenCaptureMode)
            : sceneFx is not null ? new D3D9SurfaceFrameEncoder(bindings, sceneFx)
            : objectFx is not null ? new D3D9SurfaceFrameEncoder(bindings, objectFx)
            : new D3D9SurfaceFrameEncoder(bindings);
        encoder.UseClassicPassInputs(classicPassInputs);
        return (frozen, encoder);
    }

    internal static ViewRenderRequest FreezeSurfaceRequest(ViewRenderRequest request, Vector4 background)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.CaptureMemory != ViewCaptureMemoryMode.Auto)
            throw new NotSupportedException("Explicit capture memory policies are available only with the modern render engine.");
        if (request.Width > 4096 || request.Height > 4096)
            throw new NotSupportedException("The isolated surface experiment supports dimensions up to 4096x4096.");
        if (!float.IsFinite(background.X) || !float.IsFinite(background.Y) || !float.IsFinite(background.Z)
            || background.X is < 0 or > 1 || background.Y is < 0 or > 1 || background.Z is < 0 or > 1 || background.W != 1)
            throw new NotSupportedException("The isolated surface experiment requires a finite opaque background in [0,1].");
        var quality = request.EffectiveQuality;
        ValidateQuality(quality);
        return request with { Quality = quality with
        {
            Gi = quality.Gi is null ? null : quality.Gi with { },
            Upscaling = quality.Upscaling is null ? null : quality.Upscaling with { },
            TemporalUpscaling = quality.TemporalUpscaling is null ? null : quality.TemporalUpscaling with { },
            Diffusion = quality.Diffusion is null ? null : quality.Diffusion with { }
        } };
    }

    private static void ValidateQuality(ViewQualityOptions quality)
    {
        quality.Validate();
        if (quality.EffectiveGi.Enabled || quality.Antialiasing is not (null or FrameAntialiasing.Off)
            || quality.SpatialSamples is > 1 || quality.EffectiveUpscaling.Mode != ViewUpscalingMode.Off
            || quality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off || quality.EffectiveDiffusion.Enabled)
            throw new NotSupportedException("Isolated D3D9 surface capture does not implement GI, AA, supersampling, upscaling, temporal reconstruction or diffusion.");
    }

    internal static void ValidateFrame(OfflineFrame frame, ViewRenderRequest request, bool hasClassicPassInputs = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != request.Width || frame.Height != request.Height)
            throw new InvalidOperationException("Offline frame dimensions differ from this renderer's configured output.");
        ArgumentNullException.ThrowIfNull(frame.Sample);
        _ = frame.Sample.SampledSourceFrame.ToDouble();
        if (!hasClassicPassInputs && frame.SelfShadow is { Mode: not 0 })
            throw new NotSupportedException("The isolated surface experiment does not implement self shadow.");
        ValidateScene(frame.Scene, request.EffectiveQuality, hasClassicPassInputs);
        ValidateStyles(frame.RenderingStyles);
    }

    internal static void ValidateStyles(IReadOnlyList<SceneRenderingStyleBinding> styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        if (styles.Any(style => style is null || style.Preset != RenderingPresetReference.BuiltIn(RenderingStyleId.Mmd)))
            throw new NotSupportedException("The isolated surface experiment accepts only the ordinary MMD rendering preset.");
    }

    internal static void ValidateScene(OriginalFrameScene scene, ViewQualityOptions quality, bool hasClassicPassInputs = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ValidateQuality(quality);
        var options = scene.Options;
        // Scene metadata must remain truthful even if this view supplied an explicit quality override.
        if (options is not null)
        {
            options.RayGi?.Validate();
            if (options.RayGi is { Enabled: true } || options.Antialiasing != FrameAntialiasing.Off || options.SpatialSamples != 1)
                throw new NotSupportedException("Inherited scene GI, AA and supersampling are unsupported by isolated surface capture.");
            if (!hasClassicPassInputs && (options.SelfShadow is not null || options.GroundShadow is not null))
                throw new NotSupportedException("The isolated surface experiment does not implement shadow passes.");
            if (options.BlendMode != OriginalAlphaBlendMode.Normal || !options.WriteDepth || options.Transp)
                throw new NotSupportedException("Isolated surface capture requires ordinary source-over blending, depth writes and normal surface alpha.");
            if (options.AlphaOutput != FrameAlphaOutput.LegacyMax)
                throw new NotSupportedException("The isolated surface experiment implements only legacy maximum alpha output.");
            if (options.BackgroundColor is { W: not 1f })
                throw new NotSupportedException("Transparent scene backgrounds are unsupported by isolated surface capture.");
        }
        if (!hasClassicPassInputs && scene.OutlineOptions is { Eligible: true } && scene.Outline is { } outline
            && outline.Materials.Where((material, index) => scene.Materials[index].SurfaceEnabled)
                .Any(material => material.DrawEnabled && material.Width > 0))
            throw new NotSupportedException("The isolated surface experiment does not implement eligible outlines.");
        if (scene.Materials.Any(flags => (!hasClassicPassInputs && flags.UseSceneLighting) || flags.CullTransparentSurface || flags.CharacterToonResponse is not null
            || flags.CharacterOutlineStyle is not null))
            throw new NotSupportedException("Modern material and scene-lighting responses are unsupported by isolated surface capture.");
        // Role-derived GI palette responses are dormant: request and inherited scene GI were rejected above.
        if (scene.Lighting is { Lights.Count: > 0 } || scene.EditorOverlay is { Lines.Count: > 0 })
            throw new NotSupportedException("Native scene lights and editor overlays are unsupported by isolated surface capture.");
        if (scene.Surface.Profile.TextureSampling != OriginalTextureSampling.LinearWrap)
            throw new NotSupportedException("Isolated surface capture currently requires linear-wrap texture sampling.");
        if (scene.Surface.Snapshot.Materials.Any(material => material.ToonUvTransform is not null || material.Emission != Vector3.Zero))
            throw new NotSupportedException("Custom toon UV transforms and emission are unsupported by isolated surface capture.");
    }

    internal static void ValidateClassicSourceFlags(IEnumerable<OriginalFrameMaterialFlags> materials)
    {
        if (materials.Any(flags => flags.UseSceneLighting || flags.PreserveDirectLightPalette
            || flags.CullTransparentSurface || flags.CharacterToonResponse is not null || flags.CharacterOutlineStyle is not null))
            throw new NotSupportedException("Classic requires original authored material responses; modern source overrides are unsupported.");
    }
}
