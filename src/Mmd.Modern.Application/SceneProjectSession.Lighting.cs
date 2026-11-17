namespace Mmd.Modern.Application;

public sealed partial class SceneProjectSession
{
    // Model deletion leaves its lights at the displayed world transform. Keeping a dangling
    // bone ID, or interpreting local coordinates as world coordinates, would silently move them.
    private void DetachLightsFromRemovedParents(SceneProjectEdit edit)
    {
        var remaining = edit.Composition.Objects.Select(item => item.Id).ToHashSet();
        var removed = edit.Lighting.Lights.Select(light => light.Parent).OfType<Mmd.Modern.Core.SceneLightParent>()
            .Where(parent => !remaining.Contains(parent.ObjectId)
                && content.Composition.Objects.Any(item => item.Id == parent.ObjectId)).Distinct().ToArray();
        var deleted = content.Composition.Objects.Select(item => item.Id).Where(id => !remaining.Contains(id)).ToHashSet();
        if (removed.Length == 0 && deleted.Count == 0) return;
        var transforms = removed.ToDictionary(parent => parent, parent => animation.ResolveLightParentTransform(parent));
        var snapshot = new Mmd.Modern.Core.SceneLightingSnapshot(edit.Lighting.Lights.Select(light =>
        {
            var result = light.Parent is { } parent && transforms.TryGetValue(parent, out var matrix)
                ? Mmd.Modern.Core.SceneLightTransform.ToWorld(light, matrix) : light;
            return result.Receivers is { } receivers && receivers.ObjectIds.Any(deleted.Contains)
                ? result with { Receivers = new(receivers.Mode, receivers.ObjectIds.Where(id => !deleted.Contains(id))) } : result;
        }), edit.Lighting.Groups, edit.Lighting.Settings);
        edit.Lighting.ReplaceSnapshot(snapshot);
    }

    // Match the entire detached non-light content, not the caller's intended edit type. A mixed
    // transaction must still validate/prepare a complete graph, as must a faulted owner or an edit
    // with solver-dependent postconditions. Only already validated immutable inputs are reused.
    private bool TryFreezeLightingOnly(SceneProjectEdit edit, out Content next)
    {
        next = content;
        if (animation.IsFaulted
            || edit.Composition.SelectedId != content.Composition.SelectedId
            || !edit.Composition.Objects.SequenceEqual(content.Composition.Objects)
            || !edit.Motions.SequenceEqual(content.Motions)
            || !edit.OutsideParents.SequenceEqual(content.Parents)
            || !ReferenceEquals(edit.SceneMotion, content.SceneMotion)
            || edit.Settings != content.Settings
            || !edit.Media.SequenceEqual(content.Media)
            || !ReferenceEquals(edit.PmmControls, content.PmmControls)
            || !ReferenceEquals(edit.RenderingStyleState, content.RenderingStyles)
            || !edit.Accessories.SequenceEqual(content.Accessories)) return false;

        // Model purpose changes affect composed material flags and require normal preparation.
        // Cloning also validates the sidecar, rejects unfinished gestures and detaches escaped editors.
        var lights = Clone(edit.Lighting);
        var roles = content.Lighting.ModelRoles;
        if ((lights.Snapshot.Lights.SequenceEqual(content.Lighting.Snapshot.Lights)
                && lights.Snapshot.Groups.SequenceEqual(content.Lighting.Snapshot.Groups)
                && lights.Settings == content.Lighting.Settings)
            || lights.ModelRoles.Count != roles.Count
            || lights.ModelRoles.Any(pair => !roles.TryGetValue(pair.Key, out var purpose) || purpose != pair.Value)) return false;
        next = content with { Lighting = lights };
        return true;
    }
}
