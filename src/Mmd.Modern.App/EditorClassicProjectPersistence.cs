using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

internal sealed class EditorClassicProjectPersistence(IEditorRenderEngine engine) : IEditorProjectPersistence
{
    public IEditorRenderEngine Engine => engine;
    public EditorProjectFormat Format => EditorProjectFormat.Classic;

    public void Save(string path, EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token)
        => D3D9ClassicProjectStore.Save(Format.RequirePath(path), EditorProjectPersistence.RequireSession(this, workspace), options, token);

    public PmaSaveSnapshot CaptureSnapshot(EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token)
        => D3D9ClassicProjectStore.CaptureSnapshot(EditorProjectPersistence.RequireSession(this, workspace), options, token);

    public EditorProjectLoadedCandidate Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions options, CancellationToken token)
    {
        var prepared = D3D9ClassicProjectStore.LoadPrepared(Format.RequirePath(path), cacheDirectory, loadModel, physics, options, token);
        var package = prepared.Package;
        try
        {
            // Adopt the exact detached metadata frozen before any host loading. Retained package ownership
            // preserves the resources on subsequent saves; this ordinary-surface experiment cannot execute them.
            token.ThrowIfCancellationRequested();
            return new(package, [], new ClassicMetadata(prepared.Emm, prepared.Tree));
        }
        catch (Exception error) { EditorProjectPersistence.DisposeFailedCandidate(package, error); throw; }
    }

    public EditorRecoverySessionState ProjectRecoveryState(EditorRecoverySessionState state)
        => state with { NativeLighting = null };

    public void ValidateRenderContent(EditorProjectWorkspace workspace)
    {
        _ = EditorProjectPersistence.RequireSession(this, workspace);
        if (workspace.ProjectMetadata is ClassicMetadata { HasConfiguredEffects: true })
            throw new NotSupportedException("Configured MME/EMM effects cannot enter the isolated ordinary-surface engine.");
    }

    private sealed record ClassicMetadata(D3D9EmmPmaState? Emm, D3D9EffectResourceTree? Tree) : IEditorProjectMetadata
    {
        internal bool HasConfiguredEffects => Emm is not null || Tree is not null;
    }
}
