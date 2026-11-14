using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class PmmProjectPlayback
{
    private bool buildingCoupledGraph;
    private SharedCharacterPhysicsSession[] retiredCoupledWorlds = [];

    // Seek, enable and saved/timeline transitions build both rigid and soft candidates. Until the
    // final publication every old controller/world remains alive and its solver history is untouched.
    private void RebuildCoupledPmmGraph(double frame, bool ik, bool morphs, bool saved)
    {
        var previousEntries = entries.ToDictionary(pair => pair.Key,
            pair => (pair.Value.Controller, pair.Value.Saved, pair.Value.Frame));
        var previousShared = pmmSharedWorld; var previousStatus = pmmSharedStatus;
        var previousWorlds = pmmIndependentWorlds.ToArray(); var previousStatuses = pmmIndependentStatuses.ToArray();
        var previousNoise = pmmGravityNoise; double previousAccumulator = pmmSharedAccumulator, previousSampleFrame = pmmSharedSampleFrame;
        var previousCombined = combined; var previousVisible = visible; var previousCamera = currentCamera;
        double previousFrame = CurrentFrame; bool previousSaved = IsSavedPose;
        retiredCoupledWorlds = previousShared is null ? previousWorlds.Select(pair => pair.Value).ToArray() : [previousShared];
        pmmSharedWorld = null; pmmSharedStatus = null; pmmIndependentWorlds.Clear(); pmmIndependentStatuses.Clear();
        pmmGravityNoise = new();
        foreach (var entry in entries.Values) { entry.Controller = null; entry.Frame = entry.Rest; }
        buildingCoupledGraph = true;
        try
        {
            EvaluatePmmShared(frame, 0, true, ik, morphs, saved);
        }
        catch
        {
            foreach (var entry in entries.Values) ReleasePmmController(entry.Controller, "Rejected coupled PMM controller cleanup failed");
            try { DisposePmmPhysicsWorlds(); }
            catch (Exception error) { controllerCleanupDiagnostics.Add("Rejected coupled PMM world cleanup failed: " + error.Message); }
            foreach (var pair in previousEntries)
            {
                var entry = entries[pair.Key]; entry.Controller = pair.Value.Controller; entry.Saved = pair.Value.Saved; entry.Frame = pair.Value.Frame;
            }
            pmmSharedWorld = previousShared; pmmSharedStatus = previousStatus;
            pmmIndependentWorlds.Clear(); foreach (var pair in previousWorlds) pmmIndependentWorlds.Add(pair.Key, pair.Value);
            pmmIndependentStatuses.Clear(); foreach (var pair in previousStatuses) pmmIndependentStatuses.Add(pair.Key, pair.Value);
            pmmGravityNoise = previousNoise; pmmSharedAccumulator = previousAccumulator; pmmSharedSampleFrame = previousSampleFrame;
            combined = previousCombined; visible = previousVisible; currentCamera = previousCamera; CurrentFrame = previousFrame; IsSavedPose = previousSaved;
            throw;
        }
        finally { buildingCoupledGraph = false; retiredCoupledWorlds = []; }
        foreach (var entry in previousEntries.Values) ReleasePmmController(entry.Controller, "Retired coupled PMM controller cleanup failed");
        foreach (var world in previousShared is null ? previousWorlds.Select(pair => pair.Value) : [previousShared])
        {
            try { world.Dispose(); }
            catch (Exception error) { controllerCleanupDiagnostics.Add("Retired coupled PMM world cleanup failed: " + error.Message); }
        }
    }
}
