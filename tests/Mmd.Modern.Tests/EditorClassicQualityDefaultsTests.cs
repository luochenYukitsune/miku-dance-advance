using Avalonia.Controls;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorClassicQualityDefaultsTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));

    [Fact]
    public async Task SelectedClassicStartsWithValidPreviewAndExportWhileExplicitUnsupportedQualityIsRejected()
        => await session.Dispatch(() =>
        {
            string directory = Path.Combine("G:/MDA-Work/compat/20261112/quality623/tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string[] paths = ["Mmd.Modern.D3D9.Compatibility.dll", "d3dx9_43.dll", "d3dcompiler_43.dll"];
                foreach (string name in paths) File.WriteAllBytes(Path.Combine(directory, name), [1]);
                var engine = new EditorClassicRenderEngine(new(Path.Combine(directory, paths[0]),
                    Path.Combine(directory, paths[1]), Path.Combine(directory, paths[2])), directory);
                using var shell = new EditorShellViewModel(new PmmCameraPose(new(0, 10, 0), default, -45, 30, true), engine);
                var window = new EditorWindow(false) { DataContext = shell };
                var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
                try
                {
                    Assert.Equal(FrameAntialiasing.Off, viewport.Quality.Antialiasing);
                    Assert.Equal(1, viewport.Quality.SpatialSamples);
                    Assert.Equal(SurfaceDrawOrder.Original, viewport.Quality.SurfaceOrder);
                    Assert.True(shell.CreateOutputSettingsViewModel().TryCreate(out var output));
                    Assert.Equal(FrameAntialiasing.Off, output!.Request.Quality!.Antialiasing);
                    Assert.Equal(SurfaceDrawOrder.Original, output.Request.Quality.SurfaceOrder);
                    Assert.Same(output.Request, engine.EmptyBinding.PrepareExport(output.Request));

                    var requested = viewport.Quality with { Gi = new(LightingMode.HardwareRayTracing) };
                    viewport.Quality = requested;
                    Assert.Same(requested, viewport.Quality);
                    var explicitExport = output.Request with { Quality = requested };
                    Assert.Throws<NotSupportedException>(() => engine.EmptyBinding.PrepareExport(explicitExport));
                    Assert.True(explicitExport.Quality!.EffectiveGi.Enabled);

                    viewport.SetRenderEngine(EditorModernRenderEngine.Instance);
                    Assert.Equal(EditorPreferences.DefaultPreviewQuality, viewport.Quality);
                    using var modern = new EditorShellViewModel();
                    Assert.True(modern.CreateOutputSettingsViewModel().TryCreate(out var modernOutput));
                    Assert.Equal(SurfaceDrawOrder.OpaqueFirst, modernOutput!.Request.Quality!.SurfaceOrder);
                }
                finally { window.DataContext = null; viewport.Dispose(); window.Close(); }
            }
            finally { Directory.Delete(directory, recursive: true); }
            return true;
        }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
