using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CoupledSoftBodyTests
{
    [Theory]
    [InlineData(PmxRigidBodyOperation.Dynamic)]
    [InlineData(PmxRigidBodyOperation.DynamicAndBoneMerge)]
    public void ActualPmxDynamicNearMovesTheOwnerBodyBeforeBoneReflection(PmxRigidBodyOperation operation)
    {
        var (_, motion) = Fixture(near: true, operation: operation);
        using var controller = new CharacterAnimationController(motion.Model, motion.Motion,
            PmxOriginalStyleAdapter.FromRestPose(motion.Model).Scene, PmxOriginalOutlineAdapter.FromRestPose(motion.Model));
        var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, null, coupledSoftBodies: true);
        controller.EnableCoupledPhysicsAt(backend, 0);
        var result = controller.Advance(.25, 1d / 120);
        Assert.True(controller.UsesCoupledSoftBodies);
        Assert.InRange(Vector3.Distance(backend.GetBodyWorldTransform(0).Translation, result.Surface.Snapshot.Vertices[4].Position), 0, 1e-5f);
        if (operation == PmxRigidBodyOperation.DynamicAndBoneMerge) Assert.Equal(Vector3.Zero, controller.BoneWorldMatrices[0].Translation);
        else Assert.InRange(Vector3.Distance(controller.BoneWorldMatrices[0].Translation,
            backend.GetBodyWorldTransform(0).Translation - new Vector3(0, 2, 0)), 0, 1e-5f);
        controller.ResetPhysics(0);
        Assert.Equal(new Vector3(0, 2, 0), backend.GetBodyWorldTransform(0).Translation);
    }

    [Fact]
    public void CoupledScenePublishesPrePhysicsPinsThenOneOwnerStepAndOverlaysAfterReflection()
    {
        var (layout, motion) = Fixture(); var backend = new Owner();
        using var scene = Scene(layout, [motion], () => backend);
        backend.Events.Clear();
        scene.Advance(1d / 120, TestContext.Current.CancellationToken);
        Assert.Equal(1, backend.Inner.Steps);
        Assert.Equal(new[] { "targets", "step", "sample" }, backend.Events);
        var binding = Assert.Single(backend.Bindings, b => !b.Disposed);
        // The rigid-driven bone falls in this boundary, while targets came from its pre-physics pose.
        Assert.Equal(3, binding.Targets[4].Y);
        Assert.True(scene.Actors.Single().PhysicsEnabled);
        Assert.Equal(binding.Targets[4] + new Vector3(0, 0, 2), scene.Frame.Surface.Snapshot.Vertices[4].Position);
    }

    [Fact]
    public void PureSoftActorsShareOneOwnerAndStoppedActorRemainsPresentButFrozen()
    {
        var (layout, first) = Fixture(false, 1); var model = first.Model;
        var actor = layout.Add("long", "long.pmx", ModelRole.Character, Rest(model));
        var second = new SceneActorMotion(actor.Id, model, Motion(4)); var owner = new Owner();
        using var scene = Scene(layout, [first, second], () => owner);
        scene.Advance(1d / 30, TestContext.Current.CancellationToken);
        scene.Advance(1d / 30, TestContext.Current.CancellationToken);
        Assert.Equal(8, owner.Inner.Steps);
        var bindings = owner.Bindings.Where(b => !b.Disposed).ToArray();
        Assert.Equal(2, bindings.Length); Assert.True(bindings[0].Frozen); Assert.False(bindings[1].Frozen);
        Assert.All(bindings, b => Assert.Empty(b.Slots));
    }

    [Fact]
    public void PlacementTargetsUseWorldCoordinatesAndSeekReplacesInvalidatedMappings()
    {
        var (layout, motion) = Fixture(); var owners = new List<Owner>();
        layout.SetTransform(motion.ObjectId, new(8, 2, -1), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f), 1);
        using var scene = Scene(layout, [motion], () => { var created = new Owner(); owners.Add(created); return created; });
        var owner = owners[0];
        var first = Assert.Single(owner.Bindings, b => !b.Disposed);
        Assert.InRange(Vector3.Distance(Vector3.Transform(motion.Model.Vertices[4].Position, layout.Objects[0].World), first.Targets[4]), 0, 1e-5f);
        scene.Advance(1d / 120, TestContext.Current.CancellationToken);
        scene.Seek(0, TestContext.Current.CancellationToken);
        Assert.True(first.Disposed);
        Assert.True(owner.Disposed); Assert.Single(owners[1].Bindings, b => !b.Disposed);
        Assert.Equal(0, scene.SharedPhysicsStepCount);
        scene.Advance(1d / 120, TestContext.Current.CancellationToken);
        Assert.Equal(1, owner.Inner.Steps); Assert.Equal(1, owners[1].Inner.Steps);
    }

    [Fact]
    public void FailedBindingCandidateKeepsActiveGraphRunning()
    {
        var (layout, motion) = Fixture(); var owners = new List<Owner>();
        using var scene = Scene(layout, [motion], () => { var b = new Owner { FailBinding = owners.Count > 0 }; owners.Add(b); return b; });
        scene.Advance(1d / 120, TestContext.Current.CancellationToken); var before = scene.Frame;
        Assert.ThrowsAny<Exception>(() => scene.BindMotion(motion with { IkEnabled = false }, TestContext.Current.CancellationToken));
        Assert.Same(before, scene.Frame); Assert.False(scene.IsFaulted);
        Assert.False(owners[0].Disposed); Assert.True(owners[1].Disposed);
        scene.Advance(1d / 120, TestContext.Current.CancellationToken); Assert.Equal(2, owners[0].Inner.Steps);
    }

    [Fact]
    public void FailedCoupledSeekKeepsThePublishedGraphAndHistoryRunning()
    {
        var (layout, motion) = Fixture(); var owners = new List<Owner>();
        using var scene = Scene(layout, [motion], () => { var owner = new Owner { FailBinding = owners.Count > 0 }; owners.Add(owner); return owner; });
        scene.Advance(1d / 120, TestContext.Current.CancellationToken);
        var before = scene.Frame;
        Assert.ThrowsAny<Exception>(() => scene.Seek(0, TestContext.Current.CancellationToken));
        Assert.Same(before, scene.Frame); Assert.False(scene.IsFaulted);
        Assert.False(owners[0].Disposed); Assert.True(owners[1].Disposed);
        scene.Advance(1d / 120, TestContext.Current.CancellationToken);
        Assert.Equal(2, owners[0].Inner.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoupledSelectionSurvivesProjectSaveLoadAndOwnerEdits(bool shared)
    {
        string folder = Path.Combine(Path.GetTempPath(), "coupled-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var (layout, motion) = Fixture(); var lights = new SceneLightingEditor();
            string modelPath = Path.Combine(folder, "cloth.pmx");
            File.WriteAllBytes(modelPath, PmxWriter.ToBytes(motion.Model));
            var source = layout.Objects.Single();
            layout = new SceneCompositionEditor();
            var actor = layout.Add(source.Name, modelPath, source.Role, source.Source);
            motion = motion with { ObjectId = actor.Id };
            var asset = SceneMotionAsset.FromMotion(motion.Motion);
            motion = motion with { Motion = asset.Motion, Asset = asset };
            var settings = new ProjectSettings { Physics = new() { CoupledSoftBodies = true, SharedWorld = shared } };
            var provider = new ProjectSettingsBackendProvider((s, _) => { Assert.True(s.CoupledSoftBodies); return new Owner(); },
                s => { Assert.True(s.CoupledSoftBodies); return new Owner(); },
                (_, _) => throw new InvalidOperationException("Coupled projects must not allocate a standalone soft solver."));
            using var project = new SceneProjectSession(layout, lights, [motion], provider, settings);
            string path = Path.Combine(folder, "scene.mmdscene");
            project.Save(path, new() { CopyModelAssets = false }, TestContext.Current.CancellationToken);
            using var loaded = SceneProjectSession.Load(path, (_, _) => new(motion.Model, Rest(motion.Model)), provider,
                TestContext.Current.CancellationToken);
            Assert.True(loaded.Settings.Physics.CoupledSoftBodies);
            Assert.True(loaded.Actors.Single().PhysicsEnabled);
            loaded.Edit(edit => edit.Settings = edit.Settings with { Width = 1280 }, TestContext.Current.CancellationToken);
            loaded.Advance(1d / 120, TestContext.Current.CancellationToken);
            Assert.Equal(.25, loaded.Actors.Single().LocalFrame);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static SceneAnimationSession Scene(SceneCompositionEditor layout, SceneActorMotion[] motions, Func<ICharacterPhysicsMutableBackend> factory)
    {
        var constructor = typeof(SceneAnimationSession).GetConstructors().Single();
        Assert.Contains(constructor.GetParameters(), p => p.Name == "coupledSoftBodies");
        var args = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        args[0] = layout.Objects; args[1] = motions; args[2] = new Func<EditableSceneObject, ICharacterPhysicsBackend>(_ => throw new InvalidOperationException());
        args[Array.FindIndex(constructor.GetParameters(), p => p.Name == "sharedPhysicsFactory")] = factory;
        args[Array.FindIndex(constructor.GetParameters(), p => p.Name == "coupledSoftBodies")] = true;
        try { return (SceneAnimationSession)constructor.Invoke(args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static (SceneCompositionEditor, SceneActorMotion) Fixture(bool rigid = true, uint duration = 60,
        bool near = false, PmxRigidBodyOperation operation = PmxRigidBodyOperation.Dynamic)
    {
        var m = PmxSoftBodyTests.MaterialFixture(); var h = m.Header;
        var model = new PmxModel { Header=new() { Version=2.1f, HeaderDataSize=h.HeaderDataSize, TextEncoding=h.TextEncoding,
                AdditionalUvCount=h.AdditionalUvCount, VertexIndexSize=h.VertexIndexSize, TextureIndexSize=h.TextureIndexSize,
                MaterialIndexSize=h.MaterialIndexSize, BoneIndexSize=h.BoneIndexSize, MorphIndexSize=h.MorphIndexSize, RigidBodyIndexSize=h.RigidBodyIndexSize },
            Name=m.Name, EnglishName=m.EnglishName, Comment=m.Comment, EnglishComment=m.EnglishComment,
            Vertices=m.Vertices, Faces=m.Faces, Textures=m.Textures, Materials=m.Materials, Bones=m.Bones, Morphs=m.Morphs,
            DisplayFrames=m.DisplayFrames, Joints=m.Joints, SoftBodies=near ? m.SoftBodies.Select(s => new PmxSoftBody {
                Name=s.Name,EnglishName=s.EnglishName,Type=s.Type,MaterialIndex=s.MaterialIndex,Group=s.Group,CollisionGroup=s.CollisionGroup,
                Flags=s.Flags,BLinkLength=s.BLinkLength,ClusterCount=s.ClusterCount,TotalMass=s.TotalMass,CollisionMargin=s.CollisionMargin,
                AeroModel=s.AeroModel,Config=s.Config,ClusterConfig=s.ClusterConfig,Iterations=s.Iterations,MaterialConfig=s.MaterialConfig,
                Anchors=[new(0,4,true)],PinVertexIndices=s.PinVertexIndices }).ToArray() : m.SoftBodies,
            RigidBodies=rigid ? [new() { Name="body",EnglishName="",BoneIndex=0,Group=0,CollisionGroup=ushort.MaxValue,
                Shape=PmxRigidBodyShape.Sphere,ShapeSize=new(.1f),Translation=new(0,2,0),Rotation=Vector3.Zero,Mass=1,
                TranslationDamping=0,RotationDamping=0,Repulsion=0,Friction=.5f,Operation=operation }] : [] };
        var layout = new SceneCompositionEditor(); var actor = layout.Add("cloth", Path.GetFullPath("cloth.pmx"), ModelRole.Character, Rest(model));
        return (layout, new(actor.Id, model, Motion(duration)));
    }
    private static VmdMotion Motion(uint duration) => new() { BoneKeyframes=[new(){BoneName="root"},new(){BoneName="root",Frame=duration}] };
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One,Matrix4x4.Identity,new(0,0,-10),30,1));

    internal sealed class Owner : ICharacterPhysicsMutableBackend, ICharacterSoftBodyProvider
    {
        internal readonly TrackingSharedBackend Inner = new();
        internal readonly List<Binding> Bindings = [];
        internal readonly List<string> Events = [];
        internal bool Disposed, FailBinding;
        public string Name => "coupled recording owner";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition d, ReadOnlySpan<Matrix4x4> p) { foreach (var b in Bindings) b.Dispose(); Inner.Initialize(d,p); }
        public int AddBody(PhysicsBodyDefinition b, Matrix4x4 p) => Inner.AddBody(b,p);
        public int AddJoint(PhysicsJointDefinition j,Matrix4x4 a,Matrix4x4 b) => Inner.AddJoint(j,a,b);
        public void RemoveBody(int i) { Assert.DoesNotContain(Bindings, b => !b.Disposed && b.Slots.Contains(i)); Inner.RemoveBody(i); }
        public void RemoveJoint(int i) => Inner.RemoveJoint(i);
        public void ResetBody(int i,Matrix4x4 p) => Inner.ResetBody(i,p);
        public void SetBodyKinematic(int i,bool k) => Inner.SetBodyKinematic(i,k);
        public void SetKinematicTarget(int i,Matrix4x4 p,float s) => Inner.SetKinematicTarget(i,p,s);
        public Matrix4x4 GetBodyWorldTransform(int i) => Inner.GetBodyWorldTransform(i);
        public void SetGravity(Vector3 g) => Inner.SetGravity(g);
        public void ApplyImpulse(int i,Vector3 l,Vector3 a) => Inner.ApplyImpulse(i,l,a);
        public void Step(float s) { Events.Add("step"); Inner.Step(s); }
        public ICharacterSoftBodyBinding CreateSoftBodyBinding(IReadOnlyList<int> slots) { var b = new Binding(this,slots.ToArray()); Bindings.Add(b); return b; }
        public void Dispose() { Disposed=true; foreach(var b in Bindings)b.Dispose(); }
    }
    internal sealed class Binding(Owner owner,int[] slots) : ICharacterSoftBodyBinding
    {
        internal int[] Slots=slots; internal Vector3[] Targets=[]; internal bool Frozen,Disposed;
        private IReadOnlyList<SoftBodyDefinition> definitions=[];
        public IReadOnlyList<string> Diagnostics=>[];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> d,ReadOnlySpan<Vector3> p) { if(owner.FailBinding)throw new InvalidOperationException("injected binding failure"); definitions=d;Targets=p.ToArray(); }
        public void SetTargets(ReadOnlySpan<Vector3> p) { ObjectDisposedException.ThrowIf(Disposed,this);owner.Events.Add("targets");Targets=p.ToArray(); }
        public IReadOnlyList<SoftBodySample> Sample() { ObjectDisposedException.ThrowIf(Disposed,this);owner.Events.Add("sample");return definitions.Select(d=>new SoftBodySample(d.VertexIndices.ToArray(),d.VertexIndices.Select(i=>Targets[i]+new Vector3(0,0,2)).ToArray(),new Vector3[d.VertexIndices.Count])).ToArray(); }
        public void SetFrozen(bool frozen)=>Frozen=frozen;
        public void Rebase(Matrix4x4 delta) { for(int i=0;i<Targets.Length;i++)Targets[i]=Vector3.Transform(Targets[i],delta); }
        public void Dispose()=>Disposed=true;
    }
}
