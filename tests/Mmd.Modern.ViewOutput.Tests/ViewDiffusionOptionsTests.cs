using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewDiffusionOptionsTests
{
    [Fact]
    public void DefaultIsOffAndDoesNotMutateOriginalFrameOptions()
    {
        var quality = new ViewQualityOptions();
        Assert.False(quality.EffectiveDiffusion.Enabled);
        Assert.Equal(.7f, quality.EffectiveDiffusion.Strength);
        Assert.Equal(.002f, quality.EffectiveDiffusion.Extent);
        var source = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        var active = quality with { Diffusion = new(true, .4f, .003f) };
        Assert.Equal(quality.ApplyTo(source), active.ApplyTo(source));
        Assert.False(quality.EffectiveDiffusion.Enabled);
    }

    [Theory]
    [InlineData(false, float.NaN, .002f)]
    [InlineData(true, float.PositiveInfinity, .002f)]
    [InlineData(false, -.01f, .002f)]
    [InlineData(true, 1.01f, .002f)]
    [InlineData(false, .7f, float.NaN)]
    [InlineData(true, .7f, float.NegativeInfinity)]
    [InlineData(false, .7f, -.001f)]
    [InlineData(true, .7f, .01001f)]
    public void DisabledSettingsStillRejectInvalidStoredNumbers(bool enabled, float strength, float extent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions(Diffusion: new(enabled, strength, extent)).Validate());

    [Theory]
    [InlineData(false, 0f, 0f)]
    [InlineData(true, 0f, .01f)]
    [InlineData(true, 1f, 0f)]
    [InlineData(true, 1f, .01f)]
    public void BoundaryValuesAreValid(bool enabled, float strength, float extent)
        => new ViewQualityOptions(Diffusion: new(enabled, strength, extent)).Validate();

    [Fact]
    public void LocalCodecKeepsIndependentPreviewAndExportAndOlderMissingField()
    {
        var preview = new ViewRenderRequest(97, 73, new(Diffusion: new(true, .25f, .001f)));
        var export = new ViewExportPreset(193, 145, new(60000, 1001), new(Diffusion: new(true, .8f, .004f)));
        var definition = new ViewOutputPresetDefinition("custom-diffusion", "Diffusion", preview, export);
        byte[] bytes = ViewOutputPresetJson.Serialize(definition);
        var read = ViewOutputPresetJson.Deserialize(bytes);
        Assert.Equal(preview, read.Preview); Assert.Equal(export, read.Export);
        Assert.NotSame(preview.Quality!.Diffusion, definition.Preview.Quality!.Diffusion);
        Assert.Equal(preview.Quality.Diffusion, definition.Preview.Quality.Diffusion);
        var request = read.Export.CreateRequest(12, 14);
        Assert.Equal(export.Quality!.Diffusion, request.Render.EffectiveQuality.Diffusion);
        var old = JsonNode.Parse(bytes)!.AsObject();
        old["preview"]!["quality"]!.AsObject().Remove("diffusion");
        old["export"]!["quality"]!.AsObject().Remove("diffusion");
        var legacy = ViewOutputPresetJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(old.ToJsonString()));
        Assert.False(legacy.Preview.EffectiveQuality.EffectiveDiffusion.Enabled);
        Assert.False(legacy.Export.Quality!.EffectiveDiffusion.Enabled);
        var legacyJson = JsonNode.Parse(ViewOutputPresetJson.Serialize(legacy))!.AsObject();
        Assert.False(legacyJson["preview"]!["quality"]!.AsObject().ContainsKey("diffusion"));
        Assert.False(legacyJson["export"]!["quality"]!.AsObject().ContainsKey("diffusion"));
    }

    [Fact]
    public void CodecRejectsUnknownOrOutOfRangePostFilterData()
    {
        var definition = new ViewOutputPresetDefinition("custom-diffusion", "Diffusion",
            new(97, 73, new(Diffusion: new(true))), new(97, 73, new(30)));
        var node = JsonNode.Parse(ViewOutputPresetJson.Serialize(definition))!.AsObject();
        node["preview"]!["quality"]!["diffusion"]!["strength"] = 2;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
        node["preview"]!["quality"]!["diffusion"]!["strength"] = .7f;
        node["preview"]!["quality"]!["diffusion"]!["mmeExecutor"] = true;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void ExistingCreateOnlyLocalStoreRoundTripsFilterWithoutProjectData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-diffusion-preset-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ViewOutputPresetStore(directory);
            var saved = store.SaveAsNew("Native post filter", new(97, 73, new(Diffusion: new(true))),
                new(193, 145, new(60), new(Diffusion: new(true, .3f, .005f))), cancellationToken: TestContext.Current.CancellationToken);
            var reopened = new ViewOutputPresetStore(directory).Find(saved.StableId, TestContext.Current.CancellationToken);
            var definition = Assert.IsType<ViewOutputPresetDefinition>(reopened.Definition);
            Assert.Equal(saved.Preview, definition.Preview); Assert.Equal(saved.Export, definition.Export);
            var copy = store.SaveAsNew(definition, "Independent copy", TestContext.Current.CancellationToken);
            Assert.NotEqual(saved.StableId, copy.StableId);
            Assert.Equal(saved.Preview, store.Find(saved.StableId, TestContext.Current.CancellationToken).Definition!.Preview);
        }
        finally
        {
            // Only files created in this uniquely allocated test directory are removed.
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)) File.Delete(path);
                Directory.Delete(directory, recursive: false);
            }
        }
    }
}
