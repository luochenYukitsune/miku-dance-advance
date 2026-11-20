using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>MMD format-to-domain boundary. Owns no clock, controls, or GPU handles.</summary>
public sealed partial class CharacterAnimationController : IDisposable
{
    private static readonly IReadOnlyDictionary<int, float> EmptyMorphWeights = new Dictionary<int, float>();
    private static readonly IReadOnlyList<string> EmptyReadOnlyDiagnostics = Array.AsReadOnly(Array.Empty<string>());
    private readonly PmxModel model;
    private readonly CharacterAnimator animator;
    private readonly OriginalStyleScene restSurface;
    private readonly OriginalOutlineData restOutline;
    private readonly MmdIkMotionAdapter ikSwitches;
    private readonly MmdModelVisibilityTrack visibility;
    private readonly PmxMorphAdapter morphs;
    private readonly PmxMaterialMorphAdapter materialMorphs;
    private readonly PmxExtendedMorphAdapter extendedMorphs;
    private readonly PmxImpulseMorphAdapter impulseMorphs;
    private readonly MmdMorphMotionAdapter morphMotion;
    private readonly int[] defaultDrivenBoneIndices;
    private readonly bool timeInvariantBoneTracks;
    private readonly IReadOnlyList<string> animationDiagnostics;
    private IReadOnlyList<string> physicsDiagnostics=Array.Empty<string>();
    private readonly List<string> physicsCleanupDiagnostics=[];
    private sealed record DiagnosticsCache(
        CharacterPhysicsPlayback? Physics,
        SharedPhysicsStatus? SharedStatus,
        IReadOnlyList<string> PhysicsDiagnostics,
        PmxSoftBodyPlayback? SoftBodies,
        PmxCoupledSoftBodyPlayback? CoupledSoftBodies,
        bool DeferCpuSoftDiagnostic,
        int CleanupCount,
        string[] SharedStatusDiags,
        string[] PhysicsBackendDiags,
        string[] SoftBodyDiags,
        string[] CoupledDiags,
        IReadOnlyList<string> Result);
    private DiagnosticsCache? diagnosticsCache;
    /// <summary>Cleanup failures after a committed replacement never invalidate the new solver.</summary>
    public IReadOnlyList<string> CleanupDiagnostics=>Array.AsReadOnly(physicsCleanupDiagnostics.ToArray());
    private CharacterPhysicsPlayback? physics;
    private ICharacterPhysicsBackend? physicsBackend;
    internal bool SupportsConcurrentIndependentPhysics => physicsBackend is null || physicsBackend.SupportsConcurrentIndependentWorlds;
    private CharacterSample? sampledPose;
    private bool disposed;
    private double? sampledFrame;
    private bool sampledIk=true;
    private bool sampledMorphs=true;
    private PreparedMorphs? preparedMorphsCache;
    private double preparedMorphsFrame;
    private bool preparedMorphsEnabled;
    private IReadOnlyDictionary<int,float>? preparedMorphsWeights;
    private PreparedMorphs? sampledMorphsPreparation;
    private IReadOnlyList<PmxAdditionalUvVertex>? preparedAdditionalUvRows;
    private SceneAdditionalUvs? preparedAdditionalUvStream;
    private (OriginalStyleScene Surface,OriginalOutlineData Outline) sampled;
    private IReadOnlyDictionary<int,Matrix4x4>? outsideParents;
    private IReadOnlyDictionary<int,OutsideParentTrackWindow>? outsideParentTrackWindows;
    private Matrix4x4[]? frozenBoneWorlds;
    private IReadOnlyDictionary<int,Matrix4x4>? frozenOutsideParents;
    /// <summary>Worlds from the latest successful sample, including physics. Each snapshot remains stable.</summary>
    public IReadOnlyList<Matrix4x4> BoneWorldMatrices {get;private set;}=Array.Empty<Matrix4x4>();
    public CharacterMorphChannels MorphChannels {get;private set;} = new(Array.Empty<PmxAdditionalUvVertex>(), Array.Empty<PmxMaterialTextureFactors>());
    public uint DurationFrames {get;}
    /// <summary>Latest successfully sampled model visibility. Hidden models continue animation and physics.</summary>
    public bool IsVisible {get;private set;}=true;
    public bool SupportsMorphs=>SupportedMorphCount>0;
    public int SupportedMorphCount {get;}
    public int MatchedMorphTrackCount=>morphMotion.MatchedTrackCount;
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            var currentShared = sharedPhysicsStatus;
            var currentPhysics = physics;
            var currentPhysicsDiags = physicsDiagnostics;
            var currentSoft = softBodies;
            var currentCoupled = CoupledSoftBodies;
            bool currentDeferSoft = deferVertexSkinningRequested && model.SoftBodies.Count != 0;
            int currentCleanupCount = physicsCleanupDiagnostics.Count;
            var currentSharedDiags = currentShared?.Diagnostics;
            var currentBackendDiags = currentPhysics?.Diagnostics;
            var currentSoftDiags = currentSoft?.Diagnostics;
            var currentCoupledDiags = currentCoupled?.Diagnostics;

