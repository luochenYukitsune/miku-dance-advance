using System.Numerics;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>One session's selected rendering implementation. Binding runs on the project owner;
/// it must freeze renderer inputs before any worker reads them and must not create a GPU device.</summary>
internal interface IEditorRenderEngine
{
    string Id { get; }
    IEditorProjectPersistence Persistence { get; }
    IEditorRenderBinding EmptyBinding { get; }
    IEditorRenderBinding Bind(EditorProjectWorkspace workspace);
}

/// <summary>Immutable, engine-owned rendering inputs shared by preview and detached export setup.
/// It borrows no editable project or solver. Each creation returns an independently owned renderer.</summary>
internal interface IEditorRenderBinding
{
    IEditorRenderEngine Engine { get; }
    string EngineId => Engine.Id;
    IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request);
    IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background);
    ExportRequest PrepareExport(ExportRequest request);
}

/// <summary>An initialization failure that explicitly transfers retryable renderer cleanup to the host.
/// Ordinary exceptions, including other disposable exceptions, do not transfer resource ownership.</summary>
internal interface IEditorRenderInitializationCleanup : IAsyncDisposable { }

internal static class EditorRenderEngineBinding
{
    internal static IEditorRenderBinding Require(IEditorRenderEngine engine, IEditorRenderBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrWhiteSpace(engine.Id)) throw new InvalidOperationException("A render engine requires a stable identity.");
        if (binding is null || !ReferenceEquals(binding.Engine, engine))
            throw new InvalidOperationException("Render inputs do not belong to the selected engine.");
        return binding;
    }
}
