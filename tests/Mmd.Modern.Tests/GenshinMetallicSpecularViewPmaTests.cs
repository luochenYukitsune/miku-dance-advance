using System.Buffers.Binary;
using System.Collections.ObjectModel;
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

public sealed class GenshinMetallicSpecularViewPmaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DetachedNestedPayloadAndRequestsHandleOptionalRampAndValidation()
    {
        var id = Guid.NewGuid();
        var slots = new Dictionary<string, int>(StringComparer.Ordinal) { ["lightmap"] = 0, ["matcap"] = 1, ["specularRamp"] = 2 };
        var value = TestStylePlugins.Bind(id, 0, MakeBinding(rampSlot: 2, rampMix: 0f));
        D3D12ViewPluginMaterialBinding[] input = [value];
        var frozen = D3D12ViewPluginMaterialBindings.Detach(input);
        input[0] = TestStylePlugins.Bind(id, 0, MakeBinding(hasSpecular: false));
        Assert.True(D3D12StylePluginAdapter.BindingDataEquivalent(value.Binding, Assert.Single(frozen).Binding));
        Assert.Same(frozen, D3D12ViewPluginMaterialBindings.Detach(frozen));

        // Zero rampMix omits the optional ramp from GetSceneTextureRequests; positive rampMix requests it.
        var zeroMixReq = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests(frozen, TestStylePlugins.AllViewCatalog));
        Assert.Equal(new[] { 0, 1 }, zeroMixReq.OptionalLocalTextureIndices.Order());

        var active = TestStylePlugins.Bind(id, 0, MakeBinding(rampSlot: 2, rampMix: .5f));
        var matcap = TestStylePlugins.Bind(id, 1, T8bTestHelpers.CreateEndfieldMatcapBinding(3, 4, 8));
        var face = TestStylePlugins.Bind(id, 2, TestStylePlugins.FaceSdf("genshin", 0, 1,
            trackingMode: StyleBoneTrackingMode.SelectedBone, boneName: "4"));
        var boneNames = new Dictionary<Guid, IReadOnlyDictionary<string, int>>
        {
            [id] = new Dictionary<string, int>(StringComparer.Ordinal) { ["4"] = 4 }
        };
        var unionReq = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests(
            [active, matcap, face], TestStylePlugins.AllViewCatalog,
            [new(id, 2, D3D12CharacterMaterialRole.Face)], boneNames));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 8 }, unionReq.OptionalLocalTextureIndices.Order());
        Assert.Equal(new[] { 4 }, unionReq.BoneIndices);

        var noRamp = TestStylePlugins.Bind(id, 0, MakeBinding(rampSlot: -1, rampMix: 0f));
        Assert.Equal(new[] { 0, 1 }, Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests([noRamp], TestStylePlugins.AllViewCatalog)).OptionalLocalTextureIndices.Order());

        foreach (var bad in new[]
        {
            TestStylePlugins.Bind(id, 0, MakeBinding(specularColor: new(float.NaN))),
            TestStylePlugins.Bind(id, 0, MakeBinding(specularSharpColor: new(float.PositiveInfinity))),
            TestStylePlugins.Bind(id, 0, MakeBinding(specularScale: float.NaN)),
            TestStylePlugins.Bind(id, 0, MakeBinding(specularAttenuation: float.NaN)),
            TestStylePlugins.Bind(id, 0, MakeBinding(specularSharpOffset: float.NaN)),
            TestStylePlugins.Bind(id, 0, MakeBinding(rampMix: float.NaN)),
            new D3D12ViewPluginMaterialBinding(id, 0, new("genshin", "metallic",
                new ReadOnlyDictionary<string, int>(slots.ToDictionary(p => p.Key, p => p.Key == "specularRamp" ? -2 : p.Value)),
                value.Binding.NumberValues, value.Binding.BoolValues, value.Binding.VectorValues, value.Binding.StringValues))
        })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Detach([bad]));

        foreach (var invalid in new[]
        {
            TestStylePlugins.Bind(id, 0, MakeBinding(specularShininess: -1f)),
            TestStylePlugins.Bind(id, 0, MakeBinding(rampSlot: -1, rampMix: .1f)),
        })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([], [invalid], TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void SameCaptureRebasesNestedRampPerObjectAndRetainsTheParentsSharedRbLightmap()
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        var first = editor.Add("first selected object", Path.Combine(Path.GetTempPath(), "metal-spec-a.pmx"), ModelRole.Character, source);
        var second = editor.Add("second selected object", Path.Combine(Path.GetTempPath(), "metal-spec-b.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("genshin"), Token);
        D3D12ViewPluginMaterialBinding[] bindings = [View(first.Id), View(second.Id)];
        D3D12ViewPluginMaterialBinding[] hair = [TestStylePlugins.Bind(first.Id, 0, TestStylePlugins.GenshinHair(0))];
        D3D12ViewPluginMaterialBinding[] all = [.. bindings, .. hair];
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(all, TestStylePlugins.AllViewCatalog);
        var capture = project.CaptureRenderSnapshot(requests);
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, pluginBindings: all, styleCatalog: TestStylePlugins.AllViewCatalog);
        foreach (var obj in capture.RenderingStyles)
        {
            var style = result.SurfaceStyles.CharacterMaterials[obj.MaterialStart];
            var draw = Assert.IsType<D3D12PluginDrawData>(style.PluginDraw);
            Assert.Equal(obj.ObjectId == first.Id ? "metallic-specular-rb" : "metallic-specular", draw.ShaderModuleId);
            var texBySlot = draw.TextureBindings!.ToDictionary(t => t.SlotIndex, t => t.SceneTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[0], texBySlot[3]);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[1], texBySlot[4]);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[2], texBySlot[5]);
            int cbOffset = obj.ObjectId == first.Id ? 16 : 0;
            var specCb = draw.ConstantBufferData.Span[(cbOffset + 64)..];
            Assert.Equal(new Vector3(.7f, 1.2f, -.1f), new(
                BinaryPrimitives.ReadSingleLittleEndian(specCb[..4]),
                BinaryPrimitives.ReadSingleLittleEndian(specCb[4..8]),
                BinaryPrimitives.ReadSingleLittleEndian(specCb[8..12])));
            Assert.Equal(12f, BinaryPrimitives.ReadSingleLittleEndian(specCb[12..16]));
            Assert.Equal(.6f, BinaryPrimitives.ReadSingleLittleEndian(specCb[40..44]));
            if (obj.ObjectId == first.Id)
                Assert.Equal(texBySlot[3], texBySlot[0]);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(Modern(capture.Scene, source),
            TestStylePlugins.AllPresetCatalog, capture.RenderingStyles,
            pluginBindings: [.. bindings, TestStylePlugins.Bind(first.Id, 0, TestStylePlugins.GenshinHair(2))],
            styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveRampRequiresACurrentImageAndProfileGuardsUnusedRamp(bool procedural)
    {
        var source = Source(); var textures = source.Surface.Snapshot.Textures.ToArray();
        if (procedural) textures[2] = SceneTexture.ProceduralToon();
        source = Source(textures); var editor = new SceneCompositionEditor();
        var item = editor.Add("selected resource", Path.Combine(Path.GetTempPath(), "metal-spec-missing.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("genshin"), Token);
        var activeMix = View(item.Id, mix: .5f);
        var capturedWithoutRamp = TestStylePlugins.Bind(item.Id, 0, MakeBinding(hasSpecular: false));
        var capture = project.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(
            [capturedWithoutRamp], TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source),
            TestStylePlugins.AllPresetCatalog, capture.RenderingStyles, sceneInputs: capture.SceneInputs,
            pluginBindings: [activeMix], styleCatalog: TestStylePlugins.AllViewCatalog));

        if (procedural)
        {
            var layout = D3D12MaterialBindingLayout.Capture(source);
            var procProfile = new D3D12MaterialBindingProfile("proc", "Proc", layout.Fingerprint,
                [new(0, D3D12CharacterMaterialRole.Body,
                    pluginBindings: [MakeBinding(rampSlot: 2, rampMix: 0f)],
                    textureGuards: [layout.CaptureTexture(0), layout.CaptureTexture(1),
                        new(2, 2, 2, SceneTextureKind.IndexedProceduralToon, layout.CaptureTexture(2).RgbaSha256)])]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                procProfile, source, item.Id, "genshin", TestStylePlugins.AllViewCatalog));
        }

        var absent = TestStylePlugins.Bind(item.Id, 0, MakeBinding(rampSlot: -1, rampMix: 0f));
        var resolved = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, pluginBindings: [absent], styleCatalog: TestStylePlugins.AllViewCatalog);
        Assert.Equal(2, Assert.Single(resolved.SurfaceStyles.CharacterMaterials).PluginDraw!.TextureBindings!.Count);
    }

    [Fact]
    public void SchemaFourteenAndLegacySchemaSevenRoundtripAndRejectMalformedPayloads()
    {
        var profile = Profile(Source()); var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        foreach (string damage in new[] { "missing", "unknown", "schema", "kind", "numeric-enum", "nonfinite" })
        {
            var doc = JsonNode.Parse(bytes)!;
            var row = doc["materials"]![0]!.AsObject();
            var binding = row["pluginBindings"]![0]!.AsObject();
            var guard = row["textureGuards"]![2]!.AsObject();
            if (damage == "missing") binding.Remove("modeId");
            if (damage == "unknown") binding["unexpected"] = 1;
            if (damage == "schema") doc["schemaVersion"] = 7;
            if (damage == "kind") guard["kind"] = "image";
            if (damage == "numeric-enum") guard["kind"] = "0";
            if (damage == "nonfinite") binding["numberValues"]!["specularScale"] = "NaN";
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
        var future = JsonNode.Parse(bytes)!; future["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(future.ToJsonString())));

        var layout = D3D12MaterialBindingLayout.Capture(Source());
        var lm = layout.CaptureTexture(0); var mc = layout.CaptureTexture(1); var rm = layout.CaptureTexture(2);
        var legacy = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 7,
            ["stableId"] = "legacy-seven", ["name"] = "Legacy Seven", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint,
            ["materials"] = new JsonArray(new JsonObject
            {
                ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
                ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
                ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
                ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null, ["endfieldMatcap"] = null,
                ["genshinMetallic"] = new JsonObject
                {
                    ["lightmap"] = Texture(lm), ["matcap"] = Texture(mc), ["matcapEncoding"] = "SrgbRgb",
                    ["darkColor"] = Vector(new(.2f, .3f, .4f)), ["lightColor"] = Vector(new(1.2f, 1.3f, 1.4f)),
                    ["shadowMultiplyColor"] = Vector(new(.8f, .7f, .6f)), ["enableMetallics"] = .6f,
                    ["tileScale"] = 1.7f, ["brightness"] = .8f, ["litMode"] = "Uniform", ["uniformLitFactor"] = .95f,
                    ["specular"] = new JsonObject
                    {
                        ["color"] = Vector(new(.7f, 1.2f, -.1f)), ["sharpColor"] = Vector(new(.2f, .4f, 1.3f)),
                        ["shininess"] = 12f, ["scale"] = .8f, ["attenuation"] = .4f, ["sharpOffset"] = .9f,
                        ["ramp"] = Texture(rm), ["rampMix"] = .6f
                    }
                }
            })
        };
        var decodedLegacy = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        Assert.True(decodedLegacy.Materials[0].PluginBindings[0].BoolValues!["hasSpecular"]);
        foreach (string damage in new[] { "missing", "unknown", "schema", "encoding", "mode", "kind", "numeric-enum", "ramp-missing", "parameter-missing", "ramp-required", "nonfinite" })
        {
            var doc = JsonNode.Parse(legacy.ToJsonString())!; var parent = doc["materials"]![0]!["genshinMetallic"]!.AsObject();
            var specular = parent["specular"]!.AsObject();
            if (damage == "missing") parent.Remove("specular");
            if (damage == "unknown") specular["intensity"] = 1;
            if (damage == "schema") doc["schemaVersion"] = 6;
            if (damage == "encoding") parent["matcapEncoding"] = "Guess";
            if (damage == "mode") parent["litMode"] = "ShaderInfo";
            if (damage == "kind") specular["ramp"]!["kind"] = "image";
            if (damage == "numeric-enum") specular["ramp"]!["kind"] = "0";
            if (damage == "ramp-missing") specular.Remove("ramp");
            if (damage == "parameter-missing") specular.Remove("sharpOffset");
            if (damage == "ramp-required") specular["ramp"] = null;
            if (damage == "nonfinite") specular["scale"] = "NaN";
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
    }

    [Fact]
    public void SchemaFourteenAllowsNoRampAtZeroMixAndMixedParentsWithoutSpecular()
    {
        var source = Source(); var profile = Profile(source, ramp: false, mix: 0);
        var rows = new[] { profile.Materials[0], profile.Materials[0] with { LocalMaterialIndex = 1,
            PluginBindings = [MakeBinding(hasSpecular: false)] } };
        profile = new(profile.StableId, profile.Name, profile.LayoutFingerprint, rows);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.False(restored.Materials[0].PluginBindings[0].TextureSlots!.ContainsKey("specularRamp"));
        Assert.Null(restored.Materials[1].PluginBindings[0].BoolValues);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void DormantProfileGuardsAnExplicitUnusedRampAndRejectsProceduralResources(string pipeline)
    {
        var source = Source(); var profile = Profile(source, mix: 0); var id = Guid.NewGuid();
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog).PluginBindings);
        var textures = source.Surface.Snapshot.Textures.ToArray();
        textures[2] = new("mutated ramp", 2, 2, new byte[16]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(textures), id, pipeline, TestStylePlugins.AllViewCatalog));
        var row = profile.Materials[0];
        var procGuards = row.TextureGuards.ToArray();
        procGuards[2] = procGuards[2] with { Kind = SceneTextureKind.IndexedProceduralToon };
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            new("proc-guard", "Proc Guard", profile.LayoutFingerprint, [row with { TextureGuards = procGuards }]),
            source, id, pipeline, TestStylePlugins.AllViewCatalog));
        var active = Assert.Single(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, "genshin", TestStylePlugins.AllViewCatalog).PluginBindings).Binding;
        Assert.Equal(2, active.TextureSlots!["specularRamp"]);
        Assert.Equal(0, active.NumberValues!["specularRampMix"]);
    }

    [Fact]
    public void LegacySchemaSixDecodesWithoutSpecularAndUpgradesCleanly()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var lm = layout.CaptureTexture(0); var mc = layout.CaptureTexture(1);
        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
            ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null, ["endfieldMatcap"] = null,
            ["genshinMetallic"] = new JsonObject
            {
                ["lightmap"] = Texture(lm), ["matcap"] = Texture(mc),
                ["matcapEncoding"] = "Srgb", ["darkColor"] = Vector(new(.2f, .3f, .4f)), ["lightColor"] = Vector(new(1.2f, 1.3f, 1.4f)),
                ["shadowMultiplyColor"] = Vector(new(.8f, .7f, .6f)), ["enableMetallics"] = .6f,
                ["tileScale"] = 1.7f, ["brightness"] = .8f, ["litMode"] = "Uniform",
                ["uniformLitFactor"] = .95f
            }
        };
        var legacy = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 6,
            ["stableId"] = "genshin-metal", ["name"] = "Explicit metal", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint, ["materials"] = new JsonArray(row)
        };
        var decoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacy.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        var binding = Assert.Single(decoded.Materials[0].PluginBindings);
        Assert.Null(binding.BoolValues);
        byte[] upgraded = D3D12MaterialBindingProfileJson.Encode(decoded);
        Assert.DoesNotContain("hasSpecular", Encoding.UTF8.GetString(upgraded));
        Assert.Equal(upgraded, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(upgraded)));
    }

    [Fact]
    public void PortableSchemaFourteenRestoresRampAndFullOriginalTextureTableAfterRelocationAndDefaultResave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "metal-spec-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "source.pmx");
            var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
            var clean = new PmxModel
            {
                Header = template.Header, Name = "portable fixture", EnglishName = "", Comment = "", EnglishComment = "",
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
            var editor = new SceneCompositionEditor(); var item = editor.Add("portable fixture", originalPath, ModelRole.Character, source);
            using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
            project.SetDefaultRenderingStyle(new("genshin"), Token);
            var profile = Profile(source, offset); string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project, [new(item.Id, profile, source)]), Token);
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
                Assert.Equal(offset + 2, selection.TextureSlots["specularRamp"]);
                Assert.Equal(extended.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < extended.Textures.Count; i++)
                {
                    Assert.Equal(extended.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                    Assert.Equal(extended.Textures[i].Width, restored.Surface.Snapshot.Textures[i].Width);
                    Assert.Equal(extended.Textures[i].Height, restored.Surface.Snapshot.Textures[i].Height);
                }
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(mapped.PluginBindings, TestStylePlugins.AllViewCatalog));
                var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
                    capture.RenderingStyles, mapped.CharacterMaterials, mapped.PluginBindings, sceneInputs: capture.SceneInputs,
                    styleCatalog: TestStylePlugins.AllViewCatalog);
                var draw = Assert.IsType<D3D12PluginDrawData>(Assert.Single(result.SurfaceStyles.CharacterMaterials).PluginDraw);
                Assert.Equal("metallic-specular", draw.ShaderModuleId);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 2], draw.TextureBindings!.Single(t => t.SlotIndex == 5).SceneTextureIndex);
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, again[item.Id].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(again[item.Id]));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static JsonArray Vector(Vector3 value) => new(value.X, value.Y, value.Z);
    private static JsonObject Texture(D3D12MaterialBindingProfileTexture image) => new()
    {
        ["sourceSceneTextureOrdinal"] = image.SourceSceneTextureOrdinal, ["width"] = image.Width,
        ["height"] = image.Height, ["kind"] = image.Kind.ToString(), ["rgbaSha256"] = image.RgbaSha256
    };
    private static StyleMaterialBindingData MakeBinding(
        int lightmapSlot = 0,
        int matcapSlot = 1,
        bool hasSpecular = true,
        Vector3? specularColor = null,
        Vector3? specularSharpColor = null,
        float specularShininess = 12f,
        float specularScale = .8f,
        float specularAttenuation = .4f,
        float specularSharpOffset = .9f,
        int rampSlot = 2,
        float rampMix = .6f)
        => TestStylePlugins.GenshinMetallic(
            lightmapSlot, matcapSlot, "SrgbRgb",
            new(.2f, .3f, .4f), new(1.2f, 1.3f, 1.4f), new(.8f, .7f, .6f),
            .6f, 1.7f, .8f, "Uniform", .95f,
            hasSpecular: hasSpecular,
            specularColor: specularColor ?? new(.7f, 1.2f, -.1f),
            specularSharpColor: specularSharpColor ?? new(.2f, .4f, 1.3f),
            specularShininess: specularShininess,
            specularScale: specularScale,
            specularAttenuation: specularAttenuation,
            specularSharpOffset: specularSharpOffset,
            specularRampSlot: rampSlot,
            specularRampMix: rampMix);
    private static D3D12ViewPluginMaterialBinding View(Guid id, float mix = .6f)
        => TestStylePlugins.Bind(id, 0, MakeBinding(rampMix: mix));
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0, bool ramp = true, float mix = .6f)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        D3D12MaterialBindingProfileTexture[] guards = ramp
            ? [layout.CaptureTexture(offset), layout.CaptureTexture(offset + 1), layout.CaptureTexture(offset + 2)]
            : [layout.CaptureTexture(offset), layout.CaptureTexture(offset + 1)];
        return new("genshin-metal", "Explicit metal", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body,
                pluginBindings: [MakeBinding(offset, offset + 1, rampSlot: ramp ? offset + 2 : -1, rampMix: mix)],
                textureGuards: guards)]);
    }
    private static OriginalFrameScene Source(SceneTexture[]? textures = null)
    {
        var frame = T8bTestHelpers.CreateMetallicFrame(seeds: false, textures: textures ??
            [new("R numerical", 1, 1, [240, 0, 130, 255]), new("color ChannelPacked", 1, 1, [12, 200, 130, 170]),
                new("numeric multirow ramp", 2, 2, [255, 0, 31, 0, 0, 255, 67, 77, 7, 33, 255, 190, 99, 200, 13, 255]),
                new("unreferenced resource", 1, 1, [3, 8, 19, 37])]);
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