            var cached = Volatile.Read(ref diagnosticsCache);
            if (cached is not null
                && ReferenceEquals(cached.SharedStatus, currentShared)
                && ReferenceEquals(cached.Physics, currentPhysics)
                && ReferenceEquals(cached.PhysicsDiagnostics, currentPhysicsDiags)
                && ReferenceEquals(cached.SoftBodies, currentSoft)
                && ReferenceEquals(cached.CoupledSoftBodies, currentCoupled)
                && cached.DeferCpuSoftDiagnostic == currentDeferSoft
                && cached.CleanupCount == currentCleanupCount
                && SameDiagnosticStrings(cached.SharedStatusDiags, currentSharedDiags)
                && SameDiagnosticStrings(cached.PhysicsBackendDiags, currentBackendDiags)
                && SameDiagnosticStrings(cached.SoftBodyDiags, currentSoftDiags)
                && SameDiagnosticStrings(cached.CoupledDiags, currentCoupledDiags))
            {
                return cached.Result;
            }

            var rigid = currentShared is not null
                ? animationDiagnostics.Where(d => !d.StartsWith("Physics is not evaluated;", StringComparison.Ordinal)).Concat(currentSharedDiags!).ToArray()
                : currentPhysics is null ? animationDiagnostics
                : animationDiagnostics.Where(d => !d.StartsWith("Physics is not evaluated;", StringComparison.Ordinal))
                    .Concat(currentPhysicsDiags).Concat(currentBackendDiags!).Distinct(StringComparer.Ordinal).ToArray();
            var combined = (currentSoft is null ? rigid : rigid.Concat(currentSoftDiags!))
                .Concat(currentCoupledDiags ?? [])
                .Concat(currentDeferSoft ? new[] { SoftBodyCpuSkinningDiagnostic } : Array.Empty<string>())
                .Concat(physicsCleanupDiagnostics).Distinct(StringComparer.Ordinal).ToArray();
            var result = combined.Length == 0 ? EmptyReadOnlyDiagnostics : Array.AsReadOnly(combined);
            Volatile.Write(ref diagnosticsCache, new DiagnosticsCache(
                currentPhysics, currentShared, currentPhysicsDiags, currentSoft, currentCoupled,
                currentDeferSoft, currentCleanupCount,
                SnapshotDiagnosticStrings(currentSharedDiags), SnapshotDiagnosticStrings(currentBackendDiags),
                SnapshotDiagnosticStrings(currentSoftDiags), SnapshotDiagnosticStrings(currentCoupledDiags),
                result));
            return result;
        }
    }
    private static bool SameDiagnosticStrings(string[] snapshot, IReadOnlyList<string>? current)
    {
        int count = current?.Count ?? 0;
        if (snapshot.Length != count) return false;
        for (int i = 0; i < count; i++)
            if (!string.Equals(snapshot[i], current![i], StringComparison.Ordinal)) return false;
        return true;
    }
    private static string[] SnapshotDiagnosticStrings(IReadOnlyList<string>? current)
    {
        if (current is null || current.Count == 0) return Array.Empty<string>();
        var copy = new string[current.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = current[i];
        return copy;
    }
    public bool SupportsPhysics=>model.RigidBodies.Count>0;
    internal const string SoftBodyCpuSkinningDiagnostic="This model has soft bodies; its vertices are skinned on the CPU, not by the renderer's GPU skinning.";
    private bool deferVertexSkinningRequested;
    /// <summary>Requested by the owning session for a renderer that skins on the GPU. Soft-body models keep CPU
    /// skinning explicitly (their solvers read skinned positions); see UsesDeferredVertexSkinning and Diagnostics.</summary>
    internal bool DeferVertexSkinning
    {
        get=>deferVertexSkinningRequested;
        set{ObjectDisposedException.ThrowIf(disposed,this);deferVertexSkinningRequested=value;animator.DeferVertexSkinning=value&&model.SoftBodies.Count==0;}
    }
    public bool UsesDeferredVertexSkinning=>animator.DeferVertexSkinning;
    public bool PhysicsEnabled=>physics is not null||sharedPhysicsStatus is not null||SoftBodiesEnabled;
    public bool PhysicsFaulted=>sharedPhysicsStatus?.IsFaulted??physics?.IsFaulted??false;
    public int PhysicsLastStepCount=>sharedPhysicsStatus?.LastStepCount??physics?.LastStepCount??0;
    public double PhysicsDiscardedSeconds=>sharedPhysicsStatus?.DiscardedSeconds??physics?.DiscardedSeconds??0;
    public string? PhysicsBackendName=>sharedPhysicsStatus?.BackendName??physics?.BackendName??softBackendName;

    public CharacterAnimationController(PmxModel model,VmdMotion motion,OriginalStyleScene surface,OriginalOutlineData outline,
        bool allowEmptyMotion = false)
    {
        ArgumentNullException.ThrowIfNull(model);ArgumentNullException.ThrowIfNull(motion);
        this.model=model;
        restSurface=surface;restOutline=outline;
        var rig=PmxCharacterRigAdapter.Convert(model,surface.Snapshot);
        ikSwitches=new(model.Bones.Select((b,i)=>(b,i)).Where(p=>p.b.Flags.HasFlag(PmxBoneFlags.Ik))
            .ToDictionary(p=>p.b.Name,p=>p.i,StringComparer.Ordinal),motion);
        visibility=new(motion);IsVisible=visibility.Evaluate(0);
        morphs=new(model);materialMorphs=new(model);extendedMorphs=new(model);impulseMorphs=new(model);
        SupportedMorphCount=morphs.SupportedIndices.Concat(materialMorphs.SupportedIndices).Concat(extendedMorphs.SupportedMorphIndices.Values)
            .Concat(impulseMorphs.SupportedMorphIndices.Values).Distinct().Count();
        var morphNames=morphs.SupportedMorphIndices.Concat(materialMorphs.SupportedMorphIndices).Concat(extendedMorphs.SupportedMorphIndices)
            .Concat(impulseMorphs.SupportedMorphIndices)
            .DistinctBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        morphMotion=new(morphNames,motion);
        var boneNames=model.Bones.Select(b=>b.Name).ToHashSet(StringComparer.Ordinal);
        bool isEmpty = motion.BoneKeyframes.Count == 0 && motion.MorphKeyframes.Count == 0 && motion.IkKeyframes.Count == 0
            && motion.CameraKeyframes.Count == 0 && motion.LightKeyframes.Count == 0 && motion.SelfShadowKeyframes.Count == 0;
        if(!(allowEmptyMotion && isEmpty)&&!motion.BoneKeyframes.Any(k=>boneNames.Contains(k.BoneName))&&!ikSwitches.HasMatchedSwitches&&!morphMotion.HasMatchedTracks&&!visibility.HasTrack)
            throw new NotSupportedException("VMD没有与模型匹配的骨骼轨道、IK开关或支持的顶点／骨／UV／材质／冲量表情轨道；仅镜头或未支持的表情动作不能播放。");
        var clip=MmdBoneMotionAdapter.Convert(motion);
        // A single frame-zero key uses the endpoint path at every nonnegative clock.
        // Equal endpoints on a longer track are deliberately not treated as constant:
        // interpolation and quaternion normalization can produce different float bits.
        timeInvariantBoneTracks=clip.Tracks.Where(track=>boneNames.Contains(track.BoneName))
            .All(track=>track.Keys.Count==1&&track.Keys[0].Frame==0);
        animator=new(rig,clip);DurationFrames=Math.Max(Math.Max(clip.DurationFrames,ikSwitches.DurationFrames),morphMotion.DurationFrames);
        var diagnostics=new List<string>(motion.Diagnostics);diagnostics.AddRange(animator.Diagnostics);
        diagnostics.AddRange(ikSwitches.Diagnostics.Where(d=>!d.StartsWith("VMD model visibility is retained",StringComparison.Ordinal)));
        diagnostics.AddRange(morphs.Diagnostics.Where(d=>!d.StartsWith("PMX morph type 'Material'",StringComparison.Ordinal)
            &&!d.StartsWith("PMX morph type 'Impulse'",StringComparison.Ordinal)));
        diagnostics.AddRange(materialMorphs.Diagnostics);diagnostics.AddRange(impulseMorphs.Diagnostics);diagnostics.AddRange(morphMotion.Diagnostics);
        if(model.Bones.Any(b=>(b.Flags&(PmxBoneFlags.FixedAxis|PmxBoneFlags.LocalAxis))!=0))diagnostics.Add("固定轴IK约束与局部轴编辑已实现；原函数单骨VMD／骨表情采样已验证这些标志不附加旋转约束或基底变换，IK／追加／物理等组合的完整原版一致性仍未全验。");
        animationDiagnostics=diagnostics.AsReadOnly();
        if(model.RigidBodies.Count==0) defaultDrivenBoneIndices=[];
        else
        {
            var seenDriven=new HashSet<int>(); var drivenList=new List<int>();
            for(int i=0;i<model.RigidBodies.Count;i++)
            {
                var body=model.RigidBodies[i];
                if(body.BoneIndex>=0&&body.Operation!=Formats.PmxRigidBodyOperation.Static&&seenDriven.Add(body.BoneIndex))
                    drivenList.Add(body.BoneIndex);
            }
            defaultDrivenBoneIndices=drivenList.Count==0?[]:drivenList.ToArray();
        }
    }
    public (OriginalStyleScene Surface,OriginalOutlineData Outline) Sample(uint frame,bool enableIk=true,bool enableMorphs=true)
        =>SampleAt(frame,enableIk,enableMorphs);
    /// <summary>Absolute sampling preserves fractional morph time; physics restarts at the exact requested pose.</summary>
    public (OriginalStyleScene Surface,OriginalOutlineData Outline) SampleAt(double frame,bool enableIk=true,bool enableMorphs=true)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        ValidateFrame(frame);
        frame=Math.Min(frame,DurationFrames);if(sampledFrame==frame&&sampledIk==enableIk&&sampledMorphs==enableMorphs)return sampled;
        var prepared=PrepareMorphs(frame,enableMorphs);
        var evaluation=Evaluation(frame,enableIk,enableMorphs);
        var pose=physics is null?animator.Sample(frame,evaluation):physics.ResetAt(frame,evaluation);
        return Store(pose,frame,enableIk,enableMorphs,prepared);
    }

    // A rigid-only scene's nonphysical actors may still supply moving outside-parent bones.
    // Retain their exact stateless sample route, without turning disabled rigid bodies into
    // physics-driven bones or constructing intermediate vertex/outline snapshots.
    internal void SampleRigidInitializationBoneWorlds(double frame,bool enableIk,bool enableMorphs)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ValidateFrame(frame);
        if(physics is not null||SoftBodiesEnabled)
            throw new InvalidOperationException("A nonphysical rigid initialization sample cannot own an independent or soft solver.");
        frame=Math.Min(frame,DurationFrames);
        if(sampledFrame==frame&&sampledIk==enableIk&&sampledMorphs==enableMorphs)return;
        if(TryRetainCompleteRigidPlaybackPose(frame,enableIk,enableMorphs))return;
        var worlds=animator.SampleBoneWorldMatrices(frame,Evaluation(frame,enableIk,enableMorphs));
        BoneWorldMatrices=Array.AsReadOnly(worlds);
        sampledFrame=null;sampledPose=null;sampledMorphsPreparation=null;
        sharedRigidBoneFrame=frame;
        frozenBoneWorlds=null;frozenOutsideParents=null;frozenSoftVertices=null;
    }

    // Only a full stateless pose with proven time-invariant bone input can move its
    // cache clock. SetOutsideParents invalidates sampledFrame whenever an effective
    // attachment changes. Any matched IK control track conservatively takes the
    // ordinary evaluator, even if its latest values happen to compare equal.
    internal bool TryRetainCompleteRigidPlaybackPose(double frame,bool enableIk,bool enableMorphs)
    {
        if(disposed||PhysicsEnabled||model.SoftBodies.Count!=0||!timeInvariantBoneTracks||ikSwitches.HasMatchedSwitches
            ||outsideParentTrackWindows is {Count:>0}||sampledFrame is null||sampledPose is null||sharedRigidBoneFrame is not null
            ||sampledIk!=enableIk||sampledMorphs!=enableMorphs||BoneWorldMatrices.Count!=model.Bones.Count
            ||frozenBoneWorlds is not null||frozenOutsideParents is not null||frozenSoftVertices is not null)return false;
        ValidateFrame(frame);frame=Math.Min(frame,DurationFrames);
        var prepared=PrepareMorphs(frame,enableMorphs);
        if(!ReferenceEquals(sampledMorphsPreparation,prepared))return false;
        sampledFrame=frame;IsVisible=visibility.Evaluate(frame);
        return true;
    }
    internal static bool OutsideParentsEqual(IReadOnlyDictionary<int,Matrix4x4>? left,IReadOnlyDictionary<int,Matrix4x4>? right)
    {
        if((left?.Count??0)!=(right?.Count??0))return false;
        if(left is null or {Count:0})return true;
        foreach(var(key,value) in left)
            if(!right!.TryGetValue(key,out var other)||other!=value)return false;
        return true;
    }
    internal static bool TrackWindowsEqual(IReadOnlyDictionary<int,OutsideParentTrackWindow>? left,IReadOnlyDictionary<int,OutsideParentTrackWindow>? right)
    {
        if((left?.Count??0)!=(right?.Count??0))return false;
        if(left is null or {Count:0})return true;
        foreach(var(key,value) in left)
            if(!right!.TryGetValue(key,out var other)||other!=value)return false;
        return true;
    }
    /// <summary>Copies target bone-world transforms. -1 attaches all model roots; other keys attach individual bones.</summary>
    public void SetOutsideParents(IReadOnlyDictionary<int,Matrix4x4>? parents)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var copy=CopyOutsideParents(parents);
        if(OutsideParentsEqual(outsideParents,copy))return;
        outsideParents=copy;sampledFrame=null;frozenBoneWorlds=null;frozenOutsideParents=null;frozenSoftVertices=null;
    }
    internal bool SetOutsideParentTrackWindows(IReadOnlyDictionary<int,OutsideParentTrackWindow>? windows)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var copy=windows?.Count>0?new Dictionary<int,OutsideParentTrackWindow>(windows):null;
        if(copy is not null)
            foreach(var(index,_) in copy)
                if(index<0||index>=model.Bones.Count)
                    throw new ArgumentException("Outside-parent track window subject is outside the model.",nameof(windows));
        if(TrackWindowsEqual(outsideParentTrackWindows,copy))return false;
        outsideParentTrackWindows=copy;sampledFrame=null;frozenBoneWorlds=null;frozenOutsideParents=null;frozenSoftVertices=null;
        return true;
    }
    private Dictionary<int,Matrix4x4>? CopyOutsideParents(IReadOnlyDictionary<int,Matrix4x4>? parents)
    {
        var copy=parents?.Count>0?new Dictionary<int,Matrix4x4>(parents):null;
        if(copy is not null)
            foreach(var (index,value) in copy)
            {
                if(index < -1 || index >= model.Bones.Count)
                    throw new ArgumentException("Outside-parent subject is outside the model.",nameof(parents));
                if(!float.IsFinite(value.M11)||!float.IsFinite(value.M12)||!float.IsFinite(value.M13)
                    ||!float.IsFinite(value.M21)||!float.IsFinite(value.M22)||!float.IsFinite(value.M23)
                    ||!float.IsFinite(value.M31)||!float.IsFinite(value.M32)||!float.IsFinite(value.M33)
                    ||!float.IsFinite(value.M41)||!float.IsFinite(value.M42)||!float.IsFinite(value.M43)
                    ||value.M14!=0||value.M24!=0||value.M34!=0||value.M44!=1||!Matrix4x4.Invert(value,out _))
                    throw new ArgumentException("Outside parents must be finite invertible affine transforms.",nameof(parents));
            }
        return copy;
    }

    /// <summary>Repositions the completed final pose with moving outside parents. The solver remains frozen;
    /// Seek/ResetPhysics is required before resuming simulation. Nested bindings replace inherited attachments.</summary>
    public (OriginalStyleScene Surface,OriginalOutlineData Outline) RefreshFrozenOutsideParents(IReadOnlyDictionary<int,Matrix4x4> parents)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ArgumentNullException.ThrowIfNull(parents);
        if(sampledFrame!=DurationFrames || outsideParents is null || BoneWorldMatrices.Count!=model.Bones.Count)
            throw new InvalidOperationException("Frozen attachment refresh requires a successfully sampled final pose and existing outside parents.");
        var next=CopyOutsideParents(parents);
        if(next is null || next.Count!=outsideParents.Count || outsideParents.Keys.Any(key=>!next.ContainsKey(key)))
            throw new InvalidOperationException("Frozen attachment slots cannot change; rebuild or seek the actor first.");
        if(next.All(pair=>outsideParents[pair.Key]==pair.Value))return sampled;
        return RefreshFrozenPose(next);
    }
    /// <summary>Transfers backend ownership. Prepare the physical pose before replacing a valid active timeline.</summary>
    public void EnablePhysics(ICharacterPhysicsBackend backend,uint frame,bool enableIk=true,bool enableMorphs=true)
        =>EnablePhysicsAt(backend,frame,enableIk,enableMorphs);
    public void EnablePhysicsAt(ICharacterPhysicsBackend backend,double frame,bool enableIk=true,bool enableMorphs=true)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ArgumentNullException.ThrowIfNull(backend);
        // An active backend is still owned by the published solver. Reject aliases before
        // entering the candidate path, whose error cleanup takes ownership of fresh inputs.
        if(ReferenceEquals(physicsBackend,backend))throw new InvalidOperationException("A replacement rigid solver requires a newly owned backend.");
        CharacterPhysicsPlayback? candidate=null;bool transferred=false;
        try
        {
            ValidateFrame(frame);
            if(!SupportsPhysics)throw new NotSupportedException("模型没有刚体数据。");
            if(sharedPhysicsStatus is not null)throw new InvalidOperationException("Detach shared physics before enabling an independent rigid solver.");
            if(UsesCoupledSoftBodies)throw new InvalidOperationException("Replace coupled physics through EnableCoupledPhysicsAt to rebuild the complete graph.");
            frame=Math.Min(frame,DurationFrames);
            if(softBodies is not null)
            {
                if(!softBodies.SupportsRigidProxyUpdates)throw new NotSupportedException("The active soft backend cannot atomically replace rigid proxy poses while preserving soft history.");
                if(frozenBoneWorlds is not null || sampledFrame!=frame || sampledIk!=enableIk || sampledMorphs!=enableMorphs)
                    throw new InvalidOperationException("Sample or reset the soft actor at the requested frame and options before replacing rigid physics.");
            }
            var prepared=PrepareMorphs(frame,enableMorphs);
            var evaluation=Evaluation(frame,enableIk,enableMorphs);
            var adapted=PmxCharacterPhysicsAdapter.Convert(model);
            transferred=true;candidate=new(animator,adapted.Definition,backend);
            var pose=candidate.ResetAt(frame,evaluation);
            // Prepare the complete scene before the only mutation of the existing soft world.
            var vertices=softBodies?.Apply(pose.Vertices);
            var scene=CreateScene(pose,prepared,vertices);
            softBodies?.UpdateRigidProxies(candidate.GetBodyWorldTransforms());
            var previous=physics;physics=candidate;physicsBackend=backend;candidate=null;physicsDiagnostics=adapted.Diagnostics;
            sampled=scene;sampledFrame=frame;sampledIk=enableIk;sampledMorphs=enableMorphs;
            IsVisible=visibility.Evaluate(frame);
            frozenBoneWorlds=null;frozenOutsideParents=null;frozenSoftVertices=null;
            BoneWorldMatrices=Array.AsReadOnly(pose.BoneWorldMatrices);
            sampledPose=pose;
            MorphChannels=prepared.Channels;
            sampledMorphsPreparation=prepared;
            try { previous?.Dispose(); }
            catch(Exception error) { physicsCleanupDiagnostics.Add("Previous rigid backend cleanup failed after replacement: "+error.Message); }
        }
        catch{candidate?.Dispose();if(!transferred)backend.Dispose();throw;}
    }
    public void DisablePhysics()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        physics?.Dispose();physics=null;physicsBackend=null;physicsDiagnostics=Array.Empty<string>();sampledFrame=null;
        frozenBoneWorlds=null;frozenOutsideParents=null;frozenSoftVertices=null;
    }
    /// <summary>Changes live acceleration without resetting the physical pose or solver timeline.</summary>
    public void SetGravity(Vector3 acceleration)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(physics is null&&softBodies is null)throw new InvalidOperationException("Enable physics before changing its gravity.");
        if(!float.IsFinite(acceleration.X)||!float.IsFinite(acceleration.Y)||!float.IsFinite(acceleration.Z))throw new ArgumentOutOfRangeException(nameof(acceleration));
        physics?.SetGravity(acceleration);softBodies?.SetGravity(acceleration);
    }
    public (OriginalStyleScene Surface,OriginalOutlineData Outline) ResetPhysics(uint frame,bool enableIk=true,bool enableMorphs=true)
    {
        ObjectDisposedException.ThrowIf(disposed,this);frame=Math.Min(frame,DurationFrames);
        var prepared=PrepareMorphs(frame,enableMorphs);
        var evaluation=Evaluation(frame,enableIk,enableMorphs);
        var pose=physics is null?animator.Sample(frame,evaluation):physics.ResetAt(frame,evaluation);
        return Store(pose,frame,enableIk,enableMorphs,prepared);
    }
    /// <summary>Continuous playback only. Explicit seeks use ResetPhysics/Sample to clear physical history.</summary>
    public (OriginalStyleScene Surface,OriginalOutlineData Outline) Advance(double frame,double elapsedSeconds,bool enableIk=true,bool enableMorphs=true,bool applyImpulses=true)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!double.IsFinite(frame)||frame<0)throw new ArgumentOutOfRangeException(nameof(frame));
        if(!double.IsFinite(elapsedSeconds)||elapsedSeconds<0||elapsedSeconds>(softBodies is null?86400:10))throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if(frozenBoneWorlds is not null)throw new InvalidOperationException("Seek or reset the actor before resuming physics after a frozen attachment refresh.");
        frame=Math.Min(frame,DurationFrames);
        if(physics is null)
        {
            if(softBodies is not null)
            {
                var prepared=PrepareMorphs(frame,enableMorphs);
                return Store(animator.Sample(frame,Evaluation(frame,enableIk,enableMorphs)),frame,enableIk,enableMorphs,prepared,elapsedSeconds);
            }
            return SampleAt(frame,enableIk,enableMorphs);
        }
        var physicalMorphs=PrepareMorphs(frame,enableMorphs);
        CharacterBodyImpulse[] impulses=applyImpulses && physicalMorphs.Channels.Impulses.Count>0
            ? CreateBodyImpulses(physicalMorphs.Channels.Impulses)
            : Array.Empty<CharacterBodyImpulse>();
        return Store(physics.Advance(frame,elapsedSeconds,Evaluation(frame,enableIk,enableMorphs),impulses),frame,enableIk,enableMorphs,physicalMorphs,elapsedSeconds);
    }
    private static CharacterBodyImpulse[] CreateBodyImpulses(IReadOnlyList<PmxRigidBodyImpulse> impulses)
    {
        if (impulses.Count == 0) return Array.Empty<CharacterBodyImpulse>();
        var result = new CharacterBodyImpulse[impulses.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var value = impulses[i];
            result[i] = new CharacterBodyImpulse(value.RigidBodyIndex, value.Local, value.TranslationVelocity, value.RotationTorque);
        }
        return result;
    }
    private CharacterEvaluationOptions Evaluation(double frame,bool enableIk,bool enableMorphs)=>new(enableIk,
        ikSwitches.HasMatchedSwitches?ikSwitches.Evaluate((uint)Math.Floor(frame)):null,
        PrepareMorphs(frame,enableMorphs).Pose,outsideParents,OutsideParentTrackWindows:outsideParentTrackWindows);
    private static void ValidateFrame(double frame)
    {if(!double.IsFinite(frame)||frame<0||frame>uint.MaxValue)throw new ArgumentOutOfRangeException(nameof(frame));}
    private (OriginalStyleScene Surface,OriginalOutlineData Outline) Store(CharacterSample pose,double frame,bool enableIk,bool enableMorphs,PreparedMorphs prepared,double? softElapsed=null)
    {
        // Deferred samples exist only for models without soft bodies; nothing reads their vertices here.
        if(pose.Deformation is not null&&softBodies is null&&CoupledSoftBodies is null)
            return StoreVertices(pose,frame,enableIk,enableMorphs,prepared,null);
        var vertices=ApplySoftBodies(pose.Vertices,pose.BoneWorldMatrices,softElapsed);
        return StoreVertices(pose,frame,enableIk,enableMorphs,prepared,vertices);
    }
    private (OriginalStyleScene Surface,OriginalOutlineData Outline) StoreVertices(CharacterSample pose,double frame,bool enableIk,bool enableMorphs,PreparedMorphs prepared,SceneVertex[]? vertices)
        =>StorePreparedScene(pose,frame,enableIk,enableMorphs,prepared,CreateScene(pose,prepared,vertices));
    private (OriginalStyleScene Surface,OriginalOutlineData Outline) StorePreparedScene(CharacterSample pose,double frame,bool enableIk,bool enableMorphs,PreparedMorphs prepared,(OriginalStyleScene Surface,OriginalOutlineData Outline) scene)
    {
        sampled=scene;MorphChannels=prepared.Channels;sampledFrame=frame;sampledIk=enableIk;sampledMorphs=enableMorphs;
        sampledMorphsPreparation=prepared;
        sharedRigidBoneFrame=null;
        IsVisible=visibility.Evaluate(frame);
        frozenBoneWorlds=null;frozenOutsideParents=null;frozenSoftVertices=null;
        BoneWorldMatrices=Array.AsReadOnly(pose.BoneWorldMatrices);sampledPose=pose;return sampled;
    }
    internal sealed record PreparedMorphs(OriginalStyleScene Surface,OriginalOutlineData Outline,CharacterMorphChannels Channels,MorphPose? Pose);
    // Only SceneAnimationSession may supply the already accepted local boundary and prove that
    // an otherwise unprepared visual leaf belongs to it. This is not an arbitrary-frame evaluator.
    // Reading materials never completes a deferred pose, samples bones, skins or touches a solver.
    internal (PreparedMorphs Prepared, bool Visible) CaptureMaterialPreparation(double frame, bool enableMorphs,
        bool pendingPreviewLeaf)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateFrame(frame);
        if (frame > DurationFrames) throw new ArgumentOutOfRangeException(nameof(frame));
        PreparedMorphs prepared;
        if (deferredRigidPose is { } deferred)
        {
            if (!pendingPreviewLeaf || deferred.Frame != frame || deferred.Morphs != enableMorphs
                || sharedRigidBoneFrame != frame || sampledFrame is not null || outsideParents is not null
                || !ReferenceEquals(BoneWorldMatrices, deferred.PublishedWorldView))
                throw new InvalidOperationException("Material inputs do not match the deferred physical boundary.");
            prepared = deferred.Prepared;
        }
        else if (sampledFrame == frame && sampledMorphs == enableMorphs && sampledPose is not null
            && sharedRigidBoneFrame is null && sampledMorphsPreparation is { } sampledPreparation)
            prepared = sampledPreparation;
        else
        {
            if (!pendingPreviewLeaf || sharedRigidBoneFrame is not null)
                throw new InvalidOperationException("Material inputs require a completed sample or the current deferred visual leaf.");
            prepared = PrepareMorphs(frame, enableMorphs);
        }
        return (prepared, visibility.Evaluate(frame));
    }
    // Numeric morph failures are input failures, not simulation failures. Evaluate every channel and
    // material against the immutable baseline before allowing an existing solver to advance/reset.
    private PreparedMorphs PrepareMorphs(double frame,bool enabled)
    {
        // These adapters snapshot their PMX/VMD definitions at construction. Parent
        // transforms and physical steps can change the solved pose at one clock, but
        // cannot change its morph inputs. Keep only the most recent successful result.
        if(preparedMorphsCache is {} cached&&preparedMorphsFrame==frame&&preparedMorphsEnabled==enabled)
            return cached;
        IReadOnlyDictionary<int,float> weights=enabled?morphMotion.Evaluate(frame):EmptyMorphWeights;
        // Motion sampling owns this dictionary. Equal effective inputs can safely share one
        // immutable preparation across clocks; bone/IK/physics/parents still sample every time.
        // Compare float bits so negative zero and distinct numeric payloads are not conflated.
        if(preparedMorphsCache is {} previous&&preparedMorphsEnabled==enabled
            &&SameMorphWeightBits(preparedMorphsWeights,weights))
        { preparedMorphsFrame=frame;return previous; }
        // Base vertex/UV and bone morphs are immutable inputs at this motion clock, just like
        // material and extended channels. Physics, IK and outside parents still evaluate afresh.
        // Do not publish the cache until every channel has passed its existing validation.
        var pose=enabled&&morphMotion.HasMatchedTracks?morphs.Evaluate(weights):null;
        var channels=new CharacterMorphChannels(extendedMorphs.EvaluateReusable(weights),materialMorphs.EvaluateTextureFactors(weights))
            { Impulses=impulseMorphs.Evaluate(weights) };
        var scene=enabled&&materialMorphs.SupportedMorphCount>0?materialMorphs.Apply(restSurface,restOutline,weights):(restSurface,restOutline);
        // The extended adapter has already evaluated groups and fractional/negative weights.
        // Copy only that result into the renderer-neutral vertex-order stream, without re-evaluating morphs.
        if(extendedMorphs.AdditionalUvCount>0)
        {
            if(!ReferenceEquals(preparedAdditionalUvRows,channels.AdditionalUvs))
            {
                var values=new SceneAdditionalUvVertex[channels.AdditionalUvs.Count];
                for(int i=0;i<values.Length;i++)
                {
                    var value=channels.AdditionalUvs[i];
                    values[i]=new(value.Uv1,value.Uv2,value.Uv3,value.Uv4);
                }
                var stream=new SceneAdditionalUvs(extendedMorphs.AdditionalUvCount,values);
                preparedAdditionalUvRows=channels.AdditionalUvs;preparedAdditionalUvStream=stream;
            }
            scene=(new(scene.Item1.Snapshot.WithAdditionalUvs(preparedAdditionalUvStream),
                scene.Item1.Materials,scene.Item1.Profile),scene.Item2);
        }
        // Attach the already evaluated local-material accumulators before project composition.
        // Identity retains the established binding instances and does not change core morph arithmetic.
        bool hasTextureFactors=!ReferenceEquals(channels.TextureFactors,materialMorphs.IdentityTextureFactors);
        if(hasTextureFactors)
        {
            hasTextureFactors=false;
            var factors=channels.TextureFactors;
            for(int i=0;i<factors.Count;i++)
                if(factors[i]!=PmxMaterialTextureFactors.Identity){hasTextureFactors=true;break;}
        }
        if(hasTextureFactors)
        {
            var bindings=scene.Item1.Materials.ToArray();
            for(int i=0;i<bindings.Length;i++)
            {
                var value=channels.TextureFactors[i];
                bindings[i]=bindings[i] with {TextureFactors=new(
                    new(value.Diffuse.Multiply,value.Diffuse.Add),new(value.Sphere.Multiply,value.Sphere.Add),
                    new(value.Toon.Multiply,value.Toon.Add))};
            }
            scene=(new(scene.Item1.Snapshot,bindings,scene.Item1.Profile),scene.Item2);
        }
        var result=new PreparedMorphs(scene.Item1,scene.Item2,channels,pose);
        preparedMorphsFrame=frame;preparedMorphsEnabled=enabled;preparedMorphsWeights=weights;preparedMorphsCache=result;
        return result;
    }
    private static bool SameMorphWeightBits(IReadOnlyDictionary<int,float>? previous,IReadOnlyDictionary<int,float> current)
    {
        if(previous is null||previous.Count!=current.Count)return false;
        foreach(var pair in previous)
            if(!current.TryGetValue(pair.Key,out float value)
                ||BitConverter.SingleToInt32Bits(pair.Value)!=BitConverter.SingleToInt32Bits(value))return false;
        return true;
    }
    private (OriginalStyleScene Surface,OriginalOutlineData Outline) CreateScene(CharacterSample pose,PreparedMorphs prepared,SceneVertex[]? vertices=null)
    {
        if(vertices is null&&pose.Deformation is {} deformation)
        {
            // Equal palettes and the same morph instance reuse the published lineage, like equal CPU bytes.
            var prior=sampled.Surface?.Snapshot;
            var reused=prior is null?null:prepared.Surface.Snapshot.ReuseUnchangedDeferredDeformation(prior,deformation);
            if(reused is not null)
                return(new(reused,prepared.Surface.Materials,prepared.Surface.Profile),sampled.Outline.WithMaterials(prepared.Outline.Materials));
            var deferred=prepared.Surface.Snapshot.WithDeferredDeformation(deformation);
            return(new(deferred,prepared.Surface.Materials,prepared.Surface.Profile),prepared.Outline.WithDeferredSurface(deferred));
        }
        vertices??=pose.Vertices;
        var previous=sampled.Surface?.Snapshot;
        var snapshot=previous is null?null:prepared.Surface.Snapshot.ReuseUnchangedDeformation(previous,vertices);
        if(snapshot is not null)
            return(new(snapshot,prepared.Surface.Materials,prepared.Surface.Profile),sampled.Outline.WithMaterials(prepared.Outline.Materials));
        // Animator allocates a fresh vertex array per pose. Its retained sampledPose alias
        // stays private and is only read after publication. Soft-body models keep the copy
        // boundary even while disabled; projected or solver-owned output is never transferred.
        snapshot=model.SoftBodies.Count==0&&ReferenceEquals(vertices,pose.Vertices)
            ?prepared.Surface.Snapshot.WithOwnedDeformedVertices(vertices)
            :prepared.Surface.Snapshot.WithDeformedVertices(vertices);
        // Base UV animation changes the surface stream, but cannot alter the outline's
        // positions/normals. Its load-time mode, scale and selected material are fixed for
        // this controller. Preserve that independent immutable geometry and its GPU caches.
        if(sampled.Outline is not null&&HasUnchangedOutlineDeformation(sampled.Outline,vertices))
            return(new(snapshot,prepared.Surface.Materials,prepared.Surface.Profile),sampled.Outline.WithMaterials(prepared.Outline.Materials));
        if(snapshot.VertexCount==0)throw new ArgumentOutOfRangeException("vertexCount");
        // The surface already validated position/normal, and morph preparation retained the
        // load-time outline metadata. Reuse those checks and the exact current material bits.
        var outline=prepared.Outline.WithSurfaceVertexChunks(snapshot,[(0,snapshot.VertexCount)],prepared.Outline.Materials,1);
        return(new(snapshot,prepared.Surface.Materials,prepared.Surface.Profile),outline);
    }
    private static bool HasUnchangedOutlineDeformation(OriginalOutlineData previous,SceneVertex[] vertices)
    {
        if(previous.VertexCount!=vertices.Length)return false;
        static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        static bool Same(Vector3 a,Vector3 b)=>Bits(a.X,b.X)&&Bits(a.Y,b.Y)&&Bits(a.Z,b.Z);
        int offset=0;
        for(int chunk=0;chunk<previous.VertexChunkCount;chunk++)
            foreach(var prior in previous.GetVertexChunk(chunk))
            {
                ref readonly var next=ref vertices[offset++];
                if(!Same(prior.Position,next.Position)||!Same(prior.Normal,next.Normal))return false;
            }
        return true;
    }
    public void Dispose(){if(disposed)return;disposed=true;try{sharedCoupledSoftBodies?.Dispose();sharedCoupledSoftBodies=null;physics?.Dispose();}finally{physics=null;physicsBackend=null;softBodies?.Dispose();softBodies=null;}}
}
