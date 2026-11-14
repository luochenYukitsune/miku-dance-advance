using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class StarMcBodyViewProfileTests
{
    [Fact]
    public void SchemaTwelveStrictRoundtripPreservesEveryBodyField()
    {
        var profile = Profile(Source()); var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(12, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        foreach (string damage in new[] { "old", "missing", "unknown", "revision", "role", "family", "uv" })
        {
            var doc = JsonNode.Parse(bytes)!; var row = doc["materials"]![0]!; var body = row["starMcBody"]!.AsObject();
            if (damage == "old") doc["schemaVersion"] = 11;
            if (damage == "missing") body.Remove("normalStrength");
            if (damage == "unknown") body["guessedLightmap"] = true;
            if (damage == "revision") body["generatorRevision"] = "guessed";
            if (damage == "role") row["role"] = "Eye";
            if (damage == "family") body["pipelineId"] = "endfield";
            if (damage == "uv") body["uvChannel"] = 5;
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("endfield")]
    [InlineData("star-rail")]
    public void DormantProfilesGuardAllImagesAndNeverRebaseByDigest(string pipeline)
    {
        var source = Source(); var profile = Profile(source); var id = Guid.NewGuid();
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline);
        Assert.Empty(resolved.StarMcBodyBindings);
        for (int i = 0; i < 4; i++)
        {
            var textures = source.Surface.Snapshot.Textures.ToArray();
            textures[i] = new("changed selected slot", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(textures), id, pipeline));
        }
    }

    [Theory]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void ExactObjectSameCaptureRebasesAllImagesAndEvaluatedGeometry(string pipeline)
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        var a = editor.Add("a", Path.Combine(Path.GetTempPath(), "body-a.pmx"), ModelRole.Character, source);
        var b = editor.Add("b", Path.Combine(Path.GetTempPath(), "body-b.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(),
            cancellationToken: TestContext.Current.CancellationToken);
        project.SetDefaultRenderingStyle(new(pipeline), TestContext.Current.CancellationToken);
        var maps = new[] { View(a.Id), View(b.Id) };
        var requests = D3D12ViewStarMcBodyBindings.CreateRequests(maps,
            [new(a.Id, optionalLocalTextureIndices: [9])], []);
        Assert.All(requests, r => Assert.True(r.IncludeSourceGeometry));
        var capture = project.CaptureRenderSnapshot(requests);
        var scene = Modern(capture.Scene);
        var result = D3D12ViewRenderingStyles.Resolve(scene, D3D12StylePresetCatalog.BuiltIn, capture.RenderingStyles,
            sceneInputs: capture.SceneInputs, starMcBodyDataBindings: maps);
        foreach (var obj in capture.RenderingStyles)
        {
            var binding = Assert.IsType<D3D12StarMcBodyBinding>(result.SurfaceStyles.CharacterMaterials[obj.MaterialStart].StarMcBody);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[0], binding.NormalCombinedTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[1], binding.DetailCombinedTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[2], binding.MaskCombinedTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[3], binding.MatcapCombinedTextureIndex);
            Assert.NotNull(binding.Geometry);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, starMcBodyDataBindings: maps));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, starMcBodyDataBindings: [maps[0]], endfieldMatcapDataBindings:
            [new(a.Id, 0, 0, 1, 3, D3D12MatcapColorEncoding.LinearRgb, 0, Vector3.One, Vector3.Zero)]));
    }

    [Fact]
    public void LocalDormantBodyNeedsNoFrameDataButComposedWrongFamilyFails()
    {
        var id = Guid.NewGuid(); var source = Source();
        var objects = new[] { new SceneRenderingStyleBinding(id, new("mmd"), ModelRole.Character, 0, 1, true) };
        var result = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects,
            starMcBodyDataBindings: [View(id)]);
        Assert.Null(Assert.Single(result.SurfaceStyles.CharacterMaterials).StarMcBody);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(source, D3D12StylePresetCatalog.BuiltIn,
            objects, [new(id, 0, new(StarMcBody: Profile(source).Materials[0].StarMcBody!.ToBinding()))]));
    }

    [Theory]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void OrdinaryDefaultsRequireExplicitMaterialClassification(string pipeline)
    {
        var id = Guid.NewGuid(); var source = Source();
        var objects = new[] { new SceneRenderingStyleBinding(id, new(pipeline), ModelRole.Character, 0, 1, true) };
        var plain = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects);
        Assert.Null(Assert.Single(plain.SurfaceStyles.CharacterMaterials).PmxMaterialTreatment);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Hair })
        {
            var classified = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects,
                [new(id, 0, new(role))]);
            var style = Assert.Single(classified.SurfaceStyles.CharacterMaterials);
            Assert.Equal(role, style.Role);
            Assert.NotNull(style.PmxMaterialTreatment);
            Assert.False(D3D12SurfaceStyleMap.Equivalent(plain.SurfaceStyles, classified.SurfaceStyles));
        }
        if (pipeline is "genshin" or "endfield")
        {
            var dormant = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, objects,
                starMcBodyDataBindings: [View(id)]);
            Assert.Null(Assert.Single(dormant.SurfaceStyles.CharacterMaterials).PmxMaterialTreatment);
            Assert.True(D3D12SurfaceStyleMap.Equivalent(plain.SurfaceStyles, dormant.SurfaceStyles));
        }
    }

    [Theory]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void HairAdaptationRetainsItsExplicitRoleAndDedicatedTreatment(string pipeline)
    {
        var source = Source(); var row = Profile(source, pipeline: pipeline).Materials[0] with { Role = D3D12CharacterMaterialRole.Hair };
        var profile = new D3D12MaterialBindingProfile("hair-adaptation", "MDA shared material adaptation",
            D3D12MaterialBindingLayout.Capture(source).Fingerprint, [row]);
        var editor = new SceneCompositionEditor();
        var item = editor.Add("hair", Path.Combine(Path.GetTempPath(), "body-hair.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: TestContext.Current.CancellationToken);
        project.SetDefaultRenderingStyle(new(pipeline), TestContext.Current.CancellationToken);
        var mapping = D3D12MaterialBindingProfileResolver.Resolve(
            D3D12MaterialBindingProfileJson.Decode(D3D12MaterialBindingProfileJson.Encode(profile)), source, item.Id, pipeline);
        var capture = project.CaptureRenderSnapshot(D3D12ViewStarMcBodyBindings.CreateRequests(mapping.StarMcBodyBindings, [], mapping.CharacterMaterials));
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene), D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, mapping.CharacterMaterials, sceneInputs: capture.SceneInputs, starMcBodyDataBindings: mapping.StarMcBodyBindings);
        var style = Assert.Single(result.SurfaceStyles.CharacterMaterials);
        Assert.Equal(D3D12CharacterMaterialRole.Hair, style.Role);
        Assert.NotNull(style.StarMcBody);
        Assert.Null(style.PmxMaterialTreatment);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(Modern(capture.Scene),
            D3D12StylePresetCatalog.BuiltIn, capture.RenderingStyles, mapping.CharacterMaterials,
            starMcBodyDataBindings: mapping.StarMcBodyBindings, starMcHairDataBindings:
            [new(item.Id, 0, 0, 1)]));
    }

    [Theory]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void EveryBodyImageAndEffectiveUvRequiresTheExactObjectCapture(string pipeline)
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        var item = editor.Add("guarded Body", Path.Combine(Path.GetTempPath(), "body-inputs.pmx"), ModelRole.Character, source);
        var id = item.Id;
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: TestContext.Current.CancellationToken);
        var objects = new[] { new SceneRenderingStyleBinding(id, new(pipeline), ModelRole.Character, 0, 1, true) };
        var binding = View(id);
        for (int absent = 0; absent < 4; absent++)
        {
            var inputs = project.CaptureRenderSnapshot([new(id, optionalLocalTextureIndices:
                Enumerable.Range(0, 4).Where(i => i != absent), includeSourceGeometry: true)]).SceneInputs;
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(source),
                D3D12StylePresetCatalog.BuiltIn, objects, sceneInputs: inputs, starMcBodyDataBindings: [binding]));
        }
        var correct = project.CaptureRenderSnapshot([new(id, optionalLocalTextureIndices: [0, 1, 2, 3], includeSourceGeometry: true)]).SceneInputs;
        var wrongObject = new Dictionary<Guid, SceneObjectRenderInputs>
        { [Guid.NewGuid()] = correct[id] };
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(source),
            D3D12StylePresetCatalog.BuiltIn, objects, sceneInputs: wrongObject, starMcBodyDataBindings: [binding]));
        var missingGeometry = project.CaptureRenderSnapshot([new(id, optionalLocalTextureIndices: [0, 1, 2, 3])]).SceneInputs;
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(source),
            D3D12StylePresetCatalog.BuiltIn, objects, sceneInputs: missingGeometry, starMcBodyDataBindings: [binding]));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(source),
            D3D12StylePresetCatalog.BuiltIn, objects, sceneInputs: correct, starMcBodyDataBindings: [binding with { UvChannel = 4 }]));
    }

    [Fact]
    public void SchemaTwelveEndfieldLiveInputsAndHighlightRangeRoundtripAndRemainDormant()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var image = layout.CaptureTexture(0);
        var matcap = new D3D12MaterialBindingProfileEndfieldMatcap(image, image, image,
            D3D12MatcapColorEncoding.LinearRgb, 0, new(.3f), new(.2f), CoupleEndfieldPalette: true,
            Palette: new(new(.2f), LightInfluence: .7f, ReceivedLightInputMode: D3D12EndfieldPaletteInputMode.RendererDiffuse),
            HighlightRange: new(.8f, 1.2f, new(-Vector3.UnitZ, .4f, .7f)));
        var profile = new D3D12MaterialBindingProfile("endfield-live", "Endfield live", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, EndfieldMatcap: matcap)]);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(12, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        var active = D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), "endfield");
        Assert.Equal(matcap.HighlightRange, Assert.Single(active.EndfieldMatcapBindings).HighlightRange);
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), "mmd").EndfieldMatcapBindings);
        var doc = JsonNode.Parse(bytes)!; doc["schemaVersion"] = 11;
        doc["materials"]![0]!.AsObject().Remove("starMcBody");
        doc["materials"]![0]!["endfieldMatcap"]!.AsObject().Remove("highlightRange");
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
    }

    [Fact]
    public void LegacySchemaElevenDoesNotGainNewNullPlaceholders()
    {
        var layout = D3D12MaterialBindingLayout.Capture(Source());
        var profile = new D3D12MaterialBindingProfile("old-eleven", "Old eleven", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, PmxMaterialTreatment: new(ValueContrast: .1f))]);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(11, profile.SchemaVersion);
        Assert.DoesNotContain("starMcBody", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("highlightRange", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
    }

    internal static OriginalFrameScene Source(SceneTexture[]? textures = null)
    {
        var source = D3D12EndfieldHairBindingTests.Frame(textures: textures ?? [
            new("normal", 1, 1, [144, 112, 0, 0]), new("detail", 1, 1, [0, 190, 0, 0]),
            new("mask", 1, 1, [230, 0, 200, 0]), new("matcap", 1, 1, [80, 160, 230, 11])]);
        var outline = new OriginalOutlineData(source.Surface.Snapshot.Vertices.ToArray().Select(v =>
            new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1, 0)),
            [new OriginalOutlineMaterial(0, Vector4.Zero, false)]);
        return new(source.Surface, outline, new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -5), 30, 0), source.Options, source.Materials);
    }

    internal static OriginalFrameScene Modern(OriginalFrameScene scene) => new(scene.Surface, scene.Outline, scene.OutlineOptions,
        new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero), scene.Materials, scene.Lighting);

    internal static D3D12ViewStarMcBodyBinding View(Guid id) => new(id, 0, 0, 1, 2, 3,
        D3D12MatcapColorEncoding.SrgbRgb, NormalStrength: 1.2f, DetailStrength: .6f, MetallicSaturation: .8f,
        HighlightCoefficient: .2f, HighlightStrength: .5f, RotationRadians: .3f, ShadowCoefficient: .4f,
        ShadowSoftness: .15f, ShadowColorStrength: .4f, WarmBandStrength: .2f, ShadowColor: new(.8f, .7f, .9f),
        RimColor: new(.3f, .4f, .5f), RimStrength: .2f, RimSize: .7f, RimDirectionRadians: .3f);

    internal static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0, string pipeline = "wuthering-waves")
    {
        var layout = D3D12MaterialBindingLayout.Capture(source); var v = View(Guid.NewGuid());
        return new("star-mc-body", "Explicit Body", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Body,
            StarMcBody: new(pipeline, layout.CaptureTexture(offset), layout.CaptureTexture(offset + 1),
                layout.CaptureTexture(offset + 2), layout.CaptureTexture(offset + 3), v.MatcapEncoding, v.UvChannel,
                v.NormalStrength, v.DetailStrength, v.MetallicSaturation, v.HighlightCoefficient, v.HighlightStrength,
                v.RotationRadians, v.ShadowCoefficient, v.ShadowSoftness, v.ShadowColorStrength, v.WarmBandStrength,
                v.ShadowColor, v.RimColor, v.RimStrength, v.RimSize, v.RimDirectionRadians))]);
    }

    internal sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
