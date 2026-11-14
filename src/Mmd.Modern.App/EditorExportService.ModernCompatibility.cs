using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

// Compatibility entry points for existing callers; modern policy belongs to the modern engine.
public static partial class EditorExportService
{
    internal static bool HasPaletteReceiver(SceneProjectSession source)
        => EditorModernRenderEngine.HasPaletteReceiver(source);

    internal static ViewRenderRequest BuildRenderRequest(ExportRequest request, bool hasPaletteReceiver)
        => EditorModernRenderEngine.BuildRenderRequest(request, hasPaletteReceiver);

    internal static ExportRequest BuildExportRequest(ExportRequest request, bool hasPaletteReceiver)
        => EditorModernRenderEngine.BuildExportRequest(request, hasPaletteReceiver);

    public static IReadOnlyList<D3D12ResolvedMaterialBindingProfile> ResolveBindings(SceneProjectSession session,
        IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> mappings)
        => EditorModernRenderEngine.ResolveBindings(session, mappings);
}
