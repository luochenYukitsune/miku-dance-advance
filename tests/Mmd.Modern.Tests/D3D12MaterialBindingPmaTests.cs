using System.Numerics;
using System.Text.Json;
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
public sealed class D3D12MaterialBindingPmaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "material-pma-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public D3D12MaterialBindingPmaTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void DedicatedImagesUnusedByPmxAndInstanceMappingsTravelWithoutOriginalFilesAndSurviveDefaultResave()
    {
        using var project = Project(out var entries);
        string first = Path.Combine(directory, "first.pma");
        PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project, entries), Token);
        string moved = Path.Combine(directory, "moved.pma"); File.Move(first, moved);
        File.Delete(project.Objects[0].SourcePath);
        string resaved = Path.Combine(directory, "resaved.pma");
        using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load, new NoPhysics(), out var profiles, cancellationToken: Token))
        {
            Assert.Equal(2, profiles.Count);
            foreach (var entry in entries)
            {
                var restored = loaded.Session.Objects.Single(obj => obj.Id == entry.ObjectId).Source;
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(entry.Profile), D3D12MaterialBindingProfileJson.Encode(profiles[entry.ObjectId]));
                Assert.Equal(entry.SourceRest.Surface.Snapshot.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < restored.Surface.Snapshot.Textures.Count; i++)
                    Assert.Equal(entry.SourceRest.Surface.Snapshot.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                Assert.Single(D3D12MaterialBindingProfileResolver.Resolve(profiles[entry.ObjectId], restored, entry.ObjectId, "genshin").GenshinHairBindings);
            }
            PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
        }
        using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(), out var again, cancellationToken: Token);
        Assert.Equal(entries.Select(entry => entry.ObjectId).Order(), again.Keys.Order());
        Assert.Contains(reopened.ResourcePaths, path => path.EndsWith(".rgba", StringComparison.Ordinal));
        Assert.NotEqual(reopened.Session.Objects[0].Source.Surface.Snapshot.Textures[^1].Rgba.ToArray(),
            reopened.Session.Objects[1].Source.Surface.Snapshot.Textures[^1].Rgba.ToArray());
    }

    [Fact]
    public void EmptySelectionExplicitlyDisablesRetainedMappingsWithoutDeletingUnknownResources()
    {
        using var project = Project(out var entries);
        string path = Path.Combine(directory, "selected.pma");
        PmaProjectStore.Save(path, project, D3D12MaterialBindingPma.CreateSaveOptions(project, entries), Token);
        using var loaded = D3D12MaterialBindingPma.Load(path, Path.Combine(directory, "cache"), Load, new NoPhysics(), out _, cancellationToken: Token);
        string empty = Path.Combine(directory, "empty.pma");
        PmaProjectStore.Save(empty, loaded.Session, D3D12MaterialBindingPma.CreateSaveOptions(loaded.Session, []), Token);
        using var cleared = D3D12MaterialBindingPma.Load(empty, Path.Combine(directory, "cache2"), Load, new NoPhysics(), out var maps, cancellationToken: Token);
        Assert.Empty(maps); Assert.NotEmpty(cleared.ResourcePaths);
    }

    [Fact]
    public void PortableResourceBudgetsAndCaseCollisionsFailDuringOptionsPreparation()
    {
        using var project=Project(out var entries);
        Assert.Throws<InvalidDataException>(()=>D3D12MaterialBindingPma.CreateSaveOptions(project,entries,
            new(){ValidationOptions=new(){MaxEntryBytes=16}}));
        Assert.Throws<InvalidDataException>(()=>D3D12MaterialBindingPma.CreateSaveOptions(project,entries,
            new(){ValidationOptions=new(){MaxFiles=1}}));
        string collision="render-resources/material-bindings/"+entries[0].ObjectId.ToString("N")+"/TEXTURES/0000.RGBA";
        Assert.Throws<ArgumentException>(()=>D3D12MaterialBindingPma.CreateSaveOptions(project,entries,
            new(){AdditionalResources=new Dictionary<string,ReadOnlyMemory<byte>>{{collision,new byte[4]}}}));
    }

    [Fact]
    public void RetainedUnrelatedResourcesAreIncludedInPreclonePortableBudget()
    {
        using var project=Project(out var entries);
        var initial=D3D12MaterialBindingPma.CreateSaveOptions(project,entries);
        long mappingBytes=initial.AdditionalResources.Sum(pair=>(long)pair.Value.Length);
        var withExtra=initial.AdditionalResources.ToDictionary(pair=>pair.Key,pair=>pair.Value);
        withExtra["render-resources/unrelated.bin"]=new byte[32];
        string path=Path.Combine(directory,"retained.pma");
        PmaProjectStore.Save(path,project,initial with{AdditionalResources=withExtra},Token);
        using var loaded=D3D12MaterialBindingPma.Load(path,Path.Combine(directory,"cache"),Load,new NoPhysics(),out var profiles,cancellationToken:Token);
        var selected=loaded.Session.Objects.Select(obj=>new D3D12MaterialBindingPmaEntry(obj.Id,profiles[obj.Id],obj.Source)).ToArray();
        Assert.Throws<InvalidDataException>(()=>D3D12MaterialBindingPma.CreateSaveOptions(loaded.Session,selected,
            new(){ValidationOptions=new(){MaxExpandedBytes=mappingBytes+31}}));
        var boundary=D3D12MaterialBindingPma.CreateSaveOptions(loaded.Session,selected,
            new(){ValidationOptions=new(){MaxExpandedBytes=mappingBytes+32}});
        Assert.Equal(mappingBytes,boundary.AdditionalResources.Sum(pair=>(long)pair.Value.Length));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("texture")]
    [InlineData("missing")]
    public void MalformedDedicatedResourcesFailExplicitlyInsteadOfDroppingMapping(string damage)
    {
        using var project=Project(out var entries);
        var options=D3D12MaterialBindingPma.CreateSaveOptions(project,entries);
        var resources=options.AdditionalResources.ToDictionary(pair=>pair.Key,pair=>pair.Value);
        string metadata=resources.Keys.First(path=>path.EndsWith("binding.json",StringComparison.Ordinal));
        if(damage=="metadata")resources[metadata]=new byte[D3D12MaterialBindingPma.MaxMetadataBytes+1];
        else if(damage=="missing")resources.Remove(metadata);
        else
        {
            string image=resources.Keys.First(path=>path.EndsWith(".rgba",StringComparison.Ordinal));
            resources[image]=new byte[resources[image].Length+1];
        }
        string path=Path.Combine(directory,damage+".pma");
        PmaProjectStore.Save(path,project,options with{AdditionalResources=resources},Token);
        Assert.Throws<SceneProjectAssetException>(()=>D3D12MaterialBindingPma.Load(path,Path.Combine(directory,"cache"),Load,new NoPhysics(),out _,cancellationToken:Token));
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("genshin", false)]
    [InlineData("mmd", false)]
    [InlineData("endfield", false)]
    [InlineData("genshin", true)]
    [InlineData("star-rail", true)]
    [InlineData("wuthering-waves", true)]
    [InlineData("endfield", true)]
    public async Task MovedPortableMappingFeedsExistingViewAndMatchesOriginalGpuFrame(string pipeline, bool generic)
    {
        using var project = Project(out var entries);
        if (generic)
        {
            var family = pipeline switch { "genshin" => D3D12SurfaceStyle.GenshinPmx,
                "star-rail" => D3D12SurfaceStyle.StarRailPmx, "wuthering-waves" => D3D12SurfaceStyle.WutheringPmx,
                _ => D3D12SurfaceStyle.EndfieldPmx };
            entries = entries.Select(entry => entry with { Profile = new(entry.Profile.StableId, entry.Profile.Name,
                entry.Profile.LayoutFingerprint, [new(0, D3D12CharacterMaterialRole.Hair,
                    PmxMaterialTreatment: D3D12PmxMaterialTreatmentCatalog.Resolve(family, D3D12CharacterMaterialRole.Hair))]) }).ToArray();
        }
        else if (pipeline == "endfield")
            entries = entries.Select(entry => entry with { Profile = new(entry.Profile.StableId, entry.Profile.Name,
                entry.Profile.LayoutFingerprint, [new(0, D3D12CharacterMaterialRole.Hair,
                    EndfieldRim: new(new(.2f,.3f,.4f), new(.7f,.8f,.9f), EdgeSize: 4f, EdgeStrength: .15f))]) }).ToArray();
        project.EditRenderingStyles(edit => edit.DefaultStyle = new(pipeline), Token);
        string path = Path.Combine(directory, "render.pma");
        PmaProjectStore.Save(path, project, D3D12MaterialBindingPma.CreateSaveOptions(project, entries), Token);
        File.Delete(project.Objects[0].SourcePath);
        using var loaded = D3D12MaterialBindingPma.Load(path, Path.Combine(directory, "cache"), Load, new NoPhysics(), out var profiles, cancellationToken: Token);
        if (generic)
        {
            string resaved = Path.Combine(directory, "generic-resaved.pma");
            PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache-resaved"), Load,
                new NoPhysics(), out var resavedProfiles, cancellationToken: Token);
            foreach (var entry in entries)
            {
                Assert.Equal(11, profiles[entry.ObjectId].SchemaVersion);
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(entry.Profile),
                    D3D12MaterialBindingProfileJson.Encode(resavedProfiles[entry.ObjectId]));
            }
        }
        var before = entries.Select(entry => D3D12MaterialBindingProfileResolver.Resolve(entry.Profile, entry.SourceRest, entry.ObjectId, pipeline)).ToArray();
        var after = loaded.Session.Objects.Select(obj => D3D12MaterialBindingProfileResolver.Resolve(profiles[obj.Id], obj.Source, obj.Id, pipeline)).ToArray();
        var request = new ViewRenderRequest(97, 65, new(Antialiasing: FrameAntialiasing.Smaa1X, SpatialSamples: 2));
        await using var expected = new D3D12ViewRenderer(request,
            characterMaterials: before.SelectMany(value => value.CharacterMaterials), hairDataBindings: before.SelectMany(value => value.GenshinHairBindings));
        await using var actual = new D3D12ViewRenderer(request,
            characterMaterials: after.SelectMany(value => value.CharacterMaterials), hairDataBindings: after.SelectMany(value => value.GenshinHairBindings));
        Assert.False(actual.IsWarp); Assert.False(expected.IsWarp);
        var plan = new OfflineFramePlan(97, 65, new(30), 0, 0);
        OfflineFrame Capture(SceneProjectSession owner, D3D12ViewRenderer renderer)
        {
            var s = owner.CaptureRenderSnapshot(renderer.SceneInputRequests);
            return new(plan.GetSample(0), 97, 65, s.Scene, s.Camera, s.Light, s.SelfShadow)
            { RenderingStyles = s.RenderingStyles, SceneInputs = s.SceneInputs };
        }
        using var a = await expected.CaptureAsync(Capture(project, expected), Token);
        using var b = await actual.CaptureAsync(Capture(loaded.Session, actual), Token);
        Assert.Equal(a.Pixels.ToArray(), b.Pixels.ToArray());
        if (generic)
        {
            await using var withoutTreatment = new D3D12ViewRenderer(request);
            using var plain = await withoutTreatment.CaptureAsync(Capture(project, withoutTreatment), Token);
            Assert.NotEqual(plain.Pixels.ToArray(), a.Pixels.ToArray());
            Assert.All(after, mapping => Assert.NotNull(Assert.Single(mapping.CharacterMaterials).Style.PmxMaterialTreatment));
        }
        else if (pipeline == "endfield")
        {
            await using var withoutRim = new D3D12ViewRenderer(request);
            using var plain = await withoutRim.CaptureAsync(Capture(project, withoutRim), Token);
            Assert.NotEqual(plain.Pixels.ToArray(), a.Pixels.ToArray());
            Assert.All(after, mapping => Assert.NotNull(Assert.Single(mapping.CharacterMaterials).Style.EndfieldRim));
        }
        Assert.Contains(b.Pixels.ToArray().Where((_, i) => i % 4 == 3), alpha => alpha != 0);
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
            var extra = new SceneTexture("explicit unused numerical image", 1, 1, [(byte)(100+i*100),255,255,0]);
            var appended = new SceneSnapshot(s.Vertices,s.Indices,s.Materials,[..s.Textures,extra],s.World,s.Camera,s.LightDirection,additionalUvs:s.AdditionalUvs);
            var frame = new OriginalFrameScene(new(appended, original.Surface.Materials,original.Surface.Profile),original.Outline,original.OutlineOptions,original.Options,original.Materials);
            var obj = composition.Add("explicit instance "+i,source,ModelRole.Character,frame);
            composition.SetTransform(obj.Id,new((i-.5f)*2,0,0),Quaternion.Identity,1);
            var layout=D3D12MaterialBindingLayout.Capture(frame);
            list.Add(new(obj.Id,new("map-"+i,"mapping "+i,layout.Fingerprint,
                [new(0,Mmd.Modern.Rendering.D3D12.D3D12CharacterMaterialRole.Hair,GenshinHair:new(layout.CaptureTexture(appended.Textures.Count-1),Multiplier:.8f))]),frame));
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
}
