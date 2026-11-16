using Mmd.Modern.Application;

namespace Mmd.Modern.App;

public sealed partial class EditorProjectWorkspace
{
    internal async Task ApplyClassicToonAsync(Guid objectId, IReadOnlyList<int> materialIndices,
        string imagePath, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var session = Session ?? throw new InvalidOperationException("Open a Classic project first.");
        var before = ProjectMetadata as EditorClassicDocumentState
            ?? throw new InvalidOperationException("Classic document metadata is required.");
        if (RenderEngine is not EditorClassicRenderEngine)
            throw new InvalidOperationException("Toon editing is available only in Classic mode.");
        var package = before.Toons ?? throw new InvalidOperationException("Classic toon resources are not prepared.");
        var obj = session.Objects.Single(value => value.Id == objectId);
        var model = session.CaptureAuthoredContent().Motions.Single(value => value.ObjectId == objectId).Model;
        int[] indices = materialIndices.ToArray();
        var replacement = await Task.Run(() =>
        {
            using var input = File.OpenRead(imagePath);
            if (input.Length is <= 0 or > 65L * 1024 * 1024) throw new InvalidDataException("Toon image exceeds the read budget.");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var next = package.Replace(objectId, model, indices, bytes, Path.GetFileName(imagePath), token);
            var asset = next.Apply(objectId, new SceneAnimationModelAsset(model, obj.Source), obj.SourcePath, token);
            return (Package: next, Source: asset.Source);
        }, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Session, session) || !ReferenceEquals(ProjectMetadata, before)
            || !ReferenceEquals(session.Objects.Single(value => value.Id == objectId).Source, obj.Source))
            throw new InvalidOperationException("The project changed while the Toon image was being prepared.");
        var mappings = SnapshotMappings();
        EditorClassicDocumentState? candidate = null;
        session.Edit(edit =>
        {
            token.ThrowIfCancellationRequested();
            edit.Composition.ReplaceMaterialSource(objectId, replacement.Source);
            // Retain the frozen effect assignments; only immutable surface image identities changed.
            candidate = before.PrepareToonEdit(edit, replacement.Package, token);
        }, token);
        ProjectMetadata = candidate;
        undo.Push(new(session, session, true, mappings, SnapshotMappings(), before, candidate, true));
        redo.Clear(); MarkChanged(true);
    }
}

internal sealed partial class EditorClassicDocumentState
{
    internal EditorClassicDocumentState PrepareToonEdit(SceneProjectEdit edit, EditorClassicToonPackage package,
        CancellationToken token)
    {
        var effects = Effects ?? throw new InvalidOperationException("Frozen Classic effects are required.");
        var toons = EditorClassicToonPackage.Capture(edit, null, package, token);
        return new(edit, effects, AccessoryAssets, toons);
    }
}
