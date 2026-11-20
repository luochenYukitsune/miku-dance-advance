using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Animation;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Explicit identity and skeleton input. The state captures names and indices, never this mutable model.</summary>
public sealed record ScenePmmActorSource(Guid ObjectId, byte PmmModelIndex, PmxModel Model);
/// <summary>Detached storage DTO. BoneIndices maps PMM name-table ordinal to PMX bone index.</summary>
public sealed record ScenePmmActorBinding(Guid ObjectId, byte PmmModelIndex, int[] BoneIndices)
{
    public string[]? NativeBindingNames { get; init; }
    public int[]? DetachedBoneOrdinals { get; init; }
}
/// <summary>A resolved camera target. PMM ordinals retain provenance; BoneIndex is matched by native name in PMX.</summary>
public sealed record ScenePmmCameraParent(Guid ObjectId, int BoneIndex, int PmmModelIndex, int PmmBoneIndex);
/// <summary>Detached storage DTO, validated again on restore. Origin identifies the initial canonical PMM snapshot.</summary>
public sealed record ScenePmmControlPayload(byte[] DocumentBytes, string Sha256, string OriginSha256,
    int AnsiCodePage, ScenePmmActorBinding[] Actors)
{
    public ScenePmmOrphanRedirection[]? RestoredBindings { get; init; }
}
public sealed record ScenePmmOrphanRedirection(Guid SourceObjectId, int BoneOrdinal, Guid TargetObjectId)
{
    public string[] TargetBoneNames { get; init; } = [];
    public bool IsDetached { get; init; }
}
public sealed record ScenePmmGroundOutsideParent(Guid ObjectId, int BoneIndex);
public sealed record ScenePmmControlSample(PmmGravityPose Gravity,
    IReadOnlyDictionary<Guid, IReadOnlyList<int>> DisabledBoneIndices,
    IReadOnlyList<SceneOutsideParentBinding> OutsideParents)
{
    internal static readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>> EmptyTrackWindows =
        new ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>>(
            new Dictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>>());
    public IReadOnlyList<ScenePmmGroundOutsideParent> GroundOutsideParents { get; init; } = Array.Empty<ScenePmmGroundOutsideParent>();
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>> OutsideParentTrackWindows { get; init; } = EmptyTrackWindows;
}

