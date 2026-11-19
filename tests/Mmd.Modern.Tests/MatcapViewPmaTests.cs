using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Plugins.Contracts;
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

        D3D12ViewPluginMaterialBinding[] mappings = [TestStylePlugins.Bind(a.Id, 0, ViewBinding()), TestStylePlugins.Bind(b.Id, 0, ViewBinding())];
        var pluginRequests = D3D12ViewPluginMaterialBindings.CreateRequests(mappings, TestStylePlugins.AllViewCatalog);
        var requests = pluginRequests.Select(req => req.ObjectId == a.Id
            ? new SceneObjectRenderRequest(req.ObjectId, req.BoneIndices, req.LocalTextureIndices, [..req.OptionalLocalTextureIndices, 99], includeSourceGeometry: true)
            : req).ToArray();
        var captured = project.CaptureRenderSnapshot(requests);
        Assert.Equal(0, project.CurrentFrame);
        Assert.Equal(world, captured.SceneInputs[a.Id].SourceGeometry!.World);
        Assert.Equal(source.Surface.Snapshot.Vertices.ToArray(), captured.SceneInputs[a.Id].SourceGeometry!.Vertices.ToArray());

        var modernScene = new OriginalFrameScene(captured.Scene.Surface, captured.Scene.Outline, captured.Scene.OutlineOptions,
            source.Options, captured.Scene.Materials, captured.Scene.Lighting);
        var resolved = D3D12ViewRenderingStyles.Resolve(modernScene, TestStylePlugins.AllPresetCatalog,
            captured.RenderingStyles, pluginBindings: mappings, sceneInputs: captured.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
        foreach (var instance in captured.RenderingStyles)
        {
            var draw = resolved.SurfaceStyles.CharacterMaterials[instance.MaterialStart].PluginDraw;
            Assert.NotNull(draw);
            Assert.Equal("matcap", draw.ShaderModuleId);
            var texBySlot = draw.TextureBindings!.ToDictionary(static t => t.SlotIndex, static t => t.SceneTextureIndex);
            Assert.Equal(captured.SceneInputs[instance.ObjectId].TextureIndices[0], texBySlot[1]);
            Assert.Equal(captured.SceneInputs[instance.ObjectId].TextureIndices[1], texBySlot[2]);
            Assert.Equal(captured.SceneInputs[instance.ObjectId].TextureIndices[2], texBySlot[3]);
            Assert.Contains(draw.BufferBindings!, static buf => buf.Kind == D3D12PluginBufferKind.MikkTangentCorners);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(modernScene,
            TestStylePlugins.AllPresetCatalog, captured.RenderingStyles, pluginBindings: mappings, styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void RequestUnionPreservesGeometryFlagAndBoneAndTextureSelections()
    {
        var id = Guid.NewGuid();
        var explicitReq = new SceneObjectRenderRequest(id, [4], [3], [9], includeSourceGeometry: true);
        Assert.True(explicitReq.IncludeSourceGeometry);
        Assert.Equal(new[] { 4 }, explicitReq.BoneIndices);
        Assert.Equal(new[] { 3 }, explicitReq.LocalTextureIndices);

        D3D12ViewPluginMaterialBinding[] bindings =
        [
            TestStylePlugins.Bind(id, 0, ViewBinding()),
            TestStylePlugins.Bind(id, 1, TestStylePlugins.GenshinHairAtlas(3, 5)),
            TestStylePlugins.Bind(id, 2, TestStylePlugins.FaceSdf("genshin", 6, trackingMode: StyleBoneTrackingMode.SelectedBone, boneName: "4")),
        ];
        D3D12ViewCharacterMaterial[] charMats =
        [
            new(id, 0, D3D12CharacterMaterialRole.Body),
            new(id, 1, D3D12CharacterMaterialRole.Hair),
            new(id, 2, D3D12CharacterMaterialRole.Face),
        ];
        var union = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests(bindings, TestStylePlugins.AllViewCatalog, charMats));
        Assert.True(union.IncludeSourceGeometry);
        Assert.Equal(new[] { 4 }, union.BoneIndices);
        Assert.Equal(new[] { 3, 5, 6 }, union.LocalTextureIndices.Distinct().Order());
        Assert.Equal(new[] { 0, 1, 2 }, union.OptionalLocalTextureIndices.Distinct().Order());
    }

    [Fact]
    public void SchemaFourteenRoundtripsEverySelectionAndRejectsUnknownFieldsWhileLegacySchemaFiveDecodes()
    {
        var source = Source();
        var profile = Profile(source);
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        Assert.DoesNotContain("derivedTangents", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        // Current schema 14 validation and strictness.
        var badRevDoc = JsonNode.Parse(bytes)!;
        badRevDoc["materials"]![0]!["pluginBindings"]![0]!["stringValues"]!["generatorRevision"] = "guessed-v2";
        var badRevProfile = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(badRevDoc.ToJsonString()));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            badRevProfile, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));

        var missingSlotDoc = JsonNode.Parse(bytes)!;
        missingSlotDoc["materials"]![0]!["pluginBindings"]![0]!["textureSlots"]!.AsObject().Remove("normal");
        var missingSlotProfile = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(missingSlotDoc.ToJsonString()));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            missingSlotProfile, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));

        var unknownDoc = JsonNode.Parse(bytes)!;
        unknownDoc["materials"]![0]!["pluginBindings"]![0]!["derivedTangents"] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(unknownDoc.ToJsonString())));

        var futureDoc = JsonNode.Parse(bytes)!;
        futureDoc["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(futureDoc.ToJsonString())));

        // Legacy schema 5 wire format decodes strictly and maps into schema 14 pluginBindings + textureGuards.
        var layout = D3D12MaterialBindingLayout.Capture(source);
        JsonObject TexObj(D3D12MaterialBindingProfileTexture t) => new()
        {
            ["sourceSceneTextureOrdinal"] = t.SourceSceneTextureOrdinal, ["width"] = t.Width,
            ["height"] = t.Height, ["kind"] = t.Kind.ToString(), ["rgbaSha256"] = t.RgbaSha256
        };
        var legacyDoc = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 5,
            ["stableId"] = "matcap", ["name"] = "Explicit MatCap", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint,
            ["materials"] = new JsonArray(new JsonObject
            {
                ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
                ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
                ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
                ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null,
                ["endfieldMatcap"] = new JsonObject
                {
                    ["normal"] = TexObj(layout.CaptureTexture(0)), ["p"] = TexObj(layout.CaptureTexture(1)),
                    ["matcap"] = TexObj(layout.CaptureTexture(2)), ["matcapEncoding"] = "SrgbRgb",
                    ["uvChannel"] = 0, ["baseColor"] = new JsonArray(.2f, .3f, .4f),
                    ["highlightColor"] = new JsonArray(.1f, .4f, .2f), ["normalStrength"] = 1.7f,
                    ["rotationRadians"] = .42f, ["highlightCoefficient"] = .2f, ["shadowHardness"] = .4f,
                    ["lightControlDirection"] = new JsonArray(.2f, .4f, .8f), ["baselineStrength"] = 1.3f,
                    ["coupleEndfieldPalette"] = false, ["generatorRevision"] = "mikk-v1"
                }
            })
        };
        var decodedLegacy = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyDoc.ToJsonString()));
        Assert.Equal(3, decodedLegacy.Materials[0].TextureGuards.Count);
        Assert.Equal("matcap", Assert.Single(decodedLegacy.Materials[0].PluginBindings).ModeId);

        foreach (string damage in new[] { "generator", "missing", "unknown", "old-schema" })
        {
            var broken = JsonNode.Parse(legacyDoc.ToJsonString())!;
            var map = broken["materials"]![0]!["endfieldMatcap"]!.AsObject();
            if (damage == "generator") map["generatorRevision"] = "guessed-v2";
            if (damage == "missing") map.Remove("normal");
            if (damage == "unknown") map["derivedTangents"] = new JsonArray();
            if (damage == "old-schema") broken["schemaVersion"] = 4;
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(broken.ToJsonString())));
        }
    }

    [Fact]
    public void LegacySchemaFourDecodesIntoHairAndReencodesAsSchemaFourteen()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var image = layout.CaptureTexture(0);
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
        var legacy = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 4,
            ["stableId"] = "legacy", ["name"] = "Legacy", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint, ["materials"] = new JsonArray(row)
        };
        var decoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        var b = Assert.Single(decoded.Materials[0].PluginBindings);
        Assert.Equal("endfield", b.StyleId);
        Assert.Equal("hair", b.ModeId);
        Assert.Single(decoded.Materials[0].TextureGuards);
        var expected = new D3D12MaterialBindingProfile("legacy", "Legacy", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair,
                pluginBindings: [T8dTestHelpers.EndfieldHair(0, new(.2f, .4f, .8f), -.5f)],
                textureGuards: [image])]);
        Assert.Equal(D3D12MaterialBindingProfileJson.Encode(expected), D3D12MaterialBindingProfileJson.Encode(decoded));
    }

    [Fact]
    public void DetachedHostConfigurationIsImmutableAndRejectsInvalidIndicesNumbersAndDuplicateMaterials()
    {
        var id = Guid.NewGuid();
        D3D12ViewPluginMaterialBinding[] input = [TestStylePlugins.Bind(id, 0, ViewBinding())];
        var frozen = D3D12ViewPluginMaterialBindings.Detach(input);
        input[0] = input[0] with { LocalMaterialIndex = 99 };
        Assert.Equal(0, frozen[0].LocalMaterialIndex);
        Assert.Same(frozen, D3D12ViewPluginMaterialBindings.Detach(frozen));

        foreach (var invalid in new[]
        {
            frozen[0] with { ObjectId = Guid.Empty },
            frozen[0] with { LocalMaterialIndex = -1 },
            TestStylePlugins.Bind(id, 0, ViewSpec().ToBinding() with { NumberValues = new Dictionary<string, double> { ["uvChannel"] = 0, ["normalStrength"] = float.NaN, ["rotationRadians"] = 0, ["highlightCoefficient"] = 0, ["shadowHardness"] = .5, ["baselineStrength"] = 1 } }),
        })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Detach([invalid]));

        var charMat = new D3D12ViewCharacterMaterial[] { new(id, 0, D3D12CharacterMaterialRole.Body) };
        foreach (var invalid in new[]
        {
            TestStylePlugins.Bind(id, 0, (ViewSpec() with { NormalSlot = -1 }).ToBinding()),
            TestStylePlugins.Bind(id, 0, (ViewSpec() with { PSlot = -1 }).ToBinding()),
            TestStylePlugins.Bind(id, 0, (ViewSpec() with { MatcapSlot = -1 }).ToBinding()),
            TestStylePlugins.Bind(id, 0, (ViewSpec() with { UvChannel = 5 }).ToBinding()),
            TestStylePlugins.Bind(id, 0, (ViewSpec() with { LightControlDirection = Vector3.Zero }).ToBinding()),
        })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate(charMat, [invalid], TestStylePlugins.AllViewCatalog));

        Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate(charMat, [frozen[0], frozen[0]], TestStylePlugins.AllViewCatalog));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void DormantProfilesGuardAllThreeImagesButDoNotActivate(string pipeline)
    {
        var source = Source(); var id = Guid.NewGuid(); var profile = Profile(source);
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog);
        Assert.Empty(resolved.PluginBindings);
        for (int index = 0; index < 3; index++)
        {
            var images = source.Surface.Snapshot.Textures.ToArray();
            images[index] = new("changed", 1, 1, [0, 0, 0, 0]);
            var changed = D3D12EndfieldHairBindingTests.Frame(textures: images);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, changed, id, pipeline, TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact]
    public void RolePaletteAndUvConflictsRejectWhileExplicitCoupledRimAndHairRemainAllowed()
    {
        var source = Source(); var baseSpec = MatcapSpec(); var matcap = baseSpec.ToBinding();
        foreach (var role in new[] { StyleMaterialRole.Face, StyleMaterialRole.Eye })
            Assert.False(T8dTestHelpers.ValidateEndfield(matcap, role).IsValid);

        Assert.False(T8dTestHelpers.ValidateEndfield([matcap, T8dTestHelpers.EndfieldRim(Vector3.One, Vector3.One)], StyleMaterialRole.Body).IsValid);

        var coupledMatcap = (baseSpec with { CoupleEndfieldPalette = true }).ToBinding();
        var coupledRim = T8dTestHelpers.EndfieldRim(baseSpec.EffectiveBaseColor, new(.5f), rangeStrength: baseSpec.BaselineStrength);
        var coupledHair = T8dTestHelpers.EndfieldHair(baseSpec.NormalSlot, baseSpec.EffectiveBaseColor);
        Assert.True(T8dTestHelpers.ValidateEndfield([coupledMatcap, coupledRim, coupledHair], StyleMaterialRole.Hair).IsValid);

        Assert.False(T8dTestHelpers.ValidateEndfield([coupledMatcap, coupledRim, T8dTestHelpers.EndfieldHair(baseSpec.NormalSlot, Vector3.One)], StyleMaterialRole.Hair).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([coupledMatcap, T8dTestHelpers.EndfieldRim(baseSpec.EffectiveBaseColor, new(.5f), rangeStrength: 1f), coupledHair], StyleMaterialRole.Hair).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([coupledMatcap, T8dTestHelpers.EndfieldRim(baseSpec.EffectiveBaseColor, new(.5f), rangeStrength: baseSpec.BaselineStrength, radialStrength: 1f), coupledHair], StyleMaterialRole.Hair).IsValid);

        var id = Guid.NewGuid();
        var invalidUv = (baseSpec with { UvChannel = 4 }).ToBinding();
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(
            source, TestStylePlugins.AllPresetCatalog,
            [new SceneRenderingStyleBinding(id, new("endfield"), ModelRole.Character, 0, 1, true)],
            [new D3D12ViewCharacterMaterial(id, 0, D3D12CharacterMaterialRole.Body)],
            [TestStylePlugins.Bind(id, 0, invalidUv)],
            sceneInputs: new Dictionary<Guid, SceneObjectRenderInputs> { [id] = TestStylePlugins.CreateSceneInputs(id, source.Surface.Snapshot.Textures.Count) },
            styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void PortableThreeImagesAndSchemaFourteenReopenAfterSourceRemovalAndDefaultResave()
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
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "endfield", TestStylePlugins.AllViewCatalog);
                var selection = Assert.Single(mapped.PluginBindings);
                Assert.Equal(offset, selection.Binding.TextureSlots!["normal"]);
                Assert.Equal(offset + 1, selection.Binding.TextureSlots!["p"]);
                Assert.Equal(offset + 2, selection.Binding.TextureSlots!["matcap"]);
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(mapped.PluginBindings, TestStylePlugins.AllViewCatalog));
                for (int i = 0; i < 3; i++)
                    Assert.Equal(extended.Textures[offset + i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[offset + i].Rgba.ToArray());
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, again[item.Id].SchemaVersion);
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

    private static EndfieldMatcapSpec ViewSpec()
        => new(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f,.3f,.4f), new(.1f,.4f,.2f));
    private static StyleMaterialBindingData ViewBinding() => ViewSpec().ToBinding();
    private static EndfieldMatcapSpec MatcapSpec(int imageOffset = 0)
        => new(imageOffset, imageOffset + 1, imageOffset + 2, T8dTestHelpers.EncodingSrgbRgb, 0,
            new(.2f,.3f,.4f), new(.1f,.4f,.2f), 1.7f, .42f, .2f, .4f, new(.2f,.4f,.8f), 1.3f);
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int imageOffset = 0)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        return new("matcap", "Explicit MatCap", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Body,
            pluginBindings: [MatcapSpec(imageOffset).ToBinding()],
            textureGuards: [layout.CaptureTexture(imageOffset), layout.CaptureTexture(imageOffset + 1), layout.CaptureTexture(imageOffset + 2)])]);
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
