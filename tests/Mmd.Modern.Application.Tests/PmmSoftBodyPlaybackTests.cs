using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmSoftBodyPlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoupledPmmSoftOnlyActorsUseActualOwnerBoundariesAndFreezeInPlace(bool shared)
    {
        var owners = new List<CoupledSoftBodyTests.Owner>();
        using var playback = Playback(shared, false, null!, () => { var b = new CoupledSoftBodyTests.Owner(); owners.Add(b); return b; }, coupled: true);
        playback.Seek(0); var active = owners.Where(o => !o.Disposed).ToArray();
        playback.Advance(1, 1d / 30); playback.Advance(2, 1d / 30);
        Assert.Equal(shared ? 1 : 2, active.Length);
        Assert.All(active, owner => Assert.Equal(8, owner.Inner.Steps));
        var bindings = active.SelectMany(owner => owner.Bindings).Where(b => !b.Disposed).ToArray();
        Assert.Equal(2, bindings.Length); Assert.True(bindings[0].Frozen); Assert.False(bindings[1].Frozen);
        var before = playback.Frame;
        playback.SetPhysics(false); Assert.All(active, owner => Assert.True(owner.Disposed));
        playback.SetPhysics(true); Assert.True(playback.PhysicsEnabled); Assert.NotSame(before, playback.Frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoupledSavedToTimelineCandidateFailurePreservesPublishedOwners(bool shared)
    {
        bool fail = false; var owners = new List<CoupledSoftBodyTests.Owner>();
        using var playback = Playback(shared, true, null!, () => { var b = new CoupledSoftBodyTests.Owner { FailBinding=fail }; owners.Add(b); return b; }, coupled:true);
        var before = playback.Frame; var active = owners.ToArray(); fail=true;
        Assert.ThrowsAny<Exception>(() => playback.Seek(0));
        Assert.Same(before, playback.Frame); Assert.True(playback.IsSavedPose);
        Assert.All(active, owner => Assert.False(owner.Disposed));
        Assert.All(owners.Except(active), owner => Assert.True(owner.Disposed));
        fail=false; playback.Seek(0); playback.Advance(.25,1d/120);
        Assert.All(active, owner => Assert.True(owner.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoupledPmmCandidateCannotInitializeOrDisposeAnActiveOwner(bool shared)
    {
        bool reuse = false; var owners = new List<CoupledSoftBodyTests.Owner>();
        using var playback = Playback(shared, true, null!, () =>
        {
            if (reuse) return owners.Last(owner => !owner.Disposed);
            var candidate = new CoupledSoftBodyTests.Owner(); owners.Add(candidate); return candidate;
        }, coupled: true);
        playback.Seek(0); var active = owners.Where(owner => !owner.Disposed).ToArray();
        var before = playback.Frame; reuse = true;
        Assert.Throws<InvalidOperationException>(() => playback.Reset());
        Assert.Same(before, playback.Frame); Assert.All(active, owner => Assert.False(owner.Disposed));
        playback.Advance(.25, 1d / 120);
        Assert.All(active, owner => Assert.Equal(1, owner.Inner.Steps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoupledFrozenParentRebasesNativeNodesWithoutReplacingBindings(bool shared)
    {
        var owners=new List<CoupledSoftBodyTests.Owner>(); var parents=new List<PmmOutsideParent>{new(0,-1,-1)};
        using var playback=Playback(shared,false,null!,()=>{var b=new CoupledSoftBodyTests.Owner();owners.Add(b);return b;},
            outsideParents:parents,parentTranslation:new(4,0,0),coupled:true);
        playback.Seek(0); playback.Advance(1,1d/30);
        var binding=owners.Where(o=>!o.Disposed).SelectMany(o=>o.Bindings).First(b=>!b.Disposed);
        var before=binding.Targets[4]; parents[0]=new(0,1,0);
        playback.Advance(2,1d/30);
        Assert.False(binding.Disposed); Assert.True(binding.Frozen);
        Assert.InRange(Vector3.Distance(before+new Vector3(2,0,0),binding.Targets[4]),0,3e-5f);
        parents[0]=new(0,-1,-1); playback.Advance(3,1d/30);
        Assert.InRange(Vector3.Distance(before,binding.Targets[4]),0,3e-5f);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void ActualCoupledPmmClothFallsAndSeekRestoresTheSingleWorld(bool shared,bool rigid)
    {
        using var playback=Playback(shared,rigid,null!,()=>new BulletCharacterPhysicsBackend(Vector3.Zero,null,coupledSoftBodies:true),
            shortEnd:60,longEnd:60,coupled:true);
        playback.Seek(0);
        for(int i=1;i<=16;i++)playback.Advance(i*.25,1d/120);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[4].Position.Y<2.99f);
        playback.Seek(0); Assert.Equal(3,playback.Frame.Surface.Snapshot.Vertices[4].Position.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PmmSoftOnlyUsesFixedBoundariesHoldsReflectionAndFreezesShortTail(bool shared)
    {
        var solvers = new Dictionary<int, RecordingSoft>();
        using var playback = Playback(shared, false, m => solvers[m.Source.Index] = new());
        playback.Seek(0); var active = solvers.Values.ToArray();
        int[] resets = active.Select(s => s.Resets).ToArray();
        playback.Advance(.125, 1d / 240); Assert.All(active, s => Assert.Equal(0, s.Steps));
        playback.Advance(.25, 1d / 240); Assert.All(active, s => Assert.Equal(1, s.Steps));
        Assert.Equal(resets, active.Select(s => s.Resets).ToArray());
        playback.Advance(1, 3d / 120); playback.Advance(2, 1d / 30);
        Assert.Equal(4, solvers[0].Steps); Assert.Equal(8, solvers[1].Steps);
        playback.Seek(0); Assert.Equal(resets.Select(n => n + 1), active.Select(s => s.Resets));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoftReceivesActualRigidBodiesAndOneSceneGravityAtSameBoundary(bool shared)
    {
        var rigid = new List<TrackingSharedBackend>(); var soft = new Dictionary<int, RecordingSoft>();
        using var playback = Playback(shared, true, m => soft[m.Source.Index] = new(), () => { var b = new TrackingSharedBackend(); rigid.Add(b); return b; });
        playback.Seek(0); playback.Advance(.25, 1d / 120);
        Assert.All(soft.Values, s => { Assert.Equal(1, s.Steps); Assert.Single(s.Rigids); });
        Assert.All(rigid, b => Assert.All(soft.Values, s => Assert.Equal(b.Gravity, s.Gravity)));
        var bodies = rigid.SelectMany(b => b.Bodies).Where(b => !b.Removed).ToArray();
        Assert.Equal(bodies.Select(b => b.Transform.Translation), soft.Values.Select(s => s.Rigids[0].Translation));
        Assert.Contains(soft.Values, s => s.Rigids[0].Translation.Y < 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PureSoftFailureBlocksAdvanceUntilSeekAndToggleOwnsFreshSolvers(bool shared)
    {
        var solvers = new Dictionary<int, RecordingSoft>();
        using var playback = Playback(shared, false, m => solvers[m.Source.Index] = new());
        playback.Seek(0); var old = solvers.Values.ToArray();
        old[1].FailStep = true;
        Assert.Throws<InvalidOperationException>(() => playback.Advance(.25, 1d / 120));
        int steps = old[0].Steps;
        Assert.Throws<InvalidOperationException>(() => playback.Advance(.5, 1d / 120)); Assert.Equal(steps, old[0].Steps);
        old[1].FailStep = false; playback.Seek(0); playback.Advance(.25, 1d / 120);
        playback.SetPhysics(false); Assert.All(old, s => Assert.Equal(1, s.Disposals));
        playback.SetPhysics(true); Assert.All(solvers.Values, s => Assert.DoesNotContain(s, old));
        playback.Advance(.5, 1d / 120); Assert.All(solvers.Values, s => Assert.Equal(1, s.Steps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedSoftBackendRejectsWithoutGivingSecondControllerOwnership(bool shared)
    {
        var solver = new RecordingSoft();
        Assert.Throws<InvalidOperationException>(() => Playback(shared, false, _ => solver));
        Assert.Equal(1, solver.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoisyGravityIsDrawnOnceForRigidAndSoftWorldsAndSeekReplaysIt(bool shared)
    {
        var rigid = new List<TrackingSharedBackend>(); var soft = new Dictionary<int, RecordingSoft>();
        var gravity = new PmmGravity { Acceleration = 1, Direction = -Vector3.UnitY,
            InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY, NoiseEnabled = true, Noise = 2 } };
        using var playback = Playback(shared, true, m => soft[m.Source.Index] = new(),
            () => { var b = new TrackingSharedBackend(); rigid.Add(b); return b; }, 60, 60, gravity);
        playback.Seek(0); var initial = soft[0].Gravity;
        void AdvanceNoise()
        {
            for (int i = 1; i <= 64; i++)
            {
                playback.Advance(i * .25, 1d / 120);
                Assert.All(soft.Values, s => Assert.Equal(soft[0].Gravity, s.Gravity));
                Assert.All(rigid, r => Assert.Equal(soft[0].Gravity, r.Gravity));
            }
        }
        AdvanceNoise(); var noisy = soft[0].Gravity;
        Assert.NotEqual(initial, noisy);
        int[] resets = soft.Values.Select(s => s.Resets).ToArray();
        playback.Advance(16.125, 1d / 240);
        Assert.Equal(noisy, soft[0].Gravity); Assert.Equal(resets, soft.Values.Select(s => s.Resets));
        playback.Seek(0); Assert.Equal(initial, soft[0].Gravity);
        AdvanceNoise(); Assert.Equal(noisy, soft[0].Gravity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedTimelineCandidateDisposesEveryCandidateAndPreservesSavedControllers(bool shared)
    {
        var solvers = new List<RecordingSoft>();
        using var playback = Playback(shared, false, _ =>
        {
            var s = new RecordingSoft { FailReset = solvers.Count == 3 }; solvers.Add(s); return s;
        });
        var saved = playback.Frame;
        Assert.Throws<InvalidOperationException>(() => playback.Seek(0));
        Assert.True(playback.IsSavedPose); Assert.Same(saved, playback.Frame);
        Assert.All(solvers.Take(2), s => Assert.Equal(0, s.Disposals));
        Assert.All(solvers.Skip(2), s => Assert.Equal(1, s.Disposals));
        playback.Seek(0); playback.Advance(.25, 1d / 120);
        Assert.All(solvers.Take(2), s => Assert.Equal(1, s.Disposals));
        Assert.All(solvers.Skip(4), s => Assert.Equal(1, s.Steps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedEnableKeepsPlaybackDisabledAndDisposesSoftCandidates(bool shared)
    {
        var solvers = new List<RecordingSoft>(); bool fail = false;
        using var playback = Playback(shared, false, m =>
        {
            var s = new RecordingSoft { FailReset = fail && m.Source.Index == 1 }; solvers.Add(s); return s;
        });
        playback.Seek(0); playback.SetPhysics(false); var old = solvers.ToArray(); var disabled = playback.Frame;
        fail = true;
        Assert.Throws<InvalidOperationException>(() => playback.SetPhysics(true));
        Assert.False(playback.PhysicsEnabled); Assert.Same(disabled, playback.Frame);
        Assert.All(solvers, s => Assert.Equal(1, s.Disposals));
        fail = false; playback.SetPhysics(true); playback.Advance(.25, 1d / 120);
        Assert.All(solvers.TakeLast(2), s => { Assert.DoesNotContain(s, old); Assert.Equal(1, s.Steps); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenSoftActorProjectsNewAndRemovedOutsideParentSlotsWithoutResettingNodes(bool shared)
    {
        var soft = new Dictionary<int, RecordingSoft>();
        var parents = new List<PmmOutsideParent> { new(0,-1,-1) };
        using var playback = Playback(shared, false, m => soft[m.Source.Index] = new(), outsideParents: parents,
            parentTranslation: new(4, 0, 0));
        playback.Seek(0); soft[0].SampleOffset = new(.3f, -.2f, .1f); playback.Advance(1, 1d / 30);
        int resets = soft[0].Resets; int steps = soft[0].Steps;
        var published = playback.Frame; var frozenVertices = published.Surface.Snapshot.Vertices.ToArray();
        var frozenNodes = Assert.Single(soft[0].Sample()).Positions.ToArray();
        Assert.Equal(new Vector3(.3f, 2.8f, .1f), frozenVertices[4].Position);
        void CheckProjection(Vector3 translation)
        {
            foreach (int id in new[] { 4, 5, 6 })
            {
                var expected = frozenVertices[id].Position + translation;
                Assert.InRange(Vector3.Distance(expected, playback.Frame.Surface.Snapshot.Vertices[id].Position), 0, 3e-5f);
                Assert.Equal(playback.Frame.Surface.Snapshot.Vertices[id].Position, playback.Frame.Outline!.Vertices[id].Position);
            }
            Assert.Equal(resets, soft[0].Resets); Assert.Equal(steps, soft[0].Steps);
            Assert.Equal(frozenNodes, Assert.Single(soft[0].Sample()).Positions);
            Assert.Equal(frozenVertices, published.Surface.Snapshot.Vertices);
        }
        // Edit an existing PMM slot after the local action has stopped.
        parents[0] = new(0,1,0);
        playback.Advance(2, 1d / 30); CheckProjection(new(2, 0, 0));
        // Fractional display keeps the projected deformation without inventing a solver step.
        playback.Advance(2.125, 1d / 240); CheckProjection(new(2.125f, 0, 0));
        playback.Advance(3, 7d / 240); CheckProjection(new(3, 0, 0));
        parents[0] = new(0,-1,-1);
        playback.Advance(4, 1d / 30); CheckProjection(Vector3.Zero);
        playback.Seek(2); Assert.Equal(resets + 1, soft[0].Resets);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ActualBulletClothFallsAndSeekRestoresNodes(bool shared, bool rigid)
    {
        using var playback = Playback(shared, rigid, _ => new SoftBodyBulletBackend(Vector3.Zero, null),
            factory: () => new BulletCharacterPhysicsBackend(Vector3.Zero, null), shortEnd: 60, longEnd: 60);
        playback.Seek(0);
        Assert.True(playback.SupportsPhysics);
        for (int i = 1; i <= 16; i++) playback.Advance(i * .25, 1d / 120);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[4].Position.Y < 2.99f);
        playback.Seek(0); Assert.Equal(3, playback.Frame.Surface.Snapshot.Vertices[4].Position.Y);
    }

    private static PmmProjectPlayback Playback(bool shared, bool rigid, Func<PreparedPmmModel, ISoftBodyBackend> soft,
        Func<ICharacterPhysicsBackend>? factory = null, int shortEnd = 1, int longEnd = 4,
        PmmGravity? gravity = null, IReadOnlyList<PmmOutsideParent>? outsideParents = null, Vector3? parentTranslation = null, bool coupled = false)
    {
        var original = PmxSoftBodyTests.MaterialFixture();
        var model = new PmxModel { Header=original.Header, Name=original.Name, EnglishName=original.EnglishName,
            Comment=original.Comment,EnglishComment=original.EnglishComment,Vertices=original.Vertices,Faces=original.Faces,
            Textures=original.Textures,Materials=original.Materials,Bones=original.Bones,Morphs=original.Morphs,
            DisplayFrames=original.DisplayFrames,Joints=original.Joints,SoftBodies=original.SoftBodies,
            RigidBodies=rigid ? [new() { Name="body",EnglishName="",BoneIndex=0,Group=0,CollisionGroup=ushort.MaxValue,
                Shape=PmxRigidBodyShape.Sphere,ShapeSize=new(.1f),Translation=new(0,2,0),Rotation=Vector3.Zero,
                Mass=1,TranslationDamping=0,RotationDamping=0,Repulsion=0,Friction=.5f,Operation=PmxRigidBodyOperation.Dynamic }] : [] };
        byte[] curves=Enumerable.Range(0,4).SelectMany(_=>new byte[]{20,20,107,107}).ToArray();
        PmmModel Actor(byte id,int end)=>new() { Index=id,DrawOrderIndex=id,TransformOrderIndex=id,Visible=true,LastFrame=end,
            BoneNames=[new(0,"root",[])],BoneStates=[new(0,Vector3.Zero,Quaternion.Identity,false,false,0)],
            InitialBoneKeyframes=[new(){NextIndex=1,Rotation=Quaternion.Identity,Interpolation=curves}],
            BoneKeyframes=[new(){RecordIndex=1,ObjectIndex=1,Frame=end,Translation=id==1 ? parentTranslation??Vector3.Zero : Vector3.Zero,Rotation=Quaternion.Identity,Interpolation=curves}],
            InitialModelKeyframe=new(){Visible=true, OutsideParents=id==0 ? outsideParents??[] : []},
            OutsideParentSubjectBoneIndices=outsideParents is not null&&id==0 ? [-1] : [],
            OutsideParentStates=outsideParents is not null&&id==0 ? [new(0,0,0,new(0,-1,-1))] : [] };
        var document=new PmmDocument { Models=[Actor(0,shortEnd),Actor(1,longEnd)],OutputWidth=1280,OutputHeight=720,CameraFov=30,
            Settings=new(){PhysicsSimulationMode=1,CameraLookAtModelIndex=-1},
            Gravity=gravity??new(){Acceleration=1,Direction=-Vector3.UnitY,InitialKeyframe=new(){Acceleration=1,Direction=-Vector3.UnitY}},
            Camera=new(){Position=new(0,0,-10),Perspective=true,InitialKeyframe=new(){Distance=-10,Fov=30,Perspective=true,ParentModelIndex=-1,
                Interpolation=Enumerable.Range(0,6).SelectMany(_=>new byte[]{20,20,107,107}).ToArray()}},
            Light=new(){Color=Vector3.One,Direction=-Vector3.UnitY,InitialKeyframe=new(){Color=Vector3.One,Direction=-Vector3.UnitY}} };
        var models=document.Models.Select(m=>new PreparedPmmModel(m,"synthetic.pmx",model,PmmMotionAdapter.Convert(m,model),PmmMotionAdapter.ConvertSavedPose(m,model))).ToList();
        var plan=(PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan),BindingFlags.Instance|BindingFlags.NonPublic,
            null,[document,"synthetic.pmm",models,new List<PmmUnresolvedAsset>(),new List<string>()],null)!;
        var rest=new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One,Matrix4x4.Identity,new(0,0,-10),30,1));
        var ctor=typeof(PmmProjectPlayback).GetConstructors().SingleOrDefault(c=>coupled
            ? c.GetParameters().Any(p=>p.Name=="coupledSoftBodies") : c.GetParameters().Any(p=>p.Name=="softPhysicsFactory"));
        Assert.NotNull(ctor);
        object?[] args=[plan,new Dictionary<int,OriginalFrameScene>{[0]=rest,[1]=rest},
            new Func<Vector3,float?,ICharacterPhysicsBackend>((_,_)=>factory?.Invoke()??new TrackingSharedBackend()),shared,soft];
        if(coupled)args=[..args,true];
        try { return (PmmProjectPlayback)ctor.Invoke(args); }
        catch(TargetInvocationException error) when(error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw; }
    }

    private sealed class RecordingSoft : ISoftBodyBackend
    {
        private SoftBodyDefinition[] definitions=[];private Vector3[] targets=[];
        public int Steps,Resets,Disposals; public bool FailStep,FailReset; public Vector3 Gravity,SampleOffset;public Matrix4x4[] Rigids=[];
        public IReadOnlyList<string> Diagnostics=>[];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> defs,IReadOnlyList<PhysicsBodyDefinition> colliders)=>definitions=defs.ToArray();
        public void Reset(ReadOnlySpan<Vector3> positions,ReadOnlySpan<Matrix4x4> rigids){if(FailReset)throw new InvalidOperationException("injected soft reset");Resets++;targets=positions.ToArray();Rigids=rigids.ToArray();}
        public void Step(float seconds,ReadOnlySpan<Vector3> positions,ReadOnlySpan<Matrix4x4> rigids)
        {if(FailStep)throw new InvalidOperationException("injected soft step");Steps++;targets=positions.ToArray();Rigids=rigids.ToArray();}
        public IReadOnlyList<SoftBodySample> Sample()=>definitions.Select(d=>new SoftBodySample(d.VertexIndices.ToArray(),d.VertexIndices.Select(i=>targets[i]+SampleOffset).ToArray(),new Vector3[d.VertexIndices.Count])).ToArray();
        public void SetGravity(Vector3 value)=>Gravity=value;
        public void Dispose()=>Disposals++;
    }
}
