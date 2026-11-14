using System.Numerics;
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

public sealed class MatcapViewPmaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SourceGeometryIsOptInAndSameCaptureRetainsInstanceWorldAndThreeImages()
    {
        var editor = new SceneCompositionEditor();
        var world = Matrix4x4.CreateScale(-2, 3, 4) * Matrix4x4.CreateRotationY(.4f);
        var source = Source(world);
        var a = editor.Add("explicit a", Path.Combine(Path.GetTempPath(), "matcap-a.pmx"), ModelRole.Character, source);
        var b = editor.Add("explicit b", Path.Combine(Path.GetTempPath(), "matcap-b.pmx"), ModelRole.Character, Source());
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        Assert.Null(project.CaptureRenderSnapshot([new(a.Id)]).SceneInputs[a.Id].SourceGeometry);
        D3D12ViewEndfieldMatcapBinding[] mappings = [View(a.Id), View(b.Id)];
        var requests = D3D12ViewEndfieldMatcapBindings.CreateRequests(mappings,
            [new(a.Id, boneIndices: [], optionalLocalTextureIndices: [99])]);
        var captured = project.CaptureRenderSnapshot(requests);
        Assert.Equal(0, project.CurrentFrame);
        Assert.Equal(world, captured.SceneInputs[a.Id].SourceGeometry!.World);
        Assert.Equal(source.Surface.Snapshot.Vertices.ToArray(), captured.SceneInputs[a.Id].SourceGeometry!.Vertices.ToArray());
        var modernScene=new OriginalFrameScene(captured.Scene.Surface,captured.Scene.Outline,captured.Scene.OutlineOptions,
            source.Options,captured.Scene.Materials,captured.Scene.Lighting);
        var resolved = D3D12ViewRenderingStyles.Resolve(modernScene, D3D12StylePresetCatalog.BuiltIn,
            captured.RenderingStyles, sceneInputs: captured.SceneInputs, endfieldMatcapDataBindings: mappings);
        foreach (var instance in captured.RenderingStyles)
        {
            var binding = Assert.IsType<D3D12EndfieldMatcapBinding>(resolved.SurfaceStyles.CharacterMaterials[instance.MaterialStart].EndfieldMatcap);
            Assert.Equal(captured.SceneInputs[instance.ObjectId].TextureIndices[0], binding.NormalCombinedTextureIndex);
            Assert.Equal(captured.SceneInputs[instance.ObjectId].TextureIndices[1], binding.PCombinedTextureIndex);
            Assert.Equal(captured.SceneInputs[instance.ObjectId].TextureIndices[2], binding.MatcapCombinedTextureIndex);
            Assert.NotNull(binding.Geometry);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(modernScene,
            D3D12StylePresetCatalog.BuiltIn, captured.RenderingStyles, endfieldMatcapDataBindings: mappings));
    }

    [Fact]
    public void RequestUnionNeverLosesGeometryFlagOrExistingBoneAndTextureSelections()
    {
        var id = Guid.NewGuid();
        var request = Assert.Single(D3D12ViewEndfieldMatcapBindings.CreateRequests([View(id)],
            [new(id, [4], [3], [9], includeSourceGeometry: true)]));
        Assert.True(request.IncludeSourceGeometry);
        Assert.Equal(new[] { 4 }, request.BoneIndices);
        Assert.Equal(new[] { 3 }, request.LocalTextureIndices);
        Assert.Equal(new[] { 0, 1, 2, 9 }, request.OptionalLocalTextureIndices.Order());
        var union = Assert.Single(D3D12ViewGenshinHairAtlasBindings.CreateRequests([], [request]));
        Assert.True(union.IncludeSourceGeometry);
        Assert.Equal(request.BoneIndices, union.BoneIndices);
    }

    [Fact]
    public void SchemaFiveRoundtripsEverySelectionAndRejectsUnknownRevisionAndMissingFields()
    {
        var profile = Profile(Source());
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(5, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        Assert.DoesNotContain("derivedTangents", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        foreach (string damage in new[] { "generator", "missing", "unknown", "old-schema" })
        {
            var doc = JsonNode.Parse(bytes)!;
            var map = doc["materials"]![0]!["endfieldMatcap"]!.AsObject();
            if (damage == "generator") map["generatorRevision"] = "guessed-v2";
            if (damage == "missing") map.Remove("normal");
            if (damage == "unknown") map["derivedTangents"] = new JsonArray();
            if (damage == "old-schema") doc["schemaVersion"] = 4;
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
    }

    [Fact]
    public void LegacySchemaFourRetainsItsExactWireBytesWithoutMatcapNullPlaceholder()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var image = layout.CaptureTexture(0);
        var profile = new D3D12MaterialBindingProfile("legacy", "Legacy", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair, EndfieldHair: new(image, new(.2f,.4f,.8f), -.5f))]);
        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Hair", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
            ["endfieldRim"] = null, ["genshinHairAtlas"] = null,
            ["endfieldHair"] = new JsonObject
            {
                ["texture"] = new JsonObject
                {
                    ["sourceSceneTextureOrdinal"] = image.SourceSceneTextureOrdinal, ["width"] = image.Width,
                    ["height"] = image.Height, ["kind"] = image.Kind.ToString(), ["rgbaSha256"] = image.RgbaSha256
                },
                ["paletteColor"] = new JsonArray(.2f,.4f,.8f), ["brightness"] = -.5f
            }
        };
        var expected = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 4,
            ["stableId"] = "legacy", ["name"] = "Legacy", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = profile.LayoutFingerprint, ["materials"] = new JsonArray(row)
        };
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(Encoding.UTF8.GetBytes(expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), bytes);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
    }

    [Fact]
    public void DetachedHostConfigurationIsImmutableAndRejectsInvalidIndicesNumbersAndDuplicateMaterials()
    {
        var id = Guid.NewGuid(); var value = View(id);
        D3D12ViewEndfieldMatcapBinding[] input = [value];
        var frozen = D3D12ViewEndfieldMatcapBindings.Detach(input);
        input[0] = value with { NormalLocalTextureIndex = 99 };
        Assert.Equal(0, frozen[0].NormalLocalTextureIndex);
        Assert.Same(frozen, D3D12ViewEndfieldMatcapBindings.Detach(frozen));
        foreach (var invalid in new[] { value with { ObjectId = Guid.Empty }, value with { LocalMaterialIndex = -1 },
            value with { NormalLocalTextureIndex = -1 }, value with { PLocalTextureIndex = -1 }, value with { MatcapLocalTextureIndex = -1 },
            value with { UvChannel = 5 }, value with { NormalStrength = float.NaN }, value with { LightControlDirection = Vector3.Zero } })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewEndfieldMatcapBindings.Detach([invalid]));
        Assert.Throws<ArgumentException>(() => D3D12ViewEndfieldMatcapBindings.Detach([value, value]));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void DormantProfilesGuardAllThreeImagesAndUvButDoNotActivate(string pipeline)
    {
        var source = Source(); var id = Guid.NewGuid(); var profile = Profile(source);
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline);
        Assert.Empty(resolved.EndfieldMatcapBindings);
        Assert.Null(Assert.Single(resolved.CharacterMaterials).Style.EndfieldMatcap);
        for (int index = 0; index < 3; index++)
        {
            var images = source.Surface.Snapshot.Textures.ToArray();
            images[index] = new("changed", 1, 1, [0, 0, 0, 0]);
            var changed = D3D12EndfieldHairBindingTests.Frame(textures: images);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, changed, id, pipeline));
        }
    }

    [Fact]
    public void RolePaletteAndUvConflictsRejectWhileExplicitCoupledRimAndHairRemainAllowed()
    {
        var source = Source(); var profile = Profile(source); var row = profile.Materials[0];
        foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => (row with { Role = role }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { EndfieldRim = new(Vector3.One, Vector3.One) }).Validate());
        var coupled = row with { EndfieldMatcap = row.EndfieldMatcap! with { CoupleEndfieldPalette = true },
            EndfieldRim = new(row.EndfieldMatcap.BaseColor, new(.5f), RangeStrength: row.EndfieldMatcap.BaselineStrength),
            Role = D3D12CharacterMaterialRole.Hair,
            EndfieldHair = new(row.EndfieldMatcap.Normal, row.EndfieldMatcap.BaseColor) };
        coupled.Validate();
        Assert.Throws<ArgumentException>(() => (coupled with { EndfieldHair = coupled.EndfieldHair! with { PaletteColor = Vector3.One } }).Validate());
        Assert.Throws<ArgumentException>(() => (coupled with { EndfieldRim = coupled.EndfieldRim! with { RangeStrength = 1f } }).Validate());
        Assert.Throws<ArgumentException>(() => (coupled with { EndfieldRim = coupled.EndfieldRim! with { RadialStrength = 1f } }).Validate());
        var invalidUv = new D3D12MaterialBindingProfile("uv", "UV", profile.LayoutFingerprint,
            [row with { EndfieldMatcap = row.EndfieldMatcap! with { UvChannel = 4 } }]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(invalidUv, source, Guid.NewGuid(), "mmd"));
    }

    [Fact]
    public void PortableThreeImagesAndSchemaFiveReopenAfterSourceRemovalAndDefaultResave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "matcap-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "source.pmx");
            var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
            var clean = new PmxModel {
                Header=template.Header,Name="portable MatCap fixture",EnglishName="",Comment="",EnglishComment="",
                Vertices=template.Vertices,Faces=template.Faces,Textures=[],Bones=template.Bones,Morphs=[],
                DisplayFrames=[],RigidBodies=[],Joints=[],SoftBodies=[],
                Materials=[new PmxMaterial { Name="fixture",EnglishName="",Diffuse=new(.5f,.4f,.3f,1),Specular=Vector3.Zero,
                    SpecularPower=10,Ambient=new(.2f),EdgeColor=Vector4.Zero,EdgeSize=0,DrawMode=PmxDrawModeFlags.BothFaces,
                    TextureIndex=-1,SphereTextureIndex=-1,SphereMode=PmxSphereMode.None,ToonMode=PmxToonMode.Common,
                    ToonTextureIndex=0,Memo="",FaceVertexCount=template.Faces.Count*3 }] };
            File.WriteAllBytes(originalPath,PmxWriter.ToBytes(clean));
            var model = PmxReader.Read(originalPath); var original = Rest(model); var snapshot = original.Surface.Snapshot;
            int offset = snapshot.Textures.Count;
            var extended = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, snapshot.Materials,
                [..snapshot.Textures, ..Source().Surface.Snapshot.Textures], snapshot.World, snapshot.Camera,
                snapshot.LightDirection, snapshot.AdditionalUvs, snapshot.RawRadialSeeds);
            var source = new OriginalFrameScene(new(extended, original.Surface.Materials, original.Surface.Profile),
                original.Outline, original.OutlineOptions, original.Options, original.Materials, original.Lighting);
            var editor = new SceneCompositionEditor(); var item = editor.Add("portable MatCap", originalPath, ModelRole.Character, source);
            using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
            project.SetDefaultRenderingStyle(new("endfield"), Token);
            var profile = Profile(source, offset);
            string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project,
                [new(item.Id, profile, source)]), Token);
            string moved = Path.Combine(directory, "moved.pma"); File.Move(first, moved); File.Delete(originalPath);
            string resaved = Path.Combine(directory, "resaved.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load, new NoPhysics(),
                out var profiles, cancellationToken: Token))
            {
                var restored = loaded.Session.Objects.Single().Source;
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(profiles[item.Id]));
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "endfield");
                var selection = Assert.Single(mapped.EndfieldMatcapBindings);
                Assert.Equal(offset, selection.NormalLocalTextureIndex);
                Assert.Equal(offset + 1, selection.PLocalTextureIndex); Assert.Equal(offset + 2, selection.MatcapLocalTextureIndex);
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewEndfieldMatcapBindings.CreateRequests(mapped.EndfieldMatcapBindings));
                Assert.NotNull(capture.SceneInputs[item.Id].SourceGeometry);
                for (int i = 0; i < 3; i++)
                    Assert.Equal(extended.Textures[offset + i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[offset + i].Rgba.ToArray());
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(5, again[item.Id].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(again[item.Id]));
            Assert.DoesNotContain(reopened.ResourcePaths, p => p.Contains("tangent", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0,0,-5), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }

    private static D3D12ViewEndfieldMatcapBinding View(Guid id)
        => new(id, 0, 0, 1, 2, D3D12MatcapColorEncoding.SrgbRgb, 0, new(.2f,.3f,.4f), new(.1f,.4f,.2f));
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int imageOffset = 0)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        return new("matcap", "Explicit MatCap", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Body,
            EndfieldMatcap: new(layout.CaptureTexture(imageOffset), layout.CaptureTexture(imageOffset + 1), layout.CaptureTexture(imageOffset + 2),
                D3D12MatcapColorEncoding.SrgbRgb, 0, new(.2f,.3f,.4f), new(.1f,.4f,.2f),
                1.7f, .42f, .2f, .4f, new(.2f,.4f,.8f), 1.3f))]);
    }
    private static OriginalFrameScene Source(Matrix4x4? world = null)
    {
        var frame=D3D12EndfieldHairBindingTests.Frame(textures: [new("N",1,1,[128,140,255,255]),
            new("P",1,1,[70,0,0,80]), new("Matcap",1,1,[12,200,130,170])], world: world);
        var outline=new OriginalOutlineData(frame.Surface.Snapshot.Vertices.ToArray().Select(v=>
            new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,0)),
            [new OriginalOutlineMaterial(0,Vector4.Zero,false)]);
        return new(frame.Surface,outline,new(Vector4.One,Matrix4x4.Identity,new(0,0,-5),30,0),frame.Options,frame.Materials);
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
