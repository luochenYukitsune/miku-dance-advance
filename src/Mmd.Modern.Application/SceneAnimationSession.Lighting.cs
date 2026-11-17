using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    /// <summary>Replaces neutral additional lights without sampling animation, completing deferred
    /// leaves, resetting a solver or consuming its fractional fixed-step accumulator.</summary>
    internal void UpdateLighting(SceneLightingSnapshot next, CancellationToken cancellationToken)
    {
        // History restoration may also change metadata on a faulted owner. Retain that fault,
        // matching style-only Undo/Redo; this update never repairs or samples the broken graph.
        RequireAlive(); ArgumentNullException.ThrowIfNull(next);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(lighting, next)) return;
        // Compose may retain its baseline when geometry has not changed. Update the published
        // envelope now so Frame, render capture and later unchanged-geometry samples agree.
        var frame = new OriginalFrameScene(published.Surface, published.Outline, published.OutlineOptions,
            published.Options, published.Materials, ResolveLighting(entries, next), published.EditorOverlay);
        cancellationToken.ThrowIfCancellationRequested();
        lighting = next; published = frame;
    }
}
