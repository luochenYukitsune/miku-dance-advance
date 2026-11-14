using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

internal sealed partial class EditorClassicRenderEngine
{
    internal D3D9ClassicEffectsPreparation PrepareObjectEdit(D3D9ClassicEffectsPreparation previous,
        IReadOnlyList<EmmObjectBinding> bindings, IReadOnlySet<Guid> changed,
        IReadOnlyDictionary<Guid, int> materialCounts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); runtime.ValidateFiles();
        using var inspector = new D3D9NativeEffectDependencyInspector(runtime.NativeLibraryPath,
            runtime.D3dxLibraryPath, runtime.CompilerLibraryPath);
        var result = previous.PrepareObjectEdit(bindings, changed,
            request => inspector.DiscoverImplicitRoot(request.OriginAbsoluteObjectPath, request.DefaultEffectAbsolutePath),
            inspector, materialCounts: materialCounts, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (result.Preparation is { } prepared) return prepared;
        var error = new InvalidDataException("Classic model edit could not prepare all new effect dependencies.");
        error.Data["ClassicEffectDiagnostics"] = result.Diagnostics;
        throw error;
    }
}
