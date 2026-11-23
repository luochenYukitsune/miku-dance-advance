using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public sealed partial class EditorBoneViewportController
{
    private const float ClickSlopSquared = 9;
    private const float CornerRadius = 11;
    private PickCycle? lastPick;
    private PendingPick? pendingPick;

    private sealed record PickCycle(Guid Actor, long Revision, int Frame, int Width, int Height, SceneCamera Camera,
        Vector2 Point, int[] Candidates, int Selected)
    {
        public bool Matches(PickCycle other) => Actor == other.Actor && Revision == other.Revision && Frame == other.Frame
            && Width == other.Width && Height == other.Height && Camera == other.Camera
            && Vector2.DistanceSquared(Point, other.Point) <= ClickSlopSquared && Candidates.SequenceEqual(other.Candidates);
    }
    private sealed record PendingPick(PickCycle Cycle, int Next, bool Moved = false);

    internal void EndPointerGesture(int x, int y)
    {
        if (drag is not null || pendingPick is not null) MoveDrag(x, y);
        EndDrag();
    }

    private void SelectSingleBone(int index)
    {
        shell.Browser.HierarchyTab = 0;
        shell.Browser.SelectedBoneIndex = index;
        shell.SceneSelectionIndex = -1;
        shell.Animation.SelectBones([index]);
    }

    private int[] CenterCandidates(CapturedPose pose, Vector2 mouse, SceneCamera camera) =>
        pose.BoneCenters.Where(center => center.Index == shell.Animation.SelectedBoneIndex || IsShown(pose.Model.PmxModel, center.Index))
            .Select(center => (center.Index, Distance: Vector2.DistanceSquared(mouse, center.Point),
                Depth: Vector3.DistanceSquared(center.WorldPoint, camera.Eye)))
            .Where(hit => hit.Distance <= 100).OrderBy(hit => hit.Distance).ThenBy(hit => hit.Depth).ThenBy(hit => hit.Index)
            .Select(hit => hit.Index).ToArray();

    private static int[] SegmentCandidates(CapturedPose pose, Vector2 mouse, SceneCamera camera)
    {
        var hits = new List<(int Index, float Distance, float Depth)>();
        foreach (var segment in pose.BoneSegments)
        {
            var direction = segment.End - segment.Start;
            float length = direction.LengthSquared();
            float along = length < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(mouse - segment.Start, direction) / length, 0, 1);
            float distance = Vector2.DistanceSquared(mouse, segment.Start + direction * along);
            if (distance <= 36) hits.Add((segment.Index, distance,
                Vector3.DistanceSquared(Vector3.Lerp(segment.WorldStart, segment.WorldEnd, along), camera.Eye)));
        }
        return hits.OrderBy(hit => hit.Distance).ThenBy(hit => hit.Depth).ThenBy(hit => hit.Index).Select(hit => hit.Index).ToArray();
    }

    private static Vector2 CornerCenter(LightDragAxis axis, int width, int height) =>
        new(width - 19 - (axis == LightDragAxis.X ? 60 : axis == LightDragAxis.Y ? 30 : 0), height - 19);

    private static readonly (Vector2, Vector2)[] LetterX = [(new(-4, -5), new(4, 5)), (new(4, -5), new(-4, 5))];
    private static readonly (Vector2, Vector2)[] LetterY = [(new(-4, -5), new(0, 0)), (new(4, -5), new(0, 0)), (new(0, 0), new(0, 5))];
    private static readonly (Vector2, Vector2)[] LetterZ = [(new(-4, -5), new(4, -5)), (new(4, -5), new(-4, 5)), (new(-4, 5), new(4, 5))];
    private static (Vector2 Start, Vector2 End)[] CornerGlyph(LightDragAxis axis) =>
        axis switch { LightDragAxis.X => LetterX, LightDragAxis.Y => LetterY, _ => LetterZ };
}
