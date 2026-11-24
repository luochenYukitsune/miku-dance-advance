using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>CPU-only preparation for an explicitly isolated, configured object-FX, scene-FX or EMM surface.
/// Freezes object metadata and reuses the exact renderer validator/encoder without loading a native
/// library, creating a device or advancing an animation/physics clock.</summary>
public sealed class D3D9SurfacePreflightInputs
{
    private readonly D3D9SurfaceFrameEncoder encoder;
    public IReadOnlyList<D3D9SurfaceObjectBinding> Bindings { get; }
    public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => encoder.SceneInputRequests;
    public D3D9SurfaceScope Scope => encoder.Scope;

    public D3D9SurfacePreflightInputs(IEnumerable<D3D9SurfaceObjectBinding> bindings, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var frozen = bindings.ToArray();
        encoder = new(frozen);
        encoder.UseClassicPassInputs(classicPassInputs);
        Bindings = Array.AsReadOnly(frozen);
    }

    /// <summary>Freezes configured identities and assignments without loading FX files or creating a device.
    /// The caller must hold the configuration's complete immutable FX include/resource tree for the native session lifetime.</summary>
    public D3D9SurfacePreflightInputs(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9ObjectFxConfiguration configuration, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configuration);
        var frozen = bindings.ToArray();
        encoder = new(frozen, configuration);
        encoder.UseClassicPassInputs(classicPassInputs);
        Bindings = Array.AsReadOnly(frozen);
    }

    /// <summary>Freezes scene-FX identities and assignments without loading FX files or creating a device.
    /// The caller must hold the declared complete dependency tree for the native session lifetime.</summary>
    public D3D9SurfacePreflightInputs(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9SceneFxConfiguration configuration, Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configuration);
        var frozen = bindings.ToArray();
        encoder = new(frozen, configuration);
        encoder.UseClassicPassInputs(classicPassInputs);
        Bindings = Array.AsReadOnly(frozen);
    }

    /// <summary>Freezes the exact EMM live instance identities and kinds without opening files or creating a device.
    /// Material inputs are requested from the existing evaluated sample for hidden and visible subset/owner validation.</summary>
    public D3D9SurfacePreflightInputs(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9EmmRuntimeConfiguration configuration,
        Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs = null, D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configuration);
        var frozen = bindings.ToArray();
        encoder = new(frozen, configuration, screenCaptureMode);
        encoder.UseClassicPassInputs(classicPassInputs);
        Bindings = Array.AsReadOnly(frozen);
    }

    public ViewRenderRequest ValidateRequest(ViewRenderRequest request, Vector4 background,
        D3D9SurfaceIsolation isolation)
    {
        RequireScope(D3D9SurfaceScope.IsolatedSurface);
        return D3D9SurfacePreflight.FreezeRequest(request, background, isolation);
    }

    public ViewRenderRequest ValidateObjectFxRequest(ViewRenderRequest request, Vector4 background)
    {
        RequireScope(D3D9SurfaceScope.ConfiguredObjectFx);
        return D3D9SurfacePreflight.FreezeSurfaceRequest(request, background);
    }

    public ViewRenderRequest ValidateSceneFxRequest(ViewRenderRequest request, Vector4 background)
    {
        RequireScope(D3D9SurfaceScope.ConfiguredSceneFx);
        return D3D9SurfacePreflight.FreezeSurfaceRequest(request, background);
    }

    public ViewRenderRequest ValidateEmmRequest(ViewRenderRequest request, Vector4 background)
    {
        RequireScope(D3D9SurfaceScope.ConfiguredEmm);
        return D3D9SurfacePreflight.FreezeSurfaceRequest(request, background);
    }

    public void ValidateSceneStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> styles,
        ViewQualityOptions quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        D3D9SurfacePreflight.ValidateScene(scene, quality, encoder.HasClassicPassInputs);
        D3D9SurfacePreflight.ValidateStyles(styles);
    }

    /// <summary>Validates one potential object's own source, including its complete instance World,
    /// independently of whether that object contributes geometry to the current visible frame.</summary>
    public void ValidatePotentialSource(OriginalFrameScene source, IReadOnlyList<SceneRenderingStyleBinding> styles,
        ViewQualityOptions quality)
    {
        ValidateSceneStyles(source, styles, quality);
        if (encoder.HasClassicPassInputs) D3D9SurfacePreflight.ValidateClassicSourceFlags(source.Materials);
        _ = D3D9SurfaceFrameEncoder.ValidateSourceSurface(source.Surface.Snapshot, source, 0, source.Materials.Count);
    }

    public void ValidateFrame(PreviewFrame frame, ViewRenderRequest request, Vector4 background,
        D3D9SurfaceIsolation isolation)
    {
        RequireScope(D3D9SurfaceScope.IsolatedSurface);
        ValidateFrame(D3D9SurfaceFrameInput.FromPreview(frame), request, background, isolation);
    }

    public void ValidateFrame(OfflineFrame frame, ViewRenderRequest request, Vector4 background,
        D3D9SurfaceIsolation isolation)
    {
        var frozen = ValidateRequest(request, background, isolation);
        ValidateEncodedFrame(frame, frozen, background);
    }

    public void ValidateObjectFxFrame(PreviewFrame frame, ViewRenderRequest request, Vector4 background)
    {
        RequireScope(D3D9SurfaceScope.ConfiguredObjectFx);
        ValidateObjectFxFrame(D3D9SurfaceFrameInput.FromPreview(frame), request, background);
    }

    public void ValidateObjectFxFrame(OfflineFrame frame, ViewRenderRequest request, Vector4 background)
    {
        var frozen = ValidateObjectFxRequest(request, background);
        ValidateEncodedFrame(frame, frozen, background);
    }

    public void ValidateSceneFxFrame(PreviewFrame frame, ViewRenderRequest request, Vector4 background)
    {
        RequireScope(D3D9SurfaceScope.ConfiguredSceneFx);
        ValidateSceneFxFrame(D3D9SurfaceFrameInput.FromPreview(frame), request, background);
    }

    public void ValidateSceneFxFrame(OfflineFrame frame, ViewRenderRequest request, Vector4 background)
    {
        var frozen = ValidateSceneFxRequest(request, background);
        ValidateEncodedFrame(frame, frozen, background);
    }

    public void ValidateEmmFrame(PreviewFrame frame, ViewRenderRequest request, Vector4 background)
    {
        RequireScope(D3D9SurfaceScope.ConfiguredEmm);
        ValidateEmmFrame(D3D9SurfaceFrameInput.FromPreview(frame), request, background);
    }

    public void ValidateEmmFrame(OfflineFrame frame, ViewRenderRequest request, Vector4 background)
    {
        var frozen = ValidateEmmRequest(request, background);
        ValidateEncodedFrame(frame, frozen, background);
    }

    private void ValidateEncodedFrame(OfflineFrame frame, ViewRenderRequest frozen, Vector4 background)
    {
        D3D9SurfacePreflight.ValidateFrame(frame, frozen, encoder.HasClassicPassInputs);
        // Encoding validates exact per-object source geometry, material/texture ownership, selected
        // bone inputs and native packet limits. Do not replace it with a second partial validator.
        _ = encoder.Encode(frame, frozen, background);
    }

    private void RequireScope(D3D9SurfaceScope expected)
    {
        if (Scope != expected)
            throw new InvalidOperationException($"This preflight was created for {Scope}; the requested operation requires {expected}.");
    }
}
