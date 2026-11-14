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
        var id = Guid.NewGuid(); var value = View(id);
        var bindings = D3D12ViewEndfieldMatcapBindings.Detach([value]);
        var request = Assert.Single(D3D12ViewEndfieldMatcapBindings.CreateRequests(bindings, [new(id, [4], [6], [9])]));
        Assert.True(request.IncludeSourceGeometry); Assert.Equal(new[] { 4 }, request.BoneIndices);
        Assert.Equal(new[] { 6 }, request.LocalTextureIndices);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9 }, request.OptionalLocalTextureIndices.Order());
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
        D3D12ViewEndfieldMatcapBinding[] values = [View(a.Id), View(b.Id)];
        var capture = project.CaptureRenderSnapshot(D3D12ViewEndfieldMatcapBindings.CreateRequests(values));
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, endfieldMatcapDataBindings: values);
        foreach (var obj in capture.RenderingStyles)
        {
            var active = result.SurfaceStyles.CharacterMaterials[obj.MaterialStart].EndfieldMatcap!;
            var mapped = capture.SceneInputs[obj.ObjectId].TextureIndices;
            var expected = View(obj.ObjectId).Palette!.BaseImage! with
            { BaseCombinedTextureIndex = mapped[1], DetailCombinedTextureIndex = mapped[4], ShadowCombinedTextureIndex = mapped[8] };
            Assert.Equal(expected, active.Palette!.BaseImage);
            Assert.Same(capture.SceneInputs[obj.ObjectId].SourceGeometry, active.Geometry!.Source);
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
        var images = Source().Surface.Snapshot.Textures.ToArray();
        if (procedural) images[selected] = SceneTexture.ProceduralToon();
        var source = Source(images); var editor = new SceneCompositionEditor();
        var item = editor.Add("required", Path.Combine(Path.GetTempPath(), "base-required.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("endfield"), Token); var value = View(item.Id);
        var request = Assert.Single(D3D12ViewEndfieldMatcapBindings.CreateRequests([value]));
        if (!procedural) request = new(item.Id, optionalLocalTextureIndices: request.OptionalLocalTextureIndices.Where(v => v != selected), includeSourceGeometry: true);
        var capture = project.CaptureRenderSnapshot([request]);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, endfieldMatcapDataBindings: [value]));
    }

    [Theory]
    [InlineData("mmd")] [InlineData("genshin")] [InlineData("star-rail")] [InlineData("wuthering-waves")]
    public void DormantFamilyStillGuardsAllRequestedCpreImages(string pipeline)
    {
        var source = Source(); var profile = Profile(source); var id = Guid.NewGuid();
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline).EndfieldMatcapBindings);
        foreach (int selected in new[] { 1, 4, 8 })
        {
            var images = source.Surface.Snapshot.Textures.ToArray(); images[selected] = new("changed", 1, 1, [0, 0, 0, 0]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(images), id, pipeline));
        }
    }

    [Fact]
    public void SchemaNineStrictlyRoundtripsAndRequiresEveryNewField()
    {
        var profile = Profile(Source()); var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(9, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        var original = JsonNode.Parse(bytes)!;
        var basis = original["materials"]![0]!["endfieldMatcap"]!["palette"]!["baseImage"]!.AsObject();
        foreach (string field in basis.Select(v => v.Key))
        {
            var doc = JsonNode.Parse(bytes)!; doc["materials"]![0]!["endfieldMatcap"]!["palette"]!["baseImage"]!.AsObject().Remove(field);
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
        foreach (string damage in new[] { "unknown", "old-schema", "encoding", "enum", "number", "uniform-image", "null-image" })
        {
            var doc = JsonNode.Parse(bytes)!; var node = doc["materials"]![0]!["endfieldMatcap"]!["palette"]!["baseImage"]!.AsObject();
            if (damage == "unknown") node["guessRole"] = true;
            if (damage == "old-schema") doc["schemaVersion"] = 8;
            if (damage == "encoding") node["baseEncoding"] = "Guess";
            if (damage == "enum") node["detailInputMode"] = "1";
            if (damage == "number") node["contrast"] = 5;
            if (damage == "uniform-image") node["shadowInputMode"] = "Uniform";
            if (damage == "null-image") node["baseTexture"] = null;
            Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        }
        var future = JsonNode.Parse(bytes)!; future["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(future.ToJsonString())));
    }

    [Fact]
    public void MixedSchemaNineRequiresNullableChildButOldSchemaEightBytesStayExact()
    {
        var source = Source(); var initial = Profile(source); var row = initial.Materials[0];
        var old = row with { EndfieldMatcap = row.EndfieldMatcap! with { Palette = row.EndfieldMatcap.Palette! with { BaseImage = null } } };
        var legacy = new D3D12MaterialBindingProfile(initial.StableId, initial.Name, initial.LayoutFingerprint, [old]);
        var bytes = D3D12MaterialBindingProfileJson.Encode(legacy);
        Assert.Equal(8, legacy.SchemaVersion); Assert.DoesNotContain("baseImage", Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        // Compare actual emitted palette bytes against the exact pre-schema-9 property order and representation.
        var originalPalette = JsonNode.Parse(bytes)!["materials"]![0]!["endfieldMatcap"]!["palette"]!;
        var expected = new JsonObject
        {
            ["receivedLightLinearRgb"] = new JsonArray(.2f, -.3f, 1.1f), ["emissionStrength"] = 0f,
            ["uniformEmissionBlue"] = 0f, ["lightInfluence"] = 0f, ["density"] = .7f,
            ["emissionInputMode"] = "Texture", ["emissionTexture"] = Texture(old.EndfieldMatcap!.Palette!.EmissionTexture!),
            ["receivedLightInputMode"] = "Texture", ["receivedLightTexture"] = Texture(old.EndfieldMatcap.Palette.ReceivedLightTexture!)
        };
        Assert.Equal(expected.ToJsonString(), originalPalette.ToJsonString());
        var mixed = new D3D12MaterialBindingProfile(initial.StableId, initial.Name, initial.LayoutFingerprint,
            [row, old with { LocalMaterialIndex = 1 }]);
        var mixedBytes = D3D12MaterialBindingProfileJson.Encode(mixed); var doc = JsonNode.Parse(mixedBytes)!;
        Assert.Null(doc["materials"]![1]!["endfieldMatcap"]!["palette"]!["baseImage"]);
        Assert.Equal(mixedBytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(mixedBytes)));
        doc["materials"]![1]!["endfieldMatcap"]!["palette"]!.AsObject().Remove("baseImage");
        Assert.Throws<InvalidDataException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(doc.ToJsonString())));
    }

    [Fact]
    public void ViewAndProfileRejectInvalidModesNumbersEncodingAndNonImageSelectors()
    {
        var view = View(Guid.NewGuid()); var basis = view.Palette!.BaseImage!;
        foreach (var invalid in new[]
        {
            basis with { BaseInputMode = (D3D12EndfieldPaletteInputMode)99 }, basis with { DetailInputMode = (D3D12EndfieldPaletteInputMode)99 },
            basis with { ShadowInputMode = (D3D12EndfieldPaletteInputMode)99 }, basis with { BaseEncoding = (D3D12MatcapColorEncoding)99 },
            basis with { BaseCombinedTextureIndex = null }, basis with { DetailCombinedTextureIndex = -1 }, basis with { ShadowCombinedTextureIndex = null },
            basis with { BaseInputMode = D3D12EndfieldPaletteInputMode.Uniform }, basis with { DetailInputMode = D3D12EndfieldPaletteInputMode.Uniform },
            basis with { ShadowInputMode = D3D12EndfieldPaletteInputMode.Uniform }, basis with { ModelColor = new(float.NaN) },
            basis with { Contrast = 4.01f }, basis with { Saturation = 16.01f }, basis with { DetailBlue = -16.01f },
            basis with { ShadowFactor = 32.01f }, basis with { ShadowStrength = float.PositiveInfinity }, basis with { WarmTint = -32.01f }
        }) Assert.ThrowsAny<ArgumentException>(() => D3D12ViewEndfieldMatcapBindings.Detach([view with { Palette = view.Palette with { BaseImage = invalid } }]));
        var uniform = basis with { BaseInputMode = D3D12EndfieldPaletteInputMode.Uniform, BaseCombinedTextureIndex = null,
            BaseEncoding = D3D12MatcapColorEncoding.LinearRgb, DetailInputMode = D3D12EndfieldPaletteInputMode.Uniform, DetailCombinedTextureIndex = null,
            ShadowInputMode = D3D12EndfieldPaletteInputMode.Uniform, ShadowCombinedTextureIndex = null };
        Assert.Single(D3D12ViewEndfieldMatcapBindings.Detach([view with { Palette = view.Palette with { BaseImage = uniform } }]));
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewEndfieldMatcapBindings.Detach([view with { Palette = view.Palette with
            { BaseImage = uniform with { BaseEncoding = D3D12MatcapColorEncoding.SrgbRgb } } }]));
        var profile = Profile(Source()).Materials[0].EndfieldMatcap!.Palette!.BaseImage!;
        foreach (var invalid in new[] { profile with { BaseTexture = profile.BaseTexture! with { Kind = SceneTextureKind.IndexedProceduralToon } },
            profile with { DetailTexture = profile.DetailTexture! with { Kind = SceneTextureKind.IndexedProceduralToon } },
            profile with { ShadowTexture = profile.ShadowTexture! with { Kind = SceneTextureKind.IndexedProceduralToon } } })
            Assert.ThrowsAny<ArgumentException>(() => invalid.Validate());
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PortableSchemaNineRetainsFullTextureTableAfterMoveSourceDeletionAndDefaultResave()
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
            var inactiveProfile = Profile(source, offset);
            var row = inactiveProfile.Materials[0];
            var matcap = row.EndfieldMatcap!;
            var profile = new D3D12MaterialBindingProfile(inactiveProfile.StableId, inactiveProfile.Name,
                inactiveProfile.LayoutFingerprint, [row with { EndfieldMatcap = matcap with
                { Palette = matcap.Palette! with { EmissionStrength = .45f, LightInfluence = .35f, Density = .013f,
                    BaseImage = matcap.Palette.BaseImage! with { ShadowStrength = .66f } } } }]);
            var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, item.Id, "endfield");
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
                Assert.Equal(offset + 1, selection.Palette.BaseImage!.BaseCombinedTextureIndex);
                Assert.Equal(offset + 4, selection.Palette.BaseImage.DetailCombinedTextureIndex);
                Assert.Equal(offset + 8, selection.Palette.BaseImage.ShadowCombinedTextureIndex);
                var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
                    capture.RenderingStyles, mapped.CharacterMaterials, sceneInputs: capture.SceneInputs, endfieldMatcapDataBindings: mapped.EndfieldMatcapBindings);
                var active = Assert.Single(result.SurfaceStyles.CharacterMaterials).EndfieldMatcap!.Palette!;
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 5], active.EmissionCombinedTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 7], active.ReceivedLightCombinedTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 1], active.BaseImage!.BaseCombinedTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 4], active.BaseImage.DetailCombinedTextureIndex);
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 8], active.BaseImage.ShadowCombinedTextureIndex);
                Assert.Equal(expected, await Capture(loaded.Session, mapped));
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(9, again[item.Id].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(again[item.Id]));
            Assert.DoesNotContain(reopened.ResourcePaths, p => p.Contains("tangent", StringComparison.OrdinalIgnoreCase));
            var reopenedObject = Assert.Single(reopened.Session.Objects);
            var reopenedMapping = D3D12MaterialBindingProfileResolver.Resolve(again[item.Id], reopenedObject.Source, item.Id, "endfield");
            Assert.Equal(expected, await Capture(reopened.Session, reopenedMapping));
        }
        finally { Directory.Delete(directory, true); }
    }


    private static async Task<byte[]> Capture(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile mapping, bool basis = true)
    {
        await using var renderer = new D3D12ViewRenderer(new(W, H), characterMaterials: mapping.CharacterMaterials,
            endfieldMatcapDataBindings: basis ? mapping.EndfieldMatcapBindings
                : mapping.EndfieldMatcapBindings.Select(v => v with { Palette = v.Palette! with { BaseImage = null } }));
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
            ReceivedLightInputMode: D3D12EndfieldPaletteInputMode.Texture, ReceivedLightCombinedTextureIndex: 7,
            BaseImage: new(new(.4f), new(.5f), new(.7f, .8f, .9f), new(.9f), WarmTint: .4f, Contrast: .1f,
                Saturation: 1.2f, ShadowStrength: 0f, BaseInputMode: D3D12EndfieldPaletteInputMode.Texture, BaseCombinedTextureIndex: 1,
                BaseEncoding: D3D12MatcapColorEncoding.SrgbRgb, DetailInputMode: D3D12EndfieldPaletteInputMode.Texture,
                DetailCombinedTextureIndex: 4, ShadowInputMode: D3D12EndfieldPaletteInputMode.Texture, ShadowCombinedTextureIndex: 8)));
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source); var value = View(Guid.NewGuid()); var palette = value.Palette!;
        return new("endfield-palette", "Explicit palette", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Body,
            EndfieldMatcap: new(layout.CaptureTexture(offset), layout.CaptureTexture(offset + 2), layout.CaptureTexture(offset + 3),
                value.MatcapEncoding, value.UvChannel, value.BaseColor, value.HighlightColor, CoupleEndfieldPalette: true,
                Palette: new(palette.ReceivedLightLinearRgb, palette.EmissionStrength, palette.UniformEmissionBlue,
                    palette.LightInfluence, palette.Density, palette.EmissionInputMode, layout.CaptureTexture(offset + 5),
                    palette.ReceivedLightInputMode, layout.CaptureTexture(offset + 7),
                    new(palette.BaseImage!.BaseImageLinearRgb, palette.BaseImage.ModelColor, palette.BaseImage.ShadowColor,
                        palette.BaseImage.ColorMultiplier, palette.BaseImage.ShadowFactor, palette.BaseImage.WarmTint,
                        palette.BaseImage.Contrast, palette.BaseImage.Saturation, palette.BaseImage.DetailBlue, palette.BaseImage.ShadowStrength,
                        palette.BaseImage.BaseInputMode, layout.CaptureTexture(offset + 1), palette.BaseImage.BaseEncoding,
                        palette.BaseImage.DetailInputMode, layout.CaptureTexture(offset + 4),
                        palette.BaseImage.ShadowInputMode, layout.CaptureTexture(offset + 8)))))]);
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
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
