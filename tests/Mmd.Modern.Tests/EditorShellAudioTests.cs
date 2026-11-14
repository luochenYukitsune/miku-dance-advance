using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorShellAudioTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession dispatcher = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ExistingSourceClockDrivesPlayShortSeekTickPauseAndResourceRelease()
        => await dispatcher.Dispatch(async () =>
    {
        var factory = new FakeFactory();
        using var shell = new EditorShellViewModel { AudioOutputFactory = factory };
        var model = MorphApplicationTests.Model([]);
        string path = Path.Combine(Path.GetTempPath(), "mda-shell-audio-fixture.pmx");
        var id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Prop);
        shell.Workspace.CommitMotion(id, new VmdMotion
        {
            ModelName = model.Name,
            BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 120 }]
        });
        var track = new SceneMediaTrack(Guid.NewGuid(), SceneMediaKind.Audio,
            SceneMediaAsset.FromBytes(PcmWav(), "clock.wav"), 0, true);
        EditorSceneAudioInputs.Validate([track]);
        shell.Workspace.Session!.Edit(edit => edit.Media.Add(track), Token);
        shell.RefreshWorkspace();
        Assert.False(shell.IsPlaying);
        Assert.Equal(0, factory.Opens);
        Assert.False(shell.HasAudioPlaybackOwner);

        shell.PlayCommand.Execute(null);
        // Keep the test deterministic: invoke the existing UI tick directly below, with a real
        // monotonic elapsed delta. The fake audio device owns no timer or scene clock.
        ((DispatcherTimer)typeof(EditorShellViewModel).GetField("playbackTimer", Instance)!.GetValue(shell)!).Stop();
        var owner = (SceneAudioPlaybackOwner)typeof(EditorShellViewModel).GetField("audioPlayback", Instance)!.GetValue(shell)!;
        await owner.FlushAsync(Token);
        var first = Assert.Single(factory.Outputs);
        Assert.True(first.Playing);
        Assert.Equal(0, shell.Workspace.Session.CurrentFrame);

        shell.CurrentFrame = 1; // 33ms, below the device's 80ms drift correction threshold.
        await owner.FlushAsync(Token);
        // Media samples are authored on the existing integer-tick clock: 1/30 second
        // truncates to 333333 ticks before conversion to PCM, so no second rounding is added here.
        long shortSeekSample = Assert.Single(shell.Workspace.Session.SampleMedia(true)).MediaTicks * 48000 / TimeSpan.TicksPerSecond;
        Assert.Equal(shortSeekSample, first.LastSeek);
        Assert.Equal(1, shell.Workspace.Session.CurrentFrame);
        shell.CurrentFrame = 30;
        await owner.FlushAsync(Token);
        Assert.Equal(48000, first.LastSeek);

        // A late dispatcher callback must advance the same project and audio
        // clock by its whole delay, rather than dropping everything after 100 ms.
        const double delay = .25;
        long lateTickStart = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * delay);
        typeof(EditorShellViewModel).GetField("lastPlaybackTimestamp", Instance)!.SetValue(shell, lateTickStart);
        typeof(EditorShellViewModel).GetMethod("PlaybackTick", Instance)!.Invoke(shell, [null, EventArgs.Empty]);
        double elapsedUpperBound = Stopwatch.GetElapsedTime(lateTickStart).TotalSeconds;
        double sourceFrame = shell.Workspace.Session.CurrentFrame;
        var sample = Assert.Single(shell.Workspace.Session.SampleMedia(true));
        await owner.FlushAsync(Token);
        Assert.InRange(sourceFrame, 30 + delay * 30 - .001, 30 + elapsedUpperBound * 30 + .001);
        Assert.Equal(sample.MediaTicks * 48000 / TimeSpan.TicksPerSecond, first.LastSeek);
        Assert.Equal(sourceFrame, shell.Workspace.Session.CurrentFrame);

        shell.StopPlayback();
        await owner.FlushAsync(Token);
        Assert.False(first.Playing);
        Assert.Equal(sourceFrame, shell.Workspace.Session.CurrentFrame);
        await shell.ReleaseAudioPlaybackAsync(Token);
        Assert.Equal(1, first.Disposals);
        Assert.Single(shell.Workspace.Session.Media);
        Assert.Equal(sourceFrame, shell.Workspace.Session.CurrentFrame);

        shell.PlayCommand.Execute(null);
        ((DispatcherTimer)typeof(EditorShellViewModel).GetField("playbackTimer", Instance)!.GetValue(shell)!).Stop();
        await owner.FlushAsync(Token);
        Assert.Equal(2, factory.Opens);
        shell.Workspace.New();
        await owner.FlushAsync(Token);
        Assert.False(shell.IsPlaying);
        Assert.All(factory.Outputs, value => Assert.Equal(1, value.Disposals));
        return true;
    }, Token);

    private static byte[] PcmWav()
    {
        byte[] bytes = new byte[44 + 48000 * 2 * 5];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 48000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 96000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
        return bytes;
    }

    private sealed class FakeFactory : ISceneAudioOutputFactory
    {
        public int Opens => Outputs.Count;
        public List<FakeOutput> Outputs { get; } = [];
        public ISceneAudioOutput Open(ScenePcmAudio audio)
        {
            var output = new FakeOutput(); Outputs.Add(output); return output;
        }
    }

    private sealed class FakeOutput : ISceneAudioOutput
    {
        public long PositionFrames => LastSeek;
        public long LastSeek { get; private set; }
        public bool Playing { get; private set; }
        public int Disposals { get; private set; }
        public void Seek(long frame) => LastSeek = frame;
        public void Play() => Playing = true;
        public void Pause() => Playing = false;
        public void Dispose() { Playing = false; Disposals++; }
    }

    public async ValueTask DisposeAsync() => await Task.Run(dispatcher.Dispose).ConfigureAwait(false);
}
