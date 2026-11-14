using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12GenshinAtlasPmaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "atlas-pma-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public D3D12GenshinAtlasPmaTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("genshin")]
    [InlineData("mmd")]
    public void MovedSchemaThreeKeepsBothSourceImagesAndReferencesThroughDefaultResave(string pipeline)
    {
        using var project = Project(out var entries);
        project.SetDefaultRenderingStyle(new(pipeline), Token);
        string path = Path.Combine(directory, "first.pma");
        PmaProjectStore.Save(path, project, D3D12MaterialBindingPma.CreateSaveOptions(project, entries), Token);
        string moved = Path.Combine(directory, "moved.pma"); File.Move(path, moved);
        File.Delete(project.Objects[0].SourcePath);
        string saved = Path.Combine(directory, "saved.pma");
        using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load, new NoPhysics(), out var profiles, cancellationToken: Token))
        {
            foreach (var entry in entries)
            {
                var restored = loaded.Session.Objects.Single(obj => obj.Id == entry.ObjectId).Source;
                Assert.Equal(3, profiles[entry.ObjectId].SchemaVersion);
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(entry.Profile), D3D12MaterialBindingProfileJson.Encode(profiles[entry.ObjectId]));
                var mapping = D3D12MaterialBindingProfileResolver.Resolve(profiles[entry.ObjectId], restored, entry.ObjectId, pipeline);
                Assert.Equal(pipeline == "genshin" ? 1 : 0, mapping.GenshinHairAtlasBindings.Count);
                foreach (int slot in new[] { entry.SourceRest.Surface.Snapshot.Textures.Count - 2, entry.SourceRest.Surface.Snapshot.Textures.Count - 1 })
                    Assert.Equal(entry.SourceRest.Surface.Snapshot.Textures[slot].Rgba.ToArray(), restored.Surface.Snapshot.Textures[slot].Rgba.ToArray());
            }
            PmaProjectStore.Save(saved, loaded.Session, cancellationToken: Token);
        }
        using var again = D3D12MaterialBindingPma.Load(saved, Path.Combine(directory, "cache2"), Load, new NoPhysics(), out var roundtrip, cancellationToken: Token);
        Assert.Equal(entries.Select(entry => entry.ObjectId).Order(), roundtrip.Keys.Order());
        Assert.All(roundtrip.Values, profile => Assert.NotNull(profile.Materials[0].GenshinHairAtlas));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PortableAtlasAndHighlightMatchBeforeAfterAndReachRealtimeWithoutReadback()
    {
        using var project = Project(out var entries); project.SetDefaultRenderingStyle(new("genshin"), Token);
        string path = Path.Combine(directory, "render.pma");
        PmaProjectStore.Save(path, project, D3D12MaterialBindingPma.CreateSaveOptions(project, entries), Token);
        using var loaded = D3D12MaterialBindingPma.Load(path, Path.Combine(directory, "cache"), Load, new NoPhysics(), out var profiles, cancellationToken: Token);
        var before = entries.Select(entry => D3D12MaterialBindingProfileResolver.Resolve(entry.Profile, entry.SourceRest, entry.ObjectId, "genshin")).ToArray();
        var after = loaded.Session.Objects.Select(obj => D3D12MaterialBindingProfileResolver.Resolve(profiles[obj.Id], obj.Source, obj.Id, "genshin")).ToArray();
        var request = new ViewRenderRequest(97,65,new(Antialiasing:FrameAntialiasing.Smaa1X,SpatialSamples:2));
        await using var a = new D3D12ViewRenderer(request, characterMaterials:before.SelectMany(m=>m.CharacterMaterials),
            hairDataBindings:before.SelectMany(m=>m.GenshinHairBindings),genshinHairAtlasDataBindings:before.SelectMany(m=>m.GenshinHairAtlasBindings));
        await using var b = new D3D12ViewRenderer(request, characterMaterials:after.SelectMany(m=>m.CharacterMaterials),
            hairDataBindings:after.SelectMany(m=>m.GenshinHairBindings),genshinHairAtlasDataBindings:after.SelectMany(m=>m.GenshinHairAtlasBindings));
        var plan = new OfflineFramePlan(97,65,new(30),0,0);
        OfflineFrame Frame(SceneProjectSession owner,D3D12ViewRenderer renderer)
        {
            var s=owner.CaptureRenderSnapshot(renderer.SceneInputRequests);
            return new(plan.GetSample(0),97,65,s.Scene,s.Camera,s.Light,s.SelfShadow){RenderingStyles=s.RenderingStyles,SceneInputs=s.SceneInputs};
        }
        using var first=await a.CaptureAsync(Frame(project,a),Token); using var second=await b.CaptureAsync(Frame(loaded.Session,b),Token);
        Assert.False(a.IsWarp); Assert.Equal(first.Pixels.ToArray(),second.Pixels.ToArray());
        Assert.Contains(first.Pixels.ToArray().Where((_,i)=>i%4==3),alpha=>alpha!=0);
        // Keep the original R/B lobe and geometry identical; the atlas alone must affect RGB.
        await using var rbOnly = new D3D12ViewRenderer(request, characterMaterials:before.SelectMany(m=>m.CharacterMaterials),
            hairDataBindings:before.SelectMany(m=>m.GenshinHairBindings));
        using var baseline=await rbOnly.CaptureAsync(Frame(project,rbOnly),Token);
        int changedRgb=0;
        for(int pixel=0;pixel<first.Pixels.Length;pixel+=4)
        {
            Assert.Equal(baseline.Pixels.Span[pixel+3],first.Pixels.Span[pixel+3]);
            if(baseline.Pixels.Span[pixel]!=first.Pixels.Span[pixel]
                ||baseline.Pixels.Span[pixel+1]!=first.Pixels.Span[pixel+1]
                ||baseline.Pixels.Span[pixel+2]!=first.Pixels.Span[pixel+2]) changedRgb++;
        }
        Assert.True(changedRgb>10,$"Only {changedRgb} pixels consumed the explicitly bound atlas.");
        nint window=CreateWindowExW(0,"STATIC","MDA Atlas",0x80000000,0,0,128,96,0,0,0,0);
        Assert.NotEqual(0,window);
        try
        {
            await using var realtime=new D3D12RealtimeViewRenderer(window,request,characterMaterials:after.SelectMany(m=>m.CharacterMaterials),
                hairDataBindings:after.SelectMany(m=>m.GenshinHairBindings),genshinHairAtlasDataBindings:after.SelectMany(m=>m.GenshinHairAtlasBindings));
            var s=loaded.Session.CaptureRenderSnapshot(realtime.SceneInputRequests);
            var frame=new PreviewFrame(97,65,0,s.Scene,s.Camera,s.Light,s.SelfShadow){RenderingStyles=s.RenderingStyles,SceneInputs=s.SceneInputs};
            realtime.ValidateRenderingStyles(s.Scene,s.RenderingStyles); realtime.ValidateSceneInputs(frame);
            await realtime.PresentAsync(frame,Token);
            Assert.Equal(1,realtime.Statistics.PresentCount); Assert.Equal(0,realtime.Statistics.Frame!.TotalReadbacks);
            var original=realtime.Statistics;
            await Assert.ThrowsAsync<ArgumentException>(()=>realtime.PresentAsync(frame with{SceneInputs=SceneObjectRenderInputs.Empty},Token).AsTask());
            Assert.Equal(original.PresentCount,realtime.Statistics.PresentCount);
        }
        finally{DestroyWindow(window);}
    }

    private SceneProjectSession Project(out D3D12MaterialBindingPmaEntry[] entries)
    {
        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var model = new PmxModel
        {
            Header = template.Header, Name = "mapping fixture", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = new[] { new Vector3(-1,-1,0), new Vector3(0,1,0), new Vector3(1,-1,0) }.Select(p => new PmxVertex
            { Position = p, Normal = -Vector3.UnitZ, Uv = new(.5f), AdditionalUvs = new Vector4[template.Header.AdditionalUvCount],
                WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0,-1,-1,-1], BoneWeights = [1,0,0,0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0,1,2)], Textures = [], Materials = [new() { Name="mat",EnglishName="",Diffuse=new(.6f,.4f,.2f,1),
                Specular=new(.5f),SpecularPower=10,Ambient=new(.2f),EdgeColor=Vector4.Zero,EdgeSize=0,DrawMode=PmxDrawModeFlags.BothFaces,
                TextureIndex=-1,SphereTextureIndex=-1,SphereMode=PmxSphereMode.None,ToonMode=PmxToonMode.Common,ToonTextureIndex=0,Memo="",FaceVertexCount=3 }],
            Bones=template.Bones,Morphs=[],DisplayFrames=[],RigidBodies=[],Joints=[],SoftBodies=[]
        };
        string source = Path.Combine(directory, "model.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor(); var list = new List<D3D12MaterialBindingPmaEntry>();
        for (int i = 0; i < 2; i++)
        {
            var original = Rest(model); var s = original.Surface.Snapshot;
            var extra = new SceneTexture("explicit unused numerical image", 1, 1, [(byte)(100+i*100),128,255,0]);
            var atlas = new SceneTexture("explicit nonwhite color atlas", 1, 1, [48,96,(byte)(144+i*16),0]);
            var appended = new SceneSnapshot(s.Vertices,s.Indices,s.Materials,[..s.Textures,extra,atlas],s.World,s.Camera,s.LightDirection,additionalUvs:s.AdditionalUvs);
            var frame = new OriginalFrameScene(new(appended, original.Surface.Materials,original.Surface.Profile),original.Outline,original.OutlineOptions,original.Options,original.Materials);
            var obj = composition.Add("explicit instance "+i,source,ModelRole.Character,frame);
            composition.SetTransform(obj.Id,new((i-.5f)*2,0,0),Quaternion.Identity,1);
            var layout=D3D12MaterialBindingLayout.Capture(frame);
            list.Add(new(obj.Id,new("map-"+i,"mapping "+i,layout.Fingerprint,
                [new(0,D3D12CharacterMaterialRole.Hair,GenshinHair:new(layout.CaptureTexture(appended.Textures.Count-2),Multiplier:.8f),
                    GenshinHairAtlas:new(layout.CaptureTexture(appended.Textures.Count-2),layout.CaptureTexture(appended.Textures.Count-1),
                        D3D12GenshinAtlasColorEncoding.LinearRgb, 2.5f, .3f, 1.1f, .5f, .7f, .8f, 1, .2f))]),frame));
        }
        entries=list.ToArray();
        return new(composition,new SceneLightingEditor(),[],new NoPhysics(),cancellationToken:Token);
    }

    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model),new(Vector4.One,Matrix4x4.Identity,new(0,0,-5),30,1),
        materials:PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset Load(string path,CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model=PmxReader.Read(path); return new(model,Rest(model)); }
    private sealed class NoPhysics:IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s,EditableSceneObject o)=>throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s)=>throw new InvalidOperationException();
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended,string className,string name,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
