using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    private sealed record ControlSampleCache(double Frame, ScenePmmControlSample Sample);
    private sealed record CachedControlParents(SceneOutsideParentBinding[] StaticBindings, SceneOutsideParentBinding[] Combined);

    private readonly ScenePmmControlState? pmmControls;
    private readonly Dictionary<Guid, SharedCharacterPhysicsSession> controlledWorlds = [];
    private readonly List<CharacterAnimationController.SharedPhysicsStatus> controlledStatuses = [];
    private PmmGravityNoise controlNoise = new();
    private ScenePmmControlSample? controlSample;
    private double controlSampleFrame = -1;
    private ControlSampleCache? controlSampleCache;
    private CachedControlParents?[]? controlParentsByInterval;
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
        var cached = Volatile.Read(ref controlSampleCache);
        if (cached is not null && cached.Frame == frame) return cached.Sample;
        var sampled = pmmControls!.Sample(frame);
        Volatile.Write(ref controlSampleCache, new(frame, sampled));
        controlSample = sampled;
        controlSampleFrame = frame;
        return sampled;
    }

    private SceneOutsideParentBinding[] ControlParents(double frame)
    {
        if (pmmControls is null) return outsideBindings;
        int interval = pmmControls.ParentIntervalIndexAt(frame);
        var cache = EnsureControlParentsCache();
        var entry = Volatile.Read(ref cache[interval]);
        if (entry is not null && ReferenceEquals(entry.StaticBindings, outsideBindings))
            return entry.Combined;
        var computed = BuildControlParents(interval, outsideBindings);
        var next = new CachedControlParents(outsideBindings, computed);
        var raced = Interlocked.CompareExchange(ref cache[interval], next, entry);
        return raced is not null && ReferenceEquals(raced.StaticBindings, outsideBindings) ? raced.Combined : computed;
    }

    private CachedControlParents?[] EnsureControlParentsCache()
    {
        var cache = Volatile.Read(ref controlParentsByInterval);
        if (cache is not null) return cache;
        var created = new CachedControlParents?[pmmControls!.ParentIntervalCount];
        return Interlocked.CompareExchange(ref controlParentsByInterval, created, null) ?? created;
    }

    private SceneOutsideParentBinding[] BuildControlParents(int interval, SceneOutsideParentBinding[] staticBindings)
    {
        var dynamic = pmmControls!.OutsideParentsArrayAtInterval(interval);
        var ground = pmmControls.GroundOutsideParentsAtInterval(interval);
        SceneOutsideParentBinding[] result;
        if (staticBindings.Length == 0)
            result = dynamic;
        else if (dynamic.Length == 0)
            result = staticBindings;
        else
        {
            result = new SceneOutsideParentBinding[staticBindings.Length + dynamic.Length];
            staticBindings.CopyTo(result, 0);
            dynamic.CopyTo(result, staticBindings.Length);
        }
        if (staticBindings.Length > 0)
        {
            var subjects = new HashSet<(Guid ObjectId, int BoneIndex)>(result.Length + ground.Count);
            for (int i = 0; i < result.Length; i++)
                if (!subjects.Add((result[i].ObjectId, result[i].BoneIndex)))
                    throw new InvalidDataException("Static and PMM outside-parent tracks cannot author the same active subject.");
            for (int i = 0; i < ground.Count; i++)
                if (!subjects.Add((ground[i].ObjectId, ground[i].BoneIndex)))
                    throw new InvalidDataException("Static and PMM outside-parent tracks cannot author the same active subject.");
        }
        return result;
    }

    private ScenePmmGroundOutsideParent[] ControlGroundParents(Guid id, double frame)
        => pmmControls is null ? [] : pmmControls.GroundParentsForActorAtInterval(id, pmmControls.ParentIntervalIndexAt(frame));

    private IReadOnlyDictionary<int, OutsideParentTrackWindow>? ControlTrackWindows(Guid id, double frame)
        => pmmControls is null ? null : pmmControls.TrackWindowsForActorAtInterval(id, pmmControls.ParentIntervalIndexAt(frame));

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
        => pmmControls is null ? EmptyDisabledBones
            : pmmControls.DisabledBoneSetForActorAtInterval(id, pmmControls.DisabledIntervalIndexAt(frame)) ?? EmptyDisabledBones;

    private void ValidateControls(Entry[] source)
    {
        if (pmmControls is null) return;
        if (source.Any(e => e.Motion is null)) throw new InvalidDataException("PMM controls require an explicitly bound motion and skeleton for every actor.");
        pmmControls.ValidateActors(source.ToDictionary(e => e.Object.Id, e => e.Motion!.Model));
        int lastInterval = -1;
        foreach (int frame in pmmControls.BoundaryFrames)
        {
            int interval = pmmControls.ParentIntervalIndexAt(frame);
            if (interval == lastInterval) continue;
            lastInterval = interval;
            _ = ValidateOutsideParents(ControlParents(frame), source);
        }
    }

    private SharedCharacterPhysicsSession? CreateSessionWorlds(Entry[] source, double frame)
    {
        if (pmmControls is null) return sharedPhysicsFactory is null ? null : CreateSharedWorld(source, frame);
        controlNoise.Reset(pmmControls.GravityAt(frame));
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
        var pose = pmmControls.GravityAt(frame);
        if (reset) controlNoise.Reset(pose);
        Vector3 gravity = reset ? controlNoise.Current : controlNoise.Advance(pose, step ? CharacterPhysicsSession.FixedStepSeconds : 0);
        currentControlGravity = gravity;
        for (int i = 0; i < source.Length; i++)
        {
            var entry = source[i];
            if (entry.Controller?.SoftBodiesEnabled != true) continue;
            Matrix4x4.Invert(entry.Object.World, out var inverse);
            entry.Controller.SetSoftBodyGravity(Vector3.TransformNormal(gravity, inverse));
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
