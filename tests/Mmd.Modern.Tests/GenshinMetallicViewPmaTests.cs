using System.Buffers.Binary;
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

public sealed class GenshinMetallicViewPmaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DetachedSelectorsRetainEveryConstantAndRejectInvalidHostConfiguration()
    {
        var value = View(Guid.NewGuid());
        D3D12ViewPluginMaterialBinding[] input = [value];
        var frozen = D3D12ViewPluginMaterialBindings.Detach(input);
        input[0] = TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(lightmapSlot: 77));
        Assert.True(D3D12StylePluginAdapter.BindingDataEquivalent(value.Binding, Assert.Single(frozen).Binding));
        Assert.Equal(0, Assert.Single(frozen).Binding.TextureSlots!["lightmap"]);
        Assert.Same(frozen, D3D12ViewPluginMaterialBindings.Detach(frozen));
        foreach (var bad in new[] {
            TestStylePlugins.Bind(Guid.Empty, 0, MakeBinding()),
            TestStylePlugins.Bind(value.ObjectId, -1, MakeBinding()),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(darkColor: new(float.NaN))),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(lightColor: new(float.PositiveInfinity))),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(shadowMultiplyColor: new(float.NaN))),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(enableMetallics: float.NaN)),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(tileScale: float.NaN)),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(brightness: float.NaN)),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(uniformLitFactor: float.NaN)) })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Detach([bad]));
        foreach (var invalid in new[] {
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(lightmapSlot: -1)),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(matcapSlot: -1)),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(litMode: "Unknown")),
            TestStylePlugins.Bind(value.ObjectId, 0, MakeBinding(matcapEncoding: "Unknown")) })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([], [invalid], TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([], [value, value], TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void RequestUnionRetainsMultipleMaterialsStylesAndSelectedBonesPerObject()
    {
        var id = Guid.NewGuid();
        var metal = View(id);
        var matcap = TestStylePlugins.Bind(id, 1, T8bTestHelpers.CreateEndfieldMatcapBinding(2, 3, 8));
        var face = TestStylePlugins.Bind(id, 2, TestStylePlugins.FaceSdf("genshin", 0, 1,
            trackingMode: StyleBoneTrackingMode.SelectedBone, boneName: "4"));
        var boneNames = new Dictionary<Guid, IReadOnlyDictionary<string, int>>
        {
            [id] = new Dictionary<string, int>(StringComparer.Ordinal) { ["4"] = 4 }
        };
        var request = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests([metal, matcap, face],
            TestStylePlugins.AllViewCatalog,
            [new(id, 2, D3D12CharacterMaterialRole.Face)],
            boneNames));
        Assert.Equal(new[] { 4 }, request.BoneIndices);
        Assert.Equal(new[] { 0, 1, 2, 3, 8 }, request.OptionalLocalTextureIndices.Order());
        var metalOnly = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests([metal], TestStylePlugins.AllViewCatalog));
        Assert.Empty(metalOnly.BoneIndices);
        Assert.Equal(new[] { 0, 1 }, metalOnly.OptionalLocalTextureIndices.Order());
    }

    [Fact]
    public void SameCaptureRebasesBothImagesPerObjectAndAllowsIdenticalRbAndAtlasLightmap()
    {
        var editor = new SceneCompositionEditor(); var source = Source();
        var a = editor.Add("first explicit object", Path.Combine(Path.GetTempPath(), "metallic-a.pmx"), ModelRole.Character, source);
        var b = editor.Add("second explicit object", Path.Combine(Path.GetTempPath(), "metallic-b.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("genshin"), Token);
        D3D12ViewPluginMaterialBinding[] maps = [View(a.Id), View(b.Id)];
        D3D12ViewPluginMaterialBinding[] hairs = [TestStylePlugins.Bind(a.Id, 0, TestStylePlugins.GenshinHair(0))];
        D3D12ViewPluginMaterialBinding[] atlas = [TestStylePlugins.Bind(a.Id, 0,
            TestStylePlugins.GenshinHairAtlas(0, 2, "LinearRgb", 1f, 0f, .5f, .2f, 1f, 1f, 0f, 0f))];
        D3D12ViewPluginMaterialBinding[] all = [.. maps, .. hairs, .. atlas];
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(all, TestStylePlugins.AllViewCatalog);
        var capture = project.CaptureRenderSnapshot(requests);
        Assert.Equal(0, project.CurrentFrame);
        var scene = Modern(capture.Scene, source);
        var resolved = D3D12ViewRenderingStyles.Resolve(scene, TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs,
            pluginBindings: all, styleCatalog: TestStylePlugins.AllViewCatalog);
        foreach (var obj in capture.RenderingStyles)
        {
            var style = resolved.SurfaceStyles.CharacterMaterials[obj.MaterialStart];
            var draw = Assert.IsType<D3D12PluginDrawData>(style.PluginDraw);
            Assert.Equal(obj.ObjectId == a.Id ? "metallic-atlas-rb" : "metallic", draw.ShaderModuleId);
            var texBySlot = draw.TextureBindings!.ToDictionary(t => t.SlotIndex, t => t.SceneTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[0], texBySlot[3]);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[1], texBySlot[4]);
            int cbOffset = obj.ObjectId == a.Id ? 64 : 0;
            var cb = draw.ConstantBufferData.Span[cbOffset..];
            var expected = View(obj.ObjectId).Binding;
            var expectedDark = expected.VectorValues!["darkColor"];
            var expectedShadow = expected.VectorValues["shadowMultiplyColor"];
            Assert.Equal(expectedDark[0], BinaryPrimitives.ReadSingleLittleEndian(cb[16..20]));
            Assert.Equal(expectedDark[1], BinaryPrimitives.ReadSingleLittleEndian(cb[20..24]));
            Assert.Equal(expectedDark[2], BinaryPrimitives.ReadSingleLittleEndian(cb[24..28]));
            Assert.Equal((float)expected.NumberValues!["uniformLitFactor"], BinaryPrimitives.ReadSingleLittleEndian(cb[28..32]));
            Assert.Equal(expectedShadow[0], BinaryPrimitives.ReadSingleLittleEndian(cb[48..52]));
            Assert.Equal(expectedShadow[1], BinaryPrimitives.ReadSingleLittleEndian(cb[52..56]));
            Assert.Equal(expectedShadow[2], BinaryPrimitives.ReadSingleLittleEndian(cb[56..60]));
            Assert.Equal(obj.ObjectId == a.Id ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body, style.Role);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, pluginBindings: maps, styleCatalog: TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, pluginBindings: [.. maps, TestStylePlugins.Bind(a.Id, 0, TestStylePlugins.GenshinHair(2))],
            styleCatalog: TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, pluginBindings: [.. maps, TestStylePlugins.Bind(a.Id, 0,
                TestStylePlugins.GenshinHairAtlas(2, 2, "LinearRgb", 1f, 0f, .5f, .2f, 1f, 1f, 0f, 0f))],
            styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void ExplicitRoleOtherFamilyOrdinaryModeAndDuplicateMetallicConflictsReject()
    {
        var id = Guid.NewGuid(); var mapping = View(id);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate(
                [new(id, 0, role)], [mapping], TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate(
            [], [mapping, TestStylePlugins.Bind(id, 0, new("genshin", "default"))], TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate(
            [], [mapping, TestStylePlugins.Bind(id, 0, TestStylePlugins.FaceSdf("genshin", 0, 1))], TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate(
            [], [mapping, mapping], TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void ActiveMappingsRejectMissingOrProceduralImagesWhileDormantMappingNeedsNoCapturedImages()
    {
        var id = Guid.NewGuid(); var source = Source();
        var instances = new[] { new SceneRenderingStyleBinding(id, new("mmd"), ModelRole.Character, 0, 1, true) };
        var dormant = D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog,
            instances, pluginBindings: [View(id)], styleCatalog: TestStylePlugins.AllViewCatalog);
        Assert.Null(Assert.Single(dormant.SurfaceStyles.CharacterMaterials).PluginDraw);
        var active = new[] { instances[0] with { Preset = new("genshin") } };
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(source, TestStylePlugins.AllPresetCatalog,
            active, pluginBindings: [View(id)], styleCatalog: TestStylePlugins.AllViewCatalog));
        var procedural = Source([new("numerical", 1, 1, [255, 0, 0, 255]), SceneTexture.ProceduralToon()]);
        var procLayout = D3D12MaterialBindingLayout.Capture(procedural);
        var procProfile = new D3D12MaterialBindingProfile("proc-metal", "Proc Metal", procLayout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, pluginBindings: [MakeBinding()],
                textureGuards: [procLayout.CaptureTexture(0), new(1, 1, 1, SceneTextureKind.IndexedProceduralToon, procLayout.CaptureTexture(1).RgbaSha256)])]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            procProfile, procedural, id, "genshin", TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void SchemaFourteenAndLegacySchemaSixStrictlyRoundtripAndRejectUnknownMissingOrOlderSchemaFields()
    {
        var profile = Profile(Source()); var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        foreach (string damage in new[] { "missing", "unknown", "schema" })
        {
            var doc = JsonNode.Parse(bytes)!;
            var binding = doc["materials"]![0]!["pluginBindings"]![0]!.AsObject();
            if (damage == "missing") binding.Remove("modeId");
            if (damage == "unknown") binding["channelPacked"] = true;
            if (damage == "schema") doc["schemaVersion"] = 13;
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }

        var layout = D3D12MaterialBindingLayout.Capture(Source());
        var lm = layout.CaptureTexture(0); var mc = layout.CaptureTexture(1);
        var legacy = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 6,
            ["stableId"] = "legacy-six", ["name"] = "Legacy Six", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint,
            ["materials"] = new JsonArray(new JsonObject
            {
                ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
                ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
                ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
                ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null, ["endfieldMatcap"] = null,
                ["genshinMetallic"] = new JsonObject
                {
                    ["lightmap"] = Texture(lm), ["matcap"] = Texture(mc), ["matcapEncoding"] = "Srgb",
                    ["darkColor"] = new JsonArray(.2f, .3f, .4f), ["lightColor"] = new JsonArray(1.2f, 1.3f, 1.4f),
                    ["shadowMultiplyColor"] = new JsonArray(.8f, .7f, .6f), ["enableMetallics"] = .6f,
                    ["tileScale"] = 1.7f, ["brightness"] = .8f, ["litMode"] = "Uniform", ["uniformLitFactor"] = .95f
                }
            })
        };
        var legacyDecoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        Assert.Equal("metallic", Assert.Single(legacyDecoded.Materials[0].PluginBindings).ModeId);
        foreach (string damage in new[] { "missing", "unknown", "schema", "encoding", "mode", "uv" })
        {
            var doc = JsonNode.Parse(legacy.ToJsonString())!;
            var map = doc["materials"]![0]!["genshinMetallic"]!.AsObject();
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
        var resolved = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog);
        Assert.Empty(resolved.PluginBindings);
        for (int index = 0; index < 2; index++)
        {
            var textures = source.Surface.Snapshot.Textures.ToArray();
            textures[index] = new("changed", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile,
                Source(textures), id, pipeline, TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact]
    public void ProfileRejectsMismatchedSharedMapAndConflictingFamilies()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var row = Profile(source).Materials[0];
        var hair = row with { Role = D3D12CharacterMaterialRole.Hair, PluginBindings = [.. row.PluginBindings, TestStylePlugins.GenshinHair(0)] };
        var id = Guid.NewGuid();
        var hairMapped = D3D12MaterialBindingProfileResolver.Resolve(
            new("ok-hair", "Ok Hair", layout.Fingerprint, [hair]), source, id, "genshin", TestStylePlugins.AllViewCatalog);
        Assert.Equal(2, hairMapped.PluginBindings.Count);
        var badHair = row with { Role = D3D12CharacterMaterialRole.Hair, PluginBindings = [.. row.PluginBindings, TestStylePlugins.GenshinHair(2)] };
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            new("bad-hair", "Bad Hair", layout.Fingerprint, [badHair]), source, id, "genshin", TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            new("bad-default", "Bad Default", layout.Fingerprint, [row with { PluginBindings = [.. row.PluginBindings, new StyleMaterialBindingData("genshin", "default")] }]),
            source, id, "genshin", TestStylePlugins.AllViewCatalog));
        foreach (var badRole in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                new("bad-role", "Bad Role", layout.Fingerprint, [row with { Role = badRole }]),
                source, id, "genshin", TestStylePlugins.AllViewCatalog));
        var mapped = D3D12MaterialBindingProfileResolver.Resolve(Profile(source), source, id, "genshin", TestStylePlugins.AllViewCatalog);
        var value = Assert.Single(mapped.PluginBindings).Binding;
        Assert.Equal(0, value.TextureSlots!["lightmap"]); Assert.Equal(1, value.TextureSlots["matcap"]);
        Assert.Equal("Uniform", value.StringValues!["litMode"]);
    }

    [Fact]
    public void LegacySchemaFiveDecodesEndfieldMatcapAndSchemaFourteenRoundtrips()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source); var image = layout.CaptureTexture(0);
        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
            ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null,
            ["endfieldMatcap"] = new JsonObject
            {
                ["normal"] = Texture(image), ["p"] = Texture(image), ["matcap"] = Texture(image),
                ["matcapEncoding"] = "Srgb", ["uvChannel"] = 0,
                ["baseColor"] = new JsonArray(1f, 1f, 1f), ["highlightColor"] = new JsonArray(0f, 0f, 0f),
                ["normalStrength"] = 2f, ["rotationRadians"] = 0f, ["highlightCoefficient"] = 0f,
                ["shadowHardness"] = .5f, ["lightControlDirection"] = null, ["baselineStrength"] = 1f,
                ["coupleEndfieldPalette"] = false, ["generatorRevision"] = "mikk-v1"
            }
        };
        var legacy = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 5,
            ["stableId"] = "legacy-five", ["name"] = "Legacy Five", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint, ["materials"] = new JsonArray(row)
        };
        var decoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacy.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        var binding = Assert.Single(decoded.Materials[0].PluginBindings);
        Assert.Equal("endfield", binding.StyleId);
        Assert.Equal("matcap", binding.ModeId);
        Assert.Equal("Srgb", binding.StringValues!["matcapEncoding"]);
        byte[] upgraded = D3D12MaterialBindingProfileJson.Encode(decoded);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, JsonNode.Parse(upgraded)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(upgraded, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(upgraded)));
    }

    [Fact]
    public void PortableSchemaFourteenPreservesFullTextureTableAfterSourceRemovalAndDefaultResave()
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
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "genshin", TestStylePlugins.AllViewCatalog);
                var selection = Assert.Single(mapped.PluginBindings).Binding;
                Assert.Equal(offset, selection.TextureSlots!["lightmap"]); Assert.Equal(offset + 1, selection.TextureSlots["matcap"]);
                Assert.Equal("SrgbRgb", selection.StringValues!["matcapEncoding"]);
                Assert.Equal(extended.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < extended.Textures.Count; i++)
                    Assert.Equal(extended.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(mapped.PluginBindings, TestStylePlugins.AllViewCatalog));
                var resolved = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
                    capture.RenderingStyles, mapped.CharacterMaterials, mapped.PluginBindings, sceneInputs: capture.SceneInputs,
                    styleCatalog: TestStylePlugins.AllViewCatalog);
                Assert.Equal("metallic", Assert.Single(resolved.SurfaceStyles.CharacterMaterials).PluginDraw!.ShaderModuleId);
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, again[item.Id].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(again[item.Id]));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static JsonObject Texture(D3D12MaterialBindingProfileTexture image) => new()
    {
        ["sourceSceneTextureOrdinal"] = image.SourceSceneTextureOrdinal, ["width"] = image.Width,
        ["height"] = image.Height, ["kind"] = image.Kind.ToString(), ["rgbaSha256"] = image.RgbaSha256
    };
    private static StyleMaterialBindingData MakeBinding(
        int lightmapSlot = 0,
        int matcapSlot = 1,
        string matcapEncoding = "SrgbRgb",
        Vector3? darkColor = null,
        Vector3? lightColor = null,
        Vector3? shadowMultiplyColor = null,
        float enableMetallics = .6f,
        float tileScale = 1.7f,
        float brightness = .8f,
        string litMode = "Uniform",
        float uniformLitFactor = .95f)
        => TestStylePlugins.GenshinMetallic(
            lightmapSlot, matcapSlot, matcapEncoding,
            darkColor ?? new(.2f, .3f, .4f),
            lightColor ?? new(1.2f, 1.3f, 1.4f),
            shadowMultiplyColor ?? new(.8f, .7f, .6f),
            enableMetallics, tileScale, brightness, litMode, uniformLitFactor);
    private static D3D12ViewPluginMaterialBinding View(Guid id) => TestStylePlugins.Bind(id, 0, MakeBinding());
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        return new("genshin-metallic", "Explicit metallic", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body,
                pluginBindings: [MakeBinding(offset, offset + 1)],
                textureGuards: [layout.CaptureTexture(offset), layout.CaptureTexture(offset + 1)])]);
    }
    private static OriginalFrameScene Source(SceneTexture[]? textures = null)
    {
        var frame = T8bTestHelpers.CreateMetallicFrame(seeds: false, textures: textures ?? [new("R numerical", 1, 1, [240, 0, 0, 255]),
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
