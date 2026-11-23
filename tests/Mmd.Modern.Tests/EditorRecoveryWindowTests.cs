using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRecoveryWindowTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NormalCloseCompletesDeferredOffersWithoutResolvingInterruptedHistoryAndCanceledCloseKeepsOffers() => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-deferred-close-" + Guid.NewGuid().ToString("N"));
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "recovery"), AutoSaveEnabled = false };
        var store = new EditorRecoveryStore(preferences.RecoveryDirectory);
        using var json = JsonDocument.Parse("{}");
        async Task<EditorRecoveryCheckpoint> WriteInterruptedSessionAsync(string name)
        {
            using var lease = await store.CreateSessionAsync(null, name, Token);
            return await store.WriteCheckpointAsync(lease, new(null, name, 1, json.RootElement),
                (path, token) => File.WriteAllTextAsync(path, name, token), Token);
        }
        var offered = await WriteInterruptedSessionAsync("offered");
        var shell = new EditorShellViewModel();
        var window = new EditorWindow(false, preferences) { DataContext = shell };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!; viewport.IsSuspended = true; viewport.IsVisible = false;
        var operationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Show(); Task review = window.ReviewRecoveryAsync();
            var limit = DateTime.UtcNow.AddSeconds(15); Window? picker = null;
            while (picker is null && DateTime.UtcNow < limit)
            {
                await Task.Delay(20, Token); Dispatcher.UIThread.RunJobs();
                picker = window.OwnedWindows.SingleOrDefault(child => child is EditorRecoveryWindow);
            }
            Assert.NotNull(picker);
            picker.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DeferRecoveryButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await review.WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.Equal(offered.SessionId, Assert.Single(await store.ListRecoverableAsync(Token)).SessionId);
            // A close rejected by the editor must not acknowledge the earlier interrupted run.
            var operation = shell.RunOperationAsync("busy", () => operationRelease.Task);
            window.Close(); Assert.True(window.IsVisible);
            Assert.Equal(offered.SessionId, Assert.Single(await store.ListRecoverableAsync(Token)).SessionId);
            Assert.Equal(offered.SessionId, Assert.Single((await EditorRecoveryDiscovery.ScanAsync(preferences, Token)).Sessions).SessionId);
            operationRelease.SetResult(); await operation;
            // This session did not appear in the offer and belongs to another interrupted run.
            var unseen = await WriteInterruptedSessionAsync("unseen");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.Equal(2, (await store.ListRecoverableAsync(Token)).Count);
            Assert.Equal(offered.CheckpointId, (await store.GetLatestValidCheckpointAsync(offered.SessionId, Token))!.CheckpointId);
            Assert.Equal(unseen.SessionId, Assert.Single((await EditorRecoveryDiscovery.ScanAsync(preferences, Token)).Sessions).SessionId);
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(offered.PackagePath)!, "session.json"), Token));
            Assert.False(metadata.RootElement.GetProperty("clean").GetBoolean());
            Assert.True(metadata.RootElement.GetProperty("startupOfferCompleted").GetBoolean());
            Assert.Equal("offered", await File.ReadAllTextAsync(offered.PackagePath, Token));
            Assert.Equal("unseen", await File.ReadAllTextAsync(unseen.PackagePath, Token));
        }
        finally
        {
            operationRelease.TrySetResult();
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return true;
    }, Token);

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task ControlledCloseAfterHandledFailureOrCancellationRetiresOnlyItsOwnCheckpoint(bool detachShell, bool cancelOperation)
        => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-controlled-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "recovery") };
        var store = new EditorRecoveryStore(preferences.RecoveryDirectory);
        var shell = new EditorShellViewModel(); shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "cache");
        var window = new EditorWindow(false, preferences) { DataContext = shell };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!; viewport.IsSuspended = true; viewport.IsVisible = false;
        try
        {
            var model = EditorRecoveryPmaIntegrationTests.Model(); string source = Path.Combine(directory, "source.pmx");
            await File.WriteAllBytesAsync(source, PmxWriter.ToBytes(model), Token);
            Guid id = shell.Workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source), Mmd.Modern.Application.ModelRole.Prop);
            shell.RefreshWorkspace(id);
            shell.Workspace.Save(Path.Combine(directory, "saved.pma"), Token);
            window.Show();
            var saved = await window.SaveRecoveryCheckpointAsync();
            var checkpoint = Assert.IsType<EditorRecoveryCheckpoint>(saved!.Checkpoint);
            byte[] package = await File.ReadAllBytesAsync(checkpoint.PackagePath, Token);
            if (cancelOperation)
                await shell.RunOperationAsync("cancelled", () => Task.FromException(new OperationCanceledException()));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunOperationAsync("failed",
                    () => Task.FromException(new InvalidOperationException("handled failure"))));
            Assert.False(shell.IsBusy);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            // Startup/embedding cleanup can detach the shell before closing the managed window.
            if (detachShell) window.DataContext = null;
            window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.Empty(await store.ListRecoverableAsync(Token));
            Assert.Empty((await EditorRecoveryDiscovery.ScanAsync(preferences, Token)).Sessions);
            Assert.Equal(package, await File.ReadAllBytesAsync(checkpoint.PackagePath, Token));
        }
        finally
        {
            window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return true;
    }, Token);

    [Fact]
    public async Task NativeLightDraftsAreRestoredWithoutCommittingOrCreatingUndo() => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-native-light-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "recovery") };
        var shell = new EditorShellViewModel(); shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "cache");
        var window = new EditorWindow(false, preferences) { DataContext = shell };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!; viewport.IsSuspended = true; viewport.IsVisible = false;
        try
        {
            var model = EditorRecoveryPmaIntegrationTests.Model(); string source = Path.Combine(directory, "source.pmx");
            await File.WriteAllBytesAsync(source, PmxWriter.ToBytes(model), Token);
            Guid id = shell.Workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source), Mmd.Modern.Application.ModelRole.Prop);
            var light = EditorRecoveryNativeLightingTests.Light("recovery spot");
            shell.Workspace.EditLighting(new(new Mmd.Modern.Core.SceneLightingSnapshot([light])));
            shell.RefreshWorkspace(id); window.Show(); shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            Assert.Equal(light.Id, inspector.ViewModel!.SelectedLight!.Id);
            inspector.ViewModel.SelectedLight.PositionX = 23.5m;
            var hex = inspector.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "LightHexColor");
            hex.Text = "unfinished hex";
            var saved = await window.SaveRecoveryCheckpointAsync();
            Assert.Equal(EditorAutoSaveStatus.Saved, saved!.Status);
            Assert.True(await window.RestoreRecoveryCheckpointAsync(saved.Checkpoint!).WaitAsync(TimeSpan.FromSeconds(30), Token));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(EditorInspectorPage.Lighting, shell.InspectorPage);
            Assert.Equal(light.Id, inspector.ViewModel!.SelectedLight!.Id);
            Assert.Equal(23.5m, inspector.ViewModel.SelectedLight.PositionX);
            Assert.Equal("unfinished hex", inspector.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "LightHexColor").Text);
            Assert.Equal(light, Assert.Single(shell.Workspace.Session!.Lighting.Lights));
            Assert.False(shell.Workspace.CanUndo); Assert.Null(shell.Workspace.ProjectPath); Assert.True(shell.Workspace.IsDirty);
            Assert.False(inspector.TryCommit()); Assert.False(shell.Workspace.CanUndo);
            hex = inspector.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "LightHexColor");
            hex.Text = "#FFFFFF"; Assert.True(inspector.TryCommit());
            shell.ShowInspectorPage(EditorInspectorPage.Animation); Dispatcher.UIThread.RunJobs();
            var passive = await window.SaveRecoveryCheckpointAsync();
            Assert.Equal(EditorAutoSaveStatus.Saved, passive!.Status);
            using var passiveJson = JsonDocument.Parse(passive.Checkpoint!.EditorState.GetRawText());
            var passiveState = EditorRecoverySessionState.FromJsonBytes(System.Text.Encoding.UTF8.GetBytes(passiveJson.RootElement.GetRawText()));
            Assert.Equal(light.Id, passiveState.NativeLighting!.SelectedLightId);
            Assert.Equal(light.Id, passiveState.NativeLighting.ActiveLightId);
            Assert.True(await window.RestoreRecoveryCheckpointAsync(passive.Checkpoint).WaitAsync(TimeSpan.FromSeconds(30), Token));
            var controller = typeof(EditorWindow).GetField("lightController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Equal(light.Id, controller.GetType().GetProperty("SelectedLightId")!.GetValue(controller));
            Assert.Equal(EditorInspectorPage.Style, shell.InspectorPage); Assert.False(shell.Workspace.CanUndo);
        }
        finally
        {
            await window.CompleteRecoveryShutdownAsync();
            window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return true;
    }, Token);

    [Fact]
    public async Task RecoveryReviewFindsPreviousDirectoryAndPublishesReplacementInTheCurrentDirectory() => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-directory-change-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var before = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "before") };
        var preferences = (before with { RecoveryDirectory = Path.Combine(directory, "after") }).WithRecoveryDirectoryHistory(before);
        string preferencePath = Path.Combine(directory, "preferences.json"); preferences.Save(preferencePath);
        preferences = EditorPreferences.Load(out var diagnostic, preferencePath); Assert.Null(diagnostic);
        var oldStore = new EditorRecoveryStore(before.RecoveryDirectory);
        EditorRecoveryCheckpoint checkpoint;
        using (var workspace = new EditorProjectWorkspace { PackageCacheDirectory = Path.Combine(directory, "writer-cache") })
        {
            var model = EditorRecoveryPmaIntegrationTests.Model(); string source = Path.Combine(directory, "source.pmx");
            await File.WriteAllBytesAsync(source, PmxWriter.ToBytes(model), Token);
            Guid id = workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source), Mmd.Modern.Application.ModelRole.Prop);
            var animation = new EditorAnimationViewModel(); animation.SetActor(id, model, workspace.Motions[0].Motion);
            var state = EditorRecoverySessionState.Capture(animation, new(), workspace.Motions);
            using var json = JsonDocument.Parse(state.ToJsonBytes()); using var snapshot = workspace.CaptureRecoverySnapshot();
            using var lease = await oldStore.CreateSessionAsync(null, "previous location", Token);
            checkpoint = await oldStore.WriteCheckpointAsync(lease, new(null, "previous location", 1, json.RootElement),
                (path, token) => Task.Run(() => snapshot.Save(path, token), token), Token);
        }
        Assert.False(Directory.Exists(preferences.RecoveryDirectory)); // Crash before B's first checkpoint.
        var shell = new EditorShellViewModel(); shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "reader-cache");
        var window = new EditorWindow(false, preferences) { DataContext = shell };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!; viewport.IsSuspended = true; viewport.IsVisible = false;
        try
        {
            window.Show(); Task review = window.ReviewRecoveryAsync();
            var limit = DateTime.UtcNow.AddSeconds(15); Window? picker = null;
            while (picker is null && DateTime.UtcNow < limit)
            {
                await Task.Delay(20, Token); Dispatcher.UIThread.RunJobs();
                picker = window.OwnedWindows.SingleOrDefault(child => child is EditorRecoveryWindow);
            }
            Assert.NotNull(picker);
            picker.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RestoreRecoveryButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await review.WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.True(shell.HasProject); Assert.Null(shell.Workspace.ProjectPath); Assert.True(shell.Workspace.IsDirty);
            Assert.Empty(await oldStore.ListRecoverableAsync(Token));
            Assert.True(Directory.EnumerateFiles(preferences.RecoveryDirectory, "*.pma", SearchOption.AllDirectories).Any());
            Assert.True(File.Exists(checkpoint.PackagePath));
        }
        finally
        {
            await window.CompleteRecoveryShutdownAsync();
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return true;
    }, Token);

    [Fact]
    public async Task ActualDispatcherTimerWritesAnIndependentPackageAndDisablingItKeepsHistory() => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-timer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "recovery") };
        var shell = new EditorShellViewModel();
        shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "cache");
        var window = new EditorWindow(false, preferences) { DataContext = shell };
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsSuspended = true;
        try
        {
            var model = EditorRecoveryPmaIntegrationTests.Model(); string source = Path.Combine(directory, "source.pmx");
            byte[] original = PmxWriter.ToBytes(model); await File.WriteAllBytesAsync(source, original, Token);
            Guid id = shell.Workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source), Mmd.Modern.Application.ModelRole.Prop);
            shell.RefreshWorkspace(id); shell.Browser.SelectedBoneIndex = 0; shell.Animation.PositionX = 3.5m;
            window.Show();
            var timer = (DispatcherTimer)typeof(EditorWindow).GetField("recoveryTimer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.True(timer.IsEnabled); Assert.Equal(TimeSpan.FromMinutes(3), timer.Interval);
            timer.Interval = TimeSpan.FromMilliseconds(40); // Only this fixture's clock interval changes.
            var limit = DateTime.UtcNow.AddSeconds(10); string? metadata = null;
            while (metadata is null && DateTime.UtcNow < limit)
            {
                await Task.Delay(20, Token);
                if (Directory.Exists(preferences.RecoveryDirectory))
                    metadata = Directory.EnumerateFiles(preferences.RecoveryDirectory, "*.json", SearchOption.AllDirectories)
                        .FirstOrDefault(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _));
            }
            Assert.NotNull(metadata);
            typeof(EditorWindow).GetMethod("ApplyPreferences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [preferences with { AutoSaveEnabled = false }]);
            Assert.False(timer.IsEnabled);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(metadata, Token));
            var state = EditorRecoverySessionState.FromJsonBytes(System.Text.Encoding.UTF8.GetBytes(saved.RootElement.GetProperty("editorState").GetRawText()));
            Assert.Equal(3.5m, Assert.Single(Assert.Single(state.Animation.Actors).Bones).X);
            Assert.True(File.Exists(Path.ChangeExtension(metadata, ".pma")));
            Assert.True(shell.Animation.HasAnyDraft); Assert.Null(shell.Workspace.ProjectPath);
            Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
            Assert.Empty(await new EditorRecoveryStore(preferences.RecoveryDirectory).ListRecoverableAsync(Token));
        }
        finally
        {
            await window.CompleteRecoveryShutdownAsync();
            window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return true;
    }, Token);

    [Fact]
    public async Task RealWindowCheckpointAndRecoveryKeepDraftsKeysAndForceSaveAs() => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "recovery") };
        var shell = new EditorShellViewModel();
        shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "cache");
        var window = new EditorWindow(false, preferences) { DataContext = shell };
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsSuspended = true;
        try
        {
            window.Show();
            var model = EditorRecoveryPmaIntegrationTests.Model();
            string source = Path.Combine(directory, "source.pmx");
            byte[] original = PmxWriter.ToBytes(model); await File.WriteAllBytesAsync(source, original, Token);
            Guid id = shell.Workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source), Mmd.Modern.Application.ModelRole.Prop);
            shell.Workspace.CommitMotion(id, new() { ModelName = "recovery", BoneKeyframes = [new() { BoneName = "root", Frame = 10 }] });
            shell.RefreshWorkspace(id); shell.Browser.SelectedBoneIndex = 0;
            shell.CurrentFrame = 7; shell.Animation.PositionX = 12.5m;
            shell.PixelsPerFrame = 12; shell.FirstVisibleFrame = 2;
            var result = await window.SaveRecoveryCheckpointAsync();
            Assert.NotNull(result); Assert.Equal(EditorAutoSaveStatus.Saved, result.Status);
            var checkpoint = Assert.IsType<EditorRecoveryCheckpoint>(result.Checkpoint);
            Assert.Equal(EditorAutoSaveStatus.Unchanged, (await window.SaveRecoveryCheckpointAsync())!.Status);
            Assert.True(shell.Animation.HasAnyDraft);
            Assert.Equal(10u, Assert.Single(shell.Workspace.Motions[0].Motion.BoneKeyframes).Frame);
            Assert.Empty(await new EditorRecoveryStore(preferences.RecoveryDirectory).ListRecoverableAsync(Token));
            // The real window restores through the production staged PMA loader and editor bindings.
            Assert.True(await window.RestoreRecoveryCheckpointAsync(checkpoint).WaitAsync(TimeSpan.FromSeconds(30), Token));
            Assert.True(shell.Workspace.IsDirty); Assert.Null(shell.Workspace.ProjectPath);
            Assert.Equal(7, shell.CurrentFrame); Assert.Equal(7, shell.Workspace.Session!.CurrentFrame);
            Assert.Equal(id, shell.Browser.SelectedModel!.ObjectId); Assert.Equal(0, shell.Browser.SelectedBoneIndex);
            Assert.Equal(12.5m, shell.Animation.PositionX); Assert.True(shell.Animation.HasAnyDraft);
            Assert.Equal(12, shell.PixelsPerFrame); Assert.Equal(2, shell.FirstVisibleFrame);
            Assert.Equal(10u, Assert.Single(shell.Workspace.Motions[0].Motion.BoneKeyframes).Frame);
            Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
            var position = window.FindControl<EditorAnimationInspector>("AnimationInspector")!
                .FindControl<EditorNumericUpDown>("BonePositionX")!;
            position.Text = "not a number";
            var unparsed = await window.SaveRecoveryCheckpointAsync();
            Assert.Equal(EditorAutoSaveStatus.Saved, unparsed!.Status);
            Assert.True(await window.RestoreRecoveryCheckpointAsync(unparsed.Checkpoint!).WaitAsync(TimeSpan.FromSeconds(30), Token));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("not a number", position.Text); Assert.False(position.IsInputValid);
            Assert.False(shell.Animation.CanRegister);
            position.Text = "12.5";
            // Real playback retains a fractional solver clock when paused; the integer UI must
            // not turn the otherwise valid saved copy into an unrecoverable checkpoint.
            shell.Animation.InvalidateDrafts();
            shell.Workspace.Session!.Advance(1d / 120, Token);
            double pausedFrame = shell.Workspace.Session.CurrentFrame;
            Assert.True(pausedFrame > 7 && pausedFrame < 8);
            var paused = await window.SaveRecoveryCheckpointAsync();
            Assert.Equal(EditorAutoSaveStatus.Saved, paused!.Status);
            Assert.True(await window.RestoreRecoveryCheckpointAsync(paused.Checkpoint!).WaitAsync(TimeSpan.FromSeconds(30), Token));
            Assert.Equal(pausedFrame, shell.Workspace.Session!.CurrentFrame);
            Assert.Equal(7, shell.CurrentFrame); Assert.False(shell.Animation.HasAnyDraft);
            // A confirmed New retires old recovery ownership without waiting for another timer tick.
            shell.Workspace.New(); Dispatcher.UIThread.RunJobs();
            await window.CompleteRecoveryShutdownAsync();
            Assert.Empty(await new EditorRecoveryStore(preferences.RecoveryDirectory).ListRecoverableAsync(Token));
        }
        finally
        {
            await window.CompleteRecoveryShutdownAsync();
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return true;
    }, Token);

    [Theory]
    [InlineData("zh-CN", false)] [InlineData("zh-CN", true)]
    [InlineData("en", false)] [InlineData("en", true)]
    [InlineData("ja", false)] [InlineData("ja", true)]
    public async Task PickerHasTranslatedActionsAtSmallestSizeAndDeferralKeepsCopies(string language, bool dark) => await session.Dispatch(async () =>
    {
        EditorLocalization.SetLanguage(language);
        var owner = new Window { Width = 800, Height = 600, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Background = dark ? Brushes.Black : Brushes.White, Foreground = dark ? Brushes.White : Brushes.Black };
        owner.Show();
        using var state = JsonDocument.Parse("{}");
        var checkpoint = new EditorRecoveryCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            "D:/test/recovery.pma", 86_064_938, new string('0', 64), "D:/a very long folder/未命名工程.pma",
            "未命名工程.pma", 1, state.RootElement.Clone(), 1);
        var copies = new[] { new EditorRecoverySessionInfo(checkpoint.SessionId, checkpoint.CreatedUtc, checkpoint.OriginalProjectPath,
            checkpoint.DisplayName, new[] { checkpoint }) };
        var dialog = new EditorRecoveryWindow(owner, copies) { Width = 520, Height = 400 };
        try
        {
            var result = dialog.ShowDialog<EditorRecoveryChoice?>(owner); dialog.UpdateLayout();
            foreach (string name in new[] { "RestoreRecoveryButton", "DeferRecoveryButton", "DismissRecoveryButton" })
            {
                var button = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
                Assert.True(button.IsEnabled); Assert.DoesNotContain("Mda.Text.", button.Content!.ToString()!);
                var position = button.TranslatePoint(default, dialog)!.Value;
                Assert.True(position.X >= 0 && position.Y >= 0 && position.X + button.Bounds.Width <= dialog.Bounds.Width + .1
                    && position.Y + button.Bounds.Height <= dialog.Bounds.Height + .1, name + " spills beyond the window");
            }
            dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DeferRecoveryButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(await result);
        }
        finally { dialog.Close(); owner.Close(); }
        return true;
    }, Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
