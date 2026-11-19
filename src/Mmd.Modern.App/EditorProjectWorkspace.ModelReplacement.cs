using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

public sealed partial class EditorProjectWorkspace
{
    public bool ReplacementKeepsMaterialMapping(SceneModelReplacementPlan plan, SceneAnimationModelAsset asset)
    {
        if (!MaterialBindings.TryGetValue(plan.ObjectId, out var profile)) return true;
        string requestedId = Session?.GetRenderingStyle(plan.ObjectId).StableId ?? D3D12SurfaceStyleMap.DefaultStyleId;
        var resolvedPreset = PresetCatalog.Find(requestedId);
        string pipelineId = resolvedPreset.IsAvailable && resolvedPreset.Definition is not null
            ? resolvedPreset.Definition.PipelineId
            : D3D12SurfaceStyleMap.DefaultStyleId;
        try { _ = D3D12MaterialBindingProfileResolver.Resolve(profile, asset.Source, plan.ObjectId, pipelineId, StyleCatalog); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException)
        { return false; }
    }

    public void ApplyModelReplacement(SceneModelReplacementPlan plan, bool restoreOrphans,
        bool removeIncompatibleMaterialMapping, CancellationToken token = default)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        var before = SnapshotMappings();
        var beforeMetadata = ProjectMetadata;
        var preparedMetadata = beforeMetadata;
        session.ApplyModelReplacement(plan, restoreOrphans, token,
            candidate => preparedMetadata = PrepareObjectEditMetadata(candidate, token));
        if (removeIncompatibleMaterialMapping) MaterialBindings.Remove(plan.ObjectId);
        ProjectMetadata = preparedMetadata;
        undo.Push(new(session, session, true, before, SnapshotMappings(), beforeMetadata, preparedMetadata)); redo.Clear(); MarkChanged();
    }

    public void ApplyOrphanRestoration(SceneOrphanRestorationPlan plan, CancellationToken token = default)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        if (plan.RecoverableCount == 0) return;
        var before = SnapshotMappings();
        session.ApplyOrphanRestoration(plan, confirmed: true, token);
        undo.Push(new(session, session, true, before, SnapshotMappings(), ProjectMetadata, ProjectMetadata)); redo.Clear(); MarkChanged();
    }
}
