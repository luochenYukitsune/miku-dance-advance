using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;
namespace Mmd.Modern.Application.Tests;
public sealed class ProjectSettingsTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "project-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(folder,"scene.mmdscene");
    public ProjectSettingsTests() => Directory.CreateDirectory(folder);
    [Fact] public void Version7SettingsMediaArePortableAndEffectiveOutputContract()
    {
        var (composition, lights, session, model)=Scene(); using(session)
        {
            var settings = new ProjectSettings { Width=2560, Height=1440, FpsNumerator=60000, FpsDenominator=1001,
                Physics = new() { Backend=ProjectPhysicsBackend.Bepu, SharedWorld=true, GravityY=-12, GroundEnabled=false },
                BackgroundRed=.2f, SelfShadowEnabled=false };
            var asset=SceneMediaAsset.FromBytes(new byte[]{1,2,3,4}, "sound.wav");
            var media=new SceneMediaTrack(Guid.NewGuid(),SceneMediaKind.Audio,asset,TimeSpan.FromSeconds(2).Ticks,true);
            SceneAnimationProjectStore.Save(FilePath,composition,lights,session,settings,[media],new(){CopyModelAssets=false},Token);
            var provider=new Provider();
            using var loaded=SceneAnimationProjectStore.Load(FilePath,(_,_)=>new(model,Rest(model)),provider,Token);
            Assert.Equal(settings.Physics,provider.Last); Assert.True(loaded.Animation.UsesSharedPhysicsWorld);
            Assert.Equal(settings,loaded.Settings); Assert.Equal(media.Asset.Sha256,Assert.Single(loaded.Media).Asset.Sha256);
            var plan=loaded.Settings.CreateOutputPlan(0,30); Assert.Equal(2560,plan.Width);Assert.Equal(60000,plan.FrameRate.Numerator);
            Assert.Equal(7,JsonNode.Parse(File.ReadAllText(FilePath))!["version"]!.GetValue<int>());
            Assert.Throws<NotSupportedException>(()=>SceneAnimationProjectStore.Load(FilePath,(_,_)=>new(model,Rest(model)),NoPhysics,Token));
        }
    }
    [Fact] public void TamperedMediaRejectedBeforeModelLoaderAndOldFileSurvivesInvalidSave()
    {
        var(c,l,s,m)=Scene();using(s)
        {
            var asset=SceneMediaAsset.FromBytes(new byte[]{7,8},"movie.mp4");
            SceneAnimationProjectStore.Save(FilePath,c,l,s,new ProjectSettings(),[new(Guid.NewGuid(),SceneMediaKind.Video,asset,0,true)],new(){CopyModelAssets=false},Token);
            string old=File.ReadAllText(FilePath);
            Assert.ThrowsAny<ArgumentException>(()=>SceneAnimationProjectStore.Save(FilePath,c,l,s,new ProjectSettings{Width=0},[],new(){CopyModelAssets=false},Token));
            Assert.Equal(old,File.ReadAllText(FilePath));
            File.WriteAllBytes(Path.Combine(folder,"assets/media/"+asset.Sha256+".media"),new byte[]{9,9});
            int calls=0;Assert.Throws<InvalidDataException>(()=>SceneAnimationProjectStore.Load(FilePath,(_,_)=>{calls++;return new(m,Rest(m));},new Provider(),Token));Assert.Equal(0,calls);
            Assert.Throws<InvalidDataException>(()=>SceneAnimationProjectStore.Save(FilePath,c,l,s,new ProjectSettings(),[new(Guid.NewGuid(),SceneMediaKind.Video,asset,0,true)],new(){CopyModelAssets=false},Token));
            Assert.Equal(old,File.ReadAllText(FilePath));
        }
    }
    [Fact] public void LegacyProjectsReceiveExplicitDefaults()
    {
        var(c,l,s,m)=Scene();using(s)
        {
            SceneAnimationProjectStore.Save(FilePath,c,l,s,new SceneProjectSaveOptions{CopyModelAssets=false},Token);
            using var loaded=SceneAnimationProjectStore.Load(FilePath,(_,_)=>new(m,Rest(m)),NoPhysics,Token);
            Assert.Equal(new ProjectSettings(),loaded.Settings);Assert.Empty(loaded.Media);
        }
    }
    [Fact] public void MediaClockPlayPauseSeekAndOffsetsRemainExact()
    {
        var clock=new SceneMediaClock(new OfflineFrameRate(60000,1001)); clock.Play();
        clock.Advance(TimeSpan.FromSeconds(3));Assert.Equal(90,clock.SourceFrame.ToDouble());
        var track=new SceneMediaTrack(Guid.NewGuid(),SceneMediaKind.Audio,SceneMediaAsset.FromBytes(new byte[]{1},"x.wav"),TimeSpan.FromSeconds(2).Ticks,true);
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks,clock.Sample(track).MediaTicks);
        clock.Pause();clock.Advance(TimeSpan.FromSeconds(4));Assert.Equal(90,clock.SourceFrame.ToDouble());
        clock.Seek(new OfflineFrameTime(15,1));Assert.False(clock.Sample(track).Active);
        clock.SeekOutputFrame(60);Assert.Equal(new OfflineFrameTime(3003,100),clock.SourceFrame);
        Assert.False(clock.Sample(track with {Enabled=false}).Active);
    }
    [Theory] [InlineData(-1)] [InlineData(0)] public void InvalidFpsIsRejected(int fps)
        => Assert.ThrowsAny<ArgumentException>(()=>new ProjectSettings{FpsNumerator=fps}.Validate());
    [Theory]
    [InlineData("path")][InlineData("duplicate")][InlineData("enum")][InlineData("oldVersion")][InlineData("nullPhysics")]
    public void MalformedVersion7RejectsBeforeHostAllocation(string corruption)
    {
        var(c,l,session,model)=Scene();using(session)
        {
            var media=new SceneMediaTrack(Guid.NewGuid(),SceneMediaKind.Audio,SceneMediaAsset.FromBytes(new byte[]{1},"a.wav"),0,true);
            SceneAnimationProjectStore.Save(FilePath,c,l,session,new(),[media],new(){CopyModelAssets=false},Token);
            var json=JsonNode.Parse(File.ReadAllText(FilePath))!;
            if(corruption=="path")json["media"]![0]!["assetPath"]="../a.wav";
            if(corruption=="duplicate")json["media"]!.AsArray().Add(json["media"]![0]!.DeepClone());
            if(corruption=="enum")json["media"]![0]!["kind"]=900;
            if(corruption=="oldVersion")json["version"]=6;
            if(corruption=="nullPhysics")json["settings"]!["physics"]=null;
            int calls=0;Assert.ThrowsAny<Exception>(()=>SceneAnimationProjectStore.Prepare(json.ToJsonString(),FilePath,
                (_,_)=>{calls++;return new(model,Rest(model));},new Provider(),Token));Assert.Equal(0,calls);
        }
    }
    [Fact] public void CancelledSaveKeepsOldDocumentAndMediaBytesAreDefensivelyCopied()
    {
        var(c,l,session,model)=Scene();using(session)
        {
            byte[] bytes=[1,2];var asset=SceneMediaAsset.FromBytes(bytes,"a.wav");bytes[0]=9;asset.CopyBytes()[0]=8;
            var tracks=new[]{new SceneMediaTrack(Guid.NewGuid(),SceneMediaKind.Audio,asset,-TimeSpan.TicksPerSecond,true)};
            SceneAnimationProjectStore.Save(FilePath,c,l,session,new(),tracks,new(){CopyModelAssets=false},Token);
            byte[] old=File.ReadAllBytes(FilePath);using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(()=>SceneAnimationProjectStore.Save(FilePath,c,l,session,new(),[],new(){CopyModelAssets=false},cancelled.Token));
            Assert.Equal(old,File.ReadAllBytes(FilePath));
            using var loaded=SceneAnimationProjectStore.Load(FilePath,(_,_)=>new(model,Rest(model)),new Provider(),Token);
            Assert.Equal(new byte[]{1,2},Assert.Single(loaded.Media).Asset.CopyBytes());
            var clock=new SceneMediaClock(new(30));Assert.Equal(TimeSpan.TicksPerSecond,clock.Sample(tracks[0]).MediaTicks);
        }
    }
    [Fact] public void ClockHasNoAccumulatedOutputFrameDriftAndRejectsImpossibleTime()
    {
        var clock=new SceneMediaClock(new(30000,1001));clock.SeekOutputFrame(1_000_000);
        Assert.Equal(new OfflineFrameTime(1_001_000,1),clock.SourceFrame);
        Assert.ThrowsAny<ArgumentException>(()=>clock.Seek(default));
        Assert.ThrowsAny<ArgumentException>(()=>clock.Advance(TimeSpan.FromTicks(-1)));
        Assert.ThrowsAny<ArgumentException>(()=>clock.SetOutputFrameRate(default));
    }
    [Fact] public void MediaResourcesRelocateWithTheProjectAndDuplicatePayloadUsesOneFile()
    {
        var(c,l,session,model)=Scene();using(session)
        {
            var a=SceneMediaAsset.FromBytes(new byte[]{2,4,6},"a.wav");
            var b=SceneMediaAsset.FromBytes(new byte[]{2,4,6},"b.ogg");
            SceneAnimationProjectStore.Save(FilePath,c,l,session,new(),
                [new(Guid.NewGuid(),SceneMediaKind.Audio,a,0,true),new(Guid.NewGuid(),SceneMediaKind.Audio,b,0,false)],new(){CopyModelAssets=false},Token);
            Assert.Single(Directory.GetFiles(Path.Combine(folder,"assets/media")));
            string moved=folder+"-moved";Directory.Move(folder,moved);
            try
            {
                using var loaded=SceneAnimationProjectStore.Load(Path.Combine(moved,"scene.mmdscene"),(_,_)=>new(model,Rest(model)),new Provider(),Token);
                Assert.Equal(new[]{"a.wav","b.ogg"},loaded.Media.Select(track=>track.Asset.Name));
                Assert.Equal(new byte[]{2,4,6},loaded.Media[1].Asset.CopyBytes());
            }
            finally {Directory.Move(moved,folder);}
        }
    }
    private (SceneCompositionEditor,SceneLightingEditor,SceneAnimationSession,PmxModel) Scene()
    {
        var model=Model();string path=Path.Combine(folder,"model.pmx");File.WriteAllText(path,"test loader");
        var composition=new SceneCompositionEditor();composition.Add("actor",path,ModelRole.Character,Rest(model));var lights=new SceneLightingEditor();
        return(composition,lights,new SceneAnimationSession(composition.Objects,[],NoPhysics,lights.Snapshot,Token),model);
    }
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject item)=>throw new InvalidOperationException("Unexpected backend");
    private sealed class Provider : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)=>throw new InvalidOperationException();
        public ProjectPhysicsSettings? Last;
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings){Last=settings;return new EmptyBackend();}
    }
    private sealed class EmptyBackend : ICharacterPhysicsMutableBackend
    {
        public string Name=>"empty test world"; public IReadOnlyList<string> Diagnostics=>[];
        public void Initialize(CharacterPhysicsDefinition definition,ReadOnlySpan<Matrix4x4> input){}
        public void SetKinematicTarget(int index,Matrix4x4 world,float seconds)=>throw new NotSupportedException();
        public Matrix4x4 GetBodyWorldTransform(int index)=>throw new NotSupportedException();public void Step(float seconds){}public void Dispose(){}
        public void SetGravity(Vector3 gravity){}public void ApplyImpulse(int index,Vector3 linear,Vector3 angular)=>throw new NotSupportedException();
        public int AddBody(PhysicsBodyDefinition body,Matrix4x4 world)=>throw new NotSupportedException();
        public int AddJoint(PhysicsJointDefinition joint,Matrix4x4 a,Matrix4x4 b)=>throw new NotSupportedException();
        public void RemoveJoint(int index)=>throw new NotSupportedException();public void RemoveBody(int index)=>throw new NotSupportedException();
        public void ResetBody(int index,Matrix4x4 world)=>throw new NotSupportedException();public void SetBodyKinematic(int index,bool state)=>throw new NotSupportedException();
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));

    private static PmxModel Model(bool physics = false, bool morph = false) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "fixture", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2)], Textures = [],
        Materials = [new() { Name = "toon", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 4,
            Ambient = new(.3f), DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = new(0, 0, 0, 1), EdgeSize = 1,
            TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
            ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = morph ? [new() { Name = "smile", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position,
            PositionOffsets = [new(0, new(0, 0, 2))] }] : [], DisplayFrames = [], Joints = [], SoftBodies = [],
        RigidBodies = physics ? [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] : [],
    };

    public void Dispose(){if(Directory.Exists(folder))Directory.Delete(folder,true);}
}
