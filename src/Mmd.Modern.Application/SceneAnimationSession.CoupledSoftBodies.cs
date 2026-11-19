using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    // A coupled seek replaces rigid bodies and their borrowed soft bindings together. Build a
    // complete target-pose graph while the old world, its clock and its display remain usable.
    private OriginalFrameScene SeekCoupledGraph(double frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ICharacterPhysicsBackend Independent(EditableSceneObject item)
        {
            var backend = physicsFactory(item) ?? throw new InvalidOperationException("Physics factory returned no backend.");
            if (UsesPhysicsBackend(backend))
                throw new InvalidOperationException("A coupled seek requires a newly owned backend; the active world cannot be reused.");
            return backend;
        }
        ICharacterPhysicsMutableBackend Shared()
        {
            var backend = sharedPhysicsFactory!() ?? throw new InvalidOperationException("Shared physics factory returned no backend.");
            if (UsesPhysicsBackend(backend))
                throw new InvalidOperationException("A coupled seek requires a newly owned backend; the active world cannot be reused.");
            return backend;
        }
        using var candidate = new SceneAnimationSession(entries.Select(entry => entry.Object), null,
            entries.Where(entry => entry.Motion is not null).Select(entry => entry.Motion!), Independent, lighting,
            token, frame, outsideBindings, sceneMotion, sharedPhysicsFactory is null ? null : Shared,
            null, sharedWorldGravity, pmmControls, coupledSoftBodies: true, modelRoles: modelRoles,
            deferInitialComposition: false, initialTextureSource: null, deferVertexSkinning: deferVertexSkinning);
        token.ThrowIfCancellationRequested();

        var oldEntries = entries;
        var oldWorlds = controlledWorlds.Count == 0
            ? sharedWorld is null ? [] : new[] { sharedWorld }
            : controlledWorlds.Values.ToArray();
        entries = candidate.entries; published = candidate.published; states = candidate.states;
        sharedWorld = candidate.sharedWorld; sharedStatus = candidate.sharedStatus;
        sharedAccumulator = candidate.sharedAccumulator; sharedSampleFrame = candidate.sharedSampleFrame;
        controlledWorlds.Clear();
        foreach (var pair in candidate.controlledWorlds) controlledWorlds.Add(pair.Key, pair.Value);
        controlledStatuses.Clear(); controlledStatuses.AddRange(candidate.controlledStatuses);
        controlNoise = candidate.controlNoise; controlSample = candidate.controlSample;
        controlSampleFrame = candidate.controlSampleFrame; currentControlGravity = candidate.currentControlGravity;
        CurrentFrame = candidate.CurrentFrame; DurationFrames = candidate.DurationFrames; ClearFault();
        // Ownership has moved to this session; the temporary wrapper must not dispose transferred content.
        candidate.disposed = true;
        foreach (var entry in oldEntries) Release(entry);
        foreach (var world in oldWorlds)
        {
            try { world.Dispose(); }
            catch (Exception error) { cleanupDiagnostics.Add("Retired coupled seek world cleanup failed: " + error.Message); }
        }
        return published;
    }
}
