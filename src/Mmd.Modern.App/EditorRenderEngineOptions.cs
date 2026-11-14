using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Initial per-view choices for the selected engine. Explicit user requests are never coerced.</summary>
internal static class EditorRenderEngineOptions
{
    private static readonly ViewQualityOptions ClassicQuality = new(
        Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1, SurfaceOrder: SurfaceDrawOrder.Original);

    internal static bool UsesModernQualityPreferences(IEditorRenderEngine engine)
        => engine is not EditorClassicRenderEngine;

    internal static ViewQualityOptions DefaultPreviewQuality(IEditorRenderEngine engine)
        => UsesModernQualityPreferences(engine) ? EditorPreferences.DefaultPreviewQuality : ClassicQuality;

    internal static ViewQualityOptions DefaultExportQuality(IEditorRenderEngine engine)
        => UsesModernQualityPreferences(engine)
            ? new(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1) : ClassicQuality;
}
