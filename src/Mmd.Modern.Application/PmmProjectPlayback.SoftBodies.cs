using System.Numerics;

namespace Mmd.Modern.Application;

public sealed partial class PmmProjectPlayback
{
    private readonly List<string> controllerCleanupDiagnostics = [];
    /// <summary>Cleanup failures from rejected candidates or retired controllers; committed solvers remain usable.</summary>
    public IReadOnlyList<string> CleanupDiagnostics => Array.AsReadOnly(controllerCleanupDiagnostics.ToArray());

    private void ReleasePmmController(CharacterAnimationController? controller, string context)
    {
        try { controller?.Dispose(); }
        catch (Exception error) { controllerCleanupDiagnostics.Add(context + ": " + error.Message); }
    }

    private void PreparePmmSoft(Entry entry, CharacterAnimationController controller, double frame, bool ik, bool morphs,
        IEnumerable<CharacterAnimationController> candidates)
    {
        if (coupledSoftBodies || softPhysicsFactory is null || !controller.SupportsSoftBodies || controller.SoftBodiesEnabled) return;
        var backend = softPhysicsFactory(entry.Source)
            ?? throw new InvalidOperationException("PMM soft factory returned no backend.");
        // Identity check precedes the controller ownership transfer: the old owner
        // alone may dispose a live backend returned again by a broken factory.
        if (entries.Values.Any(active => active.Controller?.UsesSoftBodyBackend(backend) == true)
            || candidates.Any(active => active.UsesSoftBodyBackend(backend)))
            throw new InvalidOperationException("Each PMM soft world requires a newly owned backend.");
        controller.EnableSoftBodies(backend, frame, ik, morphs);
    }

    // Stage every replacement before disposing any currently published controller.
    // A failed soft initialize/reset leaves the old saved/timeline controllers usable.
    private void PreparePmmControllers(double frame, bool saved, bool ik, bool morphs)
    {
        var candidates = new Dictionary<int, CharacterAnimationController>();
        try
        {
            foreach (var pair in entries)
            {
                var entry = pair.Value;
                bool needsSoft = (softPhysicsFactory is not null || coupledSoftBodies) && entry.Source.Model.SoftBodies.Count != 0;
                if (entry.Controller is not null && entry.Saved == saved && (!needsSoft || entry.Controller.SoftBodiesEnabled)) continue;
                var candidate = new CharacterAnimationController(entry.Source.Model,
                    saved ? entry.Source.SavedPoseMotion : entry.Source.Motion, entry.Rest.Surface, entry.Rest.Outline!);
                candidates.Add(pair.Key, candidate);
                PreparePmmSoft(entry, candidate, saved ? 0 : Math.Min(frame, candidate.DurationFrames), ik, morphs,
                    candidates.Values.Where(active => !ReferenceEquals(active, candidate)));
            }
        }
        catch
        {
            foreach (var candidate in candidates.Values) ReleasePmmController(candidate, "Rejected PMM controller cleanup failed");
            throw;
        }
        // Resolve every retired owner before publishing. Disposal may call injected code and throw;
        // it must only run after all replacement references and saved/timeline flags are committed.
        var retired = candidates.Keys.Select(id => entries[id].Controller).ToArray();
        foreach (var pair in candidates)
        {
            var entry = entries[pair.Key];
            entry.Controller = pair.Value; entry.Saved = saved;
        }
        foreach (var controller in retired) ReleasePmmController(controller, "Retired PMM controller cleanup failed");
    }

    private Matrix4x4[] PmmSoftRigidWorlds(int id)
    {
        var entry = entries[id];
        if (!entry.Controller!.SoftBodiesEnabled || entry.Source.Model.RigidBodies.Count == 0) return [];
        var world = PmmWorld(id);
        return Enumerable.Range(0, entry.Source.Model.RigidBodies.Count)
            .Select(index => world.GetBodyWorldTransform(SharedActorId(id), index)).ToArray();
    }
}
