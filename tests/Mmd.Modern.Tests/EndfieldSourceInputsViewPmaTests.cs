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
public sealed class EndfieldSourceInputsViewPmaTests
{
    private const int W = 97, H = 97;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);
    private static readonly OriginalFrameOptions ModernOptions = new(OriginalAlphaBlendMode.Normal, true, false,
        BackgroundColor: Vector4.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void StrictSchemaFourteenRoundtripKeepsEachOptionalProviderAndLegacySchemaTenDecodes(bool facing, bool directional)
    {
        var source = Source();
        var spec = ViewSpec();
        var basis = spec.Palette!.BaseImage!;
        var selected = basis with
        {
            FacingBase = facing ? basis.FacingBase : null,
            DirectionalShadow = directional ? basis.DirectionalShadow : null
        };
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var profile = ProfileWithBasis(layout, selected);
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profile.SchemaVersion);
        var decoded = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(decoded));

        var binding = decoded.Materials[0].PluginBindings[0];
        Assert.Equal(facing, binding.BoolValues?.GetValueOrDefault("hasFacingBase", false) ?? false);
        Assert.Equal(directional, binding.BoolValues?.GetValueOrDefault("hasBaseDirectionalShadow", false) ?? false);

        // Legacy schema 10 JSON requires facingBase & directionalShadow fields strictly.
        var legacyDoc = LegacySchemaTenDoc(layout, facing, directional);
        var legacyDecoded = D3D12MaterialBindingProfileJson.Decode(Encode(legacyDoc));
        Assert.Equal(facing, legacyDecoded.Materials[0].PluginBindings[0].BoolValues?.GetValueOrDefault("hasFacingBase", false) ?? false);
        Assert.Equal(directional, legacyDecoded.Materials[0].PluginBindings[0].BoolValues?.GetValueOrDefault("hasBaseDirectionalShadow", false) ?? false);

        foreach (string field in new[] { "facingBase", "directionalShadow" })
        {
            var broken = JsonNode.Parse(legacyDoc.ToJsonString())!;
            Basis(broken).Remove(field);
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encode(broken)));
        }
        var old = JsonNode.Parse(legacyDoc.ToJsonString())!; old["schemaVersion"] = 9;
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encode(old)));
    }

    [Fact]
    public void SchemaNineWireDecodesWithoutProvidersAndMixedFourteenRoundtrips()
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var spec = ViewSpec();
        var withoutProviders = spec.Palette!.BaseImage! with { FacingBase = null, DirectionalShadow = null };
        var legacyNineDoc = LegacySchemaTenDoc(layout, facing: false, directional: false);
        legacyNineDoc["schemaVersion"] = 9;
        var legacyNineBasis = Basis(legacyNineDoc);
        legacyNineBasis.Remove("facingBase");
        legacyNineBasis.Remove("directionalShadow");
        var decodedNine = D3D12MaterialBindingProfileJson.Decode(Encode(legacyNineDoc));
        Assert.False(decodedNine.Materials[0].PluginBindings[0].BoolValues!.ContainsKey("hasFacingBase"));
        Assert.False(decodedNine.Materials[0].PluginBindings[0].BoolValues!.ContainsKey("hasBaseDirectionalShadow"));

        var mixed = new D3D12MaterialBindingProfile("endfield-palette", "Mixed providers", layout.Fingerprint,
            [
                ProfileWithBasis(layout, spec.Palette.BaseImage!).Materials[0],
                ProfileWithBasis(layout, withoutProviders, localMaterialIndex: 1).Materials[0],
            ]);
        byte[] mixedBytes = D3D12MaterialBindingProfileJson.Encode(mixed);
        var restored = D3D12MaterialBindingProfileJson.Decode(mixedBytes);
        Assert.True(restored.Materials[0].PluginBindings[0].BoolValues!["hasFacingBase"]);
        Assert.True(restored.Materials[0].PluginBindings[0].BoolValues!["hasBaseDirectionalShadow"]);
        Assert.False(restored.Materials[1].PluginBindings[0].BoolValues!.ContainsKey("hasFacingBase"));
        Assert.Equal(mixedBytes, D3D12MaterialBindingProfileJson.Encode(restored));
    }

    [Theory]
    [InlineData("unknown")] [InlineData("missing-blend")] [InlineData("missing-direction")]
    [InlineData("blend-range")] [InlineData("width-range")] [InlineData("normal-range")]
    [InlineData("zero-direction")] [InlineData("nonnormalized-direction")] [InlineData("shadow-texture-conflict")]
    public void InvalidProviderPayloadCannotBypassGuards(string damage)
    {
        var source = Source();
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var spec = ViewSpec();
        var basis = spec.Palette!.BaseImage!;

        // 1. Legacy schema 10 strictness.
        var document = LegacySchemaTenDoc(layout, facing: true, directional: true);
        var bNode = Basis(document); var facing = bNode["facingBase"]!.AsObject(); var directional = bNode["directionalShadow"]!.AsObject();
        switch (damage)
        {
            case "unknown": facing["guessBone"] = "head"; break;
            case "missing-blend": facing.Remove("blend"); break;
            case "missing-direction": directional.Remove("worldDirection"); break;
            case "blend-range": facing["blend"] = 1.1f; break;
            case "width-range": directional["width"] = 4.1f; break;
            case "normal-range": directional["normalStrength"] = -1; break;
            case "zero-direction": directional["worldDirection"] = new JsonArray(0, 0, 0); break;
            case "nonnormalized-direction": directional["worldDirection"] = new JsonArray(0, 0, 2); break;
            case "shadow-texture-conflict":
                bNode["shadowInputMode"] = "Texture";
                bNode["shadowTexture"] = Texture(layout.CaptureTexture(8)); break;
        }
        if (damage is "nonnormalized-direction" or "shadow-texture-conflict" or "width-range")
        {
            var decodedLegacy = D3D12MaterialBindingProfileJson.Decode(Encode(document));
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(
                decodedLegacy, source, Guid.NewGuid(), "endfield", TestStylePlugins.AllViewCatalog));
        }
        else
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encode(document)));

        // 2. Direct plugin validation mirrors all bounds/semantic checks.
        if (damage is "blend-range" or "width-range" or "normal-range" or "zero-direction" or "nonnormalized-direction" or "shadow-texture-conflict")
        {
            EndfieldBaseImageSpec badBasis = damage switch
            {
                "blend-range" => basis with { FacingBase = new(1.1f) },
                "width-range" => basis with { DirectionalShadow = basis.DirectionalShadow! with { Width = 4.1f } },
                "normal-range" => basis with { DirectionalShadow = basis.DirectionalShadow! with { NormalStrength = -1f } },
                "zero-direction" => basis with { DirectionalShadow = basis.DirectionalShadow! with { WorldDirection = Vector3.Zero } },
                "nonnormalized-direction" => basis with { DirectionalShadow = basis.DirectionalShadow! with { WorldDirection = new(0, 0, 2) } },
                _ => basis with { ShadowInputMode = T8dTestHelpers.ModeTexture, ShadowSlot = 8 }
            };
            var badBinding = (spec with { Palette = spec.Palette with { BaseImage = badBasis } }).ToBinding();
            Assert.False(T8dTestHelpers.ValidateEndfield(badBinding, StyleMaterialRole.Body, source.Surface.Snapshot.Textures.Count).IsValid);
        }
    }

    [Theory]
    [InlineData("mmd")] [InlineData("genshin")] [InlineData("star-rail")] [InlineData("wuthering-waves")]
    public void DormantProviderMappingStillGuardsItsSelectedSourceImages(string family)
    {
        var source = Source(); var profile = Profile(source); var id = Guid.NewGuid();
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, family, TestStylePlugins.AllViewCatalog).PluginBindings);
        foreach (int selected in new[] { 0, 1, 4 })
        {
            var images = source.Surface.Snapshot.Textures.ToArray(); images[selected] = new("modified", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(images), id, family, TestStylePlugins.AllViewCatalog));
        }
        var basis = ViewSpec().Palette!.BaseImage!;
        Assert.ThrowsAny<ArgumentException>(() => D3D12StylePluginAdapter.DetachBindingData(
            (ViewSpec() with { Palette = ViewSpec().Palette! with { BaseImage = basis with { FacingBase = new(float.NaN) } } }).ToBinding()));
        Assert.False(T8dTestHelpers.ValidateEndfield(
            (ViewSpec() with { Palette = ViewSpec().Palette! with { BaseImage = basis with { DirectionalShadow = new(Vector3.Zero) } } }).ToBinding(),
            StyleMaterialRole.Body, 16).IsValid);
    }

    [Fact]
    public void ViewRebasesOnlyImagesAndPreservesWorldDirectionAndFacing()
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        editor.Add("preceding", Path.Combine(Path.GetTempPath(), "providers-pre.pmx"), ModelRole.Character, source);
        var item = editor.Add("selected", Path.Combine(Path.GetTempPath(), "providers-pmx.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        var viewBinding = TestStylePlugins.Bind(item.Id, 0, ViewSpec().ToBinding());
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests([viewBinding], TestStylePlugins.AllViewCatalog));
        var result = D3D12ViewRenderingStyles.Resolve(Modern(snapshot.Scene, source), TestStylePlugins.AllPresetCatalog,
            snapshot.RenderingStyles, pluginBindings: [viewBinding], sceneInputs: snapshot.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
        var obj = Assert.Single(snapshot.RenderingStyles, v => v.ObjectId == item.Id);
        var draw = result.SurfaceStyles.CharacterMaterials[obj.MaterialStart].PluginDraw!;
        Assert.Equal("matcap-palette-base", draw.ShaderModuleId);
        var cb = MemoryMarshal.Cast<byte, Vector4>(draw.ConstantBufferData.Span);
        Assert.Equal(new Vector4(Vector3.Normalize(new(1, 0, 1)), .3f), cb[16]);
        Assert.Equal(new Vector4(1f, .7f, 1f, 2f), cb[17]);

        var texBySlot = draw.TextureBindings!.ToDictionary(static t => t.SlotIndex, static t => t.SceneTextureIndex);
        Assert.Equal(snapshot.SceneInputs[item.Id].TextureIndices[1], texBySlot[6]);
        Assert.Equal(snapshot.SceneInputs[item.Id].TextureIndices[4], texBySlot[7]);
        Assert.False(texBySlot.ContainsKey(8));
    }

    private static JsonObject Basis(JsonNode node) => node["materials"]![0]!["endfieldMatcap"]!["palette"]!["baseImage"]!.AsObject();
    private static byte[] Encode(JsonNode document) => Encoding.UTF8.GetBytes(document.ToJsonString());

    private static D3D12MaterialBindingProfile ProfileWithBasis(
        D3D12MaterialBindingLayout layout,
        EndfieldBaseImageSpec basis,
        int offset = 0,
        int localMaterialIndex = 0)
    {
        var spec = ViewSpec(offset);
        spec = spec with { Palette = spec.Palette! with { BaseImage = basis } };
        var guards = new List<D3D12MaterialBindingProfileTexture>
        {
            layout.CaptureTexture(offset),
            layout.CaptureTexture(offset + 2),
            layout.CaptureTexture(offset + 3),
            layout.CaptureTexture(offset + 5),
            layout.CaptureTexture(offset + 7),
            layout.CaptureTexture(offset + 1),
            layout.CaptureTexture(offset + 4),
        };
        if (basis.ShadowInputMode == T8dTestHelpers.ModeTexture && basis.ShadowSlot is { } sSlot)
            guards.Add(layout.CaptureTexture(sSlot));
        return new("endfield-palette", "Explicit palette", layout.Fingerprint,
            [new(localMaterialIndex, D3D12CharacterMaterialRole.Body, pluginBindings: [spec.ToBinding()], textureGuards: guards)]);
    }

    private static JsonObject LegacySchemaTenDoc(D3D12MaterialBindingLayout layout, bool facing, bool directional)
    {
        JsonObject Tex(int ordinal) => Texture(layout.CaptureTexture(ordinal));
        return new()
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 10,
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
                            ["shadowInputMode"] = "Uniform", ["shadowTexture"] = null,
                            ["facingBase"] = facing ? new JsonObject { ["blend"] = .7f } : null,
                            ["directionalShadow"] = directional ? new JsonObject
                            {
                                ["worldDirection"] = Vector(Vector3.Normalize(new(1, 0, 1))),
                                ["width"] = .3f,
                                ["normalStrength"] = 2f
                            } : null
                        }
                    }
                },
                ["genshinMetallic"] = null
            })
        };
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PortableSourceProvidersSurviveMoveDeletionAndDefaultResaveWithExactActivePixels()
    {
        string directory = Path.Combine(Path.GetTempPath(), "endfield-providers-portable-" + Guid.NewGuid().ToString("N"));
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
                Assert.False(slots.ContainsKey("baseImageShadow"));
                var nums = selection.Binding.NumberValues!;
                var vecs = selection.Binding.VectorValues!;
                Assert.Equal(.7, nums["facingBaseBlend"], 5);
                Assert.Equal(Vector3.Normalize(new(1, 0, 1)), new Vector3(vecs["baseDirectionalShadowWorldDirection"][0], vecs["baseDirectionalShadowWorldDirection"][1], vecs["baseDirectionalShadowWorldDirection"][2]));
                Assert.Equal(.3, nums["baseDirectionalShadowWidth"], 5);
                Assert.Equal(2.0, nums["baseDirectionalShadowNormalStrength"], 5);

                var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), TestStylePlugins.AllPresetCatalog,
                    capture.RenderingStyles, mapped.CharacterMaterials, mapped.PluginBindings,
                    sceneInputs: capture.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
                var draw = Assert.Single(result.SurfaceStyles.CharacterMaterials).PluginDraw!;
                var texBySlot = draw.TextureBindings!.ToDictionary(static t => t.SlotIndex, static t => t.SceneTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 5], texBySlot[4]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 7], texBySlot[5]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 1], texBySlot[6]);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 4], texBySlot[7]);
                Assert.False(texBySlot.ContainsKey(8));
                var cb = MemoryMarshal.Cast<byte, Vector4>(draw.ConstantBufferData.Span);
                Assert.Equal(new Vector4(Vector3.Normalize(new(1, 0, 1)), .3f), cb[16]);
                Assert.Equal(new Vector4(1f, .7f, 1f, 2f), cb[17]);
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
                var noProviders = ViewSpec(offset) with
                {
                    Palette = ViewSpec(offset).Palette! with
                    {
                        EmissionStrength = .45f, LightInfluence = .35f, Density = .013f,
                        BaseImage = ViewSpec(offset).Palette!.BaseImage! with { ShadowStrength = .66f, FacingBase = null, DirectionalShadow = null }
                    }
                };
                return v with { Binding = noProviders.ToBinding() };
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
                DetailSlot: offset + 4,
                FacingBase: new(.7f),
                DirectionalShadow: new(Vector3.Normalize(new(1, 0, 1)), .3f, 2))));
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0)
        => ProfileWithBasis(D3D12MaterialBindingLayout.Capture(source), ViewSpec(offset).Palette!.BaseImage!, offset);
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
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