/// <summary>Immutable PMM control/provenance bridge. Reuses the PMM evaluator and transactional editor.
/// This is authored metadata; no solver, noise-generator or simulation history is captured.</summary>
public sealed partial class ScenePmmControlState
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;
    private static readonly IReadOnlyList<int> EmptyBones = Array.AsReadOnly(Array.Empty<int>());
    private static readonly IReadOnlyList<SceneOutsideParentBinding> EmptyOutsideParents = Array.AsReadOnly(Array.Empty<SceneOutsideParentBinding>());
    private static readonly IReadOnlyList<ScenePmmGroundOutsideParent> EmptyGroundOutsideParents = Array.Empty<ScenePmmGroundOutsideParent>();

    private sealed record Actor(Guid ObjectId, byte ModelIndex, int[] BoneIndices, string[] NativeBoneNames, string[]? BindingNames = null, int[]? DetachedOrdinals = null);
    private sealed record ParentInterval(
        SceneOutsideParentBinding[] OutsideParentsArray,
        IReadOnlyList<SceneOutsideParentBinding> OutsideParents,
        IReadOnlyList<ScenePmmGroundOutsideParent> GroundOutsideParents,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>> TrackWindows,
        Dictionary<Guid, ScenePmmGroundOutsideParent[]>? GroundByActor);
    private sealed record DisabledInterval(
        IReadOnlyDictionary<Guid, IReadOnlyList<int>> ListsByActor,
        Dictionary<Guid, IReadOnlySet<int>>? SetsByActor);
    private sealed record ConstantSampleCache(int DisabledIdx, int ParentIdx, int GravitySegment, ScenePmmControlSample Sample);

    private readonly byte[] bytes;
    private readonly PmmDocument document;
    private readonly Actor[] actors;
    private readonly ScenePmmOrphanRedirection[] restoredBindings;
    private readonly Dictionary<int, Actor> byModel;
    private readonly PmmPhysicsControlTracks physics;
    private readonly PmmSceneTracks scene;
    private readonly Dictionary<byte, PmmModelKeyframe[]> modelKeys;
    private readonly int[] parentIntervalFrames;
    private readonly ParentInterval[] parentIntervals;
    private readonly ParentInterval savedParentInterval;
    private readonly int[] disabledIntervalFrames;
    private readonly DisabledInterval?[] disabledIntervals;
    private readonly bool eagerDisabledIntervals;
    private readonly DisabledInterval emptyDisabledInterval;
    private readonly Guid[] allDisabledActorIds;
    private readonly ScenePmmControlSample savedSample;
    private ConstantSampleCache? lastConstantSample;
    public string Sha256 { get; }
    public string OriginSha256 { get; }
    public uint DurationFrames { get; }
    public IReadOnlyList<int> BoundaryFrames { get; }
    public IReadOnlyDictionary<Guid, byte> ModelIndices { get; }

    private ScenePmmControlState(PmmDocument source, IEnumerable<ScenePmmActorSource> sources, string? origin)
        : this(source, CaptureSkeletons(sources), origin) { }

    private ScenePmmControlState(PmmDocument source, Actor[] input, string? origin, ScenePmmOrphanRedirection[]? restored = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Version != 2) throw new NotSupportedException("PMM control provenance requires PMM 0002; PMM 0001 has no dynamic physics or outside-parent fields.");
        bytes = PmmWriter.ToBytes(source);
        if (bytes.Length > MaxPayloadBytes) throw new InvalidDataException("PMM control provenance exceeds its payload budget.");
        document = Read(bytes, source.AnsiCodePage);
        ValidateParentMetadata(document);
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        OriginSha256 = origin ?? Sha256; ValidateDigest(OriginSha256);
        if (input.Any(a => a.ObjectId == Guid.Empty)
            || input.Select(a => a.ObjectId).Distinct().Count() != input.Length
            || input.Select(a => a.ModelIndex).Distinct().Count() != input.Length
            || input.Length != document.Models.Count
            || input.Any(a => !document.Models.Any(m => m.Index == a.ModelIndex)))
            throw new InvalidDataException("PMM control actors must map every stored model ID exactly once to a unique non-empty scene identity.");
        actors = input.Select(a => Map(a.ObjectId, a.ModelIndex, a.NativeBoneNames, document.Models.Single(m => m.Index == a.ModelIndex), a.BindingNames, a.DetachedOrdinals)).ToArray();
        byModel = actors.ToDictionary(a => (int)a.ModelIndex);
        ValidateRestoredBindings(ActorBindings(), restored ?? []);
        restoredBindings = restored?.Select(r => r with { TargetBoneNames = r.TargetBoneNames.ToArray() }).ToArray() ?? [];
        ValidateMappedControls(document, actors.ToDictionary(a => (int)a.ModelIndex, a => a.BoneIndices), actors.ToDictionary(a => (int)a.ModelIndex, a => a.DetachedOrdinals ?? []));
        ModelIndices = new ReadOnlyDictionary<Guid, byte>(actors.ToDictionary(a => a.ObjectId, a => a.ModelIndex));
        physics = new(document);
        scene = new(document);
        modelKeys = document.Models.ToDictionary(m => m.Index, m => new[] { m.InitialModelKeyframe }.Concat(m.ModelKeyframes).OrderBy(k => k.Frame).ToArray());
        var frames = document.Models.SelectMany(m => new[] { m.InitialModelKeyframe }.Concat(m.ModelKeyframes))
            .Select(k => k.Frame).Append(0).Distinct().Order().ToArray();
        BoundaryFrames = Array.AsReadOnly(frames);
        (parentIntervalFrames, parentIntervals, savedParentInterval) = BuildParentIntervals(frames);
        allDisabledActorIds = actors.Select(a => a.ObjectId).Concat(restoredBindings.Select(r => r.TargetObjectId)).Distinct().ToArray();
        var emptyDisabledMap = new Dictionary<Guid, IReadOnlyList<int>>(allDisabledActorIds.Length);
        for (int i = 0; i < allDisabledActorIds.Length; i++) emptyDisabledMap.Add(allDisabledActorIds[i], EmptyBones);
        emptyDisabledInterval = new(new ReadOnlyDictionary<Guid, IReadOnlyList<int>>(emptyDisabledMap), null);
        (disabledIntervalFrames, disabledIntervals, eagerDisabledIntervals) = BuildDisabledIntervals();
        var savedDisabledInterval = BuildDisabledInterval(0, saved: true, previous: null);
        savedSample = new(physics.SavedGravity, savedDisabledInterval.ListsByActor, savedParentInterval.OutsideParents)
        {
            GroundOutsideParents = savedParentInterval.GroundOutsideParents,
            OutsideParentTrackWindows = savedParentInterval.TrackWindows,
        };
        DurationFrames = Math.Max(Math.Max(Math.Max(physics.DurationFrames, scene.DurationFrames), (uint)frames.Max()),
            (uint)document.Accessories.SelectMany(a => new[] { a.InitialKeyframe }.Concat(a.Keyframes)).Select(k => k.Frame).DefaultIfEmpty(0).Max());
    }

    public static ScenePmmControlState Create(PmmDocument document, IEnumerable<ScenePmmActorSource> actors)
        => new(document, actors, null);

    public PmmDocument ExportDocument() => Read(bytes, document.AnsiCodePage);

    public ScenePmmControlPayload Capture() => new((byte[])bytes.Clone(), Sha256, OriginSha256, document.AnsiCodePage,
        actors.Select(a => new ScenePmmActorBinding(a.ObjectId, a.ModelIndex, (int[])a.BoneIndices.Clone())
        {
            NativeBindingNames = RequiresBindingMetadata(a) ? a.BindingNames!.ToArray() : null,
            DetachedBoneOrdinals = a.DetachedOrdinals is { Length: > 0 } ? a.DetachedOrdinals.ToArray() : null
        }).ToArray())
        { RestoredBindings = restoredBindings.Length == 0 ? null : restoredBindings.Select(r => r with { TargetBoneNames = r.TargetBoneNames.ToArray() }).ToArray() };

    public static ScenePmmControlState Restore(ScenePmmControlPayload payload, IReadOnlyDictionary<Guid, PmxModel> models)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(models);
        ValidatePayload(payload, models.Keys.ToArray());
        if (payload.DocumentBytes is null || payload.DocumentBytes.Length is < 1 or > MaxPayloadBytes || payload.Actors is null)
            throw new InvalidDataException("PMM control provenance requires a bounded non-empty payload and explicit actors.");
        ValidateDigest(payload.Sha256); ValidateDigest(payload.OriginSha256);
        byte[] owned = (byte[])payload.DocumentBytes.Clone();
        if (!Convert.ToHexString(SHA256.HashData(owned)).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PMM control provenance payload digest mismatch.");
        var sources = payload.Actors.Select(a => a is not null && models.TryGetValue(a.ObjectId, out var model)
            ? new ScenePmmActorSource(a.ObjectId, a.PmmModelIndex, model)
            : throw new InvalidDataException("PMM control provenance refers to an unknown scene actor.")).ToArray();
        var captured = CaptureSkeletons(sources).Select(a => { var stored = payload.Actors.Single(p => p.ObjectId == a.ObjectId);
            return a with { BindingNames = stored.NativeBindingNames, DetachedOrdinals = stored.DetachedBoneOrdinals }; }).ToArray();
        var state = new ScenePmmControlState(Read(owned, payload.AnsiCodePage), captured, payload.OriginSha256, payload.RestoredBindings);
        state.ValidateActors(models);
        foreach (var actor in payload.Actors)
            if (actor.BoneIndices is null || !state.byModel[actor.PmmModelIndex].BoneIndices.SequenceEqual(actor.BoneIndices))
                throw new InvalidDataException("PMM control bone mapping differs from the explicit native-name skeleton mapping.");
        return state;
    }

    /// <summary>Validates bounded content and identity metadata before a project loader opens models or allocates solvers.</summary>
    public static void ValidatePayload(ScenePmmControlPayload payload, IReadOnlyCollection<Guid>? expectedActors = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.DocumentBytes is null || payload.DocumentBytes.Length is < 1 or > MaxPayloadBytes || payload.Actors is null
            || payload.AnsiCodePage is not (932 or 936)) throw new InvalidDataException("Invalid PMM control payload envelope.");
        ValidateDigest(payload.Sha256); ValidateDigest(payload.OriginSha256);
        if (!Convert.ToHexString(SHA256.HashData(payload.DocumentBytes)).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PMM control provenance payload digest mismatch.");
        var source = Read(payload.DocumentBytes, payload.AnsiCodePage);
        if (source.Version != 2 || payload.Actors.Length != source.Models.Count
            || payload.Actors.Any(a => a is null || a.ObjectId == Guid.Empty || a.BoneIndices is null)
            || payload.Actors.Select(a => a.ObjectId).Distinct().Count() != payload.Actors.Length
            || payload.Actors.Select(a => a.PmmModelIndex).Distinct().Count() != payload.Actors.Length)
            throw new InvalidDataException("PMM control payload requires complete unique actor identities.");
        foreach (var actor in payload.Actors)
        {
            var model = source.Models.SingleOrDefault(m => m.Index == actor.PmmModelIndex);
            if (model is null || actor.BoneIndices.Length != model.BoneNames.Count || actor.BoneIndices.Any(i => i < -1)
                || actor.BoneIndices.Where(i => i >= 0).Distinct().Count() != actor.BoneIndices.Count(i => i >= 0))
                throw new InvalidDataException("PMM control payload has an invalid explicit bone mapping.");
            if (actor.NativeBindingNames is { } names && (names.Length != actor.BoneIndices.Length || names.Any(n => n is null || n.Length > 4096))
                || actor.DetachedBoneOrdinals is { } detached && (detached.Length > 0 && actor.NativeBindingNames is null
                    || detached.Length > actor.BoneIndices.Length || detached.Distinct().Count() != detached.Length || detached.Any(i => (uint)i >= (uint)actor.BoneIndices.Length || actor.BoneIndices[i] != -1)))
                throw new InvalidDataException("Invalid explicit detached PMM bone mapping.");
        }
        ValidateRestoredBindings(payload.Actors, payload.RestoredBindings ?? []);
        if (expectedActors is not null && (payload.Actors.Any(a => !expectedActors.Contains(a.ObjectId)) || (payload.RestoredBindings ?? []).Any(r => !expectedActors.Contains(r.TargetObjectId))))
            throw new InvalidDataException("PMM control payload does not match the complete project actor set.");
        _ = new PmmPhysicsControlTracks(source);
        ValidateParentMetadata(source);
        ValidateMappedControls(source, payload.Actors.ToDictionary(a => (int)a.PmmModelIndex, a => a.BoneIndices), payload.Actors.ToDictionary(a => (int)a.PmmModelIndex, a => a.DetachedBoneOrdinals ?? []));
    }

    // This part needs only PMM ordinals and stored IDs, so it also runs before model asset loaders.
    private static void ValidateParentMetadata(PmmDocument source)
    {
        if (source.Models.Select(m => m.Index).Distinct().Count() != source.Models.Count)
            throw new InvalidDataException("PMM parent metadata requires unique stored model IDs.");
        var byId = source.Models.ToDictionary(m => (int)m.Index);
        ValidateAccessoryParentMetadata(source);
        // nanoem (MIT), document.c CameraKeyframeGetParentModelBoneName resolves the stored
        // model ID, then its native bone-name ordinal. Never treat that ordinal as a PMX index.
        void ValidateCameraParent(int modelIndex, int boneIndex, string location)
        {
            if (modelIndex < 0) return;
            if (!byId.TryGetValue(modelIndex, out var target) || boneIndex < 0 || boneIndex >= target.BoneNames.Count)
                throw new InvalidDataException($"PMM camera parent at {location} refers to unavailable model #{modelIndex} bone #{boneIndex}.");
        }
        foreach (var key in new[] { source.Camera.InitialKeyframe }.Concat(source.Camera.Keyframes))
            ValidateCameraParent(key.ParentModelIndex, key.ParentBoneIndex, $"frame {key.Frame}");
        ValidateCameraParent(source.Settings.CameraLookAtModelIndex, source.Settings.CameraLookAtBoneIndex, "saved editor pose");
        var keys = new Dictionary<byte, PmmModelKeyframe[]>();
        foreach (var model in source.Models)
        {
            var subjects = model.OutsideParentSubjectBoneIndices;
            if (subjects.Any(i => i < -1 || i >= model.BoneNames.Count) || subjects.Distinct().Count() != subjects.Count)
                throw new InvalidDataException("PMM outside-parent subject ordinals must be unique and within their native bone table.");
            var resolved = PmmMotionAdapter.ResolveTracks([model.InitialModelKeyframe], model.ModelKeyframes, 1, "outside-parent model");
            keys.Add(model.Index, resolved.Select(p => p.Key).OrderBy(k => k.Frame).ToArray());
            void ValidateSlots(IReadOnlyList<PmmOutsideParent> parents)
            {
                if (parents.Count != subjects.Count)
                    throw new InvalidDataException("PMM outside-parent slot count differs from its subject table.");
                foreach (var parent in parents.Where(p => p.ModelIndex >= 0))
                    if (!byId.TryGetValue(parent.ModelIndex, out var target) || parent.BoneIndex < 0 || parent.BoneIndex >= target.BoneNames.Count)
                        throw new InvalidDataException("PMM outside-parent target is outside its native model/bone table.");
            }
            foreach (var key in keys[model.Index]) ValidateSlots(key.OutsideParents);
            ValidateSlots(model.OutsideParentStates.Select(p => p.Parent).ToArray());
        }
        void ValidateGraph(Func<PmmModel, IReadOnlyList<PmmOutsideParent>> parents)
        {
            var visiting = new HashSet<int>(); var done = new HashSet<int>();
            void Visit(int id)
            {
                if (done.Contains(id)) return;
                if (!visiting.Add(id)) throw new InvalidDataException("PMM outside-parent model dependency cycle.");
                foreach (int target in parents(byId[id]).Where(p => p.ModelIndex >= 0).Select(p => p.ModelIndex).Distinct()) Visit(target);
                visiting.Remove(id); done.Add(id);
            }
            foreach (int id in byId.Keys) Visit(id);
        }
        foreach (int frame in keys.Values.SelectMany(k => k).Select(k => k.Frame).Distinct())
            ValidateGraph(model => PmmTrackEditorRecords.At(keys[model.Index], frame).OutsideParents);
        ValidateGraph(model => model.OutsideParentStates.Select(p => p.Parent).ToArray());
    }

    /// <summary>Checks the complete actor/skeleton set before a runtime candidate is constructed.</summary>
    public void ValidateActors(IReadOnlyDictionary<Guid, PmxModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (actors.Any(a => !models.TryGetValue(a.ObjectId, out var model) || model is null)
            || restoredBindings.Any(r => !models.TryGetValue(r.TargetObjectId, out var model) || model is null
                || !model.Bones.Select(b => b.Name).SequenceEqual(r.TargetBoneNames)))
            throw new InvalidDataException("PMM control provenance requires its complete original actor set; detach or explicitly remap controls before adding/removing actors.");
        foreach (var actor in actors)
        {
            var mapped = Map(actor.ObjectId, actor.ModelIndex, models[actor.ObjectId].Bones.Select(b => b.Name).ToArray(),
                document.Models.Single(m => m.Index == actor.ModelIndex), actor.BindingNames, actor.DetachedOrdinals);
            if (!mapped.NativeBoneNames.SequenceEqual(actor.NativeBoneNames) || !mapped.BoneIndices.SequenceEqual(actor.BoneIndices))
                throw new InvalidDataException("PMM control provenance skeleton changed; explicit remapping is required.");
        }
    }

    public ScenePmmControlSample Sample(double frame, bool saved = false)
    {
        ValidateTime(frame);
        if (saved) return savedSample;
        int parentIdx = ParentIntervalIndexAtValidated(frame);
        int disabledIdx = DisabledIntervalIndexAtValidated(frame);
        var parentInterval = parentIntervals[parentIdx];
        var disabledInterval = GetDisabledInterval(disabledIdx);
        if (physics.TryGetConstantGravitySegment(frame, out int gravitySegment, out var constantGravity))
        {
            var cached = Volatile.Read(ref lastConstantSample);
            if (cached is not null && cached.DisabledIdx == disabledIdx && cached.ParentIdx == parentIdx
                && cached.GravitySegment == gravitySegment)
                return cached.Sample;
            var created = new ScenePmmControlSample(constantGravity, disabledInterval.ListsByActor, parentInterval.OutsideParents)
            {
                GroundOutsideParents = parentInterval.GroundOutsideParents,
                OutsideParentTrackWindows = parentInterval.TrackWindows,
            };
            Volatile.Write(ref lastConstantSample, new(disabledIdx, parentIdx, gravitySegment, created));
            return created;
        }
        return new(physics.GravityAt(frame), disabledInterval.ListsByActor, parentInterval.OutsideParents)
        {
            GroundOutsideParents = parentInterval.GroundOutsideParents,
            OutsideParentTrackWindows = parentInterval.TrackWindows,
        };
    }

    internal int ParentIntervalCount => parentIntervals.Length;
    internal int ParentIntervalIndexAt(double frame)
    {
        ValidateTime(frame);
        return ParentIntervalIndexAtValidated(frame);
    }
    internal SceneOutsideParentBinding[] OutsideParentsArrayAtInterval(int intervalIndex)
        => parentIntervals[intervalIndex].OutsideParentsArray;
    internal IReadOnlyList<ScenePmmGroundOutsideParent> GroundOutsideParentsAtInterval(int intervalIndex)
        => parentIntervals[intervalIndex].GroundOutsideParents;
    internal ScenePmmGroundOutsideParent[] GroundParentsForActorAtInterval(Guid id, int intervalIndex)
        => parentIntervals[intervalIndex].GroundByActor is { } map && map.TryGetValue(id, out var arr)
            ? arr : Array.Empty<ScenePmmGroundOutsideParent>();
    internal IReadOnlyDictionary<int, OutsideParentTrackWindow>? TrackWindowsForActorAtInterval(Guid id, int intervalIndex)
        => parentIntervals[intervalIndex].TrackWindows.TryGetValue(id, out var windows) ? windows : null;
    internal int DisabledIntervalIndexAt(double frame)
    {
        ValidateTime(frame);
        return DisabledIntervalIndexAtValidated(frame);
    }
    internal IReadOnlySet<int>? DisabledBoneSetForActorAtInterval(Guid id, int intervalIndex)
        => GetDisabledInterval(intervalIndex).SetsByActor is { } sets && sets.TryGetValue(id, out var set) ? set : null;
    internal PmmGravityPose GravityAt(double frame) => physics.GravityAt(frame);

    private int ParentIntervalIndexAtValidated(double frame)
        => parentIntervalFrames.Length == 1 ? 0 : FindIntervalIndex(parentIntervalFrames, frame);

    private int DisabledIntervalIndexAtValidated(double frame)
        => disabledIntervalFrames.Length == 1 ? 0 : FindIntervalIndex(disabledIntervalFrames, frame);

    private static int FindIntervalIndex(int[] starts, double frame)
    {
        int lo = 0, hi = starts.Length;
        while (lo < hi)
        {
            int middle = lo + (hi - lo) / 2;
            if (starts[middle] <= frame) lo = middle + 1; else hi = middle;
        }
        return Math.Max(0, lo - 1);
    }

    private static void ValidateTime(double frame)
    {
        if (!double.IsFinite(frame) || frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
    }

    /// <summary>Authored local camera channels. The parent is a discrete hold channel, independent of VMD curves.</summary>
    public PmmCameraPose CameraAt(double frame, bool saved = false)
    {
        ValidateTime(frame);
        return saved ? scene.SavedCamera : scene.CameraAt(frame);
    }

    /// <summary>Maps the held PMM target through explicit actor IDs and native bone names, without sampling a solver.</summary>
    public ScenePmmCameraParent? CameraParentAt(double frame, bool saved = false)
    {
        return ResolveCameraParent(CameraAt(frame, saved));
    }

    /// <summary>Resolves an explicit saved/draft camera's target through this immutable provenance.</summary>
    public ScenePmmCameraParent? ResolveCameraParent(PmmCameraPose pose)
    {
        if (pose.ParentModelIndex < 0) return null;
        if (!byModel.TryGetValue(pose.ParentModelIndex, out var target) || (uint)pose.ParentBoneIndex >= (uint)target.BoneIndices.Length)
            throw new InvalidDataException($"PMM camera parent model #{pose.ParentModelIndex} bone #{pose.ParentBoneIndex} is unavailable.");
        if (ResolveMappedBone(target, pose.ParentBoneIndex) is { } mapped)
            return new(mapped.Id, mapped.Bone, pose.ParentModelIndex, pose.ParentBoneIndex);
        if (target.DetachedOrdinals?.Contains(pose.ParentBoneIndex) == true) return null;
        throw new InvalidDataException("Unavailable PMM camera native-name binding.");
    }

    /// <summary>Existing conflict/anchor/link validation is authoritative. The current state is never mutated.
    /// Full PMM bone edits also change pose/curve; the project owner must synchronize authored VMD in its same transaction.</summary>
    public ScenePmmControlState Edit(Func<PmmTrackEditor, PmmTrackEditPlan> prepare, bool confirmOverwrite = false)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        var editor = new PmmTrackEditor(ExportDocument());
        var plan = prepare(editor) ?? throw new ArgumentException("A PMM control edit must return an edit plan.", nameof(prepare));
        editor.Apply(plan, confirmOverwrite);
        var edited = editor.ExportDocument();
        return new(edited, actors, OriginSha256, restoredBindings);
    }

    /// <summary>Updates the retained template from current authored VMD before a full PMM bone-record edit.
    /// Existing exporter validation rejects erasing physics switch frames.</summary>
    public ScenePmmControlState SynchronizeMotions(IReadOnlyList<SceneActorMotion> motions)
    {
        ArgumentNullException.ThrowIfNull(motions);
        ValidateActors(motions.ToDictionary(m => m.ObjectId, m => m.Model));
        var result = ExportDocument(); var byId = motions.ToDictionary(m => m.ObjectId);
        for (int position = 0; position < result.Models.Count; position++)
        {
            var actor = byModel[result.Models[position].Index]; var motion = byId[actor.ObjectId];
            result = PmmEditedMotionExporter.ReplaceModelMotion(result, position, motion.Model, motion.Motion);
        }
        return new(result, actors, OriginSha256, restoredBindings);
    }

    private IReadOnlyList<SceneOutsideParentBinding> ParentsAt(double frame, bool saved)
        => ParentsAndGroundAt(frame, saved).OutsideParents;

    private (IReadOnlyList<SceneOutsideParentBinding> OutsideParents, IReadOnlyList<ScenePmmGroundOutsideParent> GroundOutsideParents)
        ParentsAndGroundAt(double frame, bool saved)
    {
        ValidateTime(frame);
        var interval = saved ? savedParentInterval : parentIntervals[ParentIntervalIndexAtValidated(frame)];
        return (interval.OutsideParents, interval.GroundOutsideParents);
    }

    private IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>> TrackWindowsAt(double frame, bool saved)
    {
        ValidateTime(frame);
        var interval = saved ? savedParentInterval : parentIntervals[ParentIntervalIndexAtValidated(frame)];
        return interval.TrackWindows;
    }

    private (int[] Frames, ParentInterval[] Intervals, ParentInterval Saved) BuildParentIntervals(int[] frames)
    {
        int modelCount = document.Models.Count;
        if (modelCount == 0)
        {
            var emptyInterval = new ParentInterval([], EmptyOutsideParents, EmptyGroundOutsideParents, ScenePmmControlSample.EmptyTrackWindows, null);
            return ([0], [emptyInterval], emptyInterval);
        }

        var orderedKeysByModel = new PmmModelKeyframe[modelCount][];
        for (int m = 0; m < modelCount; m++) orderedKeysByModel[m] = modelKeys[document.Models[m].Index];

        var keyCursor = new int[modelCount];
        var intervalFramesList = new List<int>();
        var intervalsList = new List<ParentInterval>();
        for (int fi = 0; fi < frames.Length; fi++)
        {
            int frame = frames[fi];
            bool anyChanged = fi == 0;
            for (int m = 0; m < modelCount; m++)
            {
                var keys = orderedKeysByModel[m];
                int cur = keyCursor[m];
                while (cur + 1 < keys.Length && keys[cur + 1].Frame <= frame)
                {
                    cur++;
                    if (!SameOutsideParents(keys[cur - 1].OutsideParents, keys[cur].OutsideParents))
                        anyChanged = true;
                }
                keyCursor[m] = cur;
            }
            if (!anyChanged) continue;
            var interval = BuildParentInterval(orderedKeysByModel, keyCursor, saved: false);
            if (intervalsList.Count == 0 || !ParentIntervalEquals(intervalsList[^1], interval))
            {
                intervalFramesList.Add(frame);
                intervalsList.Add(interval);
            }
        }
        var savedInterval = BuildParentInterval(orderedKeysByModel, null, saved: true);
        return (intervalFramesList.ToArray(), intervalsList.ToArray(), savedInterval);
    }

    private ParentInterval BuildParentInterval(PmmModelKeyframe[][] orderedKeysByModel, int[]? keyCursors, bool saved)
    {
        var result = new List<SceneOutsideParentBinding>();
        var ground = new List<ScenePmmGroundOutsideParent>();
        Dictionary<Guid, Dictionary<int, OutsideParentTrackWindow>>? mutableWindows = null;
        for (int m = 0; m < document.Models.Count; m++)
        {
            var model = document.Models[m];
            IReadOnlyList<PmmOutsideParent> parents = saved
                ? model.OutsideParentStates.Select(s => s.Parent).ToArray()
                : orderedKeysByModel[m][keyCursors![m]].OutsideParents;
            if (parents.Count != model.OutsideParentSubjectBoneIndices.Count)
                throw new InvalidDataException("PMM control outside-parent slot count differs from the subject table.");
            var actor = byModel[model.Index];
            for (int slot = 0; slot < parents.Count; slot++)
            {
                var parent = parents[slot];
                int subject = model.OutsideParentSubjectBoneIndices[slot];
                if (parent.ModelIndex == -2 && subject >= 0)
                {
                    if (ResolveMappedBone(actor, subject) is { } groundSubject)
                        ground.Add(new(groundSubject.Id, groundSubject.Bone));
                    continue;
                }
                if (parent.ModelIndex < 0) continue;
                if (!byModel.TryGetValue(parent.ModelIndex, out var target)) throw new InvalidDataException("Unknown PMM outside-parent actor.");
                var mappedTarget = ResolveMappedBone(target, parent.BoneIndex);
                var mappedSubject = subject < 0 ? (Id: actor.ObjectId, Bone: -1) : ResolveMappedBone(actor, subject);
                if (mappedTarget is null || mappedSubject is null) continue;
                result.Add(new(mappedSubject.Value.Id, mappedSubject.Value.Bone, mappedTarget.Value.Id, mappedTarget.Value.Bone, Matrix4x4.Identity));
            }
            CollectTrackWindowsForActiveIndex(model, orderedKeysByModel[m], saved ? 0 : keyCursors![m], saved,
                subject => ResolveMappedBone(actor, subject),
                (targetId, bone, window) =>
                {
                    mutableWindows ??= [];
                    if (!mutableWindows.TryGetValue(targetId, out var dict)) mutableWindows[targetId] = dict = [];
                    dict[bone] = window;
                });
        }
        int totalSubjects = result.Count + ground.Count;
        if (result.Select(p => (p.ObjectId, p.BoneIndex)).Concat(ground.Select(g => (g.ObjectId, g.BoneIndex))).Distinct().Count() != totalSubjects)
            throw new InvalidDataException("PMM control outside-parent subjects must be unique.");
        var edges = result.GroupBy(p => p.ObjectId).ToDictionary(g => g.Key, g => g.Select(p => p.ParentObjectId).Distinct().ToArray());
        var visiting = new HashSet<Guid>(); var done = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException("PMM control outside-parent model dependency cycle.");
            if (edges.TryGetValue(id, out var parents)) foreach (var parent in parents) Visit(parent);
            visiting.Remove(id); done.Add(id);
        }
        foreach (var id in edges.Keys) Visit(id);

        SceneOutsideParentBinding[] resultArray = result.Count == 0 ? [] : result.ToArray();
        IReadOnlyList<SceneOutsideParentBinding> resultReadOnly = resultArray.Length == 0 ? EmptyOutsideParents : Array.AsReadOnly(resultArray);
        IReadOnlyList<ScenePmmGroundOutsideParent> groundReadOnly = ground.Count == 0 ? EmptyGroundOutsideParents : Array.AsReadOnly(ground.ToArray());
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>> windowsReadOnly;
        if (mutableWindows is null || mutableWindows.Count == 0)
            windowsReadOnly = ScenePmmControlSample.EmptyTrackWindows;
        else
        {
            var readonlyMap = new Dictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>>(mutableWindows.Count);
            foreach (var (id, dict) in mutableWindows) readonlyMap.Add(id, new ReadOnlyDictionary<int, OutsideParentTrackWindow>(dict));
            windowsReadOnly = new ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>>(readonlyMap);
        }
        Dictionary<Guid, ScenePmmGroundOutsideParent[]>? groundByActor = null;
        if (ground.Count > 0)
        {
            groundByActor = [];
            var grouped = new Dictionary<Guid, List<ScenePmmGroundOutsideParent>>();
            for (int i = 0; i < ground.Count; i++)
            {
                var item = ground[i];
                if (!grouped.TryGetValue(item.ObjectId, out var list)) grouped[item.ObjectId] = list = [];
                list.Add(item);
            }
            foreach (var (id, list) in grouped) groundByActor.Add(id, list.ToArray());
        }
        return new(resultArray, resultReadOnly, groundReadOnly, windowsReadOnly, groundByActor);
    }

    private (int[] Frames, DisabledInterval?[] Intervals, bool Eager) BuildDisabledIntervals()
    {
        var frameSet = new SortedSet<int> { 0 };
        for (int a = 0; a < actors.Length; a++)
        {
            var starts = physics.GetIntervalStarts(actors[a].ModelIndex);
            for (int i = 1; i < starts.Length; i++) frameSet.Add(starts[i]);
        }
        var frames = frameSet.ToArray();
        const long MaxEagerActorIntervals = 100_000;
        long cost = (long)frames.Length * Math.Max(1, allDisabledActorIds.Length);
        if (cost > MaxEagerActorIntervals)
            return (frames, new DisabledInterval?[frames.Length], false);

        var intervals = new DisabledInterval?[frames.Length];
        DisabledInterval? prev = null;
        for (int i = 0; i < frames.Length; i++)
        {
            var current = BuildDisabledInterval(frames[i], saved: false, prev);
            intervals[i] = current;
            prev = current;
        }
        return (frames, intervals, true);
    }

    private DisabledInterval GetDisabledInterval(int disabledIdx)
    {
        var cached = eagerDisabledIntervals ? disabledIntervals[disabledIdx] : Volatile.Read(ref disabledIntervals[disabledIdx]);
        if (cached is not null) return cached;
        var computed = BuildDisabledInterval(disabledIntervalFrames[disabledIdx], saved: false, previous: null);
        return Interlocked.CompareExchange(ref disabledIntervals[disabledIdx], computed, null) ?? computed;
    }

    private DisabledInterval BuildDisabledInterval(double frame, bool saved, DisabledInterval? previous)
    {
        if (allDisabledActorIds.Length == 0) return emptyDisabledInterval;
        Dictionary<Guid, List<int>>? mutable = null;
        for (int a = 0; a < actors.Length; a++)
        {
            var actor = actors[a];
            var disabledOrdinals = physics.DisabledBonesAt(actor.ModelIndex, frame, saved);
            for (int i = 0; i < disabledOrdinals.Count; i++)
            {
                if (ResolveMappedBone(actor, disabledOrdinals[i]) is not { } mapped) continue;
                mutable ??= new Dictionary<Guid, List<int>>(allDisabledActorIds.Length);
                if (!mutable.TryGetValue(mapped.Id, out var list)) mutable[mapped.Id] = list = [];
                if (!list.Contains(mapped.Bone)) list.Add(mapped.Bone);
            }
        }
        if (mutable is null) return emptyDisabledInterval;
        var lists = new Dictionary<Guid, IReadOnlyList<int>>(allDisabledActorIds.Length);
        var sets = new Dictionary<Guid, IReadOnlySet<int>>(mutable.Count);
        for (int i = 0; i < allDisabledActorIds.Length; i++)
        {
            Guid id = allDisabledActorIds[i];
            if (!mutable.TryGetValue(id, out var list) || list.Count == 0)
            {
                lists.Add(id, EmptyBones);
                continue;
            }
            if (previous is not null && previous.ListsByActor.TryGetValue(id, out var prevList) && SequenceEqual(prevList, list))
            {
                lists.Add(id, prevList);
                if (previous.SetsByActor is not null && previous.SetsByActor.TryGetValue(id, out var prevSet))
                    sets.Add(id, prevSet);
            }
            else
            {
                var arr = list.ToArray();
                lists.Add(id, Array.AsReadOnly(arr));
                sets.Add(id, new HashSet<int>(arr));
            }
        }
        return new(new ReadOnlyDictionary<Guid, IReadOnlyList<int>>(lists), sets.Count == 0 ? null : sets);
    }

    private static bool SequenceEqual(IReadOnlyList<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static bool SameOutsideParents(IReadOnlyList<PmmOutsideParent> a, IReadOnlyList<PmmOutsideParent> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!SameOutsideParent(a[i], b[i])) return false;
        return true;
    }

    private static bool ParentIntervalEquals(ParentInterval a, ParentInterval b)
    {
        if (!a.OutsideParentsArray.AsSpan().SequenceEqual(b.OutsideParentsArray)) return false;
        if (a.GroundOutsideParents.Count != b.GroundOutsideParents.Count) return false;
        for (int i = 0; i < a.GroundOutsideParents.Count; i++)
            if (a.GroundOutsideParents[i] != b.GroundOutsideParents[i]) return false;
        if (a.TrackWindows.Count != b.TrackWindows.Count) return false;
        foreach (var (actorId, aWin) in a.TrackWindows)
        {
            if (!b.TrackWindows.TryGetValue(actorId, out var bWin) || aWin.Count != bWin.Count) return false;
            foreach (var (bone, win) in aWin)
                if (!bWin.TryGetValue(bone, out var otherWin) || win != otherWin) return false;
        }
        return true;
    }

    internal static IReadOnlyDictionary<int, OutsideParentTrackWindow>? ComputeTrackWindows(PmmModel model,
        PmmModelKeyframe[] orderedKeys, double frame, bool saved, Func<int, int> mapBone)
    {
        Dictionary<int, OutsideParentTrackWindow>? result = null;
        CollectTrackWindows(model, orderedKeys, frame, saved,
            subject => { int mapped = mapBone(subject); return mapped >= 0 ? (Guid.Empty, mapped) : ((Guid, int)?)null; },
            (_, bone, window) => (result ??= [])[bone] = window);
        return result;
    }

    private static void CollectTrackWindows(PmmModel model, PmmModelKeyframe[] orderedKeys, double frame, bool saved,
        Func<int, (Guid Id, int Bone)?> resolveSubject, Action<Guid, int, OutsideParentTrackWindow> emit)
    {
        var subjects = model.OutsideParentSubjectBoneIndices;
        if (subjects.Count == 0) return;
        int activeIndex = 0;
        if (!saved)
        {
            int low = 0, high = orderedKeys.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (orderedKeys[middle].Frame <= frame) low = middle + 1;
                else high = middle;
            }
            activeIndex = Math.Max(0, low - 1);
        }
        CollectTrackWindowsForActiveIndex(model, orderedKeys, activeIndex, saved, resolveSubject, emit);
    }

    private static void CollectTrackWindowsForActiveIndex(PmmModel model, PmmModelKeyframe[] orderedKeys, int activeIndex, bool saved,
        Func<int, (Guid Id, int Bone)?> resolveSubject, Action<Guid, int, OutsideParentTrackWindow> emit)
    {
        var subjects = model.OutsideParentSubjectBoneIndices;
        if (subjects.Count == 0) return;
        for (int slot = 0; slot < subjects.Count; slot++)
        {
            int subject = subjects[slot];
            if (subject < 0 || resolveSubject(subject) is not { } mapped) continue;
            uint begin, end;
            if (saved)
            {
                var state = model.OutsideParentStates[slot];
                begin = state.BeginFrame > 0 ? (uint)state.BeginFrame : 0u;
                end = state.EndFrame > 0 && state.EndFrame < int.MaxValue ? (uint)state.EndFrame : 0u;
            }
            else
            {
                var activeParent = orderedKeys[activeIndex].OutsideParents[slot];
                int beginIdx = activeIndex;
                while (beginIdx > 0 && SameOutsideParent(orderedKeys[beginIdx - 1].OutsideParents[slot], activeParent)) beginIdx--;
                begin = beginIdx == 0 ? 0u : (uint)orderedKeys[beginIdx].Frame;
                int endIdx = activeIndex + 1;
                while (endIdx < orderedKeys.Length && SameOutsideParent(orderedKeys[endIdx].OutsideParents[slot], activeParent)) endIdx++;
                end = endIdx < orderedKeys.Length ? (uint)orderedKeys[endIdx].Frame : 0u;
            }
            if (begin == 0u && end == 0u) continue;
            emit(mapped.Id, mapped.Bone, new(begin, end));
        }
    }

    private static bool SameOutsideParent(PmmOutsideParent a, PmmOutsideParent b)
        => a.ModelIndex == b.ModelIndex && (a.ModelIndex < 0 || a.BoneIndex == b.BoneIndex);

    private static Actor Map(ScenePmmActorSource actor, PmmModel model)
        => Map(actor.ObjectId, actor.PmmModelIndex, actor.Model.Bones.Select(b => b.Name).ToArray(), model);

    private static Actor Map(Guid objectId, byte modelIndex, string[] nativeNames, PmmModel model, string[]? bindingNames = null, int[]? detachedOrdinals = null)
    {
        string[] names = (string[])nativeNames.Clone();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("PMM controls require unique native bone names in the selected PMX skeleton.");
        string[] sourceNames = bindingNames is null ? PmmNameResolver.ResolveTable(model.BoneNames, names, "control bone")
            .Select((binding, index) => binding.BoundName ?? model.BoneNames[index].Value).ToArray() : (string[])bindingNames.Clone();
        if (sourceNames.Length != model.BoneNames.Count) throw new InvalidDataException("Invalid PMM retained native-name mapping.");
        int[] detached = detachedOrdinals is null ? [] : (int[])detachedOrdinals.Clone();
        int[] indices = sourceNames.Select((name, ordinal) => detached.Contains(ordinal) ? -1 : Array.IndexOf(names, name)).ToArray();
        if (indices.Where(i => i >= 0).Distinct().Count() != indices.Count(i => i >= 0))
            throw new InvalidDataException("PMM control bone mapping must be one-to-one for matched names.");
        return new(objectId, modelIndex, indices, names, sourceNames, detached.Length == 0 ? null : detached);
    }

    // Missing passive animation names remain detached (-1) in provenance; active controls may never
    // target one. This runs before model I/O for payloads and against the actual name map on creation.
    private static void ValidateMappedControls(PmmDocument source, IReadOnlyDictionary<int, int[]> mappings, IReadOnlyDictionary<int, int[]>? detached = null)
    {
        void RequireBone(int modelId, int bone, string use)
        {
            if (detached?.TryGetValue(modelId, out var ordinals) == true && ordinals.Contains(bone)) return;
            if (!mappings.TryGetValue(modelId, out var map) || (uint)bone >= (uint)map.Length || map[bone] < 0)
            {
                string name = source.Models.FirstOrDefault(m => m.Index == modelId)?.BoneNames.ElementAtOrDefault(bone)?.Value ?? "?";
                throw new InvalidDataException($"PMM {use} requires model #{modelId} bone #{bone} '{name}', which has no unique native-name match in the selected model.");
            }
        }
        foreach (var key in new[] { source.Camera.InitialKeyframe }.Concat(source.Camera.Keyframes))
            if (key.ParentModelIndex >= 0) RequireBone(key.ParentModelIndex, key.ParentBoneIndex, $"camera parent at frame {key.Frame}");
        ValidateAccessoryMappedControls(source, RequireBone);
        if (source.Settings.CameraLookAtModelIndex >= 0)
            RequireBone(source.Settings.CameraLookAtModelIndex, source.Settings.CameraLookAtBoneIndex, "saved camera parent");
        foreach (var model in source.Models)
        {
            foreach (int bone in model.ConstraintBoneIndices) RequireBone(model.Index, bone, "IK control");
            foreach (var pair in PmmMotionAdapter.ResolveTracks(model.InitialBoneKeyframes, model.BoneKeyframes, model.BoneNames.Count, "physics bone"))
                if (pair.Key.PhysicsDisabled) RequireBone(model.Index, pair.Track, $"physics control at frame {pair.Key.Frame}");
            for (int bone = 0; bone < model.BoneStates.Count; bone++)
                if (model.BoneStates[bone].PhysicsDisabled) RequireBone(model.Index, bone, "saved physics control");
            void Parents(IReadOnlyList<PmmOutsideParent> parents)
            {
                for (int slot = 0; slot < parents.Count; slot++)
                {
                    var parent = parents[slot];
                    if (parent.ModelIndex == -2)
                    {
                        int groundSubject = model.OutsideParentSubjectBoneIndices[slot];
                        if (groundSubject >= 0) RequireBone(model.Index, groundSubject, "outside-parent subject");
                        continue;
                    }
                    if (parent.ModelIndex < 0) continue;
                    RequireBone(parent.ModelIndex, parent.BoneIndex, "outside-parent target");
                    int subject = model.OutsideParentSubjectBoneIndices[slot];
                    if (subject >= 0) RequireBone(model.Index, subject, "outside-parent subject");
                }
            }
            foreach (var key in new[] { model.InitialModelKeyframe }.Concat(model.ModelKeyframes)) Parents(key.OutsideParents);
            Parents(model.OutsideParentStates.Select(p => p.Parent).ToArray());
        }
    }

    private static Actor[] CaptureSkeletons(IEnumerable<ScenePmmActorSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.Select(a => a is not null && a.Model is not null
            ? new Actor(a.ObjectId, a.PmmModelIndex, [], a.Model.Bones.Select(b => b.Name).ToArray())
            : throw new InvalidDataException("PMM control actors require explicit non-null skeletons.")).ToArray();
    }

    private static PmmDocument Read(byte[] data, int codePage)
        => PmmReader.Read(new MemoryStream(data, writable: false), new() { AnsiCodePage = codePage == 0 ? 936 : codePage, MaxInputBytes = MaxPayloadBytes });
    private static void ValidateDigest(string digest)
    {
        if (digest is null || digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("PMM control provenance requires a SHA-256 hex digest.");
    }
}
