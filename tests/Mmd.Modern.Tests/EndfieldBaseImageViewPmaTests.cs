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
using Mmd.Modern.Smoke;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class EndfieldBaseImageViewPmaTests
{
    private const int W = 97, H = 97;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);
    private static readonly OriginalFrameOptions ModernOptions = new(OriginalAlphaBlendMode.Normal, true, false,
        BackgroundColor: Vector4.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RequestsUnionAllSelectedCpreImagesEvenAtZeroShadowStrength()
    {
        var id = Guid.NewGuid();
        var bindings = D3D12ViewPluginMaterialBindings.Detach([TestStylePlugins.Bind(id, 0, ViewSpec().ToBinding())]);
        var baseReq = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests(bindings, TestStylePlugins.AllViewCatalog));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 7, 8 }, baseReq.OptionalLocalTextureIndices.Order());
        var merged = new SceneObjectRenderRequest(id, [4], [6], [..baseReq.OptionalLocalTextureIndices, 9], includeSourceGeometry: true);
        Assert.True(merged.IncludeSourceGeometry);
        Assert.Equal(new[] { 4 }, merged.BoneIndices);
        Assert.Equal(new[] { 6 }, merged.LocalTextureIndices);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9 }, merged.OptionalLocalTextureIndices.Order());
    }

    [Fact]
    public void TwoObjectsRebaseThreeNoncontiguousImagesOnceAndDeduplicateOnlyIdenticalImages()
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        var a = editor.Add("first", Path.Combine(Path.GetTempPath(), "base-a.pmx"), ModelRole.Character, source);
        var images = source.Surface.Snapshot.Textures.ToArray();
        images[1] = new("second base", 1, 1, [120, 90, 180, 4]);
        images[8] = new("second qR", 1, 1, [180, 9, 10, 11]);
        var b = editor.Add("second", Path.Combine(Path.GetTempPath(), "base-b.pmx"), ModelRole.Character, Source(images));
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        D3D12ViewPluginMaterialBinding[] values =
        [
            TestStylePlugins.Bind(a.Id, 0, ViewSpec().ToBinding()),
            TestStylePlugins.Bind(b.Id, 0, ViewSpec().ToBinding()),
        ];
        var pluginReqs = D3D12ViewPluginMaterialBindings.CreateRequests(values, TestStylePlugins.AllViewCatalog);
        var capture = project.CaptureRenderSnapshot(pluginReqs.Select(r =>
            new SceneObjectRenderRequest(r.ObjectId, r.BoneIndices, r.LocalTextureIndices, r.OptionalLocalTextureIndices, includeSourceGeometry: true)));
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, pluginBindings: values, sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
        foreach (var obj in capture.RenderingStyles)
        {
            var draw = result.SurfaceStyles.CharacterMaterials[obj.MaterialStart].PluginDraw;
            Assert.NotNull(draw);
            Assert.Equal("matcap-palette-base", draw.ShaderModuleId);
            var texBySlot = draw.TextureBindings!.ToDictionary(static t => t.SlotIndex, static t => t.SceneTextureIndex);
            var mapped = capture.SceneInputs[obj.ObjectId].TextureIndices;
            Assert.Equal(mapped[1], texBySlot[6]);
            Assert.Equal(mapped[4], texBySlot[7]);
            Assert.False(texBySlot.ContainsKey(8)); // ShadowStrength == 0f
            Assert.True(mapped.ContainsKey(8));
            Assert.NotNull(capture.SceneInputs[obj.ObjectId].SourceGeometry);
        }
        Assert.Equal(capture.SceneInputs[a.Id].TextureIndices[4], capture.SceneInputs[b.Id].TextureIndices[4]);
        Assert.NotEqual(capture.SceneInputs[a.Id].TextureIndices[1], capture.SceneInputs[b.Id].TextureIndices[1]);
        Assert.NotEqual(capture.SceneInputs[a.Id].TextureIndices[8], capture.SceneInputs[b.Id].TextureIndices[8]);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(4, false)] [InlineData(8, false)]
    [InlineData(1, true)] [InlineData(4, true)] [InlineData(8, true)]
    public void RequestedCpreImagesRemainRequiredEvenAtZeroShadowStrength(int selected, bool procedural)
    {
        var baseSource = Source();
        var images = baseSource.Surface.Snapshot.Textures.ToArray();
        if (procedural) images[selected] = SceneTexture.ProceduralToon();
        var source = Source(images); var editor = new SceneCompositionEditor();
        var item = editor.Add("required", Path.Combine(Path.GetTempPath(), "base-required.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        var value = TestStylePlugins.Bind(item.Id, 0, ViewSpec().ToBinding());
        if (!procedural)
        {
            var request = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests([value], TestStylePlugins.AllViewCatalog));
            request = new(item.Id, optionalLocalTextureIndices: request.OptionalLocalTextureIndices.Where(v => v != selected), includeSourceGeometry: true);
            var capture = project.CaptureRenderSnapshot([request]);
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
                capture.RenderingStyles, pluginBindings: [value], sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog));
        }
        else
        {
            var profile = Profile(baseSource);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                profile, source, item.Id, "endfield", TestStylePlugins.AllViewCatalog));
        }
    }

    [Theory]
    [InlineData("mmd")] [InlineData("genshin")] [InlineData("star-rail")] [InlineData("wuthering-waves")]
    public void DormantFamilyStillGuardsAllRequestedCpreImages(string pipeline)
    {
        var source = Source(); var profile = Profile(source); var id = Guid.NewGuid();
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog).PluginBindings);
        foreach (int selected in new[] { 1, 4, 8 })
        {
            var images = source.Surface.Snapshot.Textures.ToArray(); images[selected] = new("changed", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(images), id, pipeline, TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact]
    public void SchemaFourteenRoundtripsAndLegacySchemaNineStrictlyValidatesEveryField()
    {
        var source = Source();
        var profile = Profile(source);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));

        // Legacy schema 9 JSON wire strictness.
        var layout = D3D12MaterialBindingLayout.Capture(source);
        JsonObject Tex(int ordinal) => Texture(layout.CaptureTexture(ordinal));
        var legacyDoc = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 9,
            ["stableId"] = "endfield-palette", ["name"] = "Explicit palette", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint,
            ["materials"] = new JsonArray(new JsonObject
            {
                ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
                ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
                ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
                ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null,
                ["endfieldMatcap"] = new JsonObject
                {
                    ["normal"] = Tex(0), ["p"] = Tex(2), ["matcap"] = Tex(3),
                    ["matcapEncoding"] = "SrgbRgb", ["uvChannel"] = 0,
                    ["baseColor"] = Vector(new(.2f, .3f, .4f)), ["highlightColor"] = Vector(new(.1f, .4f, .2f)),
                    ["normalStrength"] = 2f, ["rotationRadians"] = 0f, ["highlightCoefficient"] = 0f,
                    ["shadowHardness"] = .5f, ["lightControlDirection"] = null,
                    ["baselineStrength"] = 1f, ["coupleEndfieldPalette"] = true, ["generatorRevision"] = "mikk-v1",
                    ["palette"] = new JsonObject
                    {
                        ["receivedLightLinearRgb"] = new JsonArray(.2f, .3f, 1.1f), ["emissionStrength"] = 0f,
                        ["uniformEmissionBlue"] = 0f, ["lightInfluence"] = 0f, ["density"] = .7f,
                        ["emissionInputMode"] = "Texture", ["emissionTexture"] = Tex(5),
                        ["receivedLightInputMode"] = "Texture", ["receivedLightTexture"] = Tex(7),
                        ["baseImage"] = new JsonObject
                        {
                            ["baseImageLinearRgb"] = Vector(new(.4f)), ["modelColor"] = Vector(new(.5f)),
                            ["shadowColor"] = Vector(new(.7f, .8f, .9f)), ["colorMultiplier"] = Vector(new(.9f)),
                            ["shadowFactor"] = 1f, ["warmTint"] = .4f, ["contrast"] = .1f,
                            ["saturation"] = 1.2f, ["detailBlue"] = 1f, ["shadowStrength"] = 0f,
                            ["baseInputMode"] = "Texture", ["baseTexture"] = Tex(1), ["baseEncoding"] = "SrgbRgb",
                            ["detailInputMode"] = "Texture", ["detailTexture"] = Tex(4),
                            ["shadowInputMode"] = "Texture", ["shadowTexture"] = Tex(8)
                        }
                    }
                },
                ["genshinMetallic"] = null
            })
        };
        var legacyDecoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyDoc.ToJsonString()));
        Assert.Equal(8, legacyDecoded.Materials[0].TextureGuards.Count);
        Assert.True(legacyDecoded.Materials[0].PluginBindings[0].BoolValues!["hasBaseImage"]);

        var basis = legacyDoc["materials"]![0]!["endfieldMatcap"]!["palette"]!["baseImage"]!.AsObject();
        foreach (string field in basis.Select(v => v.Key).ToArray())
        {
            var doc = JsonNode.Parse(legacyDoc.ToJsonString())!;
            doc["materials"]![0]!["endfieldMatcap"]!["palette"]!["baseImage"]!.AsObject().Remove(field);
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
        foreach (string damage in new[] { "unknown", "old-schema", "encoding", "enum", "number", "uniform-image", "null-image" })
        {
            var doc = JsonNode.Parse(legacyDoc.ToJsonString())!;
            var node = doc["materials"]![0]!["endfieldMatcap"]!["palette"]!["baseImage"]!.AsObject();
            if (damage == "unknown") node["guessRole"] = true;
            if (damage == "old-schema") doc["schemaVersion"] = 8;
            if (damage == "encoding") node["baseEncoding"] = "Guess";
            if (damage == "enum") node["detailInputMode"] = "1";
            if (damage == "number") node["contrast"] = 5;
            if (damage == "uniform-image") node["shadowInputMode"] = "Uniform";
            if (damage == "null-image") node["baseTexture"] = null;
            if (damage is "number" or "uniform-image")
            {
                var decodedBad = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString()));
                Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                    decodedBad, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
            }
            else
                Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
        var future = JsonNode.Parse(bytes)!; future["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(future.ToJsonString())));
    }

    [Fact]
    public void MixedRowsWithAndWithoutBaseImageRoundtripAndLegacySchemaEightStaysCompatible()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var withBase = Profile(source).Materials[0];
        var baseSpec = ViewSpec();
        var withoutBase = new D3D12MaterialBindingProfileRow(1, D3D12CharacterMaterialRole.Body,
            pluginBindings: [(baseSpec with { Palette = baseSpec.Palette! with { BaseImage = null }, BaseImage = null }).ToBinding()],
            textureGuards: [layout.CaptureTexture(0), layout.CaptureTexture(2), layout.CaptureTexture(3), layout.CaptureTexture(5), layout.CaptureTexture(7)]);
        var mixed = new D3D12MaterialBindingProfile("endfield-palette", "Mixed baseImage", layout.Fingerprint, [withBase, withoutBase]);
        var mixedBytes = D3D12MaterialBindingProfileJson.Encode(mixed);
        var restored = D3D12MaterialBindingProfileJson.Decode(mixedBytes);
        Assert.True(restored.Materials[0].PluginBindings[0].BoolValues!.ContainsKey("hasBaseImage"));
        Assert.False(restored.Materials[1].PluginBindings[0].BoolValues!.ContainsKey("hasBaseImage"));
        Assert.Equal(mixedBytes, D3D12MaterialBindingProfileJson.Encode(restored));

        // Legacy schema 8 JSON without baseImage decodes without hasBaseImage.
        JsonObject Tex(int ordinal) => Texture(layout.CaptureTexture(ordinal));
        var legacy8 = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 8,
            ["stableId"] = "legacy8", ["name"] = "Legacy 8", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint,
            ["materials"] = new JsonArray(new JsonObject
            {
                ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
                ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
                ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
                ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null,
                ["endfieldMatcap"] = new JsonObject
                {
                    ["normal"] = Tex(0), ["p"] = Tex(2), ["matcap"] = Tex(3),
                    ["matcapEncoding"] = "SrgbRgb", ["uvChannel"] = 0,
                    ["baseColor"] = Vector(new(.2f, .3f, .4f)), ["highlightColor"] = Vector(new(.1f, .4f, .2f)),
                    ["normalStrength"] = 2f, ["rotationRadians"] = 0f, ["highlightCoefficient"] = 0f,
                    ["shadowHardness"] = .5f, ["lightControlDirection"] = null,
                    ["baselineStrength"] = 1f, ["coupleEndfieldPalette"] = true, ["generatorRevision"] = "mikk-v1",
                    ["palette"] = new JsonObject
                    {
                        ["receivedLightLinearRgb"] = new JsonArray(.2f, .3f, 1.1f), ["emissionStrength"] = 0f,
                        ["uniformEmissionBlue"] = 0f, ["lightInfluence"] = 0f, ["density"] = .7f,
                        ["emissionInputMode"] = "Texture", ["emissionTexture"] = Tex(5),
                        ["receivedLightInputMode"] = "Texture", ["receivedLightTexture"] = Tex(7)
                    }
                },
                ["genshinMetallic"] = null
            })
        };
        var decoded8 = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacy8.ToJsonString()));
        Assert.False(decoded8.Materials[0].PluginBindings[0].BoolValues!.ContainsKey("hasBaseImage"));
    }

    [Fact]
    public void ViewAndProfileRejectInvalidModesNumbersEncodingAndNonImageSelectors()
    {
        var spec = ViewSpec();
        var basis = spec.Palette!.BaseImage!;
        foreach (var bad in new[]
        {
            basis with { BaseInputMode = "InvalidMode" }, basis with { DetailInputMode = "InvalidMode" },
            basis with { ShadowInputMode = "InvalidMode" }, basis with { BaseEncoding = "InvalidEnc" },
            basis with { BaseSlot = null }, basis with { DetailSlot = -1 }, basis with { ShadowSlot = null },
            basis with { BaseInputMode = T8dTestHelpers.ModeUniform }, basis with { DetailInputMode = T8dTestHelpers.ModeUniform },
            basis with { ShadowInputMode = T8dTestHelpers.ModeUniform }, basis with { ModelColor = new(float.NaN) },
            basis with { Contrast = 4.01f }, basis with { Saturation = 16.01f }, basis with { DetailBlue = -16.01f },
            basis with { ShadowFactor = 32.01f }, basis with { ShadowStrength = float.PositiveInfinity }, basis with { WarmTint = -32.01f }
        })
        {
            var binding = TestStylePlugins.Bind(Guid.NewGuid(), 0, (spec with { Palette = spec.Palette with { BaseImage = bad } }).ToBinding());
            if (!float.IsFinite(bad.ModelColor.X) || !float.IsFinite(bad.ShadowStrength))
                Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Detach([binding]));
            else
                Assert.False(T8dTestHelpers.ValidateEndfield(binding.Binding, StyleMaterialRole.Body, 16).IsValid);
        }

        var uniform = basis with
        {
            BaseInputMode = T8dTestHelpers.ModeUniform, BaseSlot = null,
            BaseEncoding = T8dTestHelpers.EncodingLinearRgb,
            DetailInputMode = T8dTestHelpers.ModeUniform, DetailSlot = null,
            ShadowInputMode = T8dTestHelpers.ModeUniform, ShadowSlot = null
        };
        Assert.True(T8dTestHelpers.ValidateEndfield((spec with { Palette = spec.Palette with { BaseImage = uniform } }).ToBinding(), StyleMaterialRole.Body, 16).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield((spec with { Palette = spec.Palette with { BaseImage = uniform with { BaseEncoding = T8dTestHelpers.EncodingSrgbRgb } } }).ToBinding(), StyleMaterialRole.Body, 16).IsValid);

        var source = Source();
        var row = Profile(source).Materials[0];
        foreach (int slot in new[] { 1, 4, 8 })
        {
            var guards = row.TextureGuards.Select(g => g.SourceSceneTextureOrdinal == slot
                ? g with { Kind = SceneTextureKind.IndexedProceduralToon } : g).ToArray();
            var badProfile = new D3D12MaterialBindingProfile("bad-kind", "Bad Kind", Profile(source).LayoutFingerprint,
                [row with { TextureGuards = guards }]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                badProfile, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PortableSchemaFourteenRetainsFullTextureTableAfterMoveSourceDeletionAndDefaultResave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "endfield-base-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "source.pmx");
            File.WriteAllBytes(originalPath, PmxWriter.ToBytes(PortableModel()));
            string basePath = Path.Combine(directory, "base-rgb.bmp");
            string detailPath = Path.Combine(directory, "detail-b.bmp");
            string shadowPath = Path.Combine(directory, "shadow-r.bmp");
            WriteBmp(basePath, 140, 190, 160); WriteBmp(detailPath, 2, 3, 160); WriteBmp(shadowPath, 110, 9, 10);
            var images = Source().Surface.Snapshot.Textures.ToArray();
            images[1] = Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "base-rgb.bmp"));
            images[4] = Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "detail-b.bmp"));
            images[8] = Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "shadow-r.bmp"));
            var model = PmxReader.Read(originalPath); var original = Rest(model); var snapshot = original.Surface.Snapshot;
            int offset = snapshot.Textures.Count;
            var extended = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, snapshot.Materials,
                [..snapshot.Textures, ..images], snapshot.World, snapshot.Camera,
                snapshot.LightDirection, snapshot.AdditionalUvs, snapshot.RawRadialSeeds);
            var source = new OriginalFrameScene(new(extended, original.Surface.Materials, original.Surface.Profile),
                original.Outline, original.OutlineOptions, original.Options, original.Materials, original.Lighting);
            var editor = new SceneCompositionEditor(); var item = editor.Add("portable MatCap", originalPath, ModelRole.Character, source);
            using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
            project.SetDefaultRenderingStyle(new("endfield"), Token);

            var baseSpec = ViewSpec(offset);
            var activeSpec = baseSpec with
            {
                Palette = baseSpec.Palette! with
                {
                    EmissionStrength = .45f, LightInfluence = .35f, Density = .013f,
                    BaseImage = baseSpec.Palette.BaseImage! with { ShadowStrength = .66f }
                }
            };
            var layout = D3D12MaterialBindingLayout.Capture(source);
            var profile = new D3D12MaterialBindingProfile("endfield-palette", "Explicit palette", layout.Fingerprint,
                [new(0, D3D12CharacterMaterialRole.Body,
                    pluginBindings: [activeSpec.ToBinding()],
                    textureGuards:
                    [
                        layout.CaptureTexture(offset),
                        layout.CaptureTexture(offset + 2),
                        layout.CaptureTexture(offset + 3),
                        layout.CaptureTexture(offset + 5),
                        layout.CaptureTexture(offset + 7),
                        layout.CaptureTexture(offset + 1),
                        layout.CaptureTexture(offset + 4),
                        layout.CaptureTexture(offset + 8),
                    ])]);
            var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, item.Id, "endfield", TestStylePlugins.AllViewCatalog);
            byte[] expected = await Capture(project, mapping);
            byte[] old = await Capture(project, mapping, basis: false);
            AssertVisibleChange(old, expected);

            string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project,
                [new(item.Id, profile, source)]), Token);
            string movedDirectory = Path.Combine(directory, "moved"); Directory.CreateDirectory(movedDirectory);
            string moved = Path.Combine(movedDirectory, "moved.pma"); File.Move(first, moved); File.Delete(originalPath);
            File.Delete(basePath); File.Delete(detailPath); File.Delete(shadowPath);
            string resaved = Path.Combine(movedDirectory, "resaved.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load, new NoPhysics(),
                out var profiles, cancellationToken: Token))
            {
                var restored = loaded.Session.Objects.Single().Source;
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(profiles[item.Id]));
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "endfield", TestStylePlugins.AllViewCatalog);
                var selection = Assert.Single(mapped.PluginBindings);
                var slots = selection.Binding.TextureSlots!;
                Assert.Equal(offset, slots["normal"]);
                Assert.Equal(offset + 2, slots["p"]);
                Assert.Equal(offset + 3, slots["matcap"]);
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(mapped.PluginBindings, TestStylePlugins.AllViewCatalog));
                Assert.Equal(extended.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < extended.Textures.Count; i++)
                    Assert.Equal(extended.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                Assert.Equal(offset + 5, slots["paletteEmission"]);
                Assert.Equal(offset + 7, slots["paletteReceivedLight"]);
                Assert.Equal(offset + 1, slots["baseImage"]);
                Assert.Equal(offset + 4, slots["baseImageDetail"]);
                Assert.Equal(offset + 8, slots["baseImageShadow"]);
                var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
                    capture.RenderingStyles, mapped.CharacterMaterials, mapped.PluginBindings,
                    sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
                var draw = Assert.Single(result.SurfaceStyles.CharacterMaterials).PluginDraw!;
                var texBySlot = draw.TextureBindings!.ToDictionary(static t => t.SlotIndex, static t => t.SceneTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 5], texBySlot[4]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 7], texBySlot[5]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 1], texBySlot[6]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 4], texBySlot[7]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 8], texBySlot[8]);
                Assert.Equal(expected, await Capture(loaded.Session, mapped));
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, again[item.Id].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(again[item.Id]));
            Assert.DoesNotContain(reopened.ResourcePaths, p => p.Contains("tangent", StringComparison.OrdinalIgnoreCase));
            var reopenedObject = Assert.Single(reopened.Session.Objects);
            var reopenedMapping = D3D12MaterialBindingProfileResolver.Resolve(again[item.Id], reopenedObject.Source, item.Id, "endfield", TestStylePlugins.AllViewCatalog);
            Assert.Equal(expected, await Capture(reopened.Session, reopenedMapping));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<byte[]> Capture(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile mapping, bool basis = true)
    {
        var bindings = basis
            ? mapping.PluginBindings
            : mapping.PluginBindings.Select(v =>
            {
                int offset = v.Binding.TextureSlots!["normal"];
                var noBasis = ViewSpec(offset) with
                {
                    Palette = ViewSpec(offset).Palette! with { EmissionStrength = .45f, LightInfluence = .35f, Density = .013f, BaseImage = null }
                };
                return v with { Binding = noBasis.ToBinding() };
            });
        await using var renderer = new D3D12ViewRenderer(new(W, H),
            stylePresets: TestStylePlugins.AllPresetCatalog,
            characterMaterials: mapping.CharacterMaterials,
            pluginBindings: bindings,
            styleCatalog: TestStylePlugins.AllViewCatalog);
        Assert.False(renderer.IsWarp);
        var snapshot = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        var modern = new OriginalFrameScene(snapshot.Scene.Surface, snapshot.Scene.Outline, snapshot.Scene.OutlineOptions,
            ModernOptions, snapshot.Scene.Materials, snapshot.Scene.Lighting);
        var plan = new OfflineFramePlan(W, H, new(30), 0, 0);
        var frame = new OfflineFrame(plan.GetSample(0), W, H, modern, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = Camera };
        using var image = await renderer.CaptureAsync(frame, Token);
        return image.Pixels.ToArray();
    }

    private static void AssertVisibleChange(byte[] before, byte[] after)
    {
        int changed = 0;
        for (int i = 0; i < before.Length; i += 4)
        {
            Assert.Equal(before[i + 3], after[i + 3]);
            if (!before.AsSpan(i, 3).SequenceEqual(after.AsSpan(i, 3))) changed++;
        }
        Assert.True(changed > 100, $"Only {changed} pixels consumed active portable palette data.");
    }

    private static PmxModel PortableModel()
    {
        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        Vector3[] positions = [new(-.8f, -.8f, 0), new(.8f, -.8f, 0), new(.8f, .8f, 0), new(-.8f, .8f, 0)];
        return new()
        {
            Header = template.Header, Name = "synthetic portable palette", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = positions.Select((p, index) => new PmxVertex
            {
                Position = p, Normal = -Vector3.UnitZ, Uv = new(index is 1 or 2 ? 1 : 0, index >= 2 ? 1 : 0),
                AdditionalUvs = new Vector4[template.Header.AdditionalUvCount], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 0
            }).ToArray(), Faces = [new(0, 2, 1), new(0, 3, 2)], Textures = [], Materials = [new()
            {
                Name = "explicit receiver", EnglishName = "", Diffuse = new(.2f, .18f, .16f, 1), Specular = Vector3.Zero,
                SpecularPower = 4, Ambient = new(.02f), EdgeColor = Vector4.Zero, EdgeSize = 0,
                DrawMode = PmxDrawModeFlags.BothFaces, TextureIndex = -1, SphereTextureIndex = -1,
                SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 6
            }], Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }

    private static void WriteBmp(string path, byte r, byte g, byte b)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4d42); writer.Write(58); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(1); writer.Write(1); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(b); writer.Write(g); writer.Write(r); writer.Write((byte)0);
    }

    private static JsonArray Vector(Vector3 value) => new(value.X, value.Y, value.Z);
    private static JsonObject Texture(D3D12MaterialBindingProfileTexture image) => new()
    {
        ["sourceSceneTextureOrdinal"] = image.SourceSceneTextureOrdinal, ["width"] = image.Width,
        ["height"] = image.Height, ["kind"] = image.Kind.ToString(), ["rgbaSha256"] = image.RgbaSha256
    };
    private static EndfieldMatcapSpec ViewSpec(int offset = 0) => new(
        offset, offset + 2, offset + 3, T8dTestHelpers.EncodingSrgbRgb, 0,
        new(.2f, .3f, .4f), new(.1f, .4f, .2f), CoupleEndfieldPalette: true,
        Palette: new(new(.2f, -.3f, 1.1f), Density: .7f,
            EmissionInputMode: T8dTestHelpers.ModeTexture, EmissionSlot: offset + 5,
            ReceivedLightInputMode: T8dTestHelpers.ModeTexture, ReceivedLightSlot: offset + 7,
            BaseImage: new(new(.4f), new(.5f), new(.7f, .8f, .9f), new(.9f), WarmTint: .4f, Contrast: .1f,
                Saturation: 1.2f, ShadowStrength: 0f, BaseInputMode: T8dTestHelpers.ModeTexture, BaseSlot: offset + 1,
                BaseEncoding: T8dTestHelpers.EncodingSrgbRgb, DetailInputMode: T8dTestHelpers.ModeTexture,
                DetailSlot: offset + 4, ShadowInputMode: T8dTestHelpers.ModeTexture, ShadowSlot: offset + 8)));
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        return new("endfield-palette", "Explicit palette", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Body,
            pluginBindings: [ViewSpec(offset).ToBinding()],
            textureGuards:
            [
                layout.CaptureTexture(offset),
                layout.CaptureTexture(offset + 2),
                layout.CaptureTexture(offset + 3),
                layout.CaptureTexture(offset + 5),
                layout.CaptureTexture(offset + 7),
                layout.CaptureTexture(offset + 1),
                layout.CaptureTexture(offset + 4),
                layout.CaptureTexture(offset + 8),
            ])]);
    }
    private static OriginalFrameScene Source(SceneTexture[]? textures = null)
    {
        var frame = D3D12EndfieldHairBindingTests.Frame(seeds: false, textures: textures ??
            [new("N", 1, 1, [128, 140, 255, 255]), new("base RGB", 1, 1, [140, 190, 160, 4]),
                new("P", 1, 1, [70, 0, 0, 80]), new("Matcap", 1, 1, [12, 200, 130, 170]),
                new("detail B", 1, 1, [2, 3, 160, 5]), new("numerical emission blue", 1, 1, [1, 2, 210, 6]),
                new("unused three", 1, 1, [3, 4, 5, 6]), new("linear received RGB", 1, 1, [40, 80, 150, 7]),
                new("shadow qR", 1, 1, [110, 9, 10, 11])]);
        var outline = new OriginalOutlineData(frame.Surface.Snapshot.Vertices.ToArray().Select(v =>
            new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 0, 0)), [new OriginalOutlineMaterial(0, Vector4.Zero, false)]);
        return new(frame.Surface, outline, new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 0), frame.Options, frame.Materials);
    }
    private static OriginalFrameScene Modern(OriginalFrameScene capture, OriginalFrameScene source)
        => new(capture.Surface, capture.Outline, capture.OutlineOptions, source.Options, capture.Materials, capture.Lighting);
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1),
        options: ModernOptions, materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
}
