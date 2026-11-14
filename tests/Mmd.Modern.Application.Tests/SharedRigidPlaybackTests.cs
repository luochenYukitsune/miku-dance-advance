using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class SharedRigidPlaybackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true,false,16u,false)]
    [InlineData(false,true,16u,false)]
    [InlineData(true,true,16u,false)]
    [InlineData(false,true,1u,false)]
    [InlineData(true,true,1u,false)]
    [InlineData(false,true,1u,true)]
    [InlineData(true,true,1u,true)]
    public void PureVisualLeavesSkipUnusedMiddleBoneSamplesButKeepDependencyAndSolverHistory(bool shared,bool pmm,uint leafEnd,bool switchParents)
    {
        var model=Model();var layout=new SceneCompositionEditor();var rest=SharedPhysicsFixture.Rest(model);
        var actors=Enumerable.Range(0,4).Select(i=>layout.Add("actor"+i,"leaf-dependency-"+i+".pmx",ModelRole.Character,rest)).ToArray();
        var motions=actors.Select((actor,i)=>new SceneActorMotion(actor.Id,model,Motion(i==3?leafEnd:16),PhysicsEnabled:i is 1 or 2)).ToArray();
        SceneOutsideParentBinding[] parents=pmm?[]:[new(actors[1].Id,0,actors[0].Id,0,Matrix4x4.Identity)];
        var controls=pmm?Controls(actors,model,leafSubject:switchParents):null;
        if(switchParents)controls=controls!.Edit(editor=>editor.PrepareRegister([
            new(new(PmmTrackKind.Model,3,"",1),new PmmModelKeyframe { Visible=true,OutsideParents=[new(0,2,0)] }),
            new(new(PmmTrackKind.Model,3,"",2),new PmmModelKeyframe { Visible=true,OutsideParents=[new(0,-1,-1)] }),
            new(new(PmmTrackKind.Model,3,"",3),new PmmModelKeyframe { Visible=true,OutsideParents=[new(0,0,0)] })]),true);
        var expectedFactory=new Factory();var actualFactory=new Factory();
        using var expected=Create(expectedFactory);using var actual=Create(actualFactory);
        var initialLeaf=actual.GetBoneWorldMatrices(actors[3].Id).ToArray();
        var entries=(Array)typeof(SceneAnimationSession).GetField("entries",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(actual)!;
        var leaf=(CharacterAnimationController)entries.GetValue(3)!.GetType().GetProperty("Controller")!.GetValue(entries.GetValue(3))!;
        var leafWorlds=leaf.BoneWorldMatrices;var middleSamples=new List<bool>();
        actualFactory.Worlds[0].BeforeStep=()=>middleSamples.Add(ReferenceEquals(leafWorlds,leaf.BoneWorldMatrices));
        foreach(var backend in expectedFactory.Worlds)backend.Clear();
        foreach(var backend in actualFactory.Worlds)backend.Clear();
        double elapsed=8*CharacterPhysicsSession.FixedStepSeconds;
        var reference=FullBoundaryReference(expected,elapsed);
        if(switchParents)
        {
            var referenceEntries=(Array)typeof(SceneAnimationSession).GetField("entries",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(expected)!;
            var referenceLeaf=(CharacterAnimationController)referenceEntries.GetValue(3)!.GetType().GetProperty("Controller")!.GetValue(referenceEntries.GetValue(3))!;
            var completed=referenceLeaf.SampleAt(leafEnd).Surface.Snapshot;
            // A full boundary's displayed geometry must include detaching its final slot,
            // independently of whether the optimized path reaches the same bones.
            AssertBitsEqual(completed.Vertices.ToArray().Select(v=>v.Position),reference.Surface.Snapshot.Vertices.ToArray().Skip(9).Select(v=>v.Position));
        }
        actual.Advance(elapsed,Token);
        Assert.Equal(8,middleSamples.Count);
        Assert.All(middleSamples.Take(leafEnd==1?3:7),same=>Assert.True(same,"A display-only leaf rebuilt its unused intermediate bone palette."));
        Assert.False(middleSamples.Last());
        AssertBitsEqual(expected.GetBoneWorldMatrices(actors[3].Id),actual.GetBoneWorldMatrices(actors[3].Id));
        AssertSpanBitsEqual<SceneVertex>(reference.Surface.Snapshot.Vertices,actual.Frame.Surface.Snapshot.Vertices);
        AssertBitsEqual(reference.Outline!.Vertices,actual.Frame.Outline!.Vertices);
        for(int i=0;i<actors.Length;i++)AssertBitsEqual(expected.GetBoneWorldMatrices(actors[i].Id),actual.GetBoneWorldMatrices(actors[i].Id));
        for(int i=0;i<actualFactory.Worlds.Count;i++)
        {
            Assert.Equal(expectedFactory.Worlds[i].Boundaries,actualFactory.Worlds[i].Boundaries);
            Assert.Equal(expectedFactory.Worlds[i].Targets,actualFactory.Worlds[i].Targets);
            Assert.Equal(expectedFactory.Worlds[i].Impulses,actualFactory.Worlds[i].Impulses);
        }
        Assert.NotEqual(initialLeaf[0],actual.GetBoneWorldMatrices(actors[3].Id)[0]);Assert.False(actual.IsFaulted);
        // Sampling its tail once must leave a valid completed anchor for later advances.
        actualFactory.Worlds[0].BeforeStep=null;
        foreach(double delta in new[] {CharacterPhysicsSession.FixedStepSeconds,1.0/30,.1})
        {
            var nextReference=FullBoundaryReference(expected,delta);actual.Advance(delta,Token);
            AssertSpanBitsEqual<SceneVertex>(nextReference.Surface.Snapshot.Vertices,actual.Frame.Surface.Snapshot.Vertices);
            AssertBitsEqual(nextReference.Outline!.Vertices,actual.Frame.Outline!.Vertices);
            for(int i=0;i<actors.Length;i++)AssertBitsEqual(expected.GetBoneWorldMatrices(actors[i].Id),actual.GetBoneWorldMatrices(actors[i].Id));
            for(int i=0;i<actualFactory.Worlds.Count;i++)
            {
                Assert.Equal(expectedFactory.Worlds[i].Boundaries,actualFactory.Worlds[i].Boundaries);
                Assert.Equal(expectedFactory.Worlds[i].Targets,actualFactory.Worlds[i].Targets);
                Assert.Equal(expectedFactory.Worlds[i].Impulses,actualFactory.Worlds[i].Impulses);
            }
            Assert.False(actual.IsFaulted);
        }
        SceneAnimationSession Create(Factory factory)=>new(layout.Objects,motions,_=>factory.New(),
            outsideParents:parents,pmmControls:controls,sharedPhysicsFactory:shared?()=>factory.New():null,cancellationToken:Token);
    }

    [Fact]
    public void ConstantNonphysicalInputsRetainTheCompletePoseAndAdvanceItsCacheClock()
    {
        var model=Model(extraBoneCount:1024); var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName="root",Translation=new(.3f,.2f,.1f)}],
            IkKeyframes=[new() {Frame=16,Visible=true}]};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        var first=controller.SampleAt(0); var bones=controller.BoneWorldMatrices;
        var boneCopy=bones.ToArray();
        var sample=typeof(CharacterAnimationController).GetMethod("SampleRigidInitializationBoneWorlds",BindingFlags.Instance|BindingFlags.NonPublic)!;
        // Resolve reflection and JIT work before measuring the subsequent unchanged boundary.
        sample.Invoke(controller,[.25,true,true]);
        long before=GC.GetAllocatedBytesForCurrentThread();
        sample.Invoke(controller,[.5,true,true]);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(allocated<32*1024,$"A constant nonphysical boundary rebuilt its bone/IK palette: {allocated} bytes.");
        Assert.Same(bones,controller.BoneWorldMatrices); AssertBitsEqual(boneCopy,bones);
        var frame=typeof(CharacterAnimationController).GetField("sampledFrame",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.Equal(.5,(double?)frame.GetValue(controller));
        var last=controller.SampleAt(.5); Assert.Same(first.Surface,last.Surface); Assert.Same(first.Outline,last.Outline);
    }

    [Theory]
    [InlineData("morph")]
    [InlineData("ik")]
    [InlineData("parent")]
    public void ConstantBoneTracksStillInvalidateForMorphIkAndOutsideParentChanges(string changedInput)
    {
        var model=Model(); var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName="root"}],
            MorphKeyframes=changedInput=="morph"?[new() {MorphName="bone"},new() {MorphName="bone",Frame=16,Weight=.8f}]:[],
            IkKeyframes=changedInput=="ik"?[new() {Visible=true,Switches=[new("goal",true)]},
                new() {Frame=1,Visible=false,Switches=[new("goal",false)]},
                new() {Frame=2,Visible=true,Switches=[new("goal",true)]}]:[new() {Frame=16,Visible=true}]};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        controller.SampleAt(0);
        var retain=typeof(CharacterAnimationController).GetMethod("TryRetainCompleteRigidPlaybackPose",BindingFlags.Instance|BindingFlags.NonPublic)!;
        foreach(double clock in new[] {.5,1,2,3})
        {
            IReadOnlyDictionary<int,Matrix4x4>? parents=changedInput=="parent"
                ?new Dictionary<int,Matrix4x4> {[0]=Matrix4x4.CreateTranslation((float)clock,1,0)}:null;
            controller.SetOutsideParents(parents);
            Assert.False((bool)retain.Invoke(controller,[clock,true,true])!);
            var actual=controller.SampleAt(clock);
            using var fresh=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
            fresh.SetOutsideParents(parents); var expected=fresh.SampleAt(clock);
            AssertSpanBitsEqual<SceneVertex>(expected.Surface.Snapshot.Vertices,actual.Surface.Snapshot.Vertices);
            AssertBitsEqual(fresh.BoneWorldMatrices,controller.BoneWorldMatrices);
            Assert.Equal(fresh.IsVisible,controller.IsVisible);
        }
    }

    [Fact]
    public void DisablingIkWithoutASwitchTrackInvalidatesTheRetainedCompletePose()
    {
        var model=Model(); var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName="root"}],IkKeyframes=[new() {Frame=16,Visible=true}]};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        controller.SampleAt(0); var old=controller.BoneWorldMatrices; var oldCopy=old.ToArray();
        var retain=typeof(CharacterAnimationController).GetMethod("TryRetainCompleteRigidPlaybackPose",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.False((bool)retain.Invoke(controller,[.5,false,true])!);
        var actual=controller.SampleAt(.5,enableIk:false);
        using var fresh=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        var expected=fresh.SampleAt(.5,enableIk:false);
        AssertSpanBitsEqual<SceneVertex>(expected.Surface.Snapshot.Vertices,actual.Surface.Snapshot.Vertices);
        AssertBitsEqual(fresh.BoneWorldMatrices,controller.BoneWorldMatrices);
        AssertBitsEqual(oldCopy,old);
    }

    [Fact]
    public void RetainedConstantPoseStillSamplesVisibilityAndTheExactLastFrame()
    {
        var model=Model(); var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName="root"}],
            IkKeyframes=[new() {Visible=true},new() {Frame=1,Visible=false},new() {Frame=2,Visible=true}]};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        controller.SampleAt(0); var worlds=controller.BoneWorldMatrices;
        var sample=typeof(CharacterAnimationController).GetMethod("SampleRigidInitializationBoneWorlds",BindingFlags.Instance|BindingFlags.NonPublic)!;
        sample.Invoke(controller,[1.0,true,true]); Assert.False(controller.IsVisible);
        sample.Invoke(controller,[2.0,true,true]); Assert.True(controller.IsVisible);
        var frame=typeof(CharacterAnimationController).GetField("sampledFrame",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.Equal((double)controller.DurationFrames,(double?)frame.GetValue(controller));
        Assert.Same(worlds,controller.BoneWorldMatrices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CappedPlaybackCrossingAttachedActorsLastFrameDoesNotFaultTheNextAdvance(bool shared)
    {
        var model=Model(); var layout=new SceneCompositionEditor(); var rest=SharedPhysicsFixture.Rest(model);
        var actors=Enumerable.Range(0,2).Select(i=>layout.Add("actor"+i,"playback-cap-end-"+i+".pmx",ModelRole.Character,rest)).ToArray();
        var motions=new[] {new SceneActorMotion(actors[0].Id,model,Motion(16),PhysicsEnabled:false),
            new SceneActorMotion(actors[1].Id,model,Motion(3))};
        var factory=new Factory();
        using var session=new SceneAnimationSession(layout.Objects,motions,_=>factory.New(),
            sharedPhysicsFactory:shared?()=>factory.New():null,pmmControls:Controls(actors,model),cancellationToken:Token);
        session.Advance(.1,Token);
        Assert.Equal(3,session.CurrentFrame);
        Assert.All(factory.Worlds,world=>Assert.Equal(CharacterPhysicsSession.MaximumSubsteps,world.Boundaries.Count));
        var failure=Record.Exception(()=>session.Advance(CharacterPhysicsSession.FixedStepSeconds,Token));
        Assert.True(failure is null,$"A valid capped playback sequence poisoned the live session: IsFaulted={session.IsFaulted}; original exception={failure}");
        Assert.False(session.IsFaulted); Assert.Equal(3.25,session.CurrentFrame);
        Assert.All(factory.Worlds,world=>Assert.Equal(CharacterPhysicsSession.MaximumSubsteps+1,world.Boundaries.Count));
    }

    [Fact]
    public void ConstantMorphInputsShareOneImmutablePreparationAcrossMovingClocksAndOutsideParents()
    {
        var model=Model(); var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=Motion().BoneKeyframes,
            MorphKeyframes=new[] {"vertex","bone","uv","impulse"}.SelectMany(name=>new[] {
                new VmdMorphKeyframe {MorphName=name,Weight=.4f},
                new VmdMorphKeyframe {MorphName=name,Frame=16,Weight=.4f}}).ToArray()};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        controller.SampleAt(.25); var channels=controller.MorphChannels;
        var oldBones=controller.BoneWorldMatrices; var oldBoneBytes=oldBones.ToArray();
        controller.SetOutsideParents(new Dictionary<int,Matrix4x4> {[0]=Matrix4x4.CreateTranslation(3,2,1)});
        controller.SampleAt(.5);
        Assert.Same(channels,controller.MorphChannels);
        Assert.NotEqual(oldBones[0],controller.BoneWorldMatrices[0]);
        AssertBitsEqual(oldBoneBytes,oldBones);
        controller.SampleAt(.75,enableMorphs:false); Assert.NotSame(channels,controller.MorphChannels);
        var disabled=controller.MorphChannels;
        controller.SampleAt(1,enableMorphs:false); Assert.Same(disabled,controller.MorphChannels);
        controller.SampleAt(1.25); Assert.NotSame(disabled,controller.MorphChannels);
    }

    [Fact]
    public void IntermediatePlaybackBoundariesDoNotAllocateVertexSizedDisplayProducts()
    {
        const int vertexCount=20000;
        var model=Model(vertexCount); var layout=new SceneCompositionEditor(); var rest=SharedPhysicsFixture.Rest(model);
        var actors=Enumerable.Range(0,3).Select(i=>layout.Add("actor"+i,"playback-allocation-"+i+".pmx",ModelRole.Character,rest)).ToArray();
        var motions=actors.Select((actor,i)=>new SceneActorMotion(actor.Id,model,Motion(i==2?1u:16u),PhysicsEnabled:i!=1,MorphsEnabled:false)).ToArray();
        SceneOutsideParentBinding[] parents=[new(actors[1].Id,0,actors[0].Id,0,Matrix4x4.Identity),
            new(actors[2].Id,0,actors[1].Id,0,Matrix4x4.Identity)];
        var factory=new Factory();
        using var owner=new SceneAnimationSession(layout.Objects,motions,_=>throw new InvalidOperationException(),
            sharedPhysicsFactory:()=>factory.New(),outsideParents:parents,cancellationToken:Token);
        owner.Advance(8*CharacterPhysicsSession.FixedStepSeconds,Token);
        var retained=owner.Frame; var retainedVertices=retained.Surface.Snapshot.Vertices.ToArray();
        var backend=Assert.Single(factory.Worlds); backend.Clear();
        owner.Advance(8*CharacterPhysicsSession.FixedStepSeconds,Token);
        Assert.Equal(8,backend.BoundaryAllocatedBytes.Count);
        // Every 120Hz bone/IK/physics/parent boundary must still execute. Seven middle
        // step-to-step intervals should have no 20000-vertex surface/outline products.
        // Final preparation is excluded because it must materialize the nonphysical parent.
        for(int i=0;i+2<backend.BoundaryAllocatedBytes.Count;i++)
        {
            long bytes=backend.BoundaryAllocatedBytes[i+1]-backend.BoundaryAllocatedBytes[i];
            Assert.True(bytes<vertexCount*24L,$"Playback middle boundary {i+1} allocated vertex-sized display: {bytes} bytes.");
        }
        AssertSpanBitsEqual<SceneVertex>(retainedVertices,retained.Surface.Snapshot.Vertices);
        Assert.Equal(4,owner.CurrentFrame); Assert.False(owner.IsFaulted);
    }

    [Theory]
    [InlineData(true,false,.1,0)]
    [InlineData(true,false,.09375,0)]
    [InlineData(false,true,.1,0)]
    [InlineData(false,true,.09375,0)]
    [InlineData(true,true,.1,0)]
    [InlineData(true,true,.09375,0)]
    [InlineData(true,true,.1,.1)]
    [InlineData(true,true,.09375,.1)]
    public void BatchedPlaybackMatchesEveryLegacyFullBoundaryAndFinalExactFrame(bool shared,bool pmm,double elapsed,double initialFrame)
    {
        var model=Model(); var layout=new SceneCompositionEditor(); var rest=SharedPhysicsFixture.Rest(model);
        var actors=Enumerable.Range(0,4).Select(i=>layout.Add("actor"+i,"playback-oracle-"+i+".pmx",ModelRole.Character,rest)).ToArray();
        for(int i=0;i<actors.Length;i++) layout.SetTransform(actors[i].Id,new(i*3,i,0),Quaternion.CreateFromAxisAngle(Vector3.UnitY,.1f*i),1);
        var motions=actors.Select((actor,i)=>new SceneActorMotion(actor.Id,model,Motion(i>=2?1u:16u),PhysicsEnabled:i!=1)).ToArray();
        SceneOutsideParentBinding[] parents=pmm?[]:[new(actors[1].Id,0,actors[0].Id,0,Matrix4x4.Identity),
            new(actors[2].Id,0,actors[1].Id,0,Matrix4x4.Identity)];
        var controls=pmm?Controls(actors,model):null;
        if(controls is not null) controls=controls.Edit(editor=>editor.PrepareRegister([
            new(new(PmmTrackKind.Model,2,"",2),new PmmModelKeyframe { Visible=true,OutsideParents=[new(0,0,0)] }),
            new(new(PmmTrackKind.Bone,0,"root",1),new PmmBoneKeyframe { Rotation=Quaternion.Identity,
                Interpolation=Enumerable.Repeat(new byte[] {20,20,107,107},4).SelectMany(v=>v).ToArray(),PhysicsDisabled=false }),
            new(new(PmmTrackKind.Gravity,0,"",0),new PmmGravityKeyframe { Acceleration=1,Direction=-Vector3.UnitY,NoiseEnabled=true,Noise=2 }),
            new(new(PmmTrackKind.Gravity,0,"",4),new PmmGravityKeyframe { Acceleration=3,Direction=Vector3.UnitX,NoiseEnabled=true,Noise=2 })]),true);
        var expectedFactory=new Factory(); var actualFactory=new Factory();
        using var expected=Create(expectedFactory); using var actual=Create(actualFactory);
        var previous=actual.Frame; var previousVertices=previous.Surface.Snapshot.Vertices.ToArray();
        var previousBones=actors.Select(actor=>actual.GetBoneWorldMatrices(actor.Id)).ToArray();
        var previousBoneCopies=previousBones.Select(b=>b.ToArray()).ToArray();
        var authored=controls is null?null:PmmWriter.ToBytes(controls.ExportDocument());
        foreach(var backend in expectedFactory.Worlds) backend.Clear();
        foreach(var backend in actualFactory.Worlds) backend.Clear();
        var reference=FullBoundaryReference(expected,elapsed);
        actual.Advance(elapsed,Token);
        Assert.Equal(initialFrame+elapsed*30,actual.CurrentFrame);
        AssertSpanBitsEqual<SceneVertex>(reference.Surface.Snapshot.Vertices,actual.Frame.Surface.Snapshot.Vertices);
        AssertBitsEqual(reference.Outline!.Vertices,actual.Frame.Outline!.Vertices);
        Assert.Equal(reference.Materials,actual.Frame.Materials);
        for(int i=0;i<actors.Length;i++)
        {
            AssertBitsEqual(expected.GetBoneWorldMatrices(actors[i].Id),actual.GetBoneWorldMatrices(actors[i].Id));
            AssertBitsEqual(previousBoneCopies[i],previousBones[i]);
        }
        Assert.Equal(expectedFactory.Worlds.Count,actualFactory.Worlds.Count);
        for(int i=0;i<actualFactory.Worlds.Count;i++)
        {
            var a=expectedFactory.Worlds[i]; var b=actualFactory.Worlds[i];
            Assert.Equal(a.Boundaries,b.Boundaries); Assert.Equal(a.Targets,b.Targets); Assert.Equal(a.Impulses,b.Impulses);
            AssertBitsEqual(a.Inner.Bodies.Select(body=>body.Transform),b.Inner.Bodies.Select(body=>body.Transform));
            AssertBitsEqual(a.Inner.Bodies.Select(body=>body.Velocity),b.Inner.Bodies.Select(body=>body.Velocity));
            Assert.Equal(a.Inner.Bodies.Select(body=>body.Kinematic),b.Inner.Bodies.Select(body=>body.Kinematic));
        }
        AssertSpanBitsEqual<SceneVertex>(previousVertices,previous.Surface.Snapshot.Vertices);
        if(authored is not null) Assert.Equal(authored,PmmWriter.ToBytes(actual.PmmControls!.ExportDocument()));
        Assert.False(actual.IsFaulted); Assert.False(actual.CanUndoOutsideParent); Assert.False(actual.CanRedoOutsideParent);
        SceneAnimationSession Create(Factory factory)=>new(layout.Objects,motions,_=>factory.New(),
            outsideParents:parents,pmmControls:controls,sharedPhysicsFactory:shared?()=>factory.New():null,
            initialFrame:initialFrame,cancellationToken:Token);
    }

    // Independent orchestration oracle for a single advance: run the existing
    // full boundary at the exact same physical clocks and final remainder as the public batch.
    // Public one-step Advance would insert extra nonphysical reflections at fractional offsets;
    // those would be a different history. No production switch/test-only flag is required.
    private static OriginalFrameScene FullBoundaryReference(SceneAnimationSession session,double elapsed)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(SceneAnimationSession);
        var entries=(Array)((Array)type.GetField("entries",flags)!.GetValue(session)!).Clone();
        var world=type.GetField("sharedWorld",flags)!.GetValue(session);
        var boundary=type.GetMethod("EvaluateSharedBoundary",flags)!;
        double frame=Math.Max(session.CurrentFrame,Math.Min(session.DurationFrames,session.CurrentFrame+elapsed*30));
        double effectiveElapsed=(frame-session.CurrentFrame)/30;
        double available=(double)type.GetField("sharedAccumulator",flags)!.GetValue(session)!+effectiveElapsed;
        int requested=(int)Math.Floor((available+1e-12)/CharacterPhysicsSession.FixedStepSeconds);
        int steps=Math.Min(CharacterPhysicsSession.MaximumSubsteps,requested);
        double sample=(double)type.GetField("sharedSampleFrame",flags)!.GetValue(session)!;
        for(int i=0;i<steps;i++)
        {
            double next=sample+CharacterPhysicsSession.FixedStepSeconds*30;
            boundary.Invoke(session,[entries,world,next,sample,false,true,Token,false,null,null,null,false,null]); sample=next;
        }
        // Preserve the established bounded catch-up contract: skipped substeps advance the
        // sample clock without solving. Reflect final animation against that held physical
        // state; this must not invent the two solver steps that a 0.1-second request drops.
        double solved=sample;
        if(requested>steps) sample+=(requested-steps)*CharacterPhysicsSession.FixedStepSeconds*30;
        bool crossedEnd=sample>solved&&entries.Cast<object>().Any(entry=>
            entry.GetType().GetProperty("Controller")!.GetValue(entry) is CharacterAnimationController controller
            &&controller.DurationFrames>solved&&controller.DurationFrames<=sample);
        if(frame!=sample || steps==0 || requested>steps || crossedEnd)
            boundary.Invoke(session,[entries,world,frame,crossedEnd?solved:sample,false,false,Token,false,null,null,null,false,null]);
        var composed=(OriginalFrameScene)type.GetMethod("Compose",flags)!.Invoke(session,[entries,session.Frame])!;
        type.GetField("entries",flags)!.SetValue(session,entries);
        type.GetField("published",flags)!.SetValue(session,composed);
        type.GetField("sharedSampleFrame",flags)!.SetValue(session,sample);
        type.GetField("sharedAccumulator",flags)!.SetValue(session,Math.Max(0,available-requested*CharacterPhysicsSession.FixedStepSeconds));
        type.GetProperty("CurrentFrame")!.SetValue(session,frame);
        return composed;
    }

    private static void AssertBoneBitsEqual(IEnumerable<KeyValuePair<int, Matrix4x4>> expected,
        IEnumerable<KeyValuePair<int, Matrix4x4>> actual)
    {
        var a = expected.OrderBy(pair => pair.Key).ToArray(); var b = actual.OrderBy(pair => pair.Key).ToArray();
        Assert.Equal(a.Select(pair => pair.Key), b.Select(pair => pair.Key));
        AssertBitsEqual(a.Select(pair => pair.Value), b.Select(pair => pair.Value));
    }

    // Byte comparison preserves negative zero and NaN payload distinctions hidden by float.Equals.
    // These public value types contain only numeric fields; all owned snapshots remain retained.
    private static void AssertBitsEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : unmanaged
    {
        var a = expected.ToArray(); var b = actual.ToArray();
        Assert.Equal(MemoryMarshal.AsBytes(a.AsSpan()).ToArray(), MemoryMarshal.AsBytes(b.AsSpan()).ToArray());
    }

    private static void AssertSpanBitsEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual) where T : unmanaged
        => Assert.Equal(MemoryMarshal.AsBytes(expected).ToArray(), MemoryMarshal.AsBytes(actual).ToArray());

    private static VmdMotion Motion(uint end = 16) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = end, Translation = new(2, 1, -.5f) }],
        MorphKeyframes = [new() { MorphName = "vertex", Frame = end, Weight = .4f }, new() { MorphName = "bone", Frame = end, Weight = .7f }, new() { MorphName = "uv", Frame = end, Weight = .6f }, new() { MorphName = "impulse", Frame = end, Weight = .2f }]
    };

    private static PmxModel Model(int vertexCount = 3,int extraBoneCount=0)
    {
        var basic = SharedPhysicsFixture.Model(PmxRigidBodyOperation.Dynamic);
        PmxBone Bone(string name, Vector3 position, int parent, PmxBoneFlags flags = PmxBoneFlags.None) =>
            new() { Name = name, EnglishName = "", Position = position, ParentIndex = parent, DeformDepth = 0, Flags = flags };
        return new()
        {
            Header = basic.Header, Name = basic.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Textures = basic.Textures, Faces = basic.Faces, Materials = basic.Materials, DisplayFrames = [], Joints = [], SoftBodies = [],
            RigidBodies = basic.RigidBodies,
            Bones = new[] {Bone("root", Vector3.Zero, -1), Bone("link", Vector3.Zero, 0), Bone("tip", Vector3.UnitX, 1),
                new() { Name = "goal", EnglishName = "", Position = Vector3.UnitY, ParentIndex = 0, DeformDepth = 1,
                    Flags = PmxBoneFlags.DeformAfterPhysics | PmxBoneFlags.Ik, IkTargetBoneIndex = 2,
                    IkIterationCount = 16, IkLimit = MathF.PI, IkLinks = [new(1, false, Vector3.Zero, Vector3.Zero)] },
                new() { Name = "grant", EnglishName = "", Position = new(2, 0, 0), ParentIndex = 0, DeformDepth = 2,
                    Flags = PmxBoneFlags.DeformAfterPhysics | PmxBoneFlags.AppendTranslate | PmxBoneFlags.AppendRotate,
                    AppendBoneIndex = 1, AppendWeight = .3f }}
                .Concat(Enumerable.Range(0,extraBoneCount).Select(index=>Bone("static"+index,Vector3.UnitZ,0))).ToArray(),
            Vertices = Enumerable.Range(0, vertexCount).Select(index => basic.Vertices[index % basic.Vertices.Count]).Select(vertex => new PmxVertex
            { Position = vertex.Position, Normal = vertex.Normal, Uv = vertex.Uv, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef2,
                BoneIndices = [2, 4, -1, -1], BoneWeights = [.3f, .7f, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [new() { Name = "vertex", EnglishName = "", ControlPanel = 4, Type = PmxMorphType.Position,
                    PositionOffsets = [new(0, new(.1f, .2f, .3f))] },
                new() { Name = "bone", EnglishName = "", ControlPanel = 4, Type = PmxMorphType.Bone,
                    BoneOffsets = [new(4, new(.1f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f))] },
                new() { Name = "uv", EnglishName = "", ControlPanel = 4, Type = PmxMorphType.Uv,
                    UvOffsets = [new(0, new(.2f, .3f, 0, 0))] },
                new() { Name = "impulse", EnglishName = "", ControlPanel = 4, Type = PmxMorphType.Impulse,
                    ImpulseOffsets = [new(0, false, new(.03f, .02f, 0), Vector3.Zero)] }]
        };
    }

    private static ScenePmmControlState Controls(EditableSceneObject[] actors, PmxModel model,bool leafSubject=false)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var models = actors.Select((actor, i) => new PmmModel
        {
            Index = (byte)i, Name = S(actor.Name), Path = S("actor" + i + ".pmx"), BoneNames = model.Bones.Select(bone => S(bone.Name)).ToArray(),
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 1,
            OutsideParentSubjectBoneIndices = i is 1 or 2 ||leafSubject&&i==3 ? [0] : [],
            OutsideParentStates = i is 1 or 2 ? [new(0, 0, int.MaxValue, new(0, i - 1, 0))] : leafSubject&&i==3?[new(0,0,int.MaxValue,new(0,-1,-1))]:[],
            InitialBoneKeyframes = model.Bones.Select((bone, index) => new PmmBoneKeyframe { ObjectIndex = index,
                Rotation = Quaternion.Identity, Interpolation = linear, PhysicsDisabled = i == 0 && index == 0 }).ToArray(),
            InitialModelKeyframe = new() { Visible = true, OutsideParents = i is 1 or 2 ? [new(0, i - 1, 0)] : leafSubject&&i==3?[new(0,-1,-1)]:[] },
            BoneStates = model.Bones.Select((bone, index) => new PmmBoneState(index, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray()
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30, Models = models,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        return ScenePmmControlState.Create(document, actors.Select((actor, index) => new ScenePmmActorSource(actor.Id, (byte)index, model)));
    }

    private sealed class Factory
    {
        internal readonly List<RecordingBackend> Worlds = [];
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 0);
        internal RecordingBackend New() => Create();
        private RecordingBackend Create() { var world = new RecordingBackend(); Worlds.Add(world); return world; }
    }

    private sealed class RecordingBackend : ICharacterPhysicsMutableBackend
    {
        internal readonly TrackingSharedBackend Inner = new();
        internal readonly List<string> Boundaries = [], Targets = [];
        internal readonly List<long> BoundaryAllocatedBytes = [];
        internal int Impulses;
        internal Action? BeforeStep;
        public string Name => "interactive deferred skin orchestration probe";
        public IReadOnlyList<string> Diagnostics => [];
        internal void Clear() { Boundaries.Clear(); Targets.Clear(); BoundaryAllocatedBytes.Clear(); Impulses = 0; }
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => Inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => Inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => Inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => Inner.RemoveBody(index);
        public void RemoveJoint(int index) => Inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => Inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool state) => Inner.SetBodyKinematic(index, state);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds)
        { Targets.Add(index + ":" + Bits(transform) + ":" + BitConverter.SingleToInt32Bits(seconds)); Inner.SetKinematicTarget(index, transform, seconds); }
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => Inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) { Impulses++; Inner.ApplyImpulse(index, linear, angular); }
        public void Step(float seconds)
        {
            BeforeStep?.Invoke();
            BoundaryAllocatedBytes.Add(GC.GetAllocatedBytesForCurrentThread());
            Boundaries.Add(BitConverter.SingleToInt32Bits(seconds) + ":" + Bits(Matrix4x4.CreateTranslation(Inner.Gravity)) + ":"
                + string.Join("|", Inner.Bodies.Select(body => Bits(body.Transform) + ":" + Bits(Matrix4x4.CreateTranslation(body.Velocity))
                    + ":" + body.Kinematic + ":" + body.Removed)));
            Inner.Step(seconds);
        }
        private static string Bits(Matrix4x4 value) => string.Join(",", new[]
            { value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
                value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44 }.Select(BitConverter.SingleToInt32Bits));
        public void Dispose() => Inner.Dispose();
    }
}
