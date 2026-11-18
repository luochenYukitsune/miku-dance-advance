using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

// Scripted headless playback with the lighting page open. The viewport is suspended, so the UI
// timer path drives every advance with a simulated 60 Hz elapsed time.
[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPlaybackPanelDiagnosticsTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ScriptedPlaybackRecordsPanelRefreshCounts() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var model = PmdPmxAdapter.Convert(PmdReader.Read(input));
            var id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Character);
            var keys = new List<VmdBoneKeyframe>();
            for (uint frame = 0; frame <= 300; frame += 2)
                foreach (var bone in model.Bones)
                    keys.Add(new() { BoneName = bone.Name, Frame = frame, Translation = new(frame * .01f, 0, 0),
                        Rotation = Quaternion.CreateFromYawPitchRoll(frame * .01f, 0, 0) });
            shell.Workspace.CommitMotion(id, new VmdMotion { ModelName = model.Name, BoneKeyframes = keys.ToArray() });
            var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Spot, new(3, 4, 5), Quaternion.Identity, Vector3.One);
            shell.Workspace.EditLighting(new(new([light]), light.Id));
            shell.Browser.SelectedModel = shell.Browser.Models.Single();
            shell.Browser.SelectedBoneIndex = model.Bones.Count - 1;
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(shell.Animation.IsBoneSelected);

            InteractionDiagnostics.Start();
            shell.PlayCommand.Execute(null);
            ((DispatcherTimer)typeof(EditorShellViewModel).GetField("playbackTimer", Instance)!.GetValue(shell)!).Stop();
            var timestamp = typeof(EditorShellViewModel).GetField("lastPlaybackTimestamp", Instance)!;
            var tick = typeof(EditorShellViewModel).GetMethod("PlaybackTick", Instance)!;
            const int ticks = 540; // 9 s at 60 Hz: 270 source frames.
            for (int index = 0; index < ticks && shell.IsPlaying; index++)
            {
                timestamp.SetValue(shell, Stopwatch.GetTimestamp() - Stopwatch.Frequency / 60);
                tick.Invoke(shell, [null, EventArgs.Empty]);
            }
            shell.StopPlayback(); Dispatcher.UIThread.RunJobs();
            var report = InteractionDiagnostics.Snapshot();
            InteractionDiagnostics.Stop();
            Assert.Equal(0, report.OverwrittenEvents);
            var stages = report.Events.GroupBy(value => value.Stage).OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new
                {
                    Count = group.Count(),
                    TotalMilliseconds = group.Sum(value => value.ElapsedTicks) * 1000d / report.TimestampFrequency
                });
            if (Environment.GetEnvironmentVariable("MDA_PLAYBACK_PANEL_REPORT") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { Ticks = ticks, shell.CurrentFrame, Keyframes = keys.Count, Stages = stages },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            return true;
        }
        finally
        {
            InteractionDiagnostics.Stop();
            typeof(EditorWindow).GetField("_closingConfirmed", Instance)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
