namespace Mmd.Modern.Application;

public sealed partial class SceneCompositionEditor
{
    public void SetObjectMetadata(Guid id, string name, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        int index = Index(id);
        var previous = objects[index];
        var updated = previous with { Name = name, Role = role,
            NameFollowsSource = name == previous.Name ? previous.NameFollowsSource : false };
        if (updated == objects[index]) return;
        Save(); objects[index] = updated; cached = null;
    }
}

public sealed partial class SceneProjectEdit
{
    /// <summary>Combines a style selection with an ordinary project edit in one Undo transaction.</summary>
    public void SetModelRenderingStyle(Guid objectId, RenderingPresetReference? style)
    {
        if (!Composition.Objects.Any(item => item.Id == objectId)) throw new ArgumentException("Unknown scene object.", nameof(objectId));
        var edit = new SceneRenderingStylesEdit(RenderingStyleState.Known);
        if (style.HasValue) edit.ModelOverrides[objectId] = style.Value;
        else edit.ModelOverrides.Remove(objectId);
        RenderingStyleState = new(edit.Freeze(Composition.Objects.Select(item => item.Id).ToHashSet()));
    }
}
