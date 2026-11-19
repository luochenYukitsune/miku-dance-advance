using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application.MotionEditing;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorTimelineLayerTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    /// <summary>Every state is rendered by one timeline that walks the sequence, as an editor does, and by a fresh
    /// timeline set up directly into that state. Equal pixels prove no layer keeps stale content. With
    /// MDA_TIMELINE_CAPTURE_DIR set, the incremental frames are also written for a cross-build comparison.</summary>
    [Fact]
    public async Task IncrementalLayersRenderExactlyLikeAFreshTimelineInEveryState() => await session.Dispatch(() =>
    {
        var capture = Environment.GetEnvironmentVariable("MDA_TIMELINE_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(capture)) Directory.CreateDirectory(capture);
        var states = States();
        var (window, timeline) = Create();
        try
        {
            for (int index = 0; index < states.Length; index++)
            {
                var (name, apply) = states[index];
                apply(window, timeline);
                var incremental = Pixels(window, out var bitmap);
                using (bitmap)
                {
                    if (!string.IsNullOrEmpty(capture))
                    {
                        string file = Path.Combine(capture, $"{index:D2}-{name}");
                        bitmap.Save(file + ".png");
                        File.WriteAllText(file + ".sha256", Convert.ToHexString(SHA256.HashData(incremental)));
                    }
                }
                var (freshWindow, fresh) = Create();
                try
                {
                    // Fresh timelines replay only their own absolute state; the ordinal states before it are not needed.
                    apply(freshWindow, fresh);
                    var reference = Pixels(freshWindow, out var freshBitmap);
                    using (freshBitmap)
                        if (!string.IsNullOrEmpty(capture) && !reference.AsSpan().SequenceEqual(incremental))
                            freshBitmap.Save(Path.Combine(capture, $"{index:D2}-{name}.fresh.png"));
                    Assert.True(reference.AsSpan().SequenceEqual(incremental), $"State {name} differs from a fresh render.");
                }
                finally { freshWindow.Close(); }
                End(window, timeline);
            }
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CurrentFrameSweepRedrawsOnlyPlayheadLayerWithoutReRenderingTracks() => await session.Dispatch(() =>
    {
        var (window, timeline) = Create();
        try
        {
            timeline.SelectedKeyframes = [KeyframeAddress.Bone("Bone0", 30), KeyframeAddress.Morph("Morph1", 60)];
            RenderStep(window);
            int trackRendersBefore = timeline.TrackLayerRenderCount;
            int playheadRendersBefore = timeline.PlayheadLayerRenderCount;
            Assert.True(trackRendersBefore > 0);
            Assert.True(playheadRendersBefore > 0);

            // Continuous CurrentFrame changes within the visible viewport must keep TrackLayerRenderCount delta strictly 0.
            for (int frame = 1; frame <= 25; frame++)
            {
                timeline.CurrentFrame = frame;
                RenderStep(window);
            }
            Assert.Equal(0, timeline.TrackLayerRenderCount - trackRendersBefore);
            Assert.Equal(25, timeline.PlayheadLayerRenderCount - playheadRendersBefore);

            // Ruler scrubbing within the visible viewport also updates only the playhead layer.
            int scrubStarted = 0, scrubEnded = 0;
            timeline.ScrubStarted += (_, _) => scrubStarted++;
            timeline.ScrubEnded += (_, _) => scrubEnded++;
            int playheadBeforeDrag = timeline.PlayheadLayerRenderCount;
            Press(window, timeline, new(220 + 28 * 8, 10));
            RenderStep(window);
            for (int frame = 29; frame <= 45; frame++)
            {
                Move(window, timeline, new(220 + frame * 8, 12));
                RenderStep(window);
            }
            Release(window, timeline, new(220 + 45 * 8, 12));
            RenderStep(window);
            Assert.Equal(1, scrubStarted);
            Assert.Equal(1, scrubEnded);
            Assert.Equal(45, timeline.CurrentFrame);
            Assert.Equal(0, timeline.TrackLayerRenderCount - trackRendersBefore);
            Assert.True(timeline.PlayheadLayerRenderCount > playheadBeforeDrag);
            End(window, timeline);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PlayheadLayerPreservesHitTestingFocusAndAutomation() => await session.Dispatch(() =>
    {
        var (window, timeline) = Create();
        try
        {
            timeline.CurrentFrame = 15;
            RenderStep(window);
            int trackBefore = timeline.TrackLayerRenderCount;

            Assert.Same(timeline, window.InputHitTest(new Point(220 + 15 * 8, 6)));
            Assert.Same(timeline, window.InputHitTest(new Point(220 + 15 * 8, 150)));
            Assert.Same(timeline, window.InputHitTest(Key(30, 0)));
            Assert.Same(timeline, window.InputHitTest(new Point(40, 60)));

            Assert.True(timeline.Focusable);
            Assert.True(timeline.Focus());
            RenderStep(window);
            window.KeyPress(Avalonia.Input.Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            RenderStep(window);
            Assert.Equal(16, timeline.CurrentFrame);
            Assert.Equal(0, timeline.TrackLayerRenderCount - trackBefore);

            var peer = ControlAutomationPeer.CreatePeerForElement(timeline);
            Assert.Empty(peer.GetChildren());
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    private static (string Name, Action<Window, EditorTimeline> Apply)[] States() =>
    [
        ("start", (_, t) => View(t, 8, 0, 0, 0)),
        ("frame-37", (_, t) => View(t, 8, 0, 0, 37)),
        ("frame-on-label", (_, t) => View(t, 8, 0, 0, 40)),
        ("selection", (_, t) => { View(t, 8, 0, 0, 55); t.SelectedKeyframes = [KeyframeAddress.Bone("Bone0", 30), KeyframeAddress.Morph("Morph1", 60)]; }),
        ("zoomed", (_, t) => { t.SelectedKeyframes = []; View(t, 2.5, 100, 0, 180); }),
        ("track-scroll", (_, t) => View(t, 8, 0, 3, 12)),
        ("focused", (_, t) => { View(t, 8, 0, 0, 20); t.Focus(); }),
        ("expanded", (_, t) => { View(t, 8, 0, 0, 24); t.Groups = Groups(t.Tracks); t.SetGroupExpanded(t.Groups[0].Key, true); }),
        ("dense", (_, t) => View(t, .5, 0, 5, 300)),
        ("box", (w, t) => { View(t, 8, 0, 0, 6); Press(w, t, new(220 + 13 * 8, 30)); Move(w, t, new(220 + 47 * 8, 90)); }),
        ("key-drag", (w, t) => { View(t, 8, 0, 0, 6); Press(w, t, Key(30, 0)); Move(w, t, new(Key(30, 0).X + 5 * 8, Key(30, 0).Y)); }),
        ("ruler-drag", (w, t) => { View(t, 8, 0, 0, 6); Press(w, t, new(220 + 9 * 8, 10)); Move(w, t, new(220 + 61 * 8, 12)); }),
        ("after-release", (_, t) => View(t, 8, 0, 0, 77))
    ];

    private static void View(EditorTimeline timeline, double zoom, double first, int track, int frame)
    {
        timeline.PixelsPerFrame = zoom; timeline.FirstVisibleFrame = first; timeline.FirstVisibleTrack = track;
        timeline.CurrentFrame = frame;
    }

    private static readonly Pointer Mouse = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
    private static Point Key(int frame, int row) => new(220 + frame * 8, EditorTimeline.RulerHeight + (row + .5) * EditorTimeline.TrackRowHeight);

    private static void End(Window window, EditorTimeline timeline)
    {
        if (Mouse.Captured == timeline)
            timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, Mouse, window, new Point(0, 0), 3,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        foreach (var group in timeline.Groups) timeline.SetGroupExpanded(group.Key, false);
        timeline.Groups = []; timeline.SelectedKeyframes = [];
        window.FocusManager?.ClearFocus();
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, EditorTimeline Timeline) Create()
    {
        var tracks = new List<EditorTimelineTrack>();
        for (int i = 0; i < 6; i++) tracks.Add(new(KeyframeTrackKind.Bone, "Bone" + i, Enumerable.Range(0, 40).Select(k => (uint)(k * 15 + i * 3))));
        tracks.Add(new(KeyframeTrackKind.Morph, "Morph1", [0, 60, 61, 62, 400]));
        tracks.Add(new(KeyframeTrackKind.Bone, "Dense", Enumerable.Range(0, 8192).Select(k => (uint)k)));
        tracks.Add(new(KeyframeTrackKind.Ik, string.Empty, [0, 90]));
        var timeline = new EditorTimeline
        {
            Tracks = tracks, FullTracks = tracks, TrackHeaderWidth = 220, PixelsPerFrame = 8, FontSize = 12, HasTracks = true,
            Background = new SolidColorBrush(Color.Parse("#151A20")), Foreground = new SolidColorBrush(Color.Parse("#8B96A3")),
            GridBrush = new SolidColorBrush(Color.Parse("#2A323C")), PlayheadBrush = new SolidColorBrush(Color.Parse("#4FC08D"))
        };
        var window = new Window { Width = 900, Height = 300, Content = timeline };
        window.Show(); Dispatcher.UIThread.RunJobs();
        return (window, timeline);
    }

    private static IReadOnlyList<EditorTimelineGroup> Groups(IReadOnlyList<EditorTimelineTrack> tracks) => EditorTimelineGrouping.Build(
        TimelineModelGroupTests.Model(TimelineModelGroupTests.Display("Mixed", new(Formats.PmxDisplayTargetType.Bone, 0),
            new(Formats.PmxDisplayTargetType.Bone, 1))), tracks, "layer");

    private static void RenderStep(Window window) { _ = Pixels(window, out var bitmap); bitmap.Dispose(); }

    private static byte[] Pixels(Window window, out WriteableBitmap bitmap)
    {
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No headless frame was rendered.");
        using var locked = bitmap.Lock();
        Assert.Equal(32, locked.Format.BitsPerPixel);
        var bytes = new byte[locked.RowBytes * locked.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void Press(Window window, EditorTimeline timeline, Point position) =>
        timeline.RaiseEvent(new PointerPressedEventArgs(timeline, Mouse, window, timeline.TranslatePoint(position, window)!.Value, 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
    private static void Move(Window window, EditorTimeline timeline, Point position) =>
        timeline.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, timeline, Mouse, window, timeline.TranslatePoint(position, window)!.Value, 2,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
    private static void Release(Window window, EditorTimeline timeline, Point position) =>
        timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, Mouse, window, timeline.TranslatePoint(position, window)!.Value, 3,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
