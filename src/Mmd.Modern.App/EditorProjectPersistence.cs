using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

/// <summary>Engine-owned native project policy. Load prepares CPU data only and never calls Bind.</summary>
internal interface IEditorProjectPersistence
{
    IEditorRenderEngine Engine { get; }
    EditorProjectFormat Format { get; }
    void Save(string path, EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token);
    PmaSaveSnapshot CaptureSnapshot(EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token);
    EditorProjectLoadedCandidate Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions options, CancellationToken token);
    EditorRecoverySessionState ProjectRecoveryState(EditorRecoverySessionState state);
    void ValidateRenderContent(EditorProjectWorkspace workspace);
}

/// <summary>Immutable engine metadata prepared before replacing the active document.</summary>
internal interface IEditorProjectMetadata { }

/// <summary>Prepared engine data that can be checked before replacing a document owner. This is CPU
/// state only; publishing it never creates a renderer or retains a materialized/native resource lease.</summary>
internal interface IEditorPreparedProjectMetadata : IEditorProjectMetadata
{
    EditorProjectFormat Format { get; }
    void ValidateSession(SceneProjectSession session);
}

/// <summary>All fallible package and mapping preparation finishes before workspace publication.</summary>
internal sealed class EditorProjectLoadedCandidate
{
    internal PmaLoadedProject Package { get; }
    internal Dictionary<Guid, D3D12MaterialBindingProfile> MaterialBindings { get; }
    internal IEditorProjectMetadata? Metadata { get; }

    internal EditorProjectLoadedCandidate(PmaLoadedProject package,
        IEnumerable<KeyValuePair<Guid, D3D12MaterialBindingProfile>> mappings, IEditorProjectMetadata? metadata = null)
    {
        Package = package;
        var objects = package.Session.Objects.Select(value => value.Id).ToHashSet();
        MaterialBindings = [];
        foreach (var pair in mappings)
            if (pair.Key == Guid.Empty || pair.Value is null || !objects.Contains(pair.Key)
                || !MaterialBindings.TryAdd(pair.Key, pair.Value))
                throw new InvalidDataException("Portable material mappings require unique known project objects.");
        Metadata = metadata;
    }
}

internal static class EditorProjectPersistence
{
    internal static IEditorProjectPersistence Require(IEditorRenderEngine engine, IEditorProjectPersistence? persistence)
    {
        if (persistence is null || !ReferenceEquals(persistence.Engine, engine))
            throw new InvalidOperationException("Project persistence does not belong to the selected engine.");
        if (!ReferenceEquals(persistence.Format, EditorProjectFormat.FromPackageFormat(persistence.Format.PackageFormat)))
            throw new InvalidOperationException("Project persistence requires a supported native identity.");
        return persistence;
    }

    internal static SceneProjectSession RequireSession(IEditorProjectPersistence persistence, EditorProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!ReferenceEquals(workspace.RenderEngine, persistence.Engine))
            throw new InvalidOperationException("The workspace belongs to another render engine.");
        return workspace.Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
    }

    internal static void DisposeFailedCandidate(PmaLoadedProject package, Exception error)
    {
        try { package.Dispose(); }
        catch (Exception cleanupError) { error.Data["EditorOpenProjectCleanupError"] = cleanupError; }
    }
}
