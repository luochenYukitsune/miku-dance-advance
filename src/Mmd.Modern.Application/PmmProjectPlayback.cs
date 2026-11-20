using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>Owns a project of character timelines. UI clocks, texture decoding and concrete solvers are injected by the host.</summary>
public sealed partial class PmmProjectPlayback : IDisposable
{
    private sealed class Entry(PreparedPmmModel source,OriginalFrameScene rest)
    {
        public PreparedPmmModel Source {get;}=source;
        public OriginalFrameScene Rest {get;}=SceneModelShadingPolicy.Apply(rest, source.Purpose.Role);
        public CharacterAnimationController? Controller;
        public bool Saved=true;
        private OriginalFrameScene frame=SceneModelShadingPolicy.Apply(rest, source.Purpose.Role);
        // Every publication route (saved, timeline, shared and coupled physics) passes through here.
        public OriginalFrameScene Frame
        {
            get => frame;
            set
            {
                var flags=value.Materials.Select((flags,index)=>flags with
                    { GiToonResponseOverride=Rest.Materials[index].GiToonResponseOverride??flags.GiToonResponseOverride,
                      CharacterToonResponse=Rest.Materials[index].CharacterToonResponse??flags.CharacterToonResponse,
                      CharacterOutlineStyle=Rest.Materials[index].CharacterOutlineStyle??flags.CharacterOutlineStyle });
                frame=SceneModelShadingPolicy.Apply(new(value.Surface,value.Outline,value.OutlineOptions,
                    value.Options,flags,value.Lighting,value.EditorOverlay),Source.Purpose.Role);
            }
        }
    }
    private readonly Dictionary<int,Entry> entries;
    private readonly Dictionary<int,PmmModelKeyframe[]> orderedModelKeys;
    private readonly Func<Vector3,float?,ICharacterPhysicsBackend> physicsFactory;
    private readonly Func<PreparedPmmModel,ISoftBodyBackend>? softPhysicsFactory;
    private readonly bool coupledSoftBodies;
    private readonly Dictionary<int,ICharacterPhysicsBackend> legacyPhysicsBackends=[];
    private int[] visible=[];
    private OriginalFrameScene? combined;
    private SceneCamera? currentCamera;
    private bool disposed;
    public PmmProjectImportPlan Plan {get;}
    public PmmSceneTracks Tracks {get;}
    public uint DurationFrames {get;}
    public double CurrentFrame {get;private set;}
    public bool IsSavedPose {get;private set;}=true;
    public bool SupportsPhysics=>entries.Values.Any(e=>e.Source.Model.RigidBodies.Count>0
        ||(softPhysicsFactory is not null||coupledSoftBodies)&&e.Source.Model.SoftBodies.Count>0);
    public bool PhysicsEnabled {get;private set;}
    public bool HasMorphTracks=>entries.Values.Any(e=>e.Source.Motion.MorphKeyframes.Count>0);
    public int PhysicsLastStepCount=>pmmSharedWorld is not null?pmmSharedStatus?.LastStepCount??0:entries.Values.Sum(e=>e.Controller?.PhysicsLastStepCount??0);
    public double PhysicsDiscardedSeconds=>pmmSharedWorld is not null?pmmSharedStatus?.DiscardedSeconds??0:entries.Values.Sum(e=>e.Controller?.PhysicsDiscardedSeconds??0);
    public IReadOnlyList<string> Diagnostics=>Plan.Diagnostics.Concat(entries.Values.SelectMany(e=>e.Controller?.Diagnostics??[]))
        .Concat([useSharedPhysics?"Project models use a shared physics world with inter-model rigid-body contacts.":
            "Project models use separate physics worlds without inter-model rigid-body contacts.",
            "PMM per-bone switches, linear gravity keys and deterministic gravity noise are evaluated. Noise uses a private replay stream at 120 Hz; original process-global random consumption and Bullet numerical parity remain unverified. Media decoding and accessory composition require their separate hosts."])
        .Where(d=>!d.Contains("outside-parent transforms are not executed",StringComparison.Ordinal)&&!d.StartsWith("PMM camera, light, output size",StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal).ToArray();

    public PmmProjectPlayback(PmmProjectImportPlan plan,IReadOnlyDictionary<int,OriginalFrameScene> restScenes,Func<Vector3,float?,ICharacterPhysicsBackend> physicsFactory,
        bool sharedPhysicsWorld=false,Func<PreparedPmmModel,ISoftBodyBackend>? softPhysicsFactory=null)
        :this(plan,restScenes,physicsFactory,sharedPhysicsWorld,softPhysicsFactory,false) { }

    /// <summary>Explicit coupled mode borrows soft content from each actual rigid numerical owner.
    /// Shared mode includes cross-actor contacts; independent mode keeps one coupled owner per model.</summary>
    public PmmProjectPlayback(PmmProjectImportPlan plan,IReadOnlyDictionary<int,OriginalFrameScene> restScenes,Func<Vector3,float?,ICharacterPhysicsBackend> physicsFactory,
        bool sharedPhysicsWorld,Func<PreparedPmmModel,ISoftBodyBackend>? standaloneSoftPhysicsFactory,bool coupledSoftBodies)
    {
        Plan=plan??throw new ArgumentNullException(nameof(plan));ArgumentNullException.ThrowIfNull(restScenes);
        this.physicsFactory=physicsFactory??throw new ArgumentNullException(nameof(physicsFactory));
        this.softPhysicsFactory=standaloneSoftPhysicsFactory;this.coupledSoftBodies=coupledSoftBodies;
        if(coupledSoftBodies&&standaloneSoftPhysicsFactory is not null)
            throw new ArgumentException("Select coupled soft content or an independent soft factory.",nameof(standaloneSoftPhysicsFactory));
        useSharedPhysics=sharedPhysicsWorld;
        physicsControls=new(plan.Document);
        requiresControlledPhysics=sharedPhysicsWorld||RequiresPmmPhysicsControls(plan.Document)
            ||(standaloneSoftPhysicsFactory is not null||coupledSoftBodies)&&plan.Models.Any(m=>m.Model.SoftBodies.Count!=0);
        if(!plan.CanCommit||plan.Models.Count==0)throw new InvalidDataException("Resolve all PMM model references before replacing the active project.");
        entries=plan.Models.ToDictionary(m=>(int)m.Source.Index,m=>new Entry(m,restScenes[m.Source.Index]));
        orderedModelKeys=plan.Models.ToDictionary(m=>(int)m.Source.Index,
            m=>new[]{m.Source.InitialModelKeyframe}.Concat(m.Source.ModelKeyframes).OrderBy(k=>k.Frame).ToArray());
        PrevalidateSharedPhysicsBindings();
        Tracks=new(plan.Document);CurrentFrame=plan.CurrentFrame;
        void ValidateCamera(PmmCameraPose pose)
        {
            if(!float.IsFinite(pose.Fov)||pose.Fov<=0||pose.Fov>=180)throw new InvalidDataException("PMM camera FOV must be between 0 and 180 degrees.");
            if(pose.ParentModelIndex>=0)
            {
                if(!entries.TryGetValue(pose.ParentModelIndex,out var parent))throw new InvalidDataException("PMM camera parent model is missing.");
                if(MapBone(parent.Source,pose.ParentBoneIndex)<0)throw new InvalidDataException("PMM camera requires a valid target bone.");
            }
            _=pose.CreateCamera().ViewProjection(plan.OutputWidth,plan.OutputHeight);
        }
        ValidateCamera(Tracks.SavedCamera);
        foreach(var key in new[]{plan.Document.Camera.InitialKeyframe}.Concat(plan.Document.Camera.Keyframes))
            ValidateCamera(new(key.LookAt,key.Angle,key.Distance,key.Fov,key.Perspective,key.ParentModelIndex,key.ParentBoneIndex));
        foreach(var entry in entries.Values)
        {
            var model=entry.Source.Source;
            void ValidateParents(IReadOnlyList<PmmOutsideParent> parents)
            {
                if(parents.Count!=model.OutsideParentSubjectBoneIndices.Count)throw new InvalidDataException("PMM outside-parent slot count mismatch.");
                var subjects=new HashSet<int>();
                for(int i=0;i<parents.Count;i++)
                {
                    int subject=MapBone(entry.Source,model.OutsideParentSubjectBoneIndices[i]);
                    if(!subjects.Add(subject))throw new InvalidDataException("PMM outside-parent subject occurs more than once.");
                    var parent=parents[i];if(parent.ModelIndex<0)continue;
                    if(!entries.TryGetValue(parent.ModelIndex,out var target))throw new InvalidDataException("PMM external parent model is missing.");
                    if(MapBone(target.Source,parent.BoneIndex)<0)throw new InvalidDataException("PMM external parent target bone is invalid.");
                }
            }
            ValidateParents(model.OutsideParentStates.Select(s=>s.Parent).ToArray());
            foreach(var key in new[]{model.InitialModelKeyframe}.Concat(model.ModelKeyframes))ValidateParents(key.OutsideParents);
        }
        void ValidateDependencies(uint frame,bool saved)
        {
            var active=new HashSet<int>();var done=new HashSet<int>();
            void Visit(int id)
            {if(done.Contains(id))return;if(!active.Add(id))throw new InvalidDataException("PMM outside-parent model dependency cycle.");
                foreach(var parent in ParentsAt(entries[id].Source.Source,frame,saved).Values.Where(p=>p.ModelIndex>=0))Visit(parent.ModelIndex);
                active.Remove(id);done.Add(id);}
            foreach(int id in entries.Keys)Visit(id);
        }
        ValidateDependencies((uint)plan.CurrentFrame,true);
        foreach(uint frame in plan.Models.SelectMany(m=>new[]{m.Source.InitialModelKeyframe}.Concat(m.Source.ModelKeyframes)).Select(k=>(uint)k.Frame).Distinct())ValidateDependencies(frame,false);
        DurationFrames=Math.Max(physicsControls.DurationFrames,Math.Max((uint)plan.CurrentFrame,Math.Max(Tracks.DurationFrames,(uint)plan.Models.Max(m=>Math.Max(m.Source.LastFrame,m.Motion.BoneKeyframes.Select(k=>(long)k.Frame).Concat(m.Motion.MorphKeyframes.Select(k=>(long)k.Frame)).Concat(m.Motion.IkKeyframes.Select(k=>(long)k.Frame)).DefaultIfEmpty(0).Max())))));
        PhysicsEnabled=plan.EffectivePhysics.Enabled;
        try{Evaluate(CurrentFrame,0,true,true,true,true);}catch{Dispose();throw;}
    }
    public OriginalFrameScene Frame=>combined??throw new InvalidOperationException("Project has not been evaluated.");
    /// <summary>Current model-world bone transform addressed by stored PMM model ID and its original bone-name table.</summary>
    public Matrix4x4 GetBoneWorldTransform(int modelIndex,int boneIndex)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!entries.TryGetValue(modelIndex,out var entry))throw new KeyNotFoundException($"PMM model #{modelIndex} does not exist.");
        int resolved=MapBone(entry.Source,boneIndex);
        var worlds=entry.Controller?.BoneWorldMatrices??throw new InvalidOperationException("Model pose is not available.");
        if((uint)resolved>=(uint)worlds.Count)throw new ArgumentOutOfRangeException(nameof(boneIndex));
        return worlds[resolved];
    }
    public PmmCameraPose CameraPose=>IsSavedPose?Tracks.SavedCamera:Tracks.CameraAt(CurrentFrame);
    /// <summary>The saved editor section has no coverage mode; expose it separately instead of inventing one.</summary>
    public PmmSelfShadowSavedState SavedSelfShadow=>Tracks.SavedSelfShadow;
    public PmmSelfShadowPose? SelfShadowPose=>IsSavedPose?null:Tracks.SelfShadowAt(CurrentFrame);
    public SceneCamera Camera=>currentCamera??throw new InvalidOperationException("Project camera has not been evaluated.");
    public Matrix4x4? CameraParentTransform => ResolveCameraParent(CameraPose);
    private SceneCamera ResolveCamera(PmmCameraPose pose)
        => pose.CreateCamera(ResolveCameraParent(pose));
    private Matrix4x4? ResolveCameraParent(PmmCameraPose pose)
    {
            Matrix4x4? parent=null;
            if(pose.ParentModelIndex>=0)
            {
                if(!entries.TryGetValue(pose.ParentModelIndex,out var target))throw new InvalidDataException("PMM camera parent model is missing.");
                int bone=MapBone(target.Source,pose.ParentBoneIndex);
                if(bone>=0&&target.Controller?.BoneWorldMatrices is {} worlds&&bone<worlds.Count)parent=worlds[bone];
            }
            return parent;
    }
    public void Seek(double frame,bool ik=true,bool morphs=true)=>Evaluate(frame,0,true,ik,morphs,false);
    public void Advance(double frame,double elapsedSeconds,bool ik=true,bool morphs=true)=>Evaluate(frame,elapsedSeconds,false,ik,morphs,false);
    public void Reset(bool ik=true,bool morphs=true)=>Evaluate(CurrentFrame,0,true,ik,morphs,IsSavedPose);
    public void SetPhysics(bool enabled,bool ik=true,bool morphs=true)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(enabled&&coupledSoftBodies)
        {
            bool previous=PhysicsEnabled;PhysicsEnabled=true;
            try{RebuildCoupledPmmGraph(CurrentFrame,ik,morphs,IsSavedPose);}
            catch{PhysicsEnabled=previous;throw;}
            return;
        }
        // Capability/factory failures must leave the disabled playback and its controllers intact.
        if(enabled&&requiresControlledPhysics)
        {
            bool created=EnsurePmmPhysicsWorlds(CurrentFrame,IsSavedPose);
            try{PreparePmmControllers(CurrentFrame,IsSavedPose,ik,morphs);}
            catch{if(created)DisposePmmPhysicsWorlds();throw;}
        }
        PhysicsEnabled=enabled;
        if(!enabled){DisposePmmPhysicsWorlds();foreach(var entry in entries.Values)entry.Controller?.DetachSharedPhysics();}
        foreach(var entry in entries.Values)if(!enabled){entry.Controller?.DisableSoftBodies();entry.Controller?.DisablePhysics();}
        if(!enabled)legacyPhysicsBackends.Clear();
        Reset(ik,morphs);
    }
    private void Evaluate(double frame,double elapsed,bool reset,bool ik,bool morphs,bool saved)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!double.IsFinite(frame)||frame<0||!double.IsFinite(elapsed)||elapsed<0||elapsed>86400)throw new ArgumentOutOfRangeException(nameof(frame));
        frame=Math.Min(frame,DurationFrames);uint integer=(uint)Math.Floor(frame);
        if(requiresControlledPhysics&&PhysicsEnabled){EvaluatePmmShared(frame,elapsed,reset,ik,morphs,saved);return;}
        var bindings=entries.ToDictionary(p=>p.Key,p=>ParentsAt(p.Value.Source.Source,integer,saved));
        var order=new List<int>();var visiting=new HashSet<int>();var complete=new HashSet<int>();
        void Visit(int id)
        {
            if(complete.Contains(id))return;if(!visiting.Add(id))throw new InvalidDataException("PMM outside-parent model dependency cycle.");
            foreach(var parent in bindings[id].Values.Where(p=>p.ModelIndex>=0))
            {if(!entries.ContainsKey(parent.ModelIndex))throw new InvalidDataException($"PMM parent model #{parent.ModelIndex} is missing.");Visit(parent.ModelIndex);}
            visiting.Remove(id);complete.Add(id);order.Add(id);
        }
        foreach(var entry in entries.OrderBy(e=>e.Value.Source.Source.TransformOrderIndex))Visit(entry.Key);
        foreach(int id in order)
        {
            var entry=entries[id];
            if(entry.Controller is null||entry.Saved!=saved)
            {
                var candidate=new CharacterAnimationController(entry.Source.Model,saved?entry.Source.SavedPoseMotion:entry.Source.Motion,entry.Rest.Surface,entry.Rest.Outline!);
                entry.Controller?.Dispose();legacyPhysicsBackends.Remove(id);entry.Controller=candidate;entry.Saved=saved;
            }
            var controller=entry.Controller!;
            double local=saved?0:Math.Min(frame,controller.DurationFrames);
            double prior=saved?0:Math.Min(CurrentFrame,controller.DurationFrames);
            var outside=new Dictionary<int,Matrix4x4>();
            foreach(var pair in bindings[id])
            {
                int sourceBone=MapBone(entry.Source,pair.Key);
                if(pair.Value.ModelIndex==-2)
                {
                    if(sourceBone>=0)outside.Add(sourceBone,Matrix4x4.CreateTranslation(entry.Source.Model.Bones[sourceBone].Position));
                    continue;
                }
                if(pair.Value.ModelIndex<0)continue;
                var target=entries[pair.Value.ModelIndex];int targetBone=MapBone(target.Source,pair.Value.BoneIndex);
                var worlds=target.Controller!.BoneWorldMatrices;
                if(targetBone<0||targetBone>=worlds.Count)throw new InvalidDataException("PMM external parent target bone is invalid.");
                outside.Add(sourceBone,worlds[targetBone]);
            }
            controller.SetOutsideParentTrackWindows(TrackWindowsAt(id,entry.Source,saved?0:frame,saved));
            controller.SetOutsideParents(outside);
            uint sampleFrame=saved?0:integer;
            if(PhysicsEnabled&&controller.SupportsPhysics&&!controller.PhysicsEnabled)
            {
                var backend=physicsFactory(Plan.EffectivePhysics.Gravity,Plan.EffectivePhysics.GroundEnabled?0:null)
                    ??throw new InvalidOperationException("PMM physics factory returned no backend.");
                if(legacyPhysicsBackends.Values.Any(active=>ReferenceEquals(active,backend)))
                    throw new InvalidOperationException("Independent PMM physics requires a distinct backend instance for each model.");
                controller.EnablePhysics(backend,sampleFrame,ik,morphs);
                legacyPhysicsBackends.Add(id,backend);
            }
            // Freeze the local clock and solver at the tail, while still refreshing outside parents
            // and evaluation options. A zero-time Advance preserves physics history; Sample resets it.
            double actorElapsed=saved?elapsed:Math.Min(elapsed,Math.Max(0,(local-prior)/SceneAnimationSession.FramesPerSecond));
            var pose=reset?controller.ResetPhysics(sampleFrame,ik,morphs):controller.Advance(local,actorElapsed,ik,morphs);
            var frameFlags=PmxOriginalFrameAdapter.FromEvaluatedMaterials(entry.Source.Model,pose.Surface,pose.Outline)
                .Select(f=>f with{CastSelfShadow=f.CastSelfShadow&&(Plan.Document.Version==1||entry.Source.Source.SelfShadowEnabled),ReceiveSelfShadow=f.ReceiveSelfShadow&&(Plan.Document.Version==1||entry.Source.Source.SelfShadowEnabled)});
            entry.Frame=new(pose.Surface,pose.Outline,entry.Rest.OutlineOptions,materials:frameFlags);
        }
        int[] shown=entries.Where(p=>saved?p.Value.Source.Source.Visible:p.Value.Source.VisibleAt(integer)).OrderBy(p=>p.Value.Source.Source.DrawOrderIndex).Select(p=>p.Key).ToArray();
        var light=saved?Tracks.SavedLight:Tracks.LightAt(frame);
        var camera=ResolveCamera(saved?Tracks.SavedCamera:Tracks.CameraAt(frame));
        _=camera.ViewProjection(Plan.OutputWidth,Plan.OutputHeight);
        var frames=shown.Select(id=>entries[id].Frame).ToArray();
        combined=frames.Length==0?EmptyFrame():combined is not null&&shown.SequenceEqual(visible)?ProjectSceneAssembler.WithVertices(combined,frames):ProjectSceneAssembler.Combine(frames,light.Color,-light.Direction);
        combined=new(new(combined.Surface.Snapshot.WithLighting(-light.Direction),combined.Surface.Materials,combined.Surface.Profile with{LightAmbient=light.Color,LightSpecular=light.Color}),combined.Outline,combined.OutlineOptions,materials:combined.Materials);
        visible=shown;currentCamera=camera;CurrentFrame=frame;IsSavedPose=saved;
    }
    private static Dictionary<int,PmmOutsideParent> ParentsAt(PmmModel model,uint frame,bool saved)
    {
        IReadOnlyList<PmmOutsideParent> parents;
        if(saved)parents=model.OutsideParentStates.Select(s=>s.Parent).ToArray();
        else{var key=new[]{model.InitialModelKeyframe}.Concat(model.ModelKeyframes).Where(k=>k.Frame<=frame).OrderBy(k=>k.Frame).LastOrDefault();parents=key?.OutsideParents??[];}
        if(parents.Count!=model.OutsideParentSubjectBoneIndices.Count)throw new InvalidDataException("PMM outside-parent slot count mismatch.");
        var result=new Dictionary<int,PmmOutsideParent>();
        for(int i=0;i<parents.Count;i++)
            if(parents[i].ModelIndex>=0||(parents[i].ModelIndex==-2&&model.OutsideParentSubjectBoneIndices[i]>=0))
                result.Add(model.OutsideParentSubjectBoneIndices[i],parents[i]);
        return result;
    }
    private IReadOnlyDictionary<int,OutsideParentTrackWindow>? TrackWindowsAt(int id,PreparedPmmModel prepared,double frame,bool saved)
        => ScenePmmControlState.ComputeTrackWindows(prepared.Source,orderedModelKeys[id],frame,saved,bone=>MapBone(prepared,bone));
    private static int MapBone(PreparedPmmModel model,int index)
    {
        if(index==-1)return -1;
        if((uint)index>=(uint)model.Source.BoneNames.Count)throw new InvalidDataException("PMM bone index is outside the name table.");
        var names=PmmAssetPathResolver.DecodeCandidates(model.Source.BoneNames[index]);
        int[] matches=model.Model.Bones.Select((b,i)=>(b,i)).Where(p=>names.Contains(p.b.Name,StringComparer.Ordinal)).Select(p=>p.i).ToArray();
        if(matches.Length!=1)throw new InvalidDataException("PMM binding bone name is missing or ambiguous in PMX.");return matches[0];
    }
    private static OriginalFrameScene EmptyFrame()
    {
        return new(new(SceneSnapshot.CreateEmpty(),[]),OriginalOutlineData.Empty,
            new(Vector4.One,Matrix4x4.Identity,new(0,0,-1),30,1,false));
    }
    public void Dispose(){if(disposed)return;disposed=true;foreach(var e in entries.Values)e.Controller?.Dispose();legacyPhysicsBackends.Clear();DisposePmmPhysicsWorlds();}
}
