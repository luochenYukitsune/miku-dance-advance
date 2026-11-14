using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicSpecularViewPmaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DetachedNestedPayloadAndRequestsPreserveExplicitRampEvenWhenItsMixIsZero()
    {
        var id = Guid.NewGuid(); var value = View(id, mix: 0);
        D3D12ViewGenshinMetallicBinding[] input = [value];
        var frozen = D3D12ViewGenshinMetallicBindings.Detach(input);
        input[0] = value with { Specular = null };
        Assert.Equal(value, Assert.Single(frozen));
        Assert.Same(frozen, D3D12ViewGenshinMetallicBindings.Detach(frozen));
        var existing = new SceneObjectRenderRequest(id, [4], [3], [8], includeSourceGeometry: true);
        var request = Assert.Single(D3D12ViewGenshinMetallicBindings.CreateRequests(frozen, [existing]));
        Assert.Equal(new[] { 0, 1, 2, 8 }, request.OptionalLocalTextureIndices.Order());
        Assert.Equal(new[] { 3 }, request.LocalTextureIndices);
        Assert.Equal(new[] { 4 }, request.BoneIndices);
        Assert.True(request.IncludeSourceGeometry);
        var noRamp = value with { Specular = value.Specular! with { RampCombinedTextureIndex = null } };
        Assert.Equal(new[] { 0, 1 }, Assert.Single(D3D12ViewGenshinMetallicBindings.CreateRequests([noRamp])).OptionalLocalTextureIndices.Order());
        D3D12ViewGenshinMetallicBindings.Detach([noRamp]);
        foreach (var invalid in new[]
        {
            value with { Specular = value.Specular! with { Color = new(float.NaN) } },
            value with { Specular = value.Specular! with { SharpColor = new(float.PositiveInfinity) } },
            value with { Specular = value.Specular! with { Shininess = -1 } },
            value with { Specular = value.Specular! with { Scale = float.NaN } },
            value with { Specular = value.Specular! with { Attenuation = float.NaN } },
            value with { Specular = value.Specular! with { SharpOffset = float.NaN } },
            value with { Specular = value.Specular! with { RampMix = float.NaN } },
            value with { Specular = value.Specular! with { RampCombinedTextureIndex = -1 } },
            noRamp with { Specular = noRamp.Specular! with { RampMix = .1f } }
        })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewGenshinMetallicBindings.Detach([invalid]));
    }

    [Fact]
    public void SameCaptureRebasesNestedRampPerObjectAndRetainsTheParentsSharedRbLightmap()
    {
        var source = Source(); var editor = new SceneCompositionEditor();
        var first = editor.Add("first selected object", Path.Combine(Path.GetTempPath(), "metal-spec-a.pmx"), ModelRole.Character, source);
        var second = editor.Add("second selected object", Path.Combine(Path.GetTempPath(), "metal-spec-b.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("genshin"), Token);
        D3D12ViewGenshinMetallicBinding[] bindings = [View(first.Id), View(second.Id)];
        D3D12ViewGenshinHairBinding[] hair = [new(first.Id, 0, 0)];
        var requests = D3D12ViewGenshinMetallicBindings.CreateRequests(bindings, D3D12ViewGenshinHairBindings.CreateRequests(hair));
        var capture = project.CaptureRenderSnapshot(requests);
        var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, hairDataBindings: hair, genshinMetallicDataBindings: bindings);
        foreach (var obj in capture.RenderingStyles)
        {
            var style = result.SurfaceStyles.CharacterMaterials[obj.MaterialStart];
            var parent = Assert.IsType<D3D12GenshinMetallicBinding>(style.GenshinMetallic);
            var specular = Assert.IsType<D3D12GenshinMetallicSpecularBinding>(parent.Specular);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[0], parent.LightmapCombinedTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[1], parent.MatcapCombinedTextureIndex);
            Assert.Equal(capture.SceneInputs[obj.ObjectId].TextureIndices[2], specular.RampCombinedTextureIndex);
            Assert.Equal(View(obj.ObjectId).Specular! with { RampCombinedTextureIndex = specular.RampCombinedTextureIndex }, specular);
            if (obj.ObjectId == first.Id)
                Assert.Equal(parent.LightmapCombinedTextureIndex, style.GenshinNonMetallicSpecular!.CombinedTextureIndex);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(Modern(capture.Scene, source),
            D3D12StylePresetCatalog.BuiltIn, capture.RenderingStyles, hairDataBindings: [new(first.Id, 0, 2)],
            genshinMetallicDataBindings: bindings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedRampRequiresACurrentImageEvenWithZeroMix(bool procedural)
    {
        var source = Source(); var textures = source.Surface.Snapshot.Textures.ToArray();
        if (procedural) textures[2] = SceneTexture.ProceduralToon();
        source = Source(textures); var editor = new SceneCompositionEditor();
        var item = editor.Add("selected resource", Path.Combine(Path.GetTempPath(), "metal-spec-missing.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("genshin"), Token);
        var mapping = View(item.Id, mix: 0);
        var captured = mapping with { Specular = null };
        var capture = project.CaptureRenderSnapshot(D3D12ViewGenshinMetallicBindings.CreateRequests([procedural ? mapping : captured]));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source),
            D3D12StylePresetCatalog.BuiltIn, capture.RenderingStyles, sceneInputs: capture.SceneInputs,
            genshinMetallicDataBindings: [mapping]));
        var absent = mapping with { Specular = mapping.Specular! with { RampCombinedTextureIndex = null } };
        var resolved = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
            capture.RenderingStyles, sceneInputs: capture.SceneInputs, genshinMetallicDataBindings: [absent]);
        Assert.Null(Assert.Single(resolved.SurfaceStyles.CharacterMaterials).GenshinMetallic!.Specular!.RampCombinedTextureIndex);
    }

    [Fact]
    public void SchemaSevenStrictlyRoundtripsAndRejectsNestedMissingUnknownEnumsAndOlderVersion()
    {
        var profile = Profile(Source()); var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(7, profile.SchemaVersion);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
        foreach (string damage in new[] { "missing", "unknown", "schema", "encoding", "mode", "kind", "numeric-enum", "ramp-missing", "parameter-missing", "ramp-required", "nonfinite" })
        {
            var doc = JsonNode.Parse(bytes)!; var parent = doc["materials"]![0]!["genshinMetallic"]!.AsObject();
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
        var future = JsonNode.Parse(bytes)!; future["schemaVersion"] = D3D12MaterialBindingProfile.CurrentSchemaVersion + 1;
        Assert.Throws<NotSupportedException>(() => D3D12MaterialBindingProfileJson.Decode(Encoding.UTF8.GetBytes(future.ToJsonString())));
    }

    [Fact]
    public void SchemaSevenAllowsNoRampAtZeroMixAndMixedParentsWithoutSpecular()
    {
        var source = Source(); var profile = Profile(source, ramp: false, mix: 0);
        var rows = new[] { profile.Materials[0], profile.Materials[0] with { LocalMaterialIndex = 1,
            GenshinMetallic = profile.Materials[0].GenshinMetallic! with { Specular = null } } };
        profile = new(profile.StableId, profile.Name, profile.LayoutFingerprint, rows);
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(7, restored.SchemaVersion);
        Assert.Null(restored.Materials[0].GenshinMetallic!.Specular!.Ramp);
        Assert.Null(restored.Materials[1].GenshinMetallic!.Specular);
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
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline).GenshinMetallicBindings);
        var textures = source.Surface.Snapshot.Textures.ToArray();
        textures[2] = new("mutated ramp", 2, 2, new byte[16]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, Source(textures), id, pipeline));
        var parent = profile.Materials[0].GenshinMetallic!;
        Assert.Throws<ArgumentException>(() => (parent with { Specular = parent.Specular! with
            { Ramp = parent.Specular.Ramp! with { Kind = SceneTextureKind.IndexedProceduralToon } } }).Validate());
        var active = Assert.Single(D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, "genshin").GenshinMetallicBindings);
        Assert.Equal(2, active.Specular!.RampCombinedTextureIndex);
        Assert.Equal(0, active.Specular.RampMix);
    }

    [Fact]
    public void LegacySchemaSixBytesContainNoSpecularPlaceholder()
    {
        var source = Source(); var profile = Profile(source);
        profile = new(profile.StableId, profile.Name, profile.LayoutFingerprint,
            [profile.Materials[0] with { GenshinMetallic = profile.Materials[0].GenshinMetallic! with { Specular = null } }]);
        var metallic = profile.Materials[0].GenshinMetallic!;
        var row = new JsonObject
        {
            ["localMaterialIndex"] = 0, ["role"] = "Body", ["faceNormalShadeStrength"] = .35f,
            ["faceGeometricShadowStrength"] = 1f, ["eyeGeometricShadowStrength"] = .2f,
            ["pmxMaterialTreatment"] = null, ["face"] = null, ["genshinHair"] = null, ["starMcHair"] = null,
            ["endfieldRim"] = null, ["genshinHairAtlas"] = null, ["endfieldHair"] = null, ["endfieldMatcap"] = null,
            ["genshinMetallic"] = new JsonObject
            {
                ["lightmap"] = Texture(metallic.Lightmap), ["matcap"] = Texture(metallic.Matcap),
                ["matcapEncoding"] = "SrgbRgb", ["darkColor"] = Vector(metallic.DarkColor), ["lightColor"] = Vector(metallic.LightColor),
                ["shadowMultiplyColor"] = Vector(metallic.ShadowMultiplyColor), ["enableMetallics"] = metallic.EnableMetallics,
                ["tileScale"] = metallic.TileScale, ["brightness"] = metallic.Brightness, ["litMode"] = "Uniform",
                ["uniformLitFactor"] = metallic.UniformLitFactor
            }
        };
        var expected = new JsonObject
        {
            ["format"] = "mda-d3d12-material-binding-profile", ["schemaVersion"] = 6,
            ["stableId"] = profile.StableId, ["name"] = profile.Name, ["layoutVersion"] = 1,
            ["layoutFingerprint"] = profile.LayoutFingerprint, ["materials"] = new JsonArray(row)
        };
        var bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(6, profile.SchemaVersion);
        Assert.DoesNotContain("specular", Encoding.UTF8.GetString(bytes));
        Assert.Equal(Encoding.UTF8.GetBytes(expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), bytes);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(D3D12MaterialBindingProfileJson.Decode(bytes)));
    }

    [Fact]
    public void PortableSchemaSevenRestoresRampAndFullOriginalTextureTableAfterRelocationAndDefaultResave()
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
                var mapped = D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored, item.Id, "genshin");
                var selection = Assert.Single(mapped.GenshinMetallicBindings);
                Assert.Equal(offset, selection.LightmapLocalTextureIndex); Assert.Equal(offset + 1, selection.MatcapLocalTextureIndex);
                Assert.Equal(offset + 2, selection.Specular!.RampCombinedTextureIndex);
                Assert.Equal(extended.Textures.Count, restored.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < extended.Textures.Count; i++)
                {
                    Assert.Equal(extended.Textures[i].Rgba.ToArray(), restored.Surface.Snapshot.Textures[i].Rgba.ToArray());
                    Assert.Equal(extended.Textures[i].Width, restored.Surface.Snapshot.Textures[i].Width);
                    Assert.Equal(extended.Textures[i].Height, restored.Surface.Snapshot.Textures[i].Height);
                }
                var capture = loaded.Session.CaptureRenderSnapshot(D3D12ViewGenshinMetallicBindings.CreateRequests(mapped.GenshinMetallicBindings));
                var result = D3D12ViewRenderingStyles.Resolve(Modern(capture.Scene, source), D3D12StylePresetCatalog.BuiltIn,
                    capture.RenderingStyles, mapped.CharacterMaterials, sceneInputs: capture.SceneInputs,
                    genshinMetallicDataBindings: mapped.GenshinMetallicBindings);
                var active = Assert.Single(result.SurfaceStyles.CharacterMaterials).GenshinMetallic!.Specular!;
                Assert.Equal(capture.SceneInputs[item.Id].TextureIndices[offset + 2], active.RampCombinedTextureIndex);
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load, new NoPhysics(),
                out var again, cancellationToken: Token);
            Assert.Equal(7, again[item.Id].SchemaVersion);
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
    private static D3D12ViewGenshinMetallicBinding View(Guid id, float mix = .6f) => new(id, 0, 0, 1,
        D3D12MatcapColorEncoding.SrgbRgb, new(.2f, .3f, .4f), new(1.2f, 1.3f, 1.4f), new(.8f, .7f, .6f),
        .6f, 1.7f, .8f, D3D12GenshinMetallicLitMode.Uniform, .95f,
        new(new(.7f, 1.2f, -.1f), new(.2f, .4f, 1.3f), 12, .8f, .4f, .9f, 2, mix));
    private static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, int offset = 0, bool ramp = true, float mix = .6f)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source); var value = View(Guid.NewGuid(), mix); var specular = value.Specular!;
        return new("genshin-metal", "Explicit metal", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, GenshinMetallic:
                new(layout.CaptureTexture(offset), layout.CaptureTexture(offset + 1), value.MatcapEncoding,
                    value.DarkColor, value.LightColor, value.ShadowMultiplyColor, value.EnableMetallics,
                    value.TileScale, value.Brightness, value.LitMode, value.UniformLitFactor,
                    new(specular.Color, specular.SharpColor, specular.Shininess, specular.Scale, specular.Attenuation,
                        specular.SharpOffset, ramp ? layout.CaptureTexture(offset + 2) : null, specular.RampMix)))]);
    }
    private static OriginalFrameScene Source(SceneTexture[]? textures = null)
    {
        var frame = D3D12EndfieldHairBindingTests.Frame(seeds: false, textures: textures ??
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
