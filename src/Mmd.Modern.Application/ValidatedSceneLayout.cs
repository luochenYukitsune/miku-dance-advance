namespace Mmd.Modern.Application;

/// <summary>Proof that an ordered set of immutable source frames and placements passed composition
/// validation. It deliberately says nothing about the externally owned, mutable PMX motion models.</summary>
internal sealed class ValidatedSceneLayout
{
    private readonly EditableSceneObject[] objects;
    internal ReadOnlySpan<EditableSceneObject> Objects => objects;

    private ValidatedSceneLayout(EditableSceneObject[] objects) => this.objects = objects;

    internal static ValidatedSceneLayout Create(IEnumerable<EditableSceneObject> objects)
        => Create(SceneCompositionEditor.FromObjects(objects, null));

    // Callers pass a detached editor. Capture the records only after the same full placement/geometry
    // validation used by the public animation entry point succeeds; the mutable editor never escapes.
    internal static ValidatedSceneLayout Create(SceneCompositionEditor layout)
    {
        if (layout.Objects.Any(item => item.Source.Surface.Snapshot.HasDynamicVertices))
            throw new ArgumentException("Animation requires static source assets, not a previously sampled pose.", "objects");
        using (PoseEvaluationMetrics.Measure("session.constructor.layout-frame"))
            _ = layout.Frame;
        return new(layout.Objects.ToArray());
    }
}
