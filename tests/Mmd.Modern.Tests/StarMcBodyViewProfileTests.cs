using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class StarMcBodyViewProfileTests
{
    [Fact]
    public void SchemaTwelveLegacyDecodeAndCurrentSchemaStrictRoundtripPreserveEveryBodyField()
    {
        var source = Source();
        var profile = Profile(source);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(14, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));

        var legacyJson = LegacySchemaTwelveBodyJson(source);
        var legacyDecoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyJson.ToJsonString()));
        var decodedBinding = Assert.Single(legacyDecoded.Materials[0].PluginBindings);
        Assert.Equal("wuthering-waves", decodedBinding.StyleId);
        Assert.Equal("body", decodedBinding.ModeId);
        Assert.Equal(4, legacyDecoded.Materials[0].TextureGuards.Count);

        foreach (string damage in new[] { "old", "missing", "unknown", "revision", "role", "family", "uv" })
        {
            var doc = JsonNode.Parse(legacyJson.ToJsonString())!;
            var row = doc["materials"]![0]!;
            var body = row["starMcBody"]!.AsObject();
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
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog);
        Assert.Empty(resolved.PluginBindings);
        for (int i = 0; i < 4; i++)
        {
            var textures = source.Surface.Snapshot.Textures.ToArray();
            textures[i] = new("changed selected slot", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(textures), id, pipeline, TestStylePlugins.AllViewCatalog));
        }
    }

    [Theory]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void ExactObjectSameCaptureRebasesAllImagesAndTangentCornerBindings(string pipeline)
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        var a = editor.Add("a", Path.Combine(Path.GetTempPath(), "body-a.pmx"), ModelRole.Character, source);
        var b = editor.Add("b", Path.Combine(Path.GetTempPath(), "body-b.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(),
            cancellationToken: TestContext.Current.CancellationToken);
        project.SetDefaultRenderingStyle(new(pipeline), TestContext.Current.CancellationToken);
        D3D12ViewPluginMaterialBinding[] maps = [
            new(a.Id, 0, ViewBinding(pipeline)),
            new(b.Id, 0, ViewBinding(pipeline))
        ];
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(maps, TestStylePlugins.AllViewCatalog);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r =>
        {
            Assert.Equal([0, 1, 2, 3], r.OptionalLocalTextureIndices.Order());
            Assert.True(r.IncludeSourceGeometry);
        });
        var capture = project.CaptureRenderSnapshot(requests);
        var scene = Modern(capture.Scene);
        var result = D3D12ViewRenderingStyles.Resolve(scene, TestStylePlugins.AllPresetCatalog, capture.RenderingStyles,
            sceneInputs: capture.SceneInputs, pluginBindings: maps, styleCatalog: TestStylePlugins.AllViewCatalog);
        foreach (var obj in capture.RenderingStyles)
        {
            var draw = Assert.IsType<D3D12PluginDrawData>(result.SurfaceStyles.GetPluginDraw(obj.MaterialStart));
            Assert.Equal("body", draw.ShaderModuleId);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[0], draw.TextureBindings![0].SceneTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[1], draw.TextureBindings[1].SceneTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[2], draw.TextureBindings[2].SceneTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[3], draw.TextureBindings[3].SceneTextureIndex);
            Assert.Contains(draw.BufferBindings!, buf => buf.Kind == D3D12PluginBufferKind.MikkTangentCorners);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, pluginBindings: maps, styleCatalog: TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles,
            characterMaterials: [new(a.Id, 0, D3D12CharacterMaterialRole.Hair)],
            pluginBindings: [maps[0], new(a.Id, 0, T8cTestHelpers.StarMcHairBinding(pipeline, 0, 1))],
            styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void LocalDormantBodyNeedsNoFrameDataButComposedWrongFamilyOrRoleFails()
    {
        var id = Guid.NewGuid(); var source = Source();
        var mmdObjects = new[] { new SceneRenderingStyleBinding(id, new("mmd"), ModelRole.Character, 0, 1, true) };
        var result = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, mmdObjects,
            pluginBindings: [new(id, 0, ViewBinding(T8cTestHelpers.StarRailStyleId))],
            styleCatalog: TestStylePlugins.AllViewCatalog);
        Assert.Null(result.SurfaceStyles.GetPluginDraw(0));

        Assert.Throws<NotSupportedException>(() => T8cTestHelpers.CreateDirectMap(
            [T8cTestHelpers.MmdStyleId],
            [T8cTestHelpers.MaterialWithBinding(T8cTestHelpers.StarRailStyleId, ViewBinding(T8cTestHelpers.StarRailStyleId))])
            .Validate(source));

        var starObjects = new[] { new SceneRenderingStyleBinding(id, new("star-rail"), ModelRole.Character, 0, 1, true) };
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(source, TestStylePlugins.AllPresetCatalog,
            starObjects, [new(id, 0, D3D12CharacterMaterialRole.Eye)],
            [new(id, 0, ViewBinding(T8cTestHelpers.StarRailStyleId))], styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    [Theory]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void OrdinaryDefaultsRespectRoleClassificationOptOutAndIgnoreDormantBodyBindings(string pipeline)
    {
        var id = Guid.NewGuid(); var source = Source();
        var objects = new[] { new SceneRenderingStyleBinding(id, new(pipeline), ModelRole.Character, 0, 1, true) };
        var plain = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, objects,
            styleCatalog: TestStylePlugins.AllViewCatalog);
        Assert.Equal(T8cTestHelpers.GetDefaultTreatment(pipeline, D3D12CharacterMaterialRole.Body),
            plain.SurfaceStyles.EffectivePmxMaterialTreatment(0));

        var hair = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, objects,
            [new(id, 0, D3D12CharacterMaterialRole.Hair)], styleCatalog: TestStylePlugins.AllViewCatalog);
        var hairStyle = Assert.Single(hair.SurfaceStyles.CharacterMaterials);
        Assert.Equal(D3D12CharacterMaterialRole.Hair, hairStyle.Role);
        Assert.Equal(T8cTestHelpers.GetDefaultTreatment(pipeline, D3D12CharacterMaterialRole.Hair),
            hair.SurfaceStyles.EffectivePmxMaterialTreatment(0));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(plain.SurfaceStyles, hair.SurfaceStyles));

        var optedOut = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, objects,
            [new(id, 0, D3D12CharacterMaterialRole.Body, PmxTreatment: new())], styleCatalog: TestStylePlugins.AllViewCatalog);
        Assert.Null(optedOut.SurfaceStyles.EffectivePmxMaterialTreatment(0));

        if (pipeline is "genshin" or "endfield")
        {
            var dormant = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog, objects,
                pluginBindings: [new(id, 0, ViewBinding(T8cTestHelpers.StarRailStyleId))],
                styleCatalog: TestStylePlugins.AllViewCatalog);
            Assert.Null(dormant.SurfaceStyles.GetPluginDraw(0));
            Assert.Equal(plain.SurfaceStyles.EffectivePmxMaterialTreatment(0),
                dormant.SurfaceStyles.EffectivePmxMaterialTreatment(0));
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
            D3D12MaterialBindingLayout.Capture(source).Fingerprint, [row], pipelineId: pipeline);
        var editor = new SceneCompositionEditor();
        var item = editor.Add("hair", Path.Combine(Path.GetTempPath(), "body-hair.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: TestContext.Current.CancellationToken);
        project.SetDefaultRenderingStyle(new(pipeline), TestContext.Current.CancellationToken);
        var mapping = D3D12MaterialBindingProfileResolver.Resolve(
            D3D12MaterialBindingProfileJson.Decode(D3D12MaterialBindingProfileJson.Encode(profile)),
            source, item.Id, pipeline, TestStylePlugins.AllViewCatalog);
        var capture = project.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(
            mapping.PluginBindings, TestStylePlugins.AllViewCatalog, mapping.CharacterMaterials));
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene), TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, mapping.CharacterMaterials, pluginBindings: mapping.PluginBindings,
            sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
        var style = Assert.Single(result.SurfaceStyles.CharacterMaterials);
        Assert.Equal(D3D12CharacterMaterialRole.Hair, style.Role);
        Assert.Equal("body", Assert.IsType<D3D12PluginDrawData>(style.PluginDraw).ShaderModuleId);
        Assert.Null(result.SurfaceStyles.EffectivePmxMaterialTreatment(0));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(Modern(capture.Scene),
            TestStylePlugins.AllPresetCatalog, capture.RenderingStyles, mapping.CharacterMaterials,
            pluginBindings: [.. mapping.PluginBindings, new(item.Id, 0, T8cTestHelpers.StarMcHairBinding(pipeline, 0, 1))],
            styleCatalog: TestStylePlugins.AllViewCatalog));
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
        var binding = ViewBinding(pipeline);
        for (int absent = 0; absent < 4; absent++)
        {
            var inputs = project.CaptureRenderSnapshot([new(id, optionalLocalTextureIndices:
                Enumerable.Range(0, 4).Where(i => i != absent))]).SceneInputs;
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(source),
                TestStylePlugins.AllPresetCatalog, objects, sceneInputs: inputs,
                pluginBindings: [new(id, 0, binding)], styleCatalog: TestStylePlugins.AllViewCatalog));
        }
        var correct = project.CaptureRenderSnapshot([new(id, optionalLocalTextureIndices: [0, 1, 2, 3])]).SceneInputs;
        var wrongObject = new Dictionary<Guid, SceneObjectRenderInputs> { [Guid.NewGuid()] = correct[id] };
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(source),
            TestStylePlugins.AllPresetCatalog, objects, sceneInputs: wrongObject,
            pluginBindings: [new(id, 0, binding)], styleCatalog: TestStylePlugins.AllViewCatalog));
        var badUv = ViewBinding(pipeline, uvChannel: 4);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(source),
            TestStylePlugins.AllPresetCatalog, objects, sceneInputs: correct,
            pluginBindings: [new(id, 0, badUv)], styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void EndfieldLiveInputsAndHighlightRangeRoundtripAndRemainDormantOnMmd()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var image = layout.CaptureTexture(0);
        var matcap = T8cTestHelpers.EndfieldMatcapBinding(0, 0, 0, "LinearRgb", 0, new(.3f), new(.2f), coupleEndfieldPalette: true);
        var palette = T8cTestHelpers.EndfieldPaletteBinding(new(.2f), lightInfluence: .7f, receivedLightInputMode: "RendererDiffuse");
        var highlightRange = T8cTestHelpers.EndfieldHighlightRangeBinding(.8f, 1.2f, -Vector3.UnitZ, .4f, .7f);
        var profile = new D3D12MaterialBindingProfile("endfield-live", "Endfield live", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, pluginBindings: [matcap, palette, highlightRange], textureGuards: [image])],
            pipelineId: "endfield");
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(14, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        var active = D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog);
        Assert.Equal(3, active.PluginBindings.Count);
        Assert.Equal(highlightRange.NumberValues!["highlightRangeOverallBrightness"],
            active.PluginBindings[2].Binding.NumberValues!["highlightRangeOverallBrightness"]);
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), "mmd", TestStylePlugins.AllViewCatalog).PluginBindings);

        var legacyEndfield = LegacySchemaTwelveEndfieldJson(source);
        Assert.NotEmpty(D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyEndfield.ToJsonString())).Materials[0].PluginBindings);
        legacyEndfield["schemaVersion"] = 11;
        legacyEndfield["materials"]![0]!.AsObject().Remove("starMcBody");
        legacyEndfield["materials"]![0]!["endfieldMatcap"]!.AsObject().Remove("highlightRange");
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyEndfield.ToJsonString())));
    }

    [Fact]
    public void LegacySchemaElevenAndOrdinarySchemaFourteenDoNotGainUnrequestedPluginBindings()
    {
        var layout = D3D12MaterialBindingLayout.Capture(Source());
        var profile = new D3D12MaterialBindingProfile("old-eleven", "Old eleven", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, pmxMaterialTreatment: new(ValueContrast: .1f))]);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        string text = Encoding.UTF8.GetString(bytes);
        Assert.Equal(14, profile.SchemaVersion);
        Assert.DoesNotContain("starMcBody", text, StringComparison.Ordinal);
        Assert.DoesNotContain("highlightRange", text, StringComparison.Ordinal);
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

    internal static StyleMaterialBindingData ViewBinding(string pipeline = "wuthering-waves", int offset = 0, int uvChannel = 0)
        => T8cTestHelpers.StarMcBodyBinding(
            pipeline,
            offset,
            offset + 1,
            offset + 2,
            offset + 3,
            "SrgbRgb",
            uvChannel: uvChannel,
            normalStrength: 1.2f,
            detailStrength: .6f,
            metallicSaturation: .8f,
            highlightCoefficient: .2f,
            highlightStrength: .5f,
            rotationRadians: .3f,
            shadowCoefficient: .4f,
            shadowSoftness: .15f,
            shadowColorStrength: .4f,
            warmBandStrength: .2f,
            shadowColor: new(.8f, .7f, .9f),
            rimColor: new(.3f, .4f, .5f),
            rimStrength: .2f,
            rimSize: .7f,
            rimDirectionRadians: .3f);

    internal static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0, string pipeline = "wuthering-waves")
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        D3D12MaterialBindingProfileTexture[] guards = [
            layout.CaptureTexture(offset),
            layout.CaptureTexture(offset + 1),
            layout.CaptureTexture(offset + 2),
            layout.CaptureTexture(offset + 3),
        ];
        return new("star-mc-body", "Explicit Body", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, pluginBindings: [ViewBinding(pipeline, offset)], textureGuards: guards)],
            pipelineId: pipeline);
    }

    private static JsonObject TextureJson(D3D12MaterialBindingProfileTexture tex) => new()
    {
        ["sourceSceneTextureOrdinal"] = tex.SourceSceneTextureOrdinal,
        ["width"] = tex.Width,
        ["height"] = tex.Height,
        ["kind"] = tex.Kind.ToString(),
        ["rgbaSha256"] = tex.RgbaSha256,
    };

    private static JsonObject LegacySchemaTwelveBodyJson(OriginalFrameScene source)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var body = new JsonObject
        {
            ["pipelineId"] = "wuthering-waves",
            ["normal"] = TextureJson(layout.CaptureTexture(0)),
            ["detail"] = TextureJson(layout.CaptureTexture(1)),
            ["mask"] = TextureJson(layout.CaptureTexture(2)),
            ["matcap"] = TextureJson(layout.CaptureTexture(3)),
            ["matcapEncoding"] = "SrgbRgb",
            ["uvChannel"] = 0,
            ["normalStrength"] = 1.2f,
            ["detailStrength"] = .6f,
            ["metallicSaturation"] = .8f,
            ["highlightCoefficient"] = .2f,
            ["highlightStrength"] = .5f,
            ["rotationRadians"] = .3f,
            ["shadowCoefficient"] = .4f,
            ["shadowSoftness"] = .15f,
            ["shadowColorStrength"] = .4f,
            ["warmBandStrength"] = .2f,
            ["shadowColor"] = new JsonArray(.8f, .7f, .9f),
            ["rimColor"] = new JsonArray(.3f, .4f, .5f),
            ["rimStrength"] = .2f,
            ["rimSize"] = .7f,
            ["rimDirectionRadians"] = .3f,
            ["generatorRevision"] = "mikk-v1",
        };
        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0,
            ["role"] = "Body",
            ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f,
            ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null,
            ["face"] = null,
            ["genshinHair"] = null,
            ["starMcHair"] = null,
            ["endfieldRim"] = null,
            ["genshinHairAtlas"] = null,
            ["endfieldHair"] = null,
            ["endfieldMatcap"] = null,
            ["genshinMetallic"] = null,
            ["starMcBody"] = body,
        };
        return new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile",
            ["schemaVersion"] = 12,
            ["stableId"] = "star-mc-body",
            ["name"] = "Explicit Body",
            ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint,
            ["materials"] = new JsonArray(row),
        };
    }

    private static JsonObject LegacySchemaTwelveEndfieldJson(OriginalFrameScene source)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var image = layout.CaptureTexture(0);
        var matcap = new JsonObject
        {
            ["normal"] = TextureJson(image),
            ["p"] = TextureJson(image),
            ["matcap"] = TextureJson(image),
            ["matcapEncoding"] = "LinearRgb",
            ["uvChannel"] = 0,
            ["baseColor"] = new JsonArray(.3f, .3f, .3f),
            ["highlightColor"] = new JsonArray(.2f, .2f, .2f),
            ["normalStrength"] = 2f,
            ["rotationRadians"] = 0f,
            ["highlightCoefficient"] = 0f,
            ["shadowHardness"] = .5f,
            ["lightControlDirection"] = null,
            ["generatorRevision"] = "mikk-v1",
            ["baselineStrength"] = 1f,
            ["coupleEndfieldPalette"] = true,
            ["palette"] = new JsonObject
            {
                ["receivedLightLinearRgb"] = new JsonArray(.2f, .2f, .2f),
                ["emissionStrength"] = 0f,
                ["uniformEmissionBlue"] = 0f,
                ["lightInfluence"] = .7f,
                ["density"] = 0f,
                ["emissionInputMode"] = "Uniform",
                ["emissionTexture"] = null,
                ["receivedLightInputMode"] = "RendererDiffuse",
                ["receivedLightTexture"] = null,
                ["baseImage"] = null,
            },
            ["highlightRange"] = new JsonObject
            {
                ["overallBrightness"] = .8f,
                ["highlightBrightness"] = 1.2f,
                ["directionalShadow"] = new JsonObject
                {
                    ["worldDirection"] = new JsonArray(0f, 0f, -1f),
                    ["width"] = .4f,
                    ["normalStrength"] = .7f,
                },
            },
        };
        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0,
            ["role"] = "Body",
            ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f,
            ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null,
            ["face"] = null,
            ["genshinHair"] = null,
            ["starMcHair"] = null,
            ["endfieldRim"] = null,
            ["genshinHairAtlas"] = null,
            ["endfieldHair"] = null,
            ["endfieldMatcap"] = matcap,
            ["genshinMetallic"] = null,
            ["starMcBody"] = null,
        };
        return new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile",
            ["schemaVersion"] = 12,
            ["stableId"] = "endfield-live",
            ["name"] = "Endfield live",
            ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint,
            ["materials"] = new JsonArray(row),
        };
    }

    internal sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
