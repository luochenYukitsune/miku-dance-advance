using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightHelperUiTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task ToggleAndLanguageRefreshStayViewLocalAndLeavingLightPageRestoresPreview() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        string language = EditorLocalization.CurrentLanguage;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
            shell.Workspace.AddModel(path, asset, ModelRole.Character);
            var light = new NativeSceneLight(Guid.NewGuid(), "helper", SceneLightKind.Spot,
                Vector3.Zero, Quaternion.Identity, Vector3.One);
            shell.Workspace.EditLighting(new(new([light])));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var toggle = window.FindControl<ToggleButton>("LightHelpersToggle")!;
            Assert.NotNull(toggle); Assert.True(toggle.IsEffectivelyVisible); Assert.True(toggle.IsChecked);
            var controller = (EditorLightViewportController)typeof(EditorWindow)
                .GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var authored = shell.Workspace.Session!.Lighting;
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(controller.HelpersVisible); Assert.Same(authored, shell.Workspace.Session.Lighting);
            foreach (var (locale, label) in new[] { ("zh-CN", "灯光辅助"), ("en", "Light helpers"), ("ja", "ライト補助") })
            {
                EditorLocalization.SetLanguage(locale); window.Width = 1100; window.Height = 720;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.Equal(label, toggle.Content); Assert.False(toggle.IsChecked); Assert.True(toggle.Bounds.Width > 0);
                string? evidence = Environment.GetEnvironmentVariable("MDA_LIGHTING_LAYOUT_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    using var bitmap = HeadlessTestHost.CaptureLayoutFrame(window); Assert.NotNull(bitmap);
                    bitmap.Save(Path.Combine(evidence, "light-helpers-minimum-" + locale + ".png"));
                }
            }
            var quality = new ViewQualityOptions(Gi: new(Mode: LightingMode.SoftwareRayTracing));
            viewport.Quality = quality;
            Assert.True(controller.SetParameterPreview(new(new([light with { Intensity = 2 }]), light.Id)));
            Assert.True(controller.IsInteracting);
            Assert.False(viewport.EffectivePreviewQuality.EffectiveGi.Enabled);
            Assert.Same(quality, viewport.Quality);
            shell.ShowInspectorPage(EditorInspectorPage.Animation);
            Assert.False(controller.IsInteracting);
            Assert.Same(quality, viewport.EffectivePreviewQuality);
            Assert.Same(authored, shell.Workspace.Session.Lighting);
            Assert.True(toggle.IsEffectivelyVisible);
            return true;
        }
        finally
        {
            EditorLocalization.SetLanguage(language);
            typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainLightDragRestoresPreviewQualityOnReleaseOrCancel(bool cancel) => await session.Dispatch(() =>
    {
        using var host = new EditorViewportHost();
        var quality = new ViewQualityOptions(Gi: new(Mode: LightingMode.SoftwareRayTracing));
        host.Quality = quality;
        var main = new EditorMainLightViewport(new(), () => true, host.RequestRender);
        host.MainLightViewport = main;
        var camera = SceneCamera.Default;
        var frame = main.Decorate(new PreviewFrame(800, 600, 0,
            new(new(SceneSnapshot.CreateEmpty(camera), [])), null, null, null), camera);
        main.FramePresented(frame);
        var source = frame.Scene.EditorOverlay!.Lines.First(line => line.Shape == ViewportOverlayShape.Circle).Start * new Vector2(800, 600);
        Assert.True(main.TryBegin(source, camera, 800, 600));
        main.Move(source + Vector2.UnitX);
        Assert.False(host.EffectivePreviewQuality.EffectiveGi.Enabled);
        Assert.Same(quality, host.Quality);
        if (cancel) main.Cancel();
        else main.End(source + Vector2.UnitX);
        Assert.Same(quality, host.EffectivePreviewQuality);
        return true;
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
