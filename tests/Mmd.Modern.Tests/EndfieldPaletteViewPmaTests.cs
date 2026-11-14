using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
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
        var id = Guid.NewGuid(); var value = View(id);
        D3D12ViewEndfieldMatcapBinding[] input = [value];
        var detached = D3D12ViewEndfieldMatcapBindings.Detach(input);
        input[0] = value with { Palette = null };
        Assert.Equal(value, Assert.Single(detached));
        Assert.Same(detached, D3D12ViewEndfieldMatcapBindings.Detach(detached));
        var request = Assert.Single(D3D12ViewEndfieldMatcapBindings.CreateRequests(detached, [new(id, [4], [6], [9])]));
        Assert.True(request.IncludeSourceGeometry);
        Assert.Equal(new[] { 4 }, request.BoneIndices);
        Assert.Equal(new[] { 6 }, request.LocalTextureIndices);
        Assert.Equal(new[] { 0, 2, 3, 5, 7, 9 }, request.OptionalLocalTextureIndices.Order());
        Assert.Equal(request.OptionalLocalTextureIndices,
            Assert.Single(D3D12ViewGenshinHairAtlasBindings.CreateRequests([], [request])).OptionalLocalTextureIndices);
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
        D3D12ViewEndfieldMatcapBinding[] values = [View(a.Id), View(b.Id)];
        var capture = project.CaptureRenderSnapshot(D3D12ViewEndfieldMatcapBindings.CreateRequests(values));
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, endfieldMatcapDataBindings: values);
        foreach (var obj in capture.RenderingStyles)
        {
            var active = Assert.IsType<D3D12EndfieldMatcapBinding>(result.SurfaceStyles.CharacterMaterials[obj.MaterialStart].EndfieldMatcap);
            var images = capture.SceneInputs[obj.ObjectId].TextureIndices;
            Assert.Equal(images[0], active.NormalCombinedTextureIndex);
            Assert.Equal(images[2], active.PCombinedTextureIndex);
            Assert.Equal(images[3], active.MatcapCombinedTextureIndex);
            Assert.Equal(images[5], active.Palette!.EmissionCombinedTextureIndex);
            Assert.Equal(images[7], active.Palette.ReceivedLightCombinedTextureIndex);
            Assert.Equal(View(obj.ObjectId).Palette! with { EmissionCombinedTextureIndex = images[5], ReceivedLightCombinedTextureIndex = images[7] }, active.Palette);
            Assert.Same(capture.SceneInputs[obj.ObjectId].SourceGeometry, active.Geometry!.Source);
            Assert.Equal(0, active.UvChannel);
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
        var images = Source().Surface.Snapshot.Textures.ToArray();
        if (procedural) images[selected] = SceneTexture.ProceduralToon();
        var source = Source(images); var editor = new SceneCompositionEditor();
        var item = editor.Add("required", Path.Combine(Path.GetTempPath(), "palette-required.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        var value = View(item.Id);
        var request = Assert.Single(D3D12ViewEndfieldMatcapBindings.CreateRequests([value]));
        if (!procedural) request = new(item.Id, optionalLocalTextureIndices: request.OptionalLocalTextureIndices.Where(v => v != selected), includeSourceGeometry: true);
        var capture = project.CaptureRenderSnapshot([request]);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, endfieldMatcapDataBindings: [value]));
    }

    [Fact]
    public void ViewAndProfileRejectMissingModeImagesInvalidEnumsNumbersAndUncoupledOwnership()
    {
        var value = View(Guid.NewGuid()); var palette = value.Palette!;
        foreach (var invalid in new[]
        {
            palette with { EmissionInputMode = (D3D12EndfieldPaletteInputMode)99 },
            palette with { ReceivedLightInputMode = (D3D12EndfieldPaletteInputMode)99 },
            palette with { EmissionCombinedTextureIndex = null }, palette with { ReceivedLightCombinedTextureIndex = null },
            palette with { EmissionCombinedTextureIndex = -1 }, palette with { ReceivedLightCombinedTextureIndex = -1 },
            palette with { EmissionInputMode = D3D12EndfieldPaletteInputMode.Uniform },
            palette with { ReceivedLightInputMode = D3D12EndfieldPaletteInputMode.Uniform },
            palette with { ReceivedLightLinearRgb = new(float.NaN) }, palette with { ReceivedLightLinearRgb = new(16.01f) },
            palette with { UniformEmissionBlue = -16.01f }, palette with { Density = 32.01f },
            palette with { EmissionStrength = float.PositiveInfinity }, palette with { LightInfluence = -32.01f }
        }) Assert.ThrowsAny<ArgumentException>(() => D3D12ViewEndfieldMatcapBindings.Detach([value with { Palette = invalid }]));
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewEndfieldMatcapBindings.Detach([value with { CoupleEndfieldPalette = false }]));
        var row = Profile(Source()).Materials[0];
        Assert.ThrowsAny<ArgumentException>(() => (row with { Role = D3D12CharacterMaterialRole.Eye }).Validate());
        Assert.ThrowsAny<ArgumentException>(() => (row.EndfieldMatcap! with { CoupleEndfieldPalette = false }).Validate());
        foreach (bool emission in new[] { true, false })
        {
            var data = row.EndfieldMatcap!.Palette!;
            var bad = emission ? data with { EmissionTexture = data.EmissionTexture! with { Kind = SceneTextureKind.IndexedProceduralToon } }
                : data with { ReceivedLightTexture = data.ReceivedLightTexture! with { Kind = SceneTextureKind.IndexedProceduralToon } };
            Assert.ThrowsAny<ArgumentException>(() => bad.Validate());
        }
    }

    [Fact]
    public void SchemaEightStrictlyRoundtripsAndRequiresEveryNestedField()
    {
        var profile = Profile(Source()); var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(8, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        foreach (string damage in new[] { "missing-palette", "missing-mode", "missing-image", "unknown", "encoding", "mode", "numeric-enum", "old-schema", "image-null", "uniform-image", "scalar", "color" })
        {
            var doc = JsonNode.Parse(bytes)!; var matcap = doc["materials"]![0]!["endfieldMatcap"]!.AsObject();
            var palette = matcap["palette"]!.AsObject();
            if (damage == "missing-palette") matcap.Remove("palette");
            if (damage == "missing-mode") palette.Remove("emissionInputMode");
            if (damage == "missing-image") palette.Remove("receivedLightTexture");
            if (damage == "unknown") palette["time"] = 1;
            if (damage == "encoding") palette["receivedLightEncoding"] = "SrgbRgb";
            if (damage == "mode") palette["emissionInputMode"] = "Guess";
            if (damage == "numeric-enum") palette["receivedLightInputMode"] = "1";
            if (damage == "old-schema") doc["schemaVersion"] = 7;
            if (damage == "image-null") palette["emissionTexture"] = null;
            if (damage == "uniform-image") palette["receivedLightInputMode"] = "Uniform";
            if (damage == "scalar") palette["density"] = 33;
            if (damage == "color") palette["receivedLightLinearRgb"] = new JsonArray(0, 0, 17);
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
        var future = JsonNode.Parse(bytes)!; future["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(future.ToJsonString())));
    }

    [Fact]
    public void SchemaEightMixedMatcapsRequireNullablePaletteAndUniformModeNeedsNoImages()
    {
        var profile = Profile(Source()); var row = profile.Materials[0];
        var uniform = row.EndfieldMatcap!.Palette! with { EmissionInputMode = D3D12EndfieldPaletteInputMode.Uniform,
            ReceivedLightInputMode = D3D12EndfieldPaletteInputMode.Uniform, EmissionTexture = null, ReceivedLightTexture = null };
        profile = new(profile.StableId, profile.Name, profile.LayoutFingerprint,
            [row with { EndfieldMatcap = row.EndfieldMatcap with { Palette = uniform } }, row with { LocalMaterialIndex = 1, EndfieldMatcap = row.EndfieldMatcap with { Palette = null } }]);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Null(restored.Materials[1].EndfieldMatcap!.Palette);
        Assert.Null(restored.Materials[0].EndfieldMatcap!.Palette!.EmissionTexture);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
        var doc = JsonNode.Parse(bytes)!; doc["materials"]![1]!["endfieldMatcap"]!.AsObject().Remove("palette");
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void DormantProfilesStillGuardBothUnusedImages(string pipeline)
    {
        var source = Source(); var profile = Profile(source); var id = Guid.NewGuid();
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline).EndfieldMatcapBindings);
        foreach (int index in new[] { 5, 7 })
        {
            var images = source.Surface.Snapshot.Textures.ToArray(); images[index] = new("changed", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(images), id, pipeline));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacySchemaFiveAndSevenRetainExactWireWithoutPalettePlaceholder(bool metallic)
    {
        var source = Source(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var row = Profile(source).Materials[0];
        row = row with { EndfieldMatcap = row.EndfieldMatcap! with { Palette = null } };
        if (metallic) row = row with { EndfieldMatcap = null, GenshinMetallic = new(layout.CaptureTexture(0), layout.CaptureTexture(2),
            D3D12MatcapColorEncoding.SrgbRgb, new(.1f), new(.8f), new(.6f), Specular: new(new(.7f), new(.2f))) };
        var profile = new D3D12MaterialBindingProfile("legacy", "Legacy", layout.Fingerprint, [row]);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(metallic ? 7 : 5, profile.SchemaVersion);
        Assert.DoesNotContain("\"palette\"", Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        var expectedRow = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
            ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null, ["endfieldMatcap"] = null
        };
        if (row.EndfieldMatcap is { } matcap) expectedRow["endfieldMatcap"] = new JsonObject
        {
            ["normal"] = Texture(matcap.Normal), ["p"] = Texture(matcap.P), ["matcap"] = Texture(matcap.Matcap),
            ["matcapEncoding"] = "SrgbRgb", ["uvChannel"] = 0, ["baseColor"] = Vector(matcap.BaseColor),
            ["highlightColor"] = Vector(matcap.HighlightColor), ["normalStrength"] = 2f, ["rotationRadians"] = 0f,
            ["highlightCoefficient"] = 0f, ["shadowHardness"] = .5f, ["lightControlDirection"] = null,
            ["baselineStrength"] = 1f, ["coupleEndfieldPalette"] = true, ["generatorRevision"] = "mikk-v1"
        };
        if (row.GenshinMetallic is { } metal) expectedRow["genshinMetallic"] = new JsonObject
        {
            ["lightmap"] = Texture(metal.Lightmap), ["matcap"] = Texture(metal.Matcap), ["matcapEncoding"] = "SrgbRgb",
            ["darkColor"] = Vector(metal.DarkColor), ["lightColor"] = Vector(metal.LightColor), ["shadowMultiplyColor"] = Vector(metal.ShadowMultiplyColor),
            ["enableMetallics"] = 1f, ["tileScale"] = 1f, ["brightness"] = 1f, ["litMode"] = "PrimaryResponse", ["uniformLitFactor"] = 1f,
            ["specular"] = new JsonObject
            {
                ["color"] = Vector(metal.Specular!.Color), ["sharpColor"] = Vector(metal.Specular.SharpColor),
                ["shininess"] = 16f, ["scale"] = 1f, ["attenuation"] = .5f, ["sharpOffset"] = 1f, ["ramp"] = null, ["rampMix"] = 0f
            }
        };
        var expected = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = metallic ? 7 : 5,
            ["stableId"] = "legacy", ["name"] = "Legacy", ["layoutVersion"] = 1,
            ["layoutFingerprint"] = layout.Fingerprint, ["materials"] = new JsonArray(expectedRow)
        };
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }
    [Fact, Trait("Category", "Gpu")]
    public async Task PortableSchemaEightRetainsFullTextureTableAfterMoveSourceDeletionAndDefaultResave()
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
            var inactiveProfile = Profile(source, offset);
            var row = inactiveProfile.Materials[0];
            var matcap = row.EndfieldMatcap!;
            var profile = new D3D12MaterialBindingProfile(inactiveProfile.StableId, inactiveProfile.Name,
                inactiveProfile.LayoutFingerprint, [row with { EndfieldMatcap = matcap with
                { Palette = matcap.Palette! with { EmissionStrength = .45f, LightInfluence = .35f, Density = .013f } } }]);
            var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, item.Id, "endfield");
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
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "endfield");
                var selection = Assert.Single(mapped.EndfieldMatcapBindings);
                Assert.Equal(offset, selection.NormalLocalTextureIndex);
                Assert.Equal(offset + 2, selection.PLocalTextureIndex); Assert.Equal(offset + 3, selection.MatcapLocalTextureIndex);
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewEndfieldMatcapBindings.CreateRequests(mapped.EndfieldMatcapBindings));
                Assert.NotNull(capture.SceneInputs[item.Id].SourceGeometry);
                Assert.Equal(extended.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < extended.Textures.Count; i++)
                    Assert.Equal(extended.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                Assert.Equal(offset + 5, selection.Palette!.EmissionCombinedTextureIndex);
                Assert.Equal(offset + 7, selection.Palette.ReceivedLightCombinedTextureIndex);
                var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
                    capture.RenderingStyles, mapped.CharacterMaterials, sceneInputs: capture.SceneInputs, endfieldMatcapDataBindings: mapped.EndfieldMatcapBindings);
                var active = Assert.Single(result.SurfaceStyles.CharacterMaterials).EndfieldMatcap!.Palette!;
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 5], active.EmissionCombinedTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 7], active.ReceivedLightCombinedTextureIndex);
                Assert.Equal(expected, await Capture(loaded.Session, mapped));
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(8, again[item.Id].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(again[item.Id]));
            Assert.DoesNotContain(reopened.ResourcePaths, p => p.Contains("tangent", StringComparison.OrdinalIgnoreCase));
            var reopenedObject = Assert.Single(reopened.Session.Objects);
            var reopenedMapping = D3D12MaterialBindingProfileResolver.Resolve(again[item.Id], reopenedObject.Source, item.Id, "endfield");
            Assert.Equal(expected, await Capture(reopened.Session, reopenedMapping));
        }
        finally { Directory.Delete(directory, true); }
    }


    private static async Task<byte[]> Capture(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile mapping, bool palette = true)
    {
        await using var renderer = new D3D12ViewRenderer(new(W, H), characterMaterials: mapping.CharacterMaterials,
            endfieldMatcapDataBindings: palette ? mapping.EndfieldMatcapBindings
                : mapping.EndfieldMatcapBindings.Select(v => v with { Palette = null }));
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
    private static D3D12ViewEndfieldMatcapBinding View(Guid id) => new(id, 0, 0, 2, 3,
        D3D12MatcapColorEncoding.SrgbRgb, 0, new(.2f, .3f, .4f), new(.1f, .4f, .2f),
        CoupleEndfieldPalette: true, Palette: new(new(.2f, -.3f, 1.1f), Density: .7f,
            EmissionInputMode: D3D12EndfieldPaletteInputMode.Texture, EmissionCombinedTextureIndex: 5,
            ReceivedLightInputMode: D3D12EndfieldPaletteInputMode.Texture, ReceivedLightCombinedTextureIndex: 7));
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source); var value = View(Guid.NewGuid()); var palette = value.Palette!;
        return new("endfield-palette", "Explicit palette", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Body,
            EndfieldMatcap: new(layout.CaptureTexture(offset), layout.CaptureTexture(offset + 2), layout.CaptureTexture(offset + 3),
                value.MatcapEncoding, value.UvChannel, value.BaseColor, value.HighlightColor, CoupleEndfieldPalette: true,
                Palette: new(palette.ReceivedLightLinearRgb, palette.EmissionStrength, palette.UniformEmissionBlue,
                    palette.LightInfluence, palette.Density, palette.EmissionInputMode, layout.CaptureTexture(offset + 5),
                    palette.ReceivedLightInputMode, layout.CaptureTexture(offset + 7))))]);
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
