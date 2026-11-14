using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12GenshinAtlasViewProfileTests
{
    internal static D3D12ViewGenshinHairAtlasBinding Binding(Guid id, int lightmap = 1, int atlas = 0)
        => new(id, 0, lightmap, atlas, D3D12GenshinAtlasColorEncoding.SrgbRgb, 2.5f, .3f, .6f, -.5f, .7f, .8f, 1, .2f);
    internal static D3D12MaterialBindingProfile Profile(OriginalFrameScene source, bool includeHighlight = true)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        return new("atlas-map", "Atlas map", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair,
                GenshinHair: includeHighlight ? new(layout.CaptureTexture(1)) : null,
                GenshinHairAtlas: new(layout.CaptureTexture(1), layout.CaptureTexture(0),
                    D3D12GenshinAtlasColorEncoding.SrgbRgb, 2.5f, .3f, .6f, -.5f, .7f, .8f, 1, .2f))]);
    }

    [Fact]
    public void SchemaThreePreservesBothGuardsAndCoexistingHighlightWithoutUpgradingOlderPayloads()
    {
        var source = Source(); var profile = Profile(source);
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        Assert.Equal(3, JsonNode.Parse(bytes)!["schemaVersion"]!.GetValue<int>());
        var restored = D3D12MaterialBindingProfileJson.Decode(bytes);
        Assert.Equal(profile.Materials, restored.Materials);
        Assert.Equal(bytes, D3D12MaterialBindingProfileJson.Encode(restored));
        foreach (var row in new[] { new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair),
            new(0, D3D12CharacterMaterialRole.Hair, EndfieldRim: new(Vector3.One, Vector3.One)) })
        {
            var old = new D3D12MaterialBindingProfile("old", "old", profile.LayoutFingerprint, [row]);
            byte[] oldBytes = D3D12MaterialBindingProfileJson.Encode(old);
            Assert.False(JsonNode.Parse(oldBytes)!["materials"]![0]!.AsObject().ContainsKey("genshinHairAtlas"));
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
    public void SchemaThreeRejectsMalformedOrSilentDowngradedAtlas(string damage)
    {
        var document = JsonNode.Parse(D3D12MaterialBindingProfileJson.Encode(Profile(Source())))!;
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
        var result = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline);
        Assert.Equal(pipeline == "genshin" ? 1 : 0, result.GenshinHairAtlasBindings.Count);
        if (pipeline == "genshin") Assert.Equal(Binding(id), Assert.Single(result.GenshinHairAtlasBindings));
        foreach (bool damageLightmap in new[] { false, true })
        {
            var row = profile.Materials[0]; var atlas = row.GenshinHairAtlas!;
            atlas = damageLightmap ? atlas with { Lightmap = atlas.Lightmap with { RgbaSha256 = new('0', 64) } }
                : atlas with { Atlas = atlas.Atlas with { RgbaSha256 = new('0', 64) } };
            var invalid = new D3D12MaterialBindingProfile("bad", "bad", profile.LayoutFingerprint, [row with { GenshinHairAtlas = atlas }]);
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(invalid, source, id, pipeline));
        }
    }

    [Fact]
    public void DuplicateInstancesRebaseBothSlotsFromOneCapturedSampleAndKeepIndependentHighlight()
    {
        var layout = new SceneCompositionEditor(); var a = layout.Add("a", SourcePath, ModelRole.Character, Source());
        var b = layout.Add("b", SourcePath, ModelRole.Character, Source(reverse: true));
        using var project = new SceneProjectSession(layout, new(), [], new NoPhysics(), cancellationToken: TestContext.Current.CancellationToken);
        project.SetDefaultRenderingStyle(new("genshin"), TestContext.Current.CancellationToken);
        D3D12ViewGenshinHairAtlasBinding[] atlas = [Binding(a.Id), Binding(b.Id, 0, 1)];
        D3D12ViewGenshinHairBinding[] highlight = [new(a.Id, 0, 1), new(b.Id, 0, 0)];
        var requests = D3D12ViewGenshinHairAtlasBindings.CreateRequests(atlas,
            D3D12ViewGenshinHairBindings.CreateRequests(highlight));
        Assert.Equal(2, requests.Count);
        // Requests are deduplicated source-local selections, not a sorted combined texture table.
        Assert.All(requests, request => Assert.Equal(new[] { 0, 1 }, request.LocalTextureIndices.Order()));
        var sample = project.CaptureRenderSnapshot(requests);
        // Mirror the real ViewOutput adapter: composition captures canonical geometry without
        // selecting modern frame options. This does not re-evaluate animation or change SceneInputs.
        var frameScene = D3D12ViewScene.Resolve(sample.Scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new());
        var result = D3D12ViewRenderingStyles.Resolve(frameScene, D3D12StylePresetCatalog.BuiltIn,
            sample.RenderingStyles, sceneInputs: sample.SceneInputs, hairDataBindings: highlight, genshinHairAtlasDataBindings: atlas);
        foreach (var instance in sample.RenderingStyles)
        {
            var binding = atlas.Single(value => value.ObjectId == instance.ObjectId);
            var style = result.SurfaceStyles.CharacterMaterials[instance.MaterialStart];
            Assert.Equal(sample.SceneInputs[instance.ObjectId].TextureIndices[binding.LightmapLocalTextureIndex], style.GenshinHairShadowAtlas!.LightmapCombinedTextureIndex);
            Assert.Equal(sample.SceneInputs[instance.ObjectId].TextureIndices[binding.AtlasLocalTextureIndex], style.GenshinHairShadowAtlas.AtlasCombinedTextureIndex);
            Assert.NotNull(style.GenshinNonMetallicSpecular);
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(frameScene, D3D12StylePresetCatalog.BuiltIn,
            sample.RenderingStyles, hairDataBindings: highlight, genshinHairAtlasDataBindings: atlas));
        Assert.Equal(0, project.CurrentFrame);
    }

    [Fact]
    public void LocalBindingsDetachAndRejectCrossFamilyOrComposedDuplicatesBeforeDeviceCreation()
    {
        var id = Guid.NewGuid(); var valid = Binding(id); var inputs = new[] { valid };
        var detached = D3D12ViewGenshinHairAtlasBindings.Detach(inputs); inputs[0] = valid with { AtlasLocalTextureIndex = 99 };
        Assert.Equal(valid, detached[0]); Assert.Same(detached, D3D12ViewGenshinHairAtlasBindings.Detach(detached));
        foreach (var candidate in new[] { valid with { ObjectId = Guid.Empty }, valid with { LocalMaterialIndex = -1 },
            valid with { LightmapLocalTextureIndex = -1 }, valid with { AtlasLocalTextureIndex = -1 },
            valid with { ExplicitRowId = float.NaN }, valid with { DayNight = 2 } })
            Assert.ThrowsAny<ArgumentException>(() => D3D12ViewGenshinHairAtlasBindings.Detach([candidate]));
        Assert.Throws<ArgumentException>(() => D3D12ViewGenshinHairAtlasBindings.Detach([valid, valid]));
        foreach (var style in new[] { new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face),
            new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 0)),
            new(D3D12CharacterMaterialRole.Hair, PmxMaterialTreatment: new()),
            new(D3D12CharacterMaterialRole.Hair, GenshinHairShadowAtlas: new(1, 0, D3D12GenshinAtlasColorEncoding.LinearRgb, 1, 0, 1, 1, 1, 1, 0, 0)) })
        {
            D3D12ViewCharacterMaterialBinding[] materials = [new(id, 0, style)];
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(32, 32), characterMaterials: materials, genshinHairAtlasDataBindings: [valid]));
            Assert.Throws<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(32, 32), characterMaterials: materials, genshinHairAtlasDataBindings: [valid]));
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
