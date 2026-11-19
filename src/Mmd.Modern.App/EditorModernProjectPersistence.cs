using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

internal sealed class EditorModernProjectPersistence(IEditorRenderEngine engine) : IEditorProjectPersistence
{
    public IEditorRenderEngine Engine => engine;
    public EditorProjectFormat Format => EditorProjectFormat.Modern;

    private PmaSaveOptions Prepare(EditorProjectWorkspace workspace, PmaSaveOptions options)
    {
        var session = EditorProjectPersistence.RequireSession(this, workspace);
        // Always write the complete current mapping set. An empty set writes an empty index, which deactivates
        // mappings retained from the opened package (their opaque resources are kept). Skipping the codec when
        // the set was empty carried the old index forward: a cleared mapping came back on reopen, and one that
        // named a removed model made the saved project impossible to open.
        return D3D12MaterialBindingPma.CreateSaveOptions(session,
            workspace.MaterialBindings.Select(pair => new D3D12MaterialBindingPmaEntry(pair.Key, pair.Value,
                session.Objects.Single(item => item.Id == pair.Key).Source)), options);
    }

    public void Save(string path, EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token)
        => PmaProjectStore.Save(Format.RequirePath(path), EditorProjectPersistence.RequireSession(this, workspace),
            Prepare(workspace, options), token);

    public PmaSaveSnapshot CaptureSnapshot(EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token)
        => PmaProjectStore.CaptureSnapshot(EditorProjectPersistence.RequireSession(this, workspace), Prepare(workspace, options), token);

    public EditorProjectLoadedCandidate Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions options, CancellationToken token)
    {
        var package = D3D12MaterialBindingPma.Load(Format.RequirePath(path), cacheDirectory, loadModel, physics,
            out var profiles, options, token);
        try { token.ThrowIfCancellationRequested(); return new(package, profiles); }
        catch (Exception error) { EditorProjectPersistence.DisposeFailedCandidate(package, error); throw; }
    }

    public EditorRecoverySessionState ProjectRecoveryState(EditorRecoverySessionState state) => state;
    public void ValidateRenderContent(EditorProjectWorkspace workspace) { }
}
