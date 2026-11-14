using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class CaptureMemorySettingsTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Root => "G:/MDA-Work/settings/20261113/memory652";

    [Fact]
    public async Task DialogChoicesReachRequestsAndMachineLocalPreferencesWithoutChangingQuality() => await session.Dispatch(() =>
    {
        Directory.CreateDirectory(Root);
        var model = new OutputSettingsViewModel();
        var dialog = new OutputSettingsWindow(model, queryGiCapabilities: _ => Task.FromResult(new EditorPreviewCapabilities()));
        dialog.Show();
        dialog.FindControl<TabControl>("OutputTabs")!.SelectedIndex = 1;
        string languageBefore = EditorLocalization.CurrentLanguage;
        string path = Path.Combine(Root, "preferences-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var box = Assert.IsType<ComboBox>(dialog.FindControl<ComboBox>("OutputCaptureMemoryBox"));
            Assert.Equal(0, box.SelectedIndex);
            Assert.True(box.IsVisible);
            Assert.True(model.TryCreate(out var baseline), model.ValidationMessage);
            foreach (var mode in Enum.GetValues<ViewCaptureMemoryMode>())
            {
                box.SelectedIndex = (int)mode;
                Dispatcher.UIThread.RunJobs();
                Assert.True(model.TryCreate(out var draft), model.ValidationMessage);
                Assert.Equal(mode, draft!.Request.CaptureMemory);
                Assert.Equal(mode, draft.Request.Render.CaptureMemory);
                Assert.Equal(mode, draft.Preset.Export.CaptureMemory);
                var before = baseline!.Request.Plan;
                var after = draft.Request.Plan;
                Assert.Equal((before.Width, before.Height, before.FrameRate, before.StartFrame, before.EndFrame,
                    before.PhysicsPreRollFrames, before.IncludeEndFrame, before.FrameCount),
                    (after.Width, after.Height, after.FrameRate, after.StartFrame, after.EndFrame,
                    after.PhysicsPreRollFrames, after.IncludeEndFrame, after.FrameCount));
                Assert.Equal(baseline.Request.Quality, draft.Request.Quality);
                EditorPreferences.SaveOutputSettings(draft, path);
                var restored = EditorPreferences.LoadOutputSettings(out var diagnostic, path);
                Assert.Null(diagnostic);
                Assert.Equal(mode, restored!.Request.CaptureMemory);
                Assert.Equal(mode, restored.Preset.Export.CaptureMemory);
                Assert.Equal((int)mode, new OutputSettingsViewModel(restored).CaptureMemoryIndex);
            }
            string[] existingChoiceControls = ["MediaKindBox", "AntialiasingBox", "SmaaQualityBox", "SpatialSamplesBox", "OutputGiModeBox",
                "OutputGiCompositionBox", "OutputGiDenoiserBox", "EncoderBox", "RateControlBox", "VideoSpeedBox", "VideoProfileBox", "VideoLevelBox"];
            var retainedChoices = existingChoiceControls.Select(name => Assert.IsType<ComboBox>(dialog.FindControl<ComboBox>(name)))
                .Select(control => (Control: control, Index: control.SelectedIndex)).ToArray();
            foreach (string language in new[] { "en", "ja", "zh-CN" })
            {
                EditorLocalization.SetLanguage(language);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, box.SelectedIndex);
                Assert.DoesNotContain("Mda.Text.", model.CaptureMemoryLabel);
                Assert.DoesNotContain("Mda.Text.", model.CaptureMemoryHint);
                foreach (var choice in retainedChoices) Assert.Equal(choice.Index, choice.Control.SelectedIndex);
                Assert.True(model.TryCreate(out var localized), model.ValidationMessage);
                Assert.Equal(baseline!.Request.Quality, localized!.Request.Quality);
            }
            // Real CPU Skia rendering, no native window or GPU device.
            var settled = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) => settled.Continue = false;
            timer.Start();
            try { Dispatcher.UIThread.PushFrame(settled); } finally { timer.Stop(); }
            dialog.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96));
            bitmap.Render(dialog);
            bitmap.Save(Path.Combine(Root, "capture-memory-dialog.png"));
        }
        finally
        {
            EditorLocalization.SetLanguage(languageBefore);
            dialog.Close();
            if (File.Exists(path)) File.Delete(path);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(20), Token);

    [Fact]
    public async Task ClassicDoesNotOfferOrSilentlyApplyModernMemoryPolicies() => await session.Dispatch(() =>
    {
        var model = new OutputSettingsViewModel(supportsCaptureMemory: false);
        var dialog = new OutputSettingsWindow(model, queryGiCapabilities: _ => Task.FromResult(new EditorPreviewCapabilities()));
        dialog.Show();
        dialog.FindControl<TabControl>("OutputTabs")!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(model.SupportsCaptureMemory);
            Assert.False(dialog.FindControl<ComboBox>("OutputCaptureMemoryBox")!.IsEffectivelyVisible);
            Assert.True(model.TryCreate(out var draft), model.ValidationMessage);
            Assert.Equal(ViewCaptureMemoryMode.Auto, draft!.Request.CaptureMemory);
            model.CaptureMemoryIndex = 2;
            Assert.False(model.TryCreate(out _));
            Assert.DoesNotContain("Mda.Text.", model.ValidationMessage);
        }
        finally { dialog.Close(); }
        return true;
    }, Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
