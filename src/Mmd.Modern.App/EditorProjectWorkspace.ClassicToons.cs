using Mmd.Modern.Application;

namespace Mmd.Modern.App;

public sealed partial class EditorProjectWorkspace
{
    internal async Task ApplyClassicToonAsync(Guid objectId, IReadOnlyList<int> materialIndices,
        string imagePath, CancellationToken token = default)
    {
        int[] indices = materialIndices.ToArray();
        await CommitClassicToonAsync(objectId, (package, model) =>
        {
            using var input = File.OpenRead(imagePath);
            if (input.Length is <= 0 or > 65L * 1024 * 1024) throw new InvalidDataException("Toon image exceeds the read budget.");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            return package.Replace(objectId, model, indices, bytes, Path.GetFileName(imagePath), token);
        }, token);
    }

    /// <summary>本工程可用的原版 MMD Toon 资源；仅打开 PMC 而未选择本机 MMD 文件夹时为 null。</summary>
    internal EditorClassicToonResources? ClassicToonResources => (RenderEngine as EditorClassicRenderEngine)?.ToonResources;

    /// <summary>把模型的 Common 槽（1–10）恢复为本机原版 MMD 的图片，与替换走同一个可撤销事务。</summary>
    internal async Task RestoreClassicToonAsync(Guid objectId, int slot, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 10);
        var resource = ClassicToonResources?.Entries[slot]
            ?? throw new InvalidOperationException(EditorLocalization.Text("Mda.Text.Mme.ToonSlots.NoOriginals"));
        await RestoreClassicToonAsync(objectId, slot, resource, token);
    }

    internal async Task RestoreClassicToonAsync(Guid objectId, int slot, EditorClassicToonResources.Resource original,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Slot != slot) throw new ArgumentException("The original Toon image belongs to another slot.", nameof(original));
        byte[] bytes = original.EncodedBytes.ToArray();
        await CommitClassicToonAsync(objectId,
            (package, model) => package.RestoreCommon(objectId, model, slot, bytes, original.AuthoredToken, token), token);
    }

    private async Task CommitClassicToonAsync(Guid objectId,
        Func<EditorClassicToonPackage, Mmd.Modern.Formats.PmxModel, EditorClassicToonPackage> edit, CancellationToken token)
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
        var replacement = await Task.Run(() =>
        {
            var next = edit(package, model);
            var asset = next.Apply(objectId, new SceneAnimationModelAsset(model, obj.Source), obj.SourcePath, token);
            return (Package: next, Source: asset.Source);
        }, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Session, session) || !ReferenceEquals(ProjectMetadata, before)
            || !ReferenceEquals(session.Objects.Single(value => value.Id == objectId).Source, obj.Source))
            throw new InvalidOperationException("The project changed while the Toon image was being prepared.");
        var mappings = SnapshotMappings();
        EditorClassicDocumentState? candidate = null;
        session.Edit(sessionEdit =>
        {
            token.ThrowIfCancellationRequested();
            sessionEdit.Composition.ReplaceMaterialSource(objectId, replacement.Source);
            // Retain the frozen effect assignments; only immutable surface image identities changed.
            candidate = before.PrepareToonEdit(sessionEdit, replacement.Package, token);
        }, token);
        ProjectMetadata = candidate;
        PushUndo(new(session, session, true, mappings, SnapshotMappings(), before, candidate, true));
        ClearRedo(); MarkChanged(true);
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
