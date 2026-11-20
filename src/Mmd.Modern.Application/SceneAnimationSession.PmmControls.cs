using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    private readonly ScenePmmControlState? pmmControls;
    private readonly Dictionary<Guid, SharedCharacterPhysicsSession> controlledWorlds = [];
    private readonly List<CharacterAnimationController.SharedPhysicsStatus> controlledStatuses = [];
    private PmmGravityNoise controlNoise = new();
    private ScenePmmControlSample? controlSample;
    private double controlSampleFrame = -1;
    private Vector3? currentControlGravity;
    public ScenePmmControlState? PmmControls => pmmControls;
    // Candidates must reject a factory alias before initializing or disposing any live solver.
    internal bool UsesPhysicsBackend(ICharacterPhysicsBackend backend)
        => entries.Any(e => ReferenceEquals(e.Backend, backend)) || sharedWorld?.UsesBackend(backend) == true
            || controlledWorlds.Values.Any(world => world.UsesBackend(backend));
    internal bool UsesSoftPhysicsBackend(ISoftBodyBackend backend)
        => entries.Any(e => e.Controller?.UsesSoftBodyBackend(backend) == true);
    internal bool SharesPhysicsBackendWith(SceneAnimationSession other)
        => ReferenceEquals(this, other)
            || entries.Any(e => (e.Backend is { } backend && other.UsesPhysicsBackend(backend))
                || (e.Controller is { } controller && controller.SharesSoftBodyBackendWith(other)));
    private ScenePmmControlSample ControlSample(double frame)
    {
        if (controlSample is null || controlSampleFrame != frame)
        { controlSample = pmmControls!.Sample(frame); controlSampleFrame = frame; }
        return controlSample;
    }

    private SceneOutsideParentBinding[] ControlParents(double frame)
    {
        if (pmmControls is null) return outsideBindings;
        var sample = ControlSample(frame);
        var dynamic = sample.OutsideParents;
        var result = outsideBindings.Concat(dynamic).ToArray();
        int totalSubjects = result.Length + sample.GroundOutsideParents.Count;
        if (result.Select(p => (p.ObjectId, p.BoneIndex))
            .Concat(sample.GroundOutsideParents.Select(g => (g.ObjectId, g.BoneIndex)))
            .Distinct().Count() != totalSubjects)
            throw new InvalidDataException("Static and PMM outside-parent tracks cannot author the same active subject.");
        return result;
    }

    private ScenePmmGroundOutsideParent[] ControlGroundParents(Guid id, double frame)
    {
        if (pmmControls is null) return [];
        var ground = ControlSample(frame).GroundOutsideParents;
        if (ground.Count == 0) return [];
        List<ScenePmmGroundOutsideParent>? list = null;
        for (int i = 0; i < ground.Count; i++)
            if (ground[i].ObjectId == id) (list ??= []).Add(ground[i]);
        return list is null ? [] : list.ToArray();
    }

    private IReadOnlyDictionary<int, OutsideParentTrackWindow>? ControlTrackWindows(Guid id, double frame)
        => pmmControls is null ? null
            : ControlSample(frame).OutsideParentTrackWindows.TryGetValue(id, out var windows) ? windows : null;

    private static Matrix4x4 GroundOutsideParentTransform(PmxModel model, int boneIndex, Matrix4x4 inverseObjectWorld)
        => AffineProduct(Matrix4x4.CreateTranslation(model.Bones[boneIndex].Position) * inverseObjectWorld);

    private static Dictionary<int, Matrix4x4>? CombineWithGroundParents(EditableSceneObject item, PmxModel model,
        IReadOnlyDictionary<int, Matrix4x4>? parents, IReadOnlyList<ScenePmmGroundOutsideParent> groundParents)
    {
        if (groundParents.Count == 0) return parents is null ? null : new Dictionary<int, Matrix4x4>(parents);
        Matrix4x4.Invert(item.World, out var inverseWorld);
        var combined = parents is null ? new Dictionary<int, Matrix4x4>(groundParents.Count) : new Dictionary<int, Matrix4x4>(parents);
        for (int i = 0; i < groundParents.Count; i++)
            combined[groundParents[i].BoneIndex] = GroundOutsideParentTransform(model, groundParents[i].BoneIndex, inverseWorld);
        return combined;
    }

    private IReadOnlySet<int> ControlDisabled(Guid id, double frame)
        => pmmControls is null ? new HashSet<int>() : ControlSample(frame).DisabledBoneIndices[id].ToHashSet();

    private void ValidateControls(Entry[] source)
    {
        if (pmmControls is null) return;
        if (source.Any(e => e.Motion is null)) throw new InvalidDataException("PMM controls require an explicitly bound motion and skeleton for every actor.");
        pmmControls.ValidateActors(source.ToDictionary(e => e.Object.Id, e => e.Motion!.Model));
        foreach (int frame in pmmControls.BoundaryFrames) _ = ValidateOutsideParents(ControlParents(frame), source);
    }

    private SharedCharacterPhysicsSession? CreateSessionWorlds(Entry[] source, double frame)
    {
        if (pmmControls is null) return sharedPhysicsFactory is null ? null : CreateSharedWorld(source, frame);
        controlNoise.Reset(ControlSample(frame).Gravity);
        if (sharedPhysicsFactory is not null) return CreateSharedWorld(source, frame);
        var allocated = new HashSet<ICharacterPhysicsBackend>(ReferenceEqualityComparer.Instance);
        try
        {
            for (int i = 0; i < source.Length; i++)
            {
                var entry = source[i];
                if (entry.Motion is not { PhysicsEnabled: true } || entry.Controller is not { } controller
                    || !(controller.SupportsPhysics || coupledSoftBodies && controller.SupportsSoftBodies)) continue;
                var backend = physicsFactory(entry.Object) ?? throw new InvalidOperationException("PMM control physics factory returned no backend.");
                if (!allocated.Add(backend)) throw new InvalidOperationException("Independent PMM control actors require distinct backend instances.");
                if (backend is not ICharacterPhysicsMutableBackend mutable)
                { backend.Dispose(); throw new NotSupportedException("PMM dynamic controls require an incremental mutable physics backend."); }
                var actor = new[] { entry };
                var world = CreateSharedWorld(actor, frame, mutable);
                controlledWorlds.Add(entry.Object.Id, world); controlledStatuses.Add(sharedStatus!); source[i] = actor[0];
            }
            sharedStatus = new("Independent PMM control worlds", []);
            sharedAccumulator = 0; sharedSampleFrame = frame;
            return controlledWorlds.Values.FirstOrDefault();
        }
        catch { DisposeSessionWorlds(); throw; }
    }

    private SharedCharacterPhysicsSession? ActorWorld(Entry entry, SharedCharacterPhysicsSession? fallback)
        => pmmControls is not null && sharedPhysicsFactory is null ? controlledWorlds.GetValueOrDefault(entry.Object.Id) : fallback;

    private void ApplyControlGravity(Entry[] source, double frame, bool reset, bool step)
    {
        if (pmmControls is null) return;
        var pose = ControlSample(frame).Gravity;
        if (reset) controlNoise.Reset(pose);
        Vector3 gravity = reset ? controlNoise.Current : controlNoise.Advance(pose, step ? CharacterPhysicsSession.FixedStepSeconds : 0);
        currentControlGravity = gravity;
        foreach (var entry in source.Where(e => e.Controller?.SoftBodiesEnabled == true))
        {
            Matrix4x4.Invert(entry.Object.World, out var inverse);
            entry.Controller!.SetSoftBodyGravity(Vector3.TransformNormal(gravity, inverse));
        }
    }

    private void DisposeSessionWorlds()
    {
        if (controlledWorlds.Count == 0) sharedWorld?.Dispose();
        else foreach (var world in controlledWorlds.Values) world.Dispose();
        controlledWorlds.Clear(); controlledStatuses.Clear(); sharedWorld = null;
    }

    private void RequireProjectControlEdit()
    {
        RequireAlive();
        if (pmmControls is not null) throw new InvalidOperationException("Edit PMM-bound motions and parent tracks through SceneProjectSession.Edit so controls, authored motion and solver candidates commit together.");
    }
}
