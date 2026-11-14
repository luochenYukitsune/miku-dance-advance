using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class MultiActorPosePreviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static (SceneProjectSession Session, Guid First, Guid Second) Create()
    {
        var model = EditedMotionProjectTests.Model();
        var composition = new SceneCompositionEditor();
        var first = composition.Add("first", Path.GetFullPath("preview-first.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var second = composition.Add("second", Path.GetFullPath("preview-second.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var physics = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("No fixture physics"),
            _ => throw new InvalidOperationException("No fixture physics"));
        return (new(composition, new(),
            [new(first.Id, model, new VmdMotion(), PhysicsEnabled: false), new(second.Id, model, new VmdMotion(), PhysicsEnabled: false)], physics, cancellationToken: Token),
            first.Id, second.Id);
    }

    private static VmdMotion Motion(float x, string name = "root") => new()
    { BoneKeyframes = [new() { BoneName = name, Frame = 0, Translation = new(x, 0, 0) }] };

    [Fact]
    public void SimultaneousDraftsRenderTogetherAndOmittedActorReturnsToItsCommittedPose()
    {
        var (source, first, second) = Create(); using (source)
        {
            var baseline = source.Frame.Surface.Snapshot.Vertices.ToArray().Select(vertex => vertex.Position).ToArray();
            using var preview = source.CreatePosePreview(new Dictionary<Guid, VmdMotion> { [first] = Motion(1), [second] = Motion(2) }, Token);
            Assert.Equal(baseline.Take(3).Select(position => position + Vector3.UnitX),
                preview.Frame.Surface.Snapshot.Vertices.ToArray().Take(3).Select(vertex => vertex.Position));
            Assert.Equal(baseline.Skip(3).Select(position => position + 2 * Vector3.UnitX),
                preview.Frame.Surface.Snapshot.Vertices.ToArray().Skip(3).Select(vertex => vertex.Position));
            preview.UpdatePosePreviewMotion(first, Motion(3), Token);
            Assert.Equal(baseline.Take(3).Select(position => position + 3 * Vector3.UnitX),
                preview.Frame.Surface.Snapshot.Vertices.ToArray().Take(3).Select(vertex => vertex.Position));
            Assert.Equal(baseline.Skip(3).Select(position => position + 2 * Vector3.UnitX),
                preview.Frame.Surface.Snapshot.Vertices.ToArray().Skip(3).Select(vertex => vertex.Position));
            preview.UpdatePosePreviewMotions(new Dictionary<Guid, VmdMotion> { [second] = Motion(2) }, Token);
            Assert.Equal(baseline.Take(3), preview.Frame.Surface.Snapshot.Vertices.ToArray().Take(3).Select(vertex => vertex.Position));
            Assert.Equal(baseline.Skip(3).Select(position => position + 2 * Vector3.UnitX),
                preview.Frame.Surface.Snapshot.Vertices.ToArray().Skip(3).Select(vertex => vertex.Position));
            preview.UpdatePosePreviewMotions(new Dictionary<Guid, VmdMotion>(), Token);
            Assert.Equal(baseline, preview.Frame.Surface.Snapshot.Vertices.ToArray().Select(vertex => vertex.Position));
            Assert.Equal(baseline, source.Frame.Surface.Snapshot.Vertices.ToArray().Select(vertex => vertex.Position));
            Assert.All(source.CaptureAuthoredContent().Motions, binding => Assert.Empty(binding.Motion.BoneKeyframes));
            Assert.False(source.CanUndo); Assert.False(preview.CanUndo);
        }
    }

    [Fact]
    public void FailedSecondActorOrCancelledBatchRetainsEveryPublishedPreviewAndSource()
    {
        var (source, first, second) = Create(); using (source)
        {
            using var preview = source.CreatePosePreview(new Dictionary<Guid, VmdMotion> { [first] = Motion(1), [second] = Motion(2) }, Token);
            var frame = preview.Frame; var sourceFrame = source.Frame;
            Assert.Throws<SceneActorAnimationException>(() => preview.UpdatePosePreviewMotions(
                new Dictionary<Guid, VmdMotion> { [first] = Motion(3), [second] = Motion(4, "unknown") }, Token));
            Assert.Same(frame, preview.Frame); Assert.Same(sourceFrame, source.Frame);
            Assert.Equal(new[] { 1f, 2f }, preview.CaptureAuthoredContent().Motions.Select(binding => binding.Motion.BoneKeyframes[0].Translation.X));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => preview.UpdatePosePreviewMotions(
                new Dictionary<Guid, VmdMotion> { [first] = Motion(3), [second] = Motion(4) }, cancellation.Token));
            Assert.Same(frame, preview.Frame); Assert.Same(sourceFrame, source.Frame);
            Assert.True(preview.CanReusePosePreview(source)); Assert.False(source.CanUndo); Assert.False(preview.CanUndo);
        }
    }

    [Fact]
    public void CallerOwnedContainersCannotMutatePublishedDraftOrHideAReplacement()
    {
        var (source, first, _) = Create(); using (source)
        {
            var keys = new[] { new VmdBoneKeyframe { BoneName = "root", Translation = Vector3.UnitX } };
            var motion = new VmdMotion { BoneKeyframes = keys };
            using var preview = source.CreatePosePreview(first, motion, TestContext.Current.CancellationToken);
            var before = preview.Frame.Surface.Snapshot.Vertices.ToArray().Select(vertex => vertex.Position).ToArray();
            keys[0] = new() { BoneName = "root", Translation = 3 * Vector3.UnitX };
            Assert.Equal(before, preview.Frame.Surface.Snapshot.Vertices.ToArray().Select(vertex => vertex.Position));
            preview.UpdatePosePreviewMotion(first, motion, TestContext.Current.CancellationToken);
            Assert.Equal(before.Take(3).Select(position => position + 2 * Vector3.UnitX),
                preview.Frame.Surface.Snapshot.Vertices.ToArray().Take(3).Select(vertex => vertex.Position));
            Assert.All(source.CaptureAuthoredContent().Motions, binding => Assert.Empty(binding.Motion.BoneKeyframes));
            Assert.False(source.CanUndo); Assert.False(preview.CanUndo);
        }
    }
}
