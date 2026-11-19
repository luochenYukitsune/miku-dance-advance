using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPreviewSettingsAsyncTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpensImmediatelyWhileProbingAndAppliesNonCapabilityChangesWithoutWaiting() => await session.Dispatch(() =>
    {
        var gate = new TaskCompletionSource<EditorPreviewCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new EditorPreviewSettingsViewModel(EditorPreferences.DefaultPreviewQuality, capabilities: null, runtime: null, capabilitiesPending: true);
        var dialog = new EditorPreviewSettingsWindow(vm, (_, _) => gate.Task);
        object? closedResult = null;
        dialog.Closed += (_, _) => closedResult = true;
        bool applyClicked = false;

        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsProbingCapabilities);
            Assert.False(vm.CapabilitiesReady);
            Assert.False(vm.NeedsCapabilitiesForApply);
            Assert.True(dialog.FindControl<TextBlock>("PreviewCapabilityProbingNotice")!.IsVisible);

            vm.SurfaceOrderIndex = 1;
            vm.DiffusionEnabled = !vm.DiffusionEnabled;
            Assert.False(vm.NeedsCapabilitiesForApply);
            Assert.True(vm.TryCreate(out var immediate), vm.ValidationMessage);
            Assert.Equal(SurfaceDrawOrder.Original, immediate!.SurfaceOrder);

            var apply = dialog.FindControl<Button>("ApplyPreviewSettingsButton")!;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            applyClicked = closedResult is true;
            Assert.True(applyClicked);
        }
        finally
        {
            gate.TrySetCanceled();
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task BackgroundProbeAndRefreshPopulateCapabilitiesWhilePreservingEdits() => await session.Dispatch(async () =>
    {
        var initialGate = new TaskCompletionSource<EditorPreviewCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshGate = new TaskCompletionSource<EditorPreviewCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<bool>();
        var vm = new EditorPreviewSettingsViewModel(EditorPreferences.DefaultPreviewQuality, capabilities: null, runtime: null, capabilitiesPending: true);
        var dialog = new EditorPreviewSettingsWindow(vm, (forceRefresh, _) =>
        {
            calls.Add(forceRefresh);
            return calls.Count == 1 ? initialGate.Task : refreshGate.Task;
        });

        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([false], calls);
            var refreshButton = dialog.FindControl<Button>("RefreshCapabilitiesButton")!;
            Assert.False(refreshButton.IsEnabled);

            bool editedDiffusion = !vm.DiffusionEnabled;
            vm.DiffusionEnabled = editedDiffusion;
            vm.SurfaceOrderIndex = 1;

            initialGate.SetResult(new EditorPreviewCapabilities(
                Gi: new(false, true, 65, 11, true, true, true, true, true, null),
                SpatialUpscalingAvailable: true,
                Fsr: new(D3D12FsrRuntimeStatus.ContextCreated, "mock-fsr", "Mock Adapter", null, Array.Empty<D3D12FsrProviderProbe>(), null, "ok")));

            await UntilAsync(() => vm.CapabilitiesReady);
            Assert.True(refreshButton.IsEnabled);
            Assert.Equal(editedDiffusion, vm.DiffusionEnabled);
            Assert.Equal(1, vm.SurfaceOrderIndex);
            Assert.True(vm.GiModes.Count >= 3);
            Assert.Contains(vm.TemporalModes, mode => mode.Contains("FSR", StringComparison.Ordinal));

            refreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([false, true], calls);
            Assert.True(vm.IsProbingCapabilities);
            Assert.False(refreshButton.IsEnabled);

            refreshGate.SetResult(new EditorPreviewCapabilities(
                Gi: new(false, true, 65, 11, true, true, true, true, false, null),
                SpatialUpscalingAvailable: true));

            await UntilAsync(() => vm.CapabilitiesReady);
            Assert.True(refreshButton.IsEnabled);
            Assert.Equal(2, vm.GiModes.Count);
        }
        finally
        {
            initialGate.TrySetCanceled();
            refreshGate.TrySetCanceled();
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 60 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(15, Token);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
