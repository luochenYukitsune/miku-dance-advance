using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GeneratedStyleMaterialTests
{
    [Theory]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void AllExplicitSurfaceKindsGeneratePortableEditableFamilyInputs(string pipeline)
    {
        foreach (var kind in Enum.GetValues<D3D12GeneratedPmxSurfaceKind>())
        {
            var source = Source();
            var first = Prepare(source, pipeline, kind);
            Assert.Single(source.Surface.Snapshot.Textures);
            Assert.Equal(source.Surface.Snapshot.Materials, first.SourceRest.Surface.Snapshot.Materials);
            Assert.Equal(source.Surface.Snapshot.Vertices.ToArray(), first.SourceRest.Surface.Snapshot.Vertices.ToArray());
            Assert.Equal(first.ManifestBytes, Prepare(source, pipeline, kind).ManifestBytes);
            var decoded = D3D12MaterialBindingProfileJson.Decode(D3D12MaterialBindingProfileJson.Encode(first.Profile));
            var restored = D3D12GeneratedStyleMaterialPreparation.Restore(first.SourceRest, decoded, first.ManifestBytes);
            Assert.Equal(first.ManifestBytes, restored.ManifestBytes);
            using var manifest = JsonDocument.Parse(first.ManifestBytes);
            var map = manifest.RootElement.GetProperty("materials")[0].GetProperty("outputs")[0];
            string name = map.GetProperty("name").GetString()!;
            int ordinal = map.GetProperty("guard").GetProperty("sourceSceneTextureOrdinal").GetInt32();
            var original = first.SourceRest.Surface.Snapshot.Textures[ordinal];
            byte[] bytes = original.Rgba.ToArray(); bytes[0] ^= 127;
            var edited = restored.WithEditedMap(0, name, new("explicit edited generated input", original.Width, original.Height, bytes));
            Assert.NotEqual(first.ManifestBytes, edited.ManifestBytes);
            Assert.Equal(first.ManifestBytes, restored.ManifestBytes);
            Assert.Equal(edited.ManifestBytes, D3D12GeneratedStyleMaterialPreparation.Restore(edited.SourceRest, edited.Profile, edited.ManifestBytes).ManifestBytes);
            var mmd = D3D12MaterialBindingProfileResolver.Resolve(edited.Profile, edited.SourceRest, Guid.NewGuid(), "mmd");
            Assert.Empty(mmd.StarMcBodyBindings); Assert.Empty(mmd.EndfieldMatcapBindings);
            Assert.Empty(mmd.GenshinHairAtlasBindings); Assert.Empty(mmd.GenshinMetallicBindings);
            Assert.Equal(new byte[] { 80, 120, 160, 255 }, source.Surface.Snapshot.Textures[0].Rgba.ToArray());
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("slot")]
    [InlineData("outputHash")]
    [InlineData("duplicate")]
    [InlineData("profile")]
    [InlineData("unknown")]
    [InlineData("boundary")]
    [InlineData("sourceDimensions")]
    public void RestoreRejectsMismatchedProvenance(string corruption)
    {
        var prepared = Prepare(Source(), "wuthering-waves", D3D12GeneratedPmxSurfaceKind.Cloth);
        var root = JsonNode.Parse(prepared.ManifestBytes)!;
        var entry = root["materials"]![0]!;
        switch (corruption)
        {
            case "source": entry["sourceRgbaSha256"] = new string('0', 64); break;
            case "slot": entry["outputs"]![0]!["guard"]!["sourceSceneTextureOrdinal"] = 0; break;
            case "outputHash": entry["outputs"]![0]!["guard"]!["rgbaSha256"] = new string('0', 64); break;
            case "duplicate": entry["outputs"]![1] = entry["outputs"]![0]!.DeepClone(); break;
            case "profile": root["profileStableId"] = "wrong-profile"; break;
            case "unknown": root["unknown"] = true; break;
            case "boundary": root["firstGeneratedOrdinal"] = 0; break;
            case "sourceDimensions": entry["sourceWidth"] = 2; break;
        }
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedStyleMaterialPreparation.Restore(
            prepared.SourceRest, prepared.Profile, System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void SameOriginalBytesWithDifferentImageDimensionsAreNotTheSameSource()
    {
        var original = Source(); var snapshot = original.Surface.Snapshot;
        byte[] rgba = [80,120,160,255, 40,90,130,255];
        OriginalFrameScene Frame(int width, int height) => new(new(new SceneSnapshot(snapshot.Vertices, snapshot.Indices,
            snapshot.Materials, [new SceneTexture("original", width, height, rgba)]), original.Surface.Materials));
        var prepared = Prepare(Frame(2,1), "star-rail", D3D12GeneratedPmxSurfaceKind.Cloth);
        var derived = prepared.SourceRest.Surface.Snapshot;
        var reshaped = new OriginalFrameScene(new(new SceneSnapshot(derived.Vertices, derived.Indices, derived.Materials,
            [new SceneTexture("reshaped original",1,2,rgba), ..derived.Textures.Skip(1)]), prepared.SourceRest.Surface.Materials));
        Assert.Throws<InvalidDataException>(() => D3D12GeneratedStyleMaterialPreparation.Restore(reshaped, prepared.Profile, prepared.ManifestBytes));
    }

    [Fact]
    public void SelectionBudgetsCancellationAndEditedImageDimensionsAreValidated()
    {
        var source = Source();
        Assert.Throws<ArgumentException>(() => D3D12GeneratedStyleMaterialPreparation.Prepare(source, [], "mmd", "generated", "Generated", TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => D3D12GeneratedStyleMaterialPreparation.Prepare(source,
            [new(0, new()), new(0, new())], "star-rail", "generated", "Generated", TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => D3D12GeneratedStyleMaterialPreparation.Prepare(source,
            [new(0, new())], "star-rail", "generated", "Generated", canceled.Token));
        var prepared = Prepare(source, "endfield", D3D12GeneratedPmxSurfaceKind.Cloth);
        Assert.Throws<ArgumentException>(() => prepared.WithEditedMap(0, "normal", new("wrong size", 1, 1, [128,128,255,255])));
        Assert.Throws<ArgumentException>(() => prepared.WithEditedMap(0, "original", source.Surface.Snapshot.Textures[0]));
    }

    internal static D3D12GeneratedStyleMaterialResult Prepare(OriginalFrameScene source, string pipeline, D3D12GeneratedPmxSurfaceKind kind)
        => D3D12GeneratedStyleMaterialPreparation.Prepare(source, [new(0, new(kind, 16))], pipeline,
            "generated-" + pipeline, "Generated " + pipeline, TestContext.Current.CancellationToken);

    internal static OriginalFrameScene Source()
    {
        SceneVertex[] vertices = [new(new(-.8f,-.8f,0),-Vector3.UnitZ,Vector2.Zero),
            new(new(.8f,-.8f,0),-Vector3.UnitZ,Vector2.UnitX), new(new(.8f,.8f,0),-Vector3.UnitZ,Vector2.One),
            new(new(-.8f,.8f,0),-Vector3.UnitZ,Vector2.UnitY)];
        return new(new(new SceneSnapshot(vertices, [0,2,1,0,3,2],
            [new("explicit",0,6,new(.5f,.4f,.3f,1),Vector3.Zero,new(.1f),10,0)],
            [new("original",1,1,[80,120,160,255])], lightDirection: -Vector3.UnitZ), [new(new(.6f,.65f,.75f))]));
    }
}
