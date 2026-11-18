using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class SurfaceDrawOrderSettingsTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DefaultsAndStrictPresetRoundTrip()
    {
        Assert.Equal(SurfaceDrawOrder.OpaqueFirst, new ViewQualityOptions().SurfaceOrder);
        Assert.Equal(SurfaceDrawOrder.OpaqueFirst, new ViewQualityOptions().ApplyTo(null).SurfaceOrder);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions(SurfaceOrder: (SurfaceDrawOrder)99).Validate());
        var preset = new ViewOutputPresetDefinition("surface-order", "Surface order",
            new(97, 73, new(SurfaceOrder: SurfaceDrawOrder.Original)),
            new(193, 145, new(30), new(SurfaceOrder: SurfaceDrawOrder.Original)));
        var bytes = ViewOutputPresetJson.Serialize(preset);
        var read = ViewOutputPresetJson.Deserialize(bytes);
        Assert.Equal(SurfaceDrawOrder.Original, read.Preview.EffectiveQuality.SurfaceOrder);
        Assert.Equal(SurfaceDrawOrder.Original, read.Export.CreateRequest(0, 1).Render.EffectiveQuality.SurfaceOrder);
        var json = JsonNode.Parse(bytes)!.AsObject();
        json["preview"]!["quality"]!.AsObject().Remove("surfaceOrder");
        json["export"]!["quality"]!.AsObject().Remove("surfaceOrder");
        var legacy = ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Equal(SurfaceDrawOrder.OpaqueFirst, legacy.Preview.EffectiveQuality.SurfaceOrder);
        Assert.Equal(SurfaceDrawOrder.OpaqueFirst, legacy.Export.Quality!.SurfaceOrder);
        json["preview"]!["quality"]!["surfaceOrder"] = "unknown";
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
        json["preview"]!["quality"]!["surfaceOrder"] = 1;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void CompatibleCharacterExportKeepsTheExplicitDrawOrder()
    {
        var request = new ExportRequest(new Mmd.Modern.Application.OfflineFramePlan(32, 32, new(60), 0, 0),
            new(Gi: new RayGiOptions(LightingMode.HardwareRayTracing, Composition: RayGiComposition.Additive),
                SurfaceOrder: SurfaceDrawOrder.Original));
        var effective = EditorExportService.BuildExportRequest(request, hasPaletteReceiver: true);
        Assert.Equal(RayGiComposition.ToonPalette, effective.Quality!.EffectiveGi.Composition);
        Assert.Equal(SurfaceDrawOrder.Original, effective.Quality.SurfaceOrder);
        Assert.Equal(RayGiComposition.Additive, request.Quality!.EffectiveGi.Composition);
    }

    [Fact]
    public async Task DialogSelectionsReachIndependentPreviewAndExportAndLocalPreferences() => await session.Dispatch(() =>
    {
        var preview = new EditorPreviewSettingsViewModel();
        var previewDialog = new EditorPreviewSettingsWindow(preview);
        var output = new OutputSettingsViewModel();
        var outputDialog = new OutputSettingsWindow(output);
        previewDialog.Show(); outputDialog.Show();
        try
        {
            var previewBox = Assert.IsType<ComboBox>(previewDialog.FindControl<ComboBox>("PreviewSurfaceOrderBox"));
            var outputBox = Assert.IsType<ComboBox>(outputDialog.FindControl<ComboBox>("OutputSurfaceOrderBox"));
            Assert.Equal(0, previewBox.SelectedIndex); Assert.Equal(0, outputBox.SelectedIndex);
            previewBox.SelectedIndex = 1; outputBox.SelectedIndex = 1;
            preview.SpatialIndex = 2; output.SpatialIndex = 2;
            string languageBefore = EditorLocalization.CurrentLanguage;
            try
            {
                foreach (string language in new[] { "en", "ja", "zh-CN" })
                {
                    EditorLocalization.SetLanguage(language);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(1, previewBox.SelectedIndex); Assert.Equal(1, outputBox.SelectedIndex);
                    Assert.DoesNotContain("Mda.Text.", preview.SurfaceOrderLabel);
                    Assert.DoesNotContain("Mda.Text.", output.SurfaceOrderLabel);
                }
            }
            finally { EditorLocalization.SetLanguage(languageBefore); }
            Assert.True(preview.TryCreate(out var previewQuality), preview.ValidationMessage);
            Assert.True(output.TryCreate(out var draft), output.ValidationMessage);
            Assert.Equal(SurfaceDrawOrder.Original, previewQuality!.SurfaceOrder);
            Assert.Equal(SurfaceDrawOrder.Original, draft!.Request.Render.EffectiveQuality.SurfaceOrder);
            Assert.Equal(SurfaceDrawOrder.Original, draft.Preset.Export.Quality!.SurfaceOrder);
            Assert.Equal(SurfaceDrawOrder.OpaqueFirst, draft.Preset.Preview.EffectiveQuality.SurfaceOrder);
            Assert.Equal(2, previewQuality.SpatialSamples); Assert.Equal(2, draft.Request.Quality!.SpatialSamples);
            Assert.Equal(1, new EditorPreviewSettingsViewModel(previewQuality).SurfaceOrderIndex);
            Assert.Equal(1, new OutputSettingsViewModel(draft).SurfaceOrderIndex);
            string path = Path.Combine(Path.GetTempPath(), "mda-surface-order-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                EditorPreferences.SavePreviewQuality(previewQuality, path);
                Assert.Equal(SurfaceDrawOrder.Original, EditorPreferences.LoadPreviewQuality(path).SurfaceOrder);
                EditorPreferences.SaveOutputSettings(draft, path);
                var restored = EditorPreferences.LoadOutputSettings(out var diagnostic, path);
                Assert.Null(diagnostic); Assert.Equal(SurfaceDrawOrder.Original, restored!.Request.Quality!.SurfaceOrder);
            }
            finally { File.Delete(path); }
        }
        finally { previewDialog.Close(); outputDialog.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
