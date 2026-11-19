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

public sealed class D3D12GenshinAtlasViewProfileTests
{
    internal static StyleMaterialBindingData AtlasBinding(int lightmap = 1, int atlas = 0)
        => TestStylePlugins.GenshinHairAtlas(lightmap, atlas, "SrgbRgb", 2.5f, .3f, .6f, -.5f, .7f, .8f, 1f, .2f);
    internal static D3D12ViewPluginMaterialBinding Binding(Guid id, int lightmap = 1, int atlas = 0)
        => TestStylePlugins.Bind(id, 0, AtlasBinding(lightmap, atlas));
    internal static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, bool includeHighlight = true)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var bindings = includeHighlight
            ? new[] { TestStylePlugins.GenshinHair(1), AtlasBinding() }
            : [AtlasBinding()];
        return new("atlas-map", "Atlas map", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair,
                pluginBindings: bindings,
                textureGuards: [layout.CaptureTexture(0), layout.CaptureTexture(1)])]);
    }

    [Fact]
    public void SchemaFourteenPreservesBothGuardsAndCoexistingHighlightWithoutUpgradingOlderPayloads()
    {
        var source = Source(); var profile = Profile(source);
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(14, JsonNode.Parse(bytes)!["schemaVersion"]!.GetValue<int>());
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(profile.Materials, restored.Materials);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
        foreach (var row in new[] { new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair),
            new(0, D3D12CharacterMaterialRole.Hair, pluginBindings: [new StyleMaterialBindingData("endfield", "rim",
                VectorValues: new Dictionary<string, float[]> { ["rimColor"] = [1f, 1f, 1f], ["shadowColor"] = [1f, 1f, 1f] }.AsReadOnly())]) })
        {
            var old = new D3D12MaterialBindingProfile("old", "old", profile.LayoutFingerprint, [row]);
            byte[] oldBytes = D3D12MaterialBindingProfileJson.Encode(old);
            var decodedBindings = JsonNode.Parse(oldBytes)!["materials"]![0]!["pluginBindings"]!.AsArray();
            Assert.DoesNotContain(decodedBindings, node => (string?)node!["modeId"] == "hair-atlas");
            Assert.Equal(oldBytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(oldBytes)));
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("enum")]
    [InlineData("range")]
    [InlineData("schema2")]
    [InlineData("duplicate")]
    public void LegacySchemaThreeRejectsMalformedOrSilentDowngradedAtlas(string damage)
    {
        var layout = D3D12MaterialBindingLayout.Capture(Source());
        var lm = layout.CaptureTexture(1);
        var at = layout.CaptureTexture(0);
        string TexJson(D3D12MaterialBindingProfileTexture t)
            => $$$"""{"sourceSceneTextureOrdinal":{{{t.SourceSceneTextureOrdinal}}},"width":{{{t.Width}}},"height":{{{t.Height}}},"kind":"{{{t.Kind}}}","rgbaSha256":"{{{t.RgbaSha256}}}"}""";
        string legacy = $$$"""
            {
              "format":"mda-d3d12-material-binding-profile",
              "schemaVersion":3,
              "stableId":"atlas-map",
              "name":"Atlas map",
              "layoutVersion":1,
              "layoutFingerprint":"{{{layout.Fingerprint}}}",
              "materials":[{
                "localMaterialIndex":0,
                "role":"Hair",
                "faceNormalShadeStrength":0.35,
                "faceGeometricShadowStrength":1,
                "eyeGeometricShadowStrength":0.2,
                "pmxMaterialTreatment":null,
                "face":null,
                "genshinHair":null,
                "starMcHair":null,
                "endfieldRim":null,
                "genshinHairAtlas":{
                  "lightmap":{{{TexJson(lm)}}},
                  "atlas":{{{TexJson(at)}}},
                  "atlasEncoding":"Srgb",
                  "explicitRowId":2.5,
                  "dayNight":0.3,
                  "shadowPosition":0.6,
                  "shadowRampWidth":0.5,
                  "vertexWidth":0.7,
                  "vertexAo":0.8,
                  "useLightmapAo":1,
                  "useVertexColorAo":0.2
                }
              }]
            }
            """;
        var document = JsonNode.Parse(legacy)!;
        var atlas = document["materials"]![0]!["genshinHairAtlas"]!.AsObject();
        switch (damage)
        {
            case "unknown": atlas["guessRow"] = true; break;
            case "missing": atlas.Remove("atlasEncoding"); break;
            case "enum": atlas["atlasEncoding"] = "srgbRgb"; break;
            case "range": atlas["explicitRowId"] = 0; break;
            case "schema2": document["schemaVersion"] = 2; break;
        }
        string json = document.ToJsonString();
        if (damage == "duplicate") json = json.Replace("\"explicitRowId\":2.5", "\"explicitRowId\":2.5,\"explicitRowId\":3", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void ProfileActivatesOnlySelectedFamilyAndChecksBothImagesEvenWhenDormant(string pipeline)
    {
        var source = Source(); var profile = Profile(source); var id = Guid.NewGuid();
        var result = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog);
        var atlasMatches = result.PluginBindings.Where(b => b.Binding.ModeId == "hair-atlas").ToArray();
        Assert.Equal(pipeline == "genshin" ? 1 : 0, atlasMatches.Length);
        if (pipeline == "genshin") Assert.True(D3D12StylePluginAdapter.BindingDataEquivalent(Binding(id).Binding, Assert.Single(atlasMatches).Binding));
        foreach (bool damageLightmap in new[] { false, true })
        {
            var row = profile.Materials[0];
            var guards = row.TextureGuards.ToArray();
            int idx = damageLightmap ? 1 : 0;
            guards[idx] = guards[idx] with { RgbaSha256 = new('0', 64) };
            var invalid = new D3D12MaterialBindingProfile("bad", "bad", profile.LayoutFingerprint, [row with { TextureGuards = guards }]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(invalid, source, id, pipeline, TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact]
    public void DuplicateInstancesRebaseBothSlotsFromOneCapturedSampleAndKeepIndependentHighlight()
    {
        var layout = new SceneCompositionEditor(); var a = layout.Add("a", SourcePath, ModelRole.Character, Source());
        var b = layout.Add("b", SourcePath, ModelRole.Character, Source(reverse: true));
        using var project = new SceneProjectSession(layout, new(), [], new NoPhysics(), cancellationToken: TestContext.Current.CancellationToken);
        project.SetDefaultRenderingStyle(new("genshin"), TestContext.Current.CancellationToken);
        D3D12ViewPluginMaterialBinding[] atlas = [Binding(a.Id), Binding(b.Id, 0, 1)];
        D3D12ViewPluginMaterialBinding[] highlight = [TestStylePlugins.Bind(a.Id, 0, TestStylePlugins.GenshinHair(1)),
            TestStylePlugins.Bind(b.Id, 0, TestStylePlugins.GenshinHair(0))];
        D3D12ViewPluginMaterialBinding[] combined = [.. atlas, .. highlight];
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(combined, TestStylePlugins.AllViewCatalog);
        Assert.Equal(2, requests.Count);
        // Requests are deduplicated source-local selections, not a sorted combined texture table.
        Assert.All(requests, request => Assert.Equal(new[] { 0, 1 }, request.LocalTextureIndices.Order()));
        var sample = project.CaptureRenderSnapshot(requests);
        // Mirror the real ViewOutput adapter: composition captures canonical geometry without
        // selecting modern frame options. This does not re-evaluate animation or change SceneInputs.
        var frameScene = D3D12ViewScene.Resolve(sample.Scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new());
        var result = D3D12ViewRenderingStyles.Resolve(frameScene, TestStylePlugins.AllPresetCatalog,
            sample.RenderingStyles, sceneInputs: sample.SceneInputs, pluginBindings: combined, styleCatalog: TestStylePlugins.AllViewCatalog);
        foreach (var instance in sample.RenderingStyles)
        {
            var binding = atlas.Single(value => value.ObjectId == instance.ObjectId);
            var style = result.SurfaceStyles.CharacterMaterials[instance.MaterialStart];
            var draw = Assert.IsType<D3D12PluginDrawData>(style.PluginDraw);
            Assert.Equal("hair-atlas-rb", draw.ShaderModuleId);
            var bySlot = draw.TextureBindings!.ToDictionary(t => t.SlotIndex, t => t.SceneTextureIndex);
            Assert.Equal(sample.SceneInputs[instance.ObjectId].TextureIndices[binding.Binding.TextureSlots!["lightmap"]], bySlot[1]);
            Assert.Equal(sample.SceneInputs[instance.ObjectId].TextureIndices[binding.Binding.TextureSlots["atlas"]], bySlot[2]);
            Assert.Equal(sample.SceneInputs[instance.ObjectId].TextureIndices[binding.Binding.TextureSlots["lightmap"]], bySlot[0]);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(frameScene, TestStylePlugins.AllPresetCatalog,
            sample.RenderingStyles, pluginBindings: combined, styleCatalog: TestStylePlugins.AllViewCatalog));
        Assert.Equal(0, project.CurrentFrame);
    }

    [Fact]
    public void LocalBindingsDetachAndRejectCrossFamilyOrComposedDuplicatesBeforeDeviceCreation()
    {
        var id = Guid.NewGuid(); var valid = Binding(id); var inputs = new[] { valid };
        var detached = D3D12ViewPluginMaterialBindings.Detach(inputs);
        inputs[0] = TestStylePlugins.Bind(id, 0, AtlasBinding(atlas: 99));
        Assert.Equal(valid.ObjectId, detached[0].ObjectId);
        Assert.Equal(0, detached[0].Binding.TextureSlots!["atlas"]);
        Assert.Same(detached, D3D12ViewPluginMaterialBindings.Detach(detached));
        foreach (var candidate in new[] {
            TestStylePlugins.Bind(Guid.Empty, 0, AtlasBinding()),
            TestStylePlugins.Bind(id, -1, AtlasBinding()),
            TestStylePlugins.Bind(id, 0, TestStylePlugins.GenshinHairAtlas(1, 0, explicitRowId: float.NaN)) })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Detach([candidate]));
        foreach (var candidate in new[] {
            TestStylePlugins.Bind(Guid.Empty, 0, AtlasBinding()),
            TestStylePlugins.Bind(id, -1, AtlasBinding()),
            TestStylePlugins.Bind(id, 0, AtlasBinding(lightmap: -1)),
            TestStylePlugins.Bind(id, 0, AtlasBinding(atlas: -1)),
            TestStylePlugins.Bind(id, 0, TestStylePlugins.GenshinHairAtlas(1, 0, explicitRowId: float.NaN)),
            TestStylePlugins.Bind(id, 0, TestStylePlugins.GenshinHairAtlas(1, 0, dayNight: 2f)) })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([], [candidate], TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([], [valid, valid], TestStylePlugins.AllViewCatalog));
        foreach (var combo in new (D3D12ViewCharacterMaterial[] Materials, D3D12ViewPluginMaterialBinding[] Bindings)[]
        {
            ([new(id, 0, D3D12CharacterMaterialRole.Face)], [valid]),
            ([new(id, 0, D3D12CharacterMaterialRole.Eye)], [valid]),
            ([new(id, 0, D3D12CharacterMaterialRole.Hair)], [valid, valid]),
            ([new(id, 0, D3D12CharacterMaterialRole.Hair)], [valid, TestStylePlugins.Bind(id, 0, new("genshin", "default"))]),
            ([new(id, 0, D3D12CharacterMaterialRole.Face)], [valid, TestStylePlugins.Bind(id, 0, TestStylePlugins.FaceSdf("genshin", 0, 1))]),
        })
        {
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(32, 32), characterMaterials: combo.Materials,
                pluginBindings: combo.Bindings, styleCatalog: TestStylePlugins.AllViewCatalog));
            Assert.Throws<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(32, 32), characterMaterials: combo.Materials,
                pluginBindings: combo.Bindings, styleCatalog: TestStylePlugins.AllViewCatalog));
        }
    }

    internal static string SourcePath => Path.Combine(Path.GetTempPath(), "mda-atlas-source.pmx");
    internal static OriginalFrameScene Source(bool reverse = false)
    {
        SceneVertex[] vertices = [new(new(-1,-1,0), -Vector3.UnitZ, Vector2.Zero), new(new(1,-1,0), -Vector3.UnitZ, Vector2.UnitX),
            new(new(1,1,0), -Vector3.UnitZ, Vector2.One), new(new(-1,1,0), -Vector3.UnitZ, Vector2.UnitY)];
        SceneTexture[] textures = [new("atlas", 1, 1, [128, 64, 32, 0]), new("numeric", 1, 1, [0, 192, 255, 255])];
        if (reverse) Array.Reverse(textures);
        var snapshot = new SceneSnapshot(vertices, [0,2,1,0,3,2],
            [new("hair", 0, 6, new(.3f,.2f,.1f,1), new(.8f,.4f,.2f), Vector3.Zero, 1)], textures,
            camera: new(new(0,0,-5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: -Vector3.UnitZ);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position,
            vertex.Normal, OriginalOutlineVertexMode.Expand, 0, 0)), [new(0, Vector4.Zero, false)]);
        return new(new(snapshot, [new(new(.2f))], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), outline,
            new(Vector4.Zero, Matrix4x4.Identity, snapshot.Camera.Eye, 30, 1), options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
}
