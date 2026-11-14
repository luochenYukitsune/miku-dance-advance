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

public sealed class GenshinMetallicViewPmaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DetachedSelectorsRetainEveryConstantAndRejectInvalidHostConfiguration()
    {
        var value = View(Guid.NewGuid());
        D3D12ViewGenshinMetallicBinding[] input = [value];
        var frozen = D3D12ViewGenshinMetallicBindings.Detach(input);
        input[0] = value with { LightmapLocalTextureIndex = 77 };
        Assert.Equal(value, Assert.Single(frozen));
        Assert.Same(frozen, D3D12ViewGenshinMetallicBindings.Detach(frozen));
        foreach (var invalid in new[] { value with { ObjectId = Guid.Empty }, value with { LocalMaterialIndex = -1 },
            value with { LightmapLocalTextureIndex = -1 }, value with { MatcapLocalTextureIndex = -1 },
            value with { DarkColor = new(float.NaN) }, value with { LightColor = new(float.PositiveInfinity) },
            value with { ShadowMultiplyColor = new(float.NaN) }, value with { EnableMetallics = float.NaN },
            value with { TileScale = float.NaN }, value with { Brightness = float.NaN },
            value with { UniformLitFactor = float.NaN }, value with { LitMode = (D3D12GenshinMetallicLitMode)99 },
            value with { MatcapEncoding = (D3D12MatcapColorEncoding)99 } })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.Detach([invalid]));
        Assert.Throws<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.Detach([value, value]));
    }

    [Fact]
    public void RequestUnionRetainsEndfieldGeometryAndAllExistingSelections()
    {
        var id = Guid.NewGuid();
        var existing = new SceneObjectRenderRequest(id, [4], [3], [8], includeSourceGeometry: true);
        var request = Assert.Single(D3D12ViewGenshinMetallicBindings.CreateRequests([View(id)], [existing]));
        Assert.True(request.IncludeSourceGeometry);
        Assert.Equal(new[] { 4 }, request.BoneIndices);
        Assert.Equal(new[] { 3 }, request.LocalTextureIndices);
        Assert.Equal(new[] { 0, 1, 8 }, request.OptionalLocalTextureIndices.Order());
        Assert.False(Assert.Single(D3D12ViewGenshinMetallicBindings.CreateRequests([View(id)])).IncludeSourceGeometry);
        var unchanged = Assert.Single(D3D12ViewGenshinMetallicBindings.CreateRequests([], [request]));
        Assert.Equal(request.LocalTextureIndices, unchanged.LocalTextureIndices);
        Assert.True(unchanged.IncludeSourceGeometry);
    }

    [Fact]
    public void SameCaptureRebasesBothImagesPerObjectAndAllowsIdenticalRbAndAtlasLightmap()
    {
        var editor = new SceneCompositionEditor(); var source = Source();
        var a = editor.Add("first explicit object", Path.Combine(Path.GetTempPath(), "metallic-a.pmx"), ModelRole.Character, source);
        var b = editor.Add("second explicit object", Path.Combine(Path.GetTempPath(), "metallic-b.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("genshin"), Token);
        D3D12ViewGenshinMetallicBinding[] maps = [View(a.Id), View(b.Id)];
        D3D12ViewGenshinHairBinding[] hairs = [new(a.Id, 0, 0)];
        D3D12ViewGenshinHairAtlasBinding[] atlas = [new(a.Id, 0, 0, 2, D3D12GenshinAtlasColorEncoding.LinearRgb,
            1, 0, .5f, .2f, 1, 1, 0, 0)];
        var requests = D3D12ViewGenshinMetallicBindings.CreateRequests(maps,
            D3D12ViewGenshinHairAtlasBindings.CreateRequests(atlas, D3D12ViewGenshinHairBindings.CreateRequests(hairs)));
        var capture = project.CaptureRenderSnapshot(requests);
        Assert.Equal(0, project.CurrentFrame);
        var scene = Modern(capture.Scene, source);
        var resolved = D3D12ViewRenderingStyles.Resolve(scene, D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, hairDataBindings: hairs,
            genshinHairAtlasDataBindings: atlas, genshinMetallicDataBindings: maps);
        foreach (var obj in capture.RenderingStyles)
        {
            var style = resolved.SurfaceStyles.CharacterMaterials[obj.MaterialStart];
            var metallic = Assert.IsType<D3D12GenshinMetallicBinding>(style.GenshinMetallic);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[0], metallic.LightmapCombinedTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[1], metallic.MatcapCombinedTextureIndex);
            Assert.Equal(View(obj.ObjectId).DarkColor, metallic.DarkColor);
            Assert.Equal(View(obj.ObjectId).ShadowMultiplyColor, metallic.ShadowMultiplyColor);
            Assert.Equal(View(obj.ObjectId).UniformLitFactor, metallic.UniformLitFactor);
            Assert.Equal(obj.ObjectId == a.Id ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body, style.Role);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, genshinMetallicDataBindings: maps));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, hairDataBindings: [new(a.Id, 0, 2)], genshinMetallicDataBindings: maps));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, genshinHairAtlasDataBindings: [atlas[0] with { LightmapLocalTextureIndex = 2 }], genshinMetallicDataBindings: maps));
    }

    [Fact]
    public void ExplicitRoleOtherFamilyOrdinaryTreatmentAndComposedMetallicConflictsReject()
    {
        var id = Guid.NewGuid(); var mapping = View(id);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials(
                [new(id, 0, new(role))], [mapping]));
        Assert.Throws<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials(
            [new(id, 0, new(PmxMaterialTreatment: new(1, 1)))], [mapping]));
        Assert.Throws<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials(
            [new(id, 0, new(GenshinMetallic: new(0, 1, mapping.MatcapEncoding,
                mapping.DarkColor, mapping.LightColor, mapping.ShadowMultiplyColor)))], [mapping]));
        Assert.Throws<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials([], [mapping],
            radial: [new(id, 0, 0, Vector3.One)]));
        Assert.Throws<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials([], [mapping],
            starMc: [new(id, 0, 0, 1)]));
    }

    [Fact]
    public void ActiveMappingsRejectMissingOrProceduralImagesWhileDormantMappingNeedsNoCapturedImages()
    {
        var id = Guid.NewGuid(); var source = Source();
        var instances = new[] { new SceneRenderingStyleBinding(id, new("mmd"), ModelRole.Character, 0, 1, true) };
        var dormant = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            instances, genshinMetallicDataBindings: [View(id)]);
        Assert.Null(Assert.Single(dormant.SurfaceStyles.CharacterMaterials).GenshinMetallic);
        var composed = new D3D12ViewCharacterMaterialBinding(id, 0, new(GenshinMetallic:
            new(0, 1, D3D12MatcapColorEncoding.LinearRgb, Vector3.One, Vector3.One, Vector3.One)));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(source, D3D12StylePresetCatalog.BuiltIn,
            instances, [composed]));
        var active = new[] { instances[0] with { Preset = new("genshin") } };
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            active, genshinMetallicDataBindings: [View(id)]));
        var editor = new SceneCompositionEditor();
        var procedural = Source([new("numerical", 1, 1, [255, 0, 0, 255]), SceneTexture.ProceduralToon()]);
        var item = editor.Add("explicit procedural rejection", Path.Combine(Path.GetTempPath(), "metallic-procedural.pmx"),
            ModelRole.Character, procedural);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("genshin"), Token);
        var capture = project.CaptureRenderSnapshot(D3D12ViewGenshinMetallicBindings.CreateRequests([View(item.Id)]));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, procedural),
            D3D12StylePresetCatalog.BuiltIn, capture.RenderingStyles, sceneInputs: capture.SceneInputs,
            genshinMetallicDataBindings: [View(item.Id)]));
    }

    [Fact]
    public void SchemaSixStrictlyRoundtripsAllFieldsAndRejectsUnknownMissingOrOlderSchemaFields()
    {
        var profile = Profile(Source()); var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(6, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        foreach (string damage in new[] { "missing", "unknown", "schema", "encoding", "mode", "uv" })
        {
            var doc = JsonNode.Parse(bytes)!; var map = doc["materials"]![0]!["genshinMetallic"]!.AsObject();
            if (damage == "missing") map.Remove("shadowMultiplyColor");
            if (damage == "unknown") map["channelPacked"] = true;
            if (damage == "schema") doc["schemaVersion"] = 5;
            if (damage == "encoding") map["matcapEncoding"] = "Guess";
            if (damage == "mode") map["litMode"] = "ShaderInfo";
            if (damage == "uv") map["uvChannel"] = 0;
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void DormantProfileGuardsBothImagesWithoutActivatingMetallic(string pipeline)
    {
        var source = Source(); var id = Guid.NewGuid(); var profile = Profile(source);
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline);
        Assert.Empty(resolved.GenshinMetallicBindings);
        Assert.Null(Assert.Single(resolved.CharacterMaterials).Style.GenshinMetallic);
        for (int index = 0; index < 2; index++)
        {
            var textures = source.Surface.Snapshot.Textures.ToArray();
            textures[index] = new("changed", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile,
                Source(textures), id, pipeline));
        }
    }

    [Fact]
    public void ProfileRejectsMismatchedSharedMapAndConflictingFamilies()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var row = Profile(source).Materials[0];
        var hair = row with { Role = D3D12CharacterMaterialRole.Hair, GenshinHair = new(layout.CaptureTexture(0)) };
        hair.Validate();
        Assert.Throws<ArgumentException>(() => (hair with { GenshinHair = new(layout.CaptureTexture(2)) }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { EndfieldRim = new(Vector3.One, Vector3.One) }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { PmxMaterialTreatment = new(1, 1) }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { Role = D3D12CharacterMaterialRole.Face }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { Role = D3D12CharacterMaterialRole.Eye }).Validate());
        var mapped = D3D12MaterialBindingProfileResolver.Resolve(Profile(source), source, Guid.NewGuid(), "genshin");
        var value = Assert.Single(mapped.GenshinMetallicBindings);
        Assert.Equal(0, value.LightmapLocalTextureIndex); Assert.Equal(1, value.MatcapLocalTextureIndex);
        Assert.Equal(D3D12GenshinMetallicLitMode.Uniform, value.LitMode);
    }

    [Fact]
    public void LegacySchemaFiveWireBytesHaveNoNewNullPlaceholder()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var image = layout.CaptureTexture(0);
        var profile = new D3D12MaterialBindingProfile("legacy-five", "Legacy Five", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, EndfieldMatcap: new(image, image, image,
                D3D12MatcapColorEncoding.SrgbRgb, 0, Vector3.One, Vector3.Zero))]);
        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
            ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null,
            ["endfieldMatcap"] = new JsonObject
            {
                ["normal"] = Texture(image), ["p"] = Texture(image), ["matcap"] = Texture(image),
                ["matcapEncoding"] = "SrgbRgb", ["uvChannel"] = 0,
                ["baseColor"] = new JsonArray(1f, 1f, 1f), ["highlightColor"] = new JsonArray(0f, 0f, 0f),
                ["normalStrength"] = 2f, ["rotationRadians"] = 0f, ["highlightCoefficient"] = 0f,
                ["shadowHardness"] = .5f, ["lightControlDirection"] = null, ["baselineStrength"] = 1f,
                ["coupleEndfieldPalette"] = false, ["generatorRevision"] = "mikk-v1"
            }
        };
        var expected = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 5,
            ["stableId"] = profile.StableId, ["name"] = profile.Name, ["layoutVersion"] = 1,
            ["layoutFingerprint"] = profile.LayoutFingerprint, ["materials"] = new JsonArray(row)
        };
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(Encoding.UTF8.GetBytes(expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), bytes);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
    }

    [Fact]
    public void PortableSchemaSixPreservesFullTextureTableAfterSourceRemovalAndDefaultResave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "metallic-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "source.pmx");
            var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
            var clean = new PmxModel
            {
                Header = template.Header, Name = "portable metallic fixture", EnglishName = "", Comment = "", EnglishComment = "",
                Vertices = template.Vertices, Faces = template.Faces, Textures = [], Bones = template.Bones, Morphs = [],
                DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
                Materials = [new PmxMaterial { Name = "fixture", EnglishName = "", Diffuse = new(.5f, .4f, .3f, 1), Specular = Vector3.Zero,
                    SpecularPower = 10, Ambient = new(.2f), EdgeColor = Vector4.Zero, EdgeSize = 0, DrawMode = PmxDrawModeFlags.BothFaces,
                    TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common,
                    ToonTextureIndex = 0, Memo = "", FaceVertexCount = template.Faces.Count * 3 }]
            };
            File.WriteAllBytes(originalPath, PmxWriter.ToBytes(clean));
            var original = Rest(PmxReader.Read(originalPath)); var snapshot = original.Surface.Snapshot;
            int offset = snapshot.Textures.Count;
            var extended = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, snapshot.Materials,
                [..snapshot.Textures, ..Source().Surface.Snapshot.Textures], snapshot.World, snapshot.Camera,
                snapshot.LightDirection, snapshot.AdditionalUvs, snapshot.RawRadialSeeds);
            var source = new OriginalFrameScene(new(extended, original.Surface.Materials, original.Surface.Profile),
                original.Outline, original.OutlineOptions, original.Options, original.Materials, original.Lighting);
            var editor = new SceneCompositionEditor(); var item = editor.Add("portable metallic", originalPath, ModelRole.Character, source);
            using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
            project.SetDefaultRenderingStyle(new("genshin"), Token);
            var profile = Profile(source, offset); string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project,
                [new(item.Id, profile, source)]), Token);
            string moved = Path.Combine(directory, "moved.pma"); File.Move(first, moved); File.Delete(originalPath);
            string resaved = Path.Combine(directory, "resaved.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load, new NoPhysics(),
                out var profiles, cancellationToken: Token))
            {
                var restored = loaded.Session.Objects.Single().Source;
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(profiles[item.Id]));
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "genshin");
                var selection = Assert.Single(mapped.GenshinMetallicBindings);
                Assert.Equal(offset, selection.LightmapLocalTextureIndex); Assert.Equal(offset + 1, selection.MatcapLocalTextureIndex);
                Assert.Equal(D3D12MatcapColorEncoding.SrgbRgb, selection.MatcapEncoding);
                Assert.Equal(extended.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < extended.Textures.Count; i++)
                    Assert.Equal(extended.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewGenshinMetallicBindings.CreateRequests(mapped.GenshinMetallicBindings));
                var resolved = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
                    capture.RenderingStyles, mapped.CharacterMaterials, sceneInputs: capture.SceneInputs,
                    genshinMetallicDataBindings: mapped.GenshinMetallicBindings);
                Assert.NotNull(Assert.Single(resolved.SurfaceStyles.CharacterMaterials).GenshinMetallic);
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(6, again[item.Id].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(again[item.Id]));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static JsonObject Texture(D3D12MaterialBindingProfileTexture image) => new()
    {
        ["sourceSceneTextureOrdinal"] = image.SourceSceneTextureOrdinal, ["width"] = image.Width,
        ["height"] = image.Height, ["kind"] = image.Kind.ToString(), ["rgbaSha256"] = image.RgbaSha256
    };
    private static D3D12ViewGenshinMetallicBinding View(Guid id) => new(id, 0, 0, 1,
        D3D12MatcapColorEncoding.SrgbRgb, new(.2f, .3f, .4f), new(1.2f, 1.3f, 1.4f), new(.8f, .7f, .6f),
        .6f, 1.7f, .8f, D3D12GenshinMetallicLitMode.Uniform, .95f);
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source); var value = View(Guid.NewGuid());
        return new("genshin-metallic", "Explicit metallic", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, GenshinMetallic:
                new(layout.CaptureTexture(offset), layout.CaptureTexture(offset + 1), value.MatcapEncoding,
                    value.DarkColor, value.LightColor, value.ShadowMultiplyColor, value.EnableMetallics,
                    value.TileScale, value.Brightness, value.LitMode, value.UniformLitFactor))]);
    }
    private static OriginalFrameScene Source(SceneTexture[]? textures = null)
    {
        var frame = D3D12EndfieldHairBindingTests.Frame(seeds: false, textures: textures ?? [new("R numerical", 1, 1, [240, 0, 0, 255]),
            new("color ChannelPacked", 1, 1, [12, 200, 130, 170]), new("unreferenced resource", 1, 1, [3, 8, 19, 37])]);
        var outline = new OriginalOutlineData(frame.Surface.Snapshot.Vertices.ToArray().Select(v =>
            new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 0, 0)),
            [new OriginalOutlineMaterial(0, Vector4.Zero, false)]);
        return new(frame.Surface, outline, new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 0), frame.Options, frame.Materials);
    }
    private static OriginalFrameScene Modern(OriginalFrameScene capture, OriginalFrameScene source)
        => new(capture.Surface, capture.Outline, capture.OutlineOptions, source.Options, capture.Materials, capture.Lighting);
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1),
        options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
