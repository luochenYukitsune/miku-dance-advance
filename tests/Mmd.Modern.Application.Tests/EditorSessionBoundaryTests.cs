using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class EditorSessionBoundaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static (SceneProjectSession Session, Guid Id, PmxModel Model) Create()
    {
        var model = EditedMotionProjectTests.Model();
        var composition = new SceneCompositionEditor();
        var item = composition.Add("model", Path.GetFullPath("editor-fixture.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var physics = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("No fixture physics"),
            _ => throw new InvalidOperationException("No fixture physics"));
        return (new(composition, new(), [new(item.Id, model, new VmdMotion(), PhysicsEnabled: false)], physics, cancellationToken: Token), item.Id, model);
    }

    [Fact]
    public void ImportedModelWithEmptyAuthoredMotionStartsAtRestWithoutCreatingKeys()
    {
        var (session, _, _) = Create(); using (session)
        {
            var authored = session.CaptureAuthoredContent();
            Assert.Empty(authored.Motions.Single().Motion.BoneKeyframes);
            Assert.Null(authored.Motions.Single().Asset);
            Assert.False(session.CanUndo); Assert.Equal(0u, session.DurationFrames);
            var positions = session.Frame.Surface.Snapshot.Vertices.ToArray().Select(v => v.Position).ToArray();
            session.Seek(10, Token);
            Assert.Equal(positions, session.Frame.Surface.Snapshot.Vertices.ToArray().Select(v => v.Position));
            Assert.Empty(session.CaptureAuthoredContent().Motions.Single().Motion.BoneKeyframes);
        }
    }

    [Fact]
    public void NonemptyUnmatchedMotionStillFailsAndDoesNotPublishTransaction()
    {
        var (session, id, _) = Create(); using (session)
        {
            var before = session.Frame;
            Assert.Throws<SceneActorAnimationException>(() => session.Edit(edit =>
                edit.Motions[0] = edit.Motions[0] with { Motion = new() { CameraKeyframes = [new() { Distance = -30, ViewAngle = 30 }] } }, Token));
            Assert.Same(before, session.Frame); Assert.False(session.CanUndo);
            Assert.Empty(session.CaptureAuthoredContent().Motions.Single(m => m.ObjectId == id).Motion.CameraKeyframes);
        }
    }

    [Fact]
    public void DetachedPoseChangesRenderedVerticesWithoutEditingSourceOrHistory()
    {
        var (session, id, model) = Create(); using (session)
        {
            var before = session.Frame.Surface.Snapshot.Vertices.ToArray().Select(v => v.Position).ToArray();
            var draft = new VmdMotion { BoneKeyframes = [new() { BoneName = model.Bones[0].Name, Frame = 0, Translation = Vector3.UnitX }] };
            using var preview = session.CreatePosePreview(id, draft, Token);
            Assert.True(preview.CanReusePosePreview(session));
            Assert.NotEqual(before, preview.Frame.Surface.Snapshot.Vertices.ToArray().Select(v => v.Position).ToArray());
            Assert.Equal(before, session.Frame.Surface.Snapshot.Vertices.ToArray().Select(v => v.Position));
            Assert.False(session.CanUndo); Assert.False(preview.CanUndo);
            Assert.Empty(session.CaptureAuthoredContent().Motions.Single().Motion.BoneKeyframes);
            session.Edit(edit => edit.Motions[0] = edit.Motions[0] with { IkEnabled = false }, Token);
            Assert.False(preview.CanReusePosePreview(session));
        }
    }

    [Fact]
    public void TransientUpdateCannotBypassLiveProjectUndoBoundary()
    {
        var (session, id, _) = Create(); using (session)
        {
            Assert.Throws<InvalidOperationException>(() => session.UpdatePosePreviewMotion(id, new VmdMotion(), Token));
            Assert.False(session.CanUndo);
        }
    }
}
