using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class EndfieldHairPmaSeedTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "endfield-pma-seeds-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public EndfieldHairPmaSeedTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void BinaryCodecPreservesSignedNonunitAndZeroBitsAndRejectsInvalidLengthsBeforeAllocation()
    {
        var seeds = new SceneRawRadialSeeds(new Vector3[] { new(-0f, -2.25f, 3.5f), Vector3.Zero, new(float.Epsilon, float.MaxValue, -1) });
        byte[] bytes = D3D12MaterialBindingSeedResource.Encode(seeds);
        Assert.Equal(36, bytes.Length);
        Assert.Equal(unchecked((int)0x80000000), BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.True(D3D12MaterialBindingSeedResource.SameBits(seeds, D3D12MaterialBindingSeedResource.Decode(bytes, 3)));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), D3D12MaterialBindingSeedResource.Digest(seeds));
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingSeedResource.ByteLength(int.MaxValue));
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingSeedResource.Decode(bytes.AsSpan(1), 3));
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingSeedResource.Decode(new byte[37], 3));
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingSeedResource.Decode(bytes, 3));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoMovedInstancesRestoreSeedsBeforeCompleteLayoutAndKeepThemThroughDefaultResave(bool dedicated)
    {
        using var project = Project(out var entries, dedicated: dedicated);
        string first = Path.Combine(directory, "first.pma"), moved = Path.Combine(directory, "moved.pma"), resaved = Path.Combine(directory, "resaved.pma");
        var save = D3D12MaterialBindingPma.CreateSaveOptions(project, entries);
        Assert.Equal(2, save.AdditionalResources.Keys.Count(path => path.EndsWith(".f32le", StringComparison.Ordinal)));
        PmaProjectStore.Save(first, project, save, Token); File.Move(first, moved); File.Delete(project.Objects[0].SourcePath);
        using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load, new NoPhysics(), out var profiles, cancellationToken: Token))
        {
            Check(loaded.Session, profiles);
            PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
        }
        using var again = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(), out var maps, cancellationToken: Token);
        Check(again.Session, maps);
        Assert.False(D3D12MaterialBindingSeedResource.SameBits(again.Session.Objects[0].Source.Surface.Snapshot.RawRadialSeeds!, again.Session.Objects[1].Source.Surface.Snapshot.RawRadialSeeds!));

        void Check(SceneProjectSession owner, IReadOnlyDictionary<Guid,D3D12MaterialBindingProfile> mappings)
        {
            foreach (var entry in entries)
            {
                var restored = owner.Objects.Single(obj => obj.Id == entry.ObjectId).Source;
                Assert.True(D3D12MaterialBindingSeedResource.SameBits(entry.SourceRest.Surface.Snapshot.RawRadialSeeds!, restored.Surface.Snapshot.RawRadialSeeds!));
                Assert.Equal(entry.Profile.LayoutFingerprint, D3D12MaterialBindingLayout.Capture(restored).Fingerprint);
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(entry.Profile), D3D12MaterialBindingProfileJson.Encode(mappings[entry.ObjectId]));
                for (int slot = 0; slot < restored.Surface.Snapshot.Textures.Count; slot++)
                    Assert.Equal(entry.SourceRest.Surface.Snapshot.Textures[slot].Rgba.ToArray(), restored.Surface.Snapshot.Textures[slot].Rgba.ToArray());
                Assert.Equal(dedicated ? 1 : 0, D3D12MaterialBindingProfileResolver.Resolve(mappings[entry.ObjectId], restored, entry.ObjectId, "endfield").EndfieldHairBindings.Count);
                Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(mappings[entry.ObjectId], restored, entry.ObjectId, "mmd").EndfieldHairBindings);
            }
        }
    }

    [Fact]
    public void SeedFreeMetadataMatchesLegacyBytesAndNullStaysDistinctFromAuthoredZero()
    {
        using var project = Project(out var entries, seeds: false, dedicated: false);
        var save = D3D12MaterialBindingPma.CreateSaveOptions(project, entries);
        foreach (var entry in entries)
        {
            var expected = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, objectId = entry.ObjectId,
                profile = JsonSerializer.Deserialize<JsonElement>(D3D12MaterialBindingProfileJson.Encode(entry.Profile)),
                textures = entry.SourceRest.Surface.Snapshot.Textures.Select((texture, ordinal) => new { width = texture.Width, height = texture.Height,
                    kind = texture.Kind, sha256 = D3D12MaterialBindingLayout.Capture(entry.SourceRest).CaptureTexture(ordinal).RgbaSha256 }).ToArray() });
            string metadata = save.AdditionalResources.Keys.Single(path => path.Contains(entry.ObjectId.ToString("N"), StringComparison.Ordinal) && path.EndsWith("binding.json", StringComparison.Ordinal));
            Assert.Equal(expected, save.AdditionalResources[metadata].ToArray());
        }
        Assert.DoesNotContain(save.AdditionalResources.Keys, path => path.EndsWith(".f32le", StringComparison.Ordinal));
        string path = Path.Combine(directory, "legacy.pma"); PmaProjectStore.Save(path, project, save, Token);
        using var loaded = D3D12MaterialBindingPma.Load(path, Path.Combine(directory, "cache"), Load, new NoPhysics(), out _, cancellationToken: Token);
        Assert.All(loaded.Session.Objects, obj => Assert.Null(obj.Source.Surface.Snapshot.RawRadialSeeds));
        var source = entries[0].SourceRest; var s = source.Surface.Snapshot;
        var zero = Replace(source, s.WithRawRadialSeeds(new SceneRawRadialSeeds(new Vector3[s.Vertices.Length])));
        Assert.NotEqual(D3D12MaterialBindingLayout.Capture(source).Fingerprint, D3D12MaterialBindingLayout.Capture(zero).Fingerprint);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("checksum")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("count")]
    [InlineData("length")]
    [InlineData("encoding")]
    [InlineData("hash-shape")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("descriptor")]
    public void InvalidSeedResourcesFailBeforeAnySolverIsCreated(string damage)
    {
        using var project = Project(out var entries);
        var save = D3D12MaterialBindingPma.CreateSaveOptions(project, entries);
        var resources = save.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value);
        string root = "render-resources/material-bindings/" + entries[0].ObjectId.ToString("N") + "/";
        string metadata = root + "binding.json", seed = root + D3D12MaterialBindingSeedResource.RelativePath;
        var doc = JsonNode.Parse(resources[metadata].Span)!.AsObject(); var descriptor = doc["rawRadialSeeds"]!.AsObject();
        byte[] bytes = resources[seed].ToArray();
        switch (damage)
        {
            case "truncated": resources[seed] = bytes[..^1]; break;
            case "extra": resources[seed] = bytes.Concat(new byte[1]).ToArray(); break;
            case "checksum": bytes[0] ^= 1; resources[seed] = bytes; break;
            case "nan": case "infinity":
                BinaryPrimitives.WriteSingleLittleEndian(bytes, damage == "nan" ? float.NaN : float.PositiveInfinity);
                resources[seed] = bytes; descriptor["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)); break;
            case "count": descriptor["vertexCount"] = 2; break;
            case "length": descriptor["byteLength"] = long.MaxValue; break;
            case "encoding": descriptor["encoding"] = "native-vector3"; break;
            case "hash-shape": descriptor["sha256"] = new string('A', 64); break;
            case "missing": descriptor.Remove("vertexCount"); break;
            case "unknown": descriptor["path"] = "other-resource"; break;
            case "descriptor": doc.Remove("rawRadialSeeds"); break;
        }
        resources[metadata] = Encoding.UTF8.GetBytes(doc.ToJsonString());
        string path = Path.Combine(directory, damage + ".pma"); PmaProjectStore.Save(path, project, save with { AdditionalResources = resources }, Token);
        var physics = new NoPhysics();
        Assert.Throws<SceneProjectAssetException>(() => D3D12MaterialBindingPma.Load(path, Path.Combine(directory, "cache"), Load, physics, out _, cancellationToken: Token));
        Assert.Equal(0, physics.Created);
    }

    [Fact]
    public void LoaderSeedConflictsAndChangedGeometryOrMaterialFailAfterRestoration()
    {
        using var project = Project(out var entries);
        string path = Path.Combine(directory, "guard.pma");
        PmaProjectStore.Save(path, project, D3D12MaterialBindingPma.CreateSaveOptions(project, entries.Take(1)), Token);
        using (var identical = D3D12MaterialBindingPma.Load(path, Path.Combine(directory, "same"),
            (file, token) => { var a = Load(file, token); return new(a.Model, Replace(a.Source, a.Source.Surface.Snapshot.WithRawRadialSeeds(entries[0].SourceRest.Surface.Snapshot.RawRadialSeeds))); },
            new NoPhysics(), out _, cancellationToken: Token)) Assert.Equal(2, identical.Session.Objects.Count);
        foreach (string damage in new[] { "seed", "signed-zero", "geometry", "material" })
        {
            Assert.Throws<SceneProjectAssetException>(() => D3D12MaterialBindingPma.Load(path, Path.Combine(directory, damage), (file, token) =>
            {
                var a = Load(file, token); var s = a.Source.Surface.Snapshot;
                if (damage is "seed" or "signed-zero")
                {
                    var values = entries[0].SourceRest.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray();
                    values[0] = damage == "signed-zero" ? values[0] with { X = 0f } : Vector3.One;
                    s = s.WithRawRadialSeeds(new SceneRawRadialSeeds(values));
                }
                else if (damage == "material") s = s.WithMaterials(s.Materials.Select(m => m with { Diffuse = Vector4.One }));
                else
                {
                    var vertices = s.Vertices.ToArray(); vertices[0] = vertices[0] with { Position = vertices[0].Position + Vector3.UnitX };
                    s = new(vertices, s.Indices, s.Materials, s.Textures, s.World, s.Camera, s.LightDirection, additionalUvs:s.AdditionalUvs);
                }
                return new(a.Model, Replace(a.Source, s));
            }, new NoPhysics(), out _, cancellationToken: Token));
        }
    }

    [Fact]
    public void SeedPathsAndAllPendingAndRetainedBudgetsAreReservedBeforePayloadClone()
    {
        using var project = Project(out var entries);
        var initial = D3D12MaterialBindingPma.CreateSaveOptions(project, entries);
        long total = initial.AdditionalResources.Sum(pair => (long)pair.Value.Length);
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingPma.CreateSaveOptions(project, entries, new() { ValidationOptions = new() { MaxExpandedBytes = total - 1 } }));
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingPma.CreateSaveOptions(project, entries, new() { ValidationOptions = new() { MaxEntryBytes = 35 } }));
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingPma.CreateSaveOptions(project, entries, new() { ValidationOptions = new() { MaxFiles = initial.AdditionalResources.Count - 1 } }));
        string collision = initial.AdditionalResources.Keys.First(path => path.EndsWith(".f32le", StringComparison.Ordinal)).ToUpperInvariant();
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingPma.CreateSaveOptions(project, entries, new() { AdditionalResources = new Dictionary<string,ReadOnlyMemory<byte>> { [collision] = new byte[1] } }));
        var withExtra = initial.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value); withExtra["render-resources/retained.bin"] = new byte[32];
        string path = Path.Combine(directory, "retained.pma"); PmaProjectStore.Save(path, project, initial with { AdditionalResources = withExtra }, Token);
        using var loaded = D3D12MaterialBindingPma.Load(path, Path.Combine(directory, "cache"), Load, new NoPhysics(), out var profiles, cancellationToken:Token);
        var selected = loaded.Session.Objects.Select(obj => new D3D12MaterialBindingPmaEntry(obj.Id, profiles[obj.Id], obj.Source));
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingPma.CreateSaveOptions(loaded.Session, selected, new() { ValidationOptions = new() { MaxExpandedBytes = total + 31 } }));
        var boundary = D3D12MaterialBindingPma.CreateSaveOptions(loaded.Session, selected, new() { ValidationOptions = new() { MaxExpandedBytes = total + 32 } });
        Assert.Equal(total, boundary.AdditionalResources.Sum(pair => (long)pair.Value.Length));
    }

    private static OriginalFrameScene Replace(OriginalFrameScene original, SceneSnapshot snapshot)
        => new(new(snapshot, original.Surface.Materials, original.Surface.Profile), original.Outline, original.OutlineOptions,
            original.Options, original.Materials, original.Lighting, original.EditorOverlay);

    private SceneProjectSession Project(out D3D12MaterialBindingPmaEntry[] entries, bool seeds = true, bool dedicated = true)
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
            var authored = new SceneRawRadialSeeds(new Vector3[] { new(-0f, -2.25f-i, 3.5f), Vector3.Zero, new(1.25f+i, -4, 0) });
            var appended = new SceneSnapshot(s.Vertices,s.Indices,s.Materials,[..s.Textures,extra],s.World,s.Camera,s.LightDirection,additionalUvs:s.AdditionalUvs,rawRadialSeeds:seeds ? authored : null);
            var frame = new OriginalFrameScene(new(appended, original.Surface.Materials,original.Surface.Profile),original.Outline,original.OutlineOptions,original.Options,original.Materials);
            var obj = composition.Add("explicit instance "+i,source,ModelRole.Character,frame);
            composition.SetTransform(obj.Id,new((i-.5f)*2,0,0),Quaternion.Identity,1);
            var layout=D3D12MaterialBindingLayout.Capture(frame);
            list.Add(new(obj.Id,new("map-"+i,"mapping "+i,layout.Fingerprint,
                [new(0,D3D12CharacterMaterialRole.Hair,
                    EndfieldHair:dedicated ? new(layout.CaptureTexture(appended.Textures.Count-1),new(.2f,.4f,.8f),-.5f) : null)]),frame));
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
        public int Created { get; private set; }
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s,EditableSceneObject o){ Created++; throw new InvalidOperationException(); }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s){ Created++; throw new InvalidOperationException(); }
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
