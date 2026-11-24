using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Mmd.Modern.Application.MotionEditing;

namespace Mmd.Modern.App.Controls;

public sealed class EditorSceneTimelineTrack(SceneTrackKind kind, IEnumerable<uint> frames)
{
    public SceneTrackKind Kind { get; } = kind;
    public IReadOnlyList<uint> Frames { get; } = Array.AsReadOnly(frames.Distinct().Order().ToArray());
    public string Label => EditorLocalization.Text(Kind == SceneTrackKind.Camera ? "相机" : "主光");
}

public sealed partial class EditorTimeline
{
    public static readonly StyledProperty<EditorSceneTimelineTrack?> SceneTrackProperty =
        AvaloniaProperty.Register<EditorTimeline, EditorSceneTimelineTrack?>(nameof(SceneTrack));
    public EditorSceneTimelineTrack? SceneTrack { get => GetValue(SceneTrackProperty); set => SetValue(SceneTrackProperty, value); }

    private void RenderSceneTrack(DrawingContext context, EditorSceneTimelineTrack track, double width, double header,
        Pen gridPen, Typeface typeface, double fontSize)
    {
        _drawnDiamondCount = 0;
        double center = RulerHeight + TrackRowHeight / 2;
        context.DrawLine(gridPen, new Point(0, RulerHeight + TrackRowHeight), new Point(width, RulerHeight + TrackRowHeight));
        if (Foreground is { } foreground && header > 0)
        {
            using var clip = context.PushClip(new Rect(0, RulerHeight, header, TrackRowHeight));
            var label = new FormattedText(track.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, foreground);
            context.DrawText(label, new Point(9, center - label.Height / 2));
        }
        using (context.PushClip(new Rect(header, RulerHeight, Math.Max(0, width - header), TrackRowHeight)))
        {
            var pen = new Pen(Foreground, 1);
            double last = FirstVisibleFrame + (width - header + 5) / PixelsPerFrame;
            double previous = double.NegativeInfinity;
            int first = EditorTimelineTrack.FrameLowerBound(track.Frames, FirstVisibleFrame - 5 / PixelsPerFrame);
            for (int i = first; i < track.Frames.Count && track.Frames[i] <= last && _drawnDiamondCount < MaximumVisibleKeyCount; i++)
            {
                double x = header + (track.Frames[i] - FirstVisibleFrame) * PixelsPerFrame;
                if (x - previous < 1) continue;
                DrawDiamond(context, x, center, Background, pen);
                previous = x;
            }
        }
    }

    private int SceneFrameAt(EditorSceneTimelineTrack track, double x)
    {
        double frame = FirstVisibleFrame + (x - TrackHeaderWidth) / PixelsPerFrame;
        int next = EditorTimelineTrack.FrameLowerBound(track.Frames, frame);
        if (next > 0 && (next == track.Frames.Count || frame - track.Frames[next - 1] <= track.Frames[next] - frame)) next--;
        return next < track.Frames.Count && track.Frames[next] <= int.MaxValue
            && Math.Abs(track.Frames[next] - frame) * PixelsPerFrame <= 7 ? (int)track.Frames[next] : FrameAtX(x);
    }
}
