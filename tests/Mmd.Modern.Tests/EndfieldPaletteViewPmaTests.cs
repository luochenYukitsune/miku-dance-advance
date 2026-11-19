using System.Numerics;
using System.Runtime.InteropServices;
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
public sealed class EndfieldPaletteViewPmaTests
{
    private const int W = 97, H = 97;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);
    private static readonly OriginalFrameOptions ModernOptions = new(OriginalAlphaBlendMode.Normal, true, false,
        BackgroundColor: Vector4.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RequestsUnionBothLocalPaletteImagesAndSourceGeometryAtZeroConsumption()
    {
        var id = Guid.NewGuid();
        var binding = TestStylePlugins.Bind(id, 0, ViewSpec().ToBinding());
        D3D12ViewPluginMaterialBinding[] input = [binding];
        var detached = D3D12ViewPluginMaterialBindings.Detach(input);
        input[0] = input[0] with { LocalMaterialIndex = 99 };
        Assert.Equal(0, Assert.Single(detached).LocalMaterialIndex);
        Assert.Same(detached, D3D12ViewPluginMaterialBindings.Detach(detached));

        var baseReq = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests(detached, TestStylePlugins.AllViewCatalog));
        Assert.Equal(new[] { 0, 2, 3, 5, 7 }, baseReq.OptionalLocalTextureIndices.Order());
        var merged = new SceneObjectRenderRequest(id, [4], [6], [..baseReq.OptionalLocalTextureIndices, 9], includeSourceGeometry: true);
        Assert.True(merged.IncludeSourceGeometry);
        Assert.Equal(new[] { 4 }, merged.BoneIndices);
        Assert.Equal(new[] { 6 }, merged.LocalTextureIndices);
        Assert.Equal(new[] { 0, 2, 3, 5, 7, 9 }, merged.OptionalLocalTextureIndices.Order());
    }

    [Fact]
    public void TwoObjectsRebaseNoncontiguousLocalPaletteImagesFromTheSameGeometryCapture()
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        var a = editor.Add("first", Path.Combine(Path.GetTempPath(), "palette-a.pmx"), ModelRole.Character, source);
        var secondImages = source.Surface.Snapshot.Textures.ToArray();
        secondImages[5] = new("second emission blue", 1, 1, [1, 2, 110, 6]);
        secondImages[7] = new("second received RGB", 1, 1, [140, 80, 50, 7]);
        var b = editor.Add("second", Path.Combine(Path.GetTempPath(), "palette-b.pmx"), ModelRole.Character, Source(secondImages));
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        D3D12ViewPluginMaterialBinding[] values =
        [
            TestStylePlugins.Bind(a.Id, 0, ViewSpec().ToBinding()),
            TestStylePlugins.Bind(b.Id, 0, ViewSpec().ToBinding()),
        ];
        var capture = project.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(values, TestStylePlugins.AllViewCatalog));
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
            capture.RenderingStyles, pluginBindings: values, sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
        foreach (var obj in capture.RenderingStyles)
        {
            var draw = result.SurfaceStyles.CharacterMaterials[obj.MaterialStart].PluginDraw;
            Assert.NotNull(draw);
            Assert.Equal("matcap-palette", draw.ShaderModuleId);
            var texBySlot = draw.TextureBindings!.ToDictionary(static t => t.SlotIndex, static t => t.SceneTextureIndex);
            var images = capture.SceneInputs[obj.ObjectId].TextureIndices;
            Assert.Equal(images[0], texBySlot[1]);
            Assert.Equal(images[2], texBySlot[2]);
            Assert.Equal(images[3], texBySlot[3]);
            Assert.Contains(draw.BufferBindings!, static buf => buf.Kind == D3D12PluginBufferKind.MikkTangentCorners && buf.DataKey == "uv0");
        }
        // Identical parent images are deduplicated, but object-specific palette images retain separate slots.
        Assert.Equal(capture.SceneInputs[a.Id].TextureIndices[0], capture.SceneInputs[b.Id].TextureIndices[0]);
        Assert.Equal(capture.SceneInputs[a.Id].TextureIndices[3], capture.SceneInputs[b.Id].TextureIndices[3]);
        Assert.NotEqual(capture.SceneInputs[a.Id].TextureIndices[5], capture.SceneInputs[b.Id].TextureIndices[5]);
        Assert.NotEqual(capture.SceneInputs[a.Id].TextureIndices[7], capture.SceneInputs[b.Id].TextureIndices[7]);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(7, false)]
    [InlineData(5, true)]
    [InlineData(7, true)]
    public void SelectedNumericalImagesAreRequiredAtZeroStrengthAndInfluence(int selected, bool procedural)
    {
        var baseSource = Source();
        var images = baseSource.Surface.Snapshot.Textures.ToArray();
        if (procedural) images[selected] = SceneTexture.ProceduralToon();
        var source = Source(images); var editor = new SceneCompositionEditor();
        var item = editor.Add("required", Path.Combine(Path.GetTempPath(), "palette-required.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        var binding = TestStylePlugins.Bind(item.Id, 0, ViewSpec().ToBinding());
        if (!procedural)
        {
            var request = Assert.Single(D3D12ViewPluginMaterialBindings.CreateRequests([binding], TestStylePlugins.AllViewCatalog));
            request = new(item.Id, optionalLocalTextureIndices: request.OptionalLocalTextureIndices.Where(v => v != selected));
            var capture = project.CaptureRenderSnapshot([request]);
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
                capture.RenderingStyles, pluginBindings: [binding], sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog));
        }
        else
        {
            var profile = Profile(baseSource);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                profile, source, item.Id, "endfield", TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact]
    public void ViewAndProfileRejectMissingModeImagesInvalidEnumsNumbersAndUncoupledOwnership()
    {
        var spec = ViewSpec();
        var palette = spec.Palette!;
        foreach (var bad in new[]
        {
            spec with { Palette = palette with { EmissionInputMode = "InvalidMode" } },
            spec with { Palette = palette with { ReceivedLightInputMode = "InvalidMode" } },
            spec with { Palette = palette with { EmissionSlot = null } },
            spec with { Palette = palette with { ReceivedLightSlot = null } },
            spec with { Palette = palette with { EmissionSlot = -1 } },
            spec with { Palette = palette with { ReceivedLightSlot = -1 } },
            spec with { Palette = palette with { EmissionInputMode = T8dTestHelpers.ModeUniform } },
            spec with { Palette = palette with { ReceivedLightInputMode = T8dTestHelpers.ModeUniform } },
            spec with { Palette = palette with { ReceivedLightLinearRgb = new(float.NaN) } },
            spec with { Palette = palette with { ReceivedLightLinearRgb = new(16.01f) } },
            spec with { Palette = palette with { UniformEmissionBlue = -16.01f } },
            spec with { Palette = palette with { Density = 32.01f } },
            spec with { Palette = palette with { EmissionStrength = float.PositiveInfinity } },
            spec with { Palette = palette with { LightInfluence = -32.01f } },
        })
        {
            var binding = TestStylePlugins.Bind(Guid.NewGuid(), 0, bad.ToBinding());
            if (!float.IsFinite(bad.Palette!.ReceivedLightLinearRgb.X) || !float.IsFinite(bad.Palette.EmissionStrength))
                Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Detach([binding]));
            else
                Assert.False(T8dTestHelpers.ValidateEndfield(binding.Binding, StyleMaterialRole.Body, 16).IsValid);
        }

        Assert.False(T8dTestHelpers.ValidateEndfield([spec.ToBinding() with { StyleId = "endfield" }, T8dTestHelpers.EndfieldRim()], StyleMaterialRole.Body).IsValid);
        var source = Source();
        var row = Profile(source).Materials[0];
        var profileWithEye = new D3D12MaterialBindingProfile("bad-eye", "Bad Eye", Profile(source).LayoutFingerprint,
            [row with { Role = D3D12CharacterMaterialRole.Eye }]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
            profileWithEye, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));

        foreach (bool emission in new[] { true, false })
        {
            int slot = emission ? 5 : 7;
            var guards = row.TextureGuards.Select(g => g.SourceSceneTextureOrdinal == slot
                ? g with { Kind = SceneTextureKind.IndexedProceduralToon } : g).ToArray();
            var badGuard = new D3D12MaterialBindingProfile("bad-kind", "Bad Kind", Profile(source).LayoutFingerprint,
                [row with { TextureGuards = guards }]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                badGuard, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
        }
    }

    [Fact]
    public void SchemaFourteenAndLegacySchemaEightAreStrictAndValidateNestedFields()
    {
        var source = Source();
        var profile = Profile(source);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));

        // Legacy schema 8 JSON wire strictness.
        var layout = D3D12MaterialBindingLayout.Capture(source);
        JsonObject Tex(int ordinal) => Texture(layout.CaptureTexture(ordinal));
        var legacyDoc = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 8,
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
                    ["baseColor"] = new JsonArray(.2f, .3f, .4f), ["highlightColor"] = new JsonArray(.1f, .4f, .2f),
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
        var legacyDecoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacyDoc.ToJsonString()));
        Assert.Equal("matcap", Assert.Single(legacyDecoded.Materials[0].PluginBindings).ModeId);
        Assert.Equal(5, legacyDecoded.Materials[0].TextureGuards.Count);

        foreach (string damage in new[] { "missing-palette", "missing-mode", "missing-image", "unknown", "encoding", "mode", "numeric-enum", "old-schema", "image-null", "uniform-image", "scalar", "color" })
        {
            var doc = JsonNode.Parse(legacyDoc.ToJsonString())!; var matcap = doc["materials"]![0]!["endfieldMatcap"]!.AsObject();
            var pal = matcap["palette"]!.AsObject();
            if (damage == "missing-palette") matcap.Remove("palette");
            if (damage == "missing-mode") pal.Remove("emissionInputMode");
            if (damage == "missing-image") pal.Remove("receivedLightTexture");
            if (damage == "unknown") pal["time"] = 1;
            if (damage == "encoding") pal["receivedLightEncoding"] = "SrgbRgb";
            if (damage == "mode") pal["emissionInputMode"] = "Guess";
            if (damage == "numeric-enum") pal["receivedLightInputMode"] = "1";
            if (damage == "old-schema") doc["schemaVersion"] = 7;
            if (damage == "image-null") pal["emissionTexture"] = null;
            if (damage == "uniform-image") pal["receivedLightInputMode"] = "Uniform";
            if (damage == "scalar") pal["density"] = 33;
            if (damage == "color") pal["receivedLightLinearRgb"] = new JsonArray(0, 0, 17);
            if (damage is "uniform-image" or "scalar" or "color")
            {
                var decodedDamage = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString()));
                Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                    decodedDamage, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
            }
            else
                Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }

        var future = JsonNode.Parse(bytes)!; future["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(future.ToJsonString())));
    }

    [Fact]
    public void UniformPaletteNeedsNoImagesAndMixedRowsRoundtripCleanly()
    {
        var source = Source();
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var uniformSpec = ViewSpec() with
        {
            Palette = new(new(.2f, -.3f, 1.1f), Density: .7f,
                EmissionInputMode: T8dTestHelpers.ModeUniform, EmissionSlot: null,
                ReceivedLightInputMode: T8dTestHelpers.ModeUniform, ReceivedLightSlot: null)
        };
        var withoutPaletteSpec = ViewSpec() with { Palette = null };
        var profile = new D3D12MaterialBindingProfile("endfield-palette", "Mixed palette", layout.Fingerprint,
            [
                new(0, D3D12CharacterMaterialRole.Body, pluginBindings: [uniformSpec.ToBinding()],
                    textureGuards: [layout.CaptureTexture(0), layout.CaptureTexture(2), layout.CaptureTexture(3)]),
                new(1, D3D12CharacterMaterialRole.Body, pluginBindings: [withoutPaletteSpec.ToBinding()],
                    textureGuards: [layout.CaptureTexture(0), layout.CaptureTexture(2), layout.CaptureTexture(3)]),
            ]);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.False(restored.Materials[1].PluginBindings[0].BoolValues!.ContainsKey("hasPalette"));
        Assert.False(restored.Materials[0].PluginBindings[0].TextureSlots!.ContainsKey("paletteEmission"));
        Assert.False(restored.Materials[0].PluginBindings[0].TextureSlots!.ContainsKey("paletteReceivedLight"));
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void DormantProfilesStillGuardBothUnusedImages(string pipeline)
    {
        var source = Source(); var profile = Profile(source); var id = Guid.NewGuid();
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline, TestStylePlugins.AllViewCatalog).PluginBindings);
        foreach (int index in new[] { 5, 7 })
        {
            var images = source.Surface.Snapshot.Textures.ToArray(); images[index] = new("changed", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(images), id, pipeline, TestStylePlugins.AllViewCatalog));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacySchemaFiveAndSevenDecodeCleanlyToPluginBindings(bool metallic)
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var expectedRow = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
            ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null, ["endfieldMatcap"] = null
        };
        if (!metallic)
        {
            expectedRow["endfieldMatcap"] = new JsonObject
            {
                ["normal"] = Texture(layout.CaptureTexture(0)), ["p"] = Texture(layout.CaptureTexture(2)), ["matcap"] = Texture(layout.CaptureTexture(3)),
                ["matcapEncoding"] = "SrgbRgb", ["uvChannel"] = 0, ["baseColor"] = Vector(new(.2f, .3f, .4f)),
                ["highlightColor"] = Vector(new(.1f, .4f, .2f)), ["normalStrength"] = 2f, ["rotationRadians"] = 0f,
                ["highlightCoefficient"] = 0f, ["shadowHardness"] = .5f, ["lightControlDirection"] = null,
                ["baselineStrength"] = 1f, ["coupleEndfieldPalette"] = true, ["generatorRevision"] = "mikk-v1"
            };
        }
        else
        {
            expectedRow["genshinMetallic"] = new JsonObject
            {
                ["lightmap"] = Texture(layout.CaptureTexture(0)), ["matcap"] = Texture(layout.CaptureTexture(2)), ["matcapEncoding"] = "SrgbRgb",
                ["darkColor"] = Vector(new(.1f)), ["lightColor"] = Vector(new(.8f)), ["shadowMultiplyColor"] = Vector(new(.6f)),
                ["enableMetallics"] = 1f, ["tileScale"] = 1f, ["brightness"] = 1f, ["litMode"] = "PrimaryResponse", ["uniformLitFactor"] = 1f,
                ["specular"] = new JsonObject
                {
                    ["color"] = Vector(new(.7f)), ["sharpColor"] = Vector(new(.2f)),
                    ["shininess"] = 16f, ["scale"] = 1f, ["attenuation"] = .5f, ["sharpOffset"] = 1f, ["ramp"] = null, ["rampMix"] = 0f
                }
            };
        }
        var legacy = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = metallic ? 7 : 5,
            ["stableId"] = "legacy", ["name"] = "Legacy", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint, ["materials"] = new JsonArray(expectedRow)
        };
        var decoded = D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        var b = Assert.Single(decoded.Materials[0].PluginBindings);
        Assert.Equal(metallic ? "genshin" : "endfield", b.StyleId);
        Assert.Equal(metallic ? "metallic" : "matcap", b.ModeId);
        Assert.False(b.BoolValues?.GetValueOrDefault("hasPalette", false) ?? false);
        byte[] reencoded = D3D12MaterialBindingProfileJson.Encode(decoded);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, JsonNode.Parse(reencoded)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(reencoded, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(reencoded)));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PortableSchemaFourteenRetainsFullTextureTableAfterMoveSourceDeletionAndDefaultResave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "endfield-palette-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "source.pmx");
            File.WriteAllBytes(originalPath, PmxWriter.ToBytes(PortableModel()));
            string emissionPath = Path.Combine(directory, "emission-blue.bmp");
            string receivedPath = Path.Combine(directory, "received-rgb.bmp");
            WriteBmp(emissionPath, 1, 2, 210);
            WriteBmp(receivedPath, 40, 80, 150);
            var images = Source().Surface.Snapshot.Textures.ToArray();
            images[5] = Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "emission-blue.bmp"));
            images[7] = Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "received-rgb.bmp"));
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

            var activeSpec = ViewSpec(offset) with
            {
                Palette = ViewSpec(offset).Palette! with { EmissionStrength = .45f, LightInfluence = .35f, Density = .013f }
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
                    ])]);
            var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, item.Id, "endfield", TestStylePlugins.AllViewCatalog);
            byte[] expected = await Capture(project, mapping);
            byte[] old = await Capture(project, mapping, palette: false);
            AssertVisibleChange(old, expected);

            string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project,
                [new(item.Id, profile, source)]), Token);
            string movedDirectory = Path.Combine(directory, "moved"); Directory.CreateDirectory(movedDirectory);
            string moved = Path.Combine(movedDirectory, "moved.pma"); File.Move(first, moved); File.Delete(originalPath);
            File.Delete(emissionPath); File.Delete(receivedPath);
            string resaved = Path.Combine(movedDirectory, "resaved.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load, new NoPhysics(),
                out var profiles, cancellationToken: Token))
            {
                var restored = loaded.Session.Objects.Single().Source;
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(profiles[item.Id]));
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "endfield", TestStylePlugins.AllViewCatalog);
                var selection = Assert.Single(mapped.PluginBindings);
                Assert.Equal(offset, selection.Binding.TextureSlots!["normal"]);
                Assert.Equal(offset + 2, selection.Binding.TextureSlots!["p"]);
                Assert.Equal(offset + 3, selection.Binding.TextureSlots!["matcap"]);
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(mapped.PluginBindings, TestStylePlugins.AllViewCatalog));
                Assert.Equal(extended.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < extended.Textures.Count; i++)
                    Assert.Equal(extended.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                Assert.Equal(offset + 5, selection.Binding.TextureSlots!["paletteEmission"]);
                Assert.Equal(offset + 7, selection.Binding.TextureSlots!["paletteReceivedLight"]);
                var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
                    capture.RenderingStyles, mapped.CharacterMaterials, mapped.PluginBindings,
                    sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
                var draw = Assert.Single(result.SurfaceStyles.CharacterMaterials).PluginDraw!;
                var texBySlot = draw.TextureBindings!.ToDictionary(static t => t.SlotIndex, static t => t.SceneTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 5], texBySlot[4]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 7], texBySlot[5]);
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

    private static async Task<byte[]> Capture(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile mapping, bool palette = true)
    {
        var bindings = palette
            ? mapping.PluginBindings
            : mapping.PluginBindings.Select(v => v with
            {
                Binding = (ViewSpec(v.Binding.TextureSlots!["normal"]) with { Palette = null }).ToBinding()
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
            ReceivedLightInputMode: T8dTestHelpers.ModeTexture, ReceivedLightSlot: offset + 7));
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
            ])]);
    }
    private static OriginalFrameScene Source(SceneTexture[]? textures = null)
    {
        var frame = D3D12EndfieldHairBindingTests.Frame(seeds: false, textures: textures ??
            [new("N", 1, 1, [128, 140, 255, 255]), new("unused one", 1, 1, [1, 2, 3, 4]),
                new("P", 1, 1, [70, 0, 0, 80]), new("Matcap", 1, 1, [12, 200, 130, 170]),
                new("unused two", 1, 1, [2, 3, 4, 5]), new("numerical emission blue", 1, 1, [1, 2, 210, 6]),
                new("unused three", 1, 1, [3, 4, 5, 6]), new("linear received RGB", 1, 1, [40, 80, 150, 7]),
                new("unused last", 1, 1, [8, 9, 10, 11])]);
        var outline = new OriginalOutlineData(frame.Surface.Snapshot.Vertices.ToArray().Select(v =>
            new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 0, 0)), [new OriginalOutlineMaterial(0, Vector4.Zero, false)]);
        return new(frame.Surface, outline, new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 0), frame.Options, frame.Materials);
    }
    private static OriginalFrameScene Modern(OriginalFrameScene capture, OriginalFrameScene source)
        => new(capture.Surface, capture.Outline, capture.OutlineOptions, source.Options, capture.Materials, capture.Lighting);
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1),
        options: ModernOptions, materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
