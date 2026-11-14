using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CaptureMemoryContractTests
{
    [Fact]
    public void DefaultsAndLegacySchemaOneKeepAutomaticCaptureMemory()
    {
        Assert.Equal(ViewCaptureMemoryMode.Auto, new ViewRenderRequest(97, 73).CaptureMemory);
        Assert.Equal(ViewCaptureMemoryMode.Auto, new ExportRequest(new(193, 145, new(30), 0, 0)).CaptureMemory);
        Assert.Equal(ViewCaptureMemoryMode.Auto, new ViewExportPreset(193, 145, new(30)).CaptureMemory);
        const string legacyJson = """
            {"format":"mmd-modern-view-output-preset","schemaVersion":1,"stableId":"legacy-memory","name":"Legacy",
             "preview":{"width":97,"height":73,"quality":null},
             "export":{"width":193,"height":145,"frameRate":{"numerator":30,"denominator":1},"quality":null},
             "encoding":null}
            """;
        var legacy = ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(legacyJson));
        Assert.Equal(1, legacy.SchemaVersion);
        Assert.Equal(ViewCaptureMemoryMode.Auto, legacy.Preview.CaptureMemory);
        Assert.Equal(ViewCaptureMemoryMode.Auto, legacy.Export.CaptureMemory);
        Assert.Equal(ViewCaptureMemoryMode.Auto, legacy.Export.CreateRequest(0, 1).Render.CaptureMemory);
    }

    [Theory]
    [InlineData(ViewCaptureMemoryMode.Auto, D3D12CaptureMemoryMode.Auto)]
    [InlineData(ViewCaptureMemoryMode.PreferSpeed, D3D12CaptureMemoryMode.Default)]
    [InlineData(ViewCaptureMemoryMode.LowMemory, D3D12CaptureMemoryMode.LowMemory)]
    public void SavedExportPolicyReachesCaptureWithoutChangingQualityOrFramePlan(
        ViewCaptureMemoryMode mode, D3D12CaptureMemoryMode expectedBackend)
    {
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X,
            SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 4, Diffusion: new(true, .6f, .003f));
        var preset = new ViewOutputPresetDefinition("capture-memory", "Capture memory",
            new(97, 73, CaptureMemory: ViewCaptureMemoryMode.LowMemory),
            new(193, 145, new(60000, 1001), quality, mode));
        var restored = ViewOutputPresetJson.Deserialize(ViewOutputPresetJson.Serialize(preset));
        Assert.Equal(preset.Preview, restored.Preview);
        Assert.Equal(preset.Export, restored.Export);
        var request = restored.Export.CreateRequest(12, 24, 4, includeEndFrame: false);
        request.Validate();
        Assert.Equal(mode, request.CaptureMemory);
        Assert.Equal(mode, request.Render.CaptureMemory);
        Assert.Equal(expectedBackend, D3D12ViewCaptureMemory.Map(request.Render.CaptureMemory).Mode);
        Assert.Equal(quality, request.Render.EffectiveQuality);
        Assert.Equal(193, request.Render.Width); Assert.Equal(145, request.Render.Height);
        Assert.Equal(new OfflineFrameRate(60000, 1001), request.Plan.FrameRate);
        Assert.Equal(12u, request.Plan.StartFrame); Assert.Equal(24u, request.Plan.EndFrame);
        Assert.Equal(4u, request.Plan.PhysicsPreRollFrames); Assert.False(request.Plan.IncludeEndFrame);
    }

    [Fact]
    public void UnknownPoliciesAreRejectedAtEachPublicContract()
    {
        const ViewCaptureMemoryMode unknown = (ViewCaptureMemoryMode)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewRenderRequest(97, 73, CaptureMemory: unknown).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportRequest(new(193, 145, new(30), 0, 0), CaptureMemory: unknown).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewExportPreset(193, 145, new(30), CaptureMemory: unknown).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12ViewCaptureMemory.Map(unknown));
    }

    [Theory]
    [InlineData("preview", "\"unknown\"")]
    [InlineData("export", "\"preferSpeed\"")]
    [InlineData("preview", "0")]
    [InlineData("export", "0")]
    public void PresetCaptureMemoryRequiresAnExactKnownName(string section, string value)
    {
        var preset = new ViewOutputPresetDefinition("capture-memory", "Capture memory",
            new(97, 73), new(193, 145, new(30)));
        var json = JsonNode.Parse(ViewOutputPresetJson.Serialize(preset))!.AsObject();
        json[section]!["captureMemory"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
}
