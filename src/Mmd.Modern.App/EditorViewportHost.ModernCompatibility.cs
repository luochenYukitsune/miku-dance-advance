using Avalonia.Threading;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

public sealed partial class EditorViewportHost
{
    /// <summary>Legacy modern material input. New editor code binds the selected engine instead.</summary>
    public void SetMaterialBindings(IEnumerable<D3D12ResolvedMaterialBindingProfile> bindings)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(RenderEngine, EditorModernRenderEngine.Instance))
            throw new InvalidOperationException("Modern material mappings require the modern render engine.");
        SetRenderBinding(EditorModernRenderEngine.CreatePreviewBinding(bindings));
    }
}
