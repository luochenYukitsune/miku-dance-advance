using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class VmdSceneTracksTests
{
    static byte[] Linear() => Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 107, 20, 107 }).ToArray();
    static VmdCameraKeyframe Camera(uint frame, float value, bool perspective = true, byte[]? curve = null) => new()
    { Frame = frame, Interest = new(value, value * 2, value * 3), Rotation = new(value, value, value),
      Distance = -10 - value, ViewAngle = (uint)(30 + value), Perspective = perspective, Interpolation = curve ?? Linear() };

    [Fact]
    public void ControlBytesAbove127AreClampedInsteadOfFailingImportOrEvaluation()
    {
        byte[] curves = Linear();
        curves[0] = 0; curves[1] = 255; curves[2] = 200; curves[3] = 255;
        var tracks = new VmdSceneTracks(new() { CameraKeyframes = [Camera(0, 0, false, curves), Camera(10, 10)] });
        float expected = (float)new BezierCurve(0, 1, 1, 1).Evaluate(.5);
        Assert.Equal(10 * expected, tracks.CameraAt(5)!.Value.LookAt.X, 5);

        byte[] bone = new byte[64];
        for (int c = 0; c < 4; c++) { bone[16 * c] = 20; bone[16 * c + 4] = 200; bone[16 * c + 8] = 255; bone[16 * c + 12] = 107; }
        var clip = MmdBoneMotionAdapter.Convert(new VmdMotion { BoneKeyframes = [new() { BoneName = "A", Frame = 10, Rotation = Quaternion.Identity, Interpolation = bone }] });
        Assert.NotNull(clip);
        Assert.Equal(BezierCurve.FromMmd(20, 127, 127, 107), BezierCurve.FromMmd(20, 200, 255, 107));
    }

    [Fact]
    public void SixOutgoingCameraCurvesUseVmdByteOrderAndKeepRawCoordinates()
    {
        byte[] curves = Linear();
        curves[0] = 0; curves[1] = 127; curves[2] = 127; curves[3] = 127;
        var tracks = new VmdSceneTracks(new() { CameraKeyframes = [Camera(0, 0, false, curves), Camera(10, 10)] });
        var pose = tracks.CameraAt(5)!.Value;
        float expected = (float)new BezierCurve(0, 1, 1, 1).Evaluate(.5);
        Assert.Equal(10 * expected, pose.LookAt.X, 5);
        Assert.Equal(10, pose.LookAt.Y, 5); Assert.Equal(15, pose.LookAt.Z, 5);
        Assert.Equal(new Vector3(5), pose.Angle); Assert.Equal(-15, pose.Distance, 5);
        Assert.Equal(35, pose.Fov, 5); Assert.False(pose.Perspective);
        Assert.True(tracks.CameraAt(10)!.Value.Perspective);
        Assert.Equal(10u, tracks.DurationFrames);
    }

    [Fact]
    public void CameraCutsHoldPreviousFrameAndExactBoundarySelectsNewKey()
    {
        var tracks = new VmdSceneTracks(new() { CameraKeyframes = [Camera(0, 0), Camera(1, 10), Camera(10, 20)] });
        Assert.Equal(Vector3.Zero, tracks.CameraAt(.75)!.Value.LookAt);
        Assert.Equal(10, tracks.CameraAt(1)!.Value.LookAt.X);
        Assert.Equal(20, tracks.CameraAt(999)!.Value.LookAt.X);
    }

    [Fact]
    public void EmptyTracksAreAbsentAndDuplicateFramesUseLastRecordWithoutMutatingSource()
    {
        var keys = new List<VmdCameraKeyframe> { Camera(10, 10), Camera(5, 4), Camera(5, 7) };
        var tracks = new VmdSceneTracks(new() { CameraKeyframes = keys }); keys.Clear();
        Assert.Equal(7, tracks.CameraAt(0)!.Value.LookAt.X);
        Assert.Null(tracks.LightAt(0)); Assert.Null(tracks.SelfShadowAt(0));
        Assert.Null(new VmdSceneTracks(new()).CameraAt(0));
    }

    [Fact]
    public void LightIsLinearWithoutNormalizationAndShadowUsesExplicitStepPolicy()
    {
        var tracks = new VmdSceneTracks(new()
        {
            LightKeyframes = [new() { Frame = 0, Color = Vector3.Zero, Direction = new(0, -2, 0) },
                new() { Frame = 10, Color = new(2, 4, 6), Direction = new(2, 0, 0) }],
            SelfShadowKeyframes = [new() { Frame = 0, Mode = 1, Distance = .01f }, new() { Frame = 10, Mode = 2, Distance = .05f }]
        });
        Assert.Equal(new Vector3(1, 2, 3), tracks.LightAt(5)!.Value.Color);
        Assert.Equal(new Vector3(1, -1, 0), tracks.LightAt(5)!.Value.Direction);
        Assert.Equal((byte)1, tracks.SelfShadowAt(5)!.Value.Mode);
        Assert.Equal(.01f, tracks.SelfShadowAt(5)!.Value.Distance, 6);
        Assert.Equal((byte)2, tracks.SelfShadowAt(10)!.Value.Mode);
    }

    [Theory]
    [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidTimeIsRejectedEvenForEmptyTracks(double frame)
    {
        var tracks = new VmdSceneTracks(new());
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.CameraAt(frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.LightAt(frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.SelfShadowAt(frame));
    }

    [Fact]
    public void InvalidPayloadsAreRejectedBeforePlayback()
    {
        Assert.Throws<ArgumentException>(() => new VmdSceneTracks(new() { LightKeyframes = [new() { Color = new(float.NaN) }] }));
        Assert.Throws<ArgumentException>(() => new VmdSceneTracks(new() { SelfShadowKeyframes = [new() { Mode = 3 }] }));
        var bytes = Linear()[..23];
        Assert.Throws<ArgumentException>(() => new VmdSceneTracks(new() { CameraKeyframes = [Camera(0, 0, curve: bytes)] }));
    }

    [Fact]
    public void SceneTrackTransactionsRequireOverwriteAndRestoreDuplicatePayloadsExactly()
    {
        var editor = new SceneTrackEditor(new() { ModelName = "scene", CameraKeyframes = [Camera(0, 0), Camera(10, 10), Camera(10, 20)],
            BoneKeyframes = [new() { BoneName = "root", Frame = 5, Translation = Vector3.One }] });
        var move = editor.PrepareMove([new(SceneTrackKind.Camera, 0)], 10);
        Assert.Equal(2, Assert.Single(move.Conflicts).ExistingRecordCount);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(move));
        Assert.False(editor.CanUndo); editor.Apply(move, confirmOverwrite: true);
        Assert.Single(editor.ExportMotion().CameraKeyframes); Assert.Equal(10u, editor.ExportMotion().CameraKeyframes[0].Frame);
        Assert.True(editor.Undo()); Assert.Equal(3, editor.ExportMotion().CameraKeyframes.Count);
        Assert.True(editor.Redo()); Assert.Single(editor.ExportMotion().BoneKeyframes);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(move, true));
    }

    [Fact]
    public void RegisterPasteDeleteAndCameraCurveEditAreIndependentUndoableTransactions()
    {
        var editor = new SceneTrackEditor(new() { CameraKeyframes = [Camera(2, 0)] });
        var curve = new VmdBezierControl(10, 20, 90, 100);
        editor.Apply(editor.PrepareSetCameraInterpolation([2u], SceneCameraChannel.Distance, curve));
        Assert.Equal(new byte[] { 10, 90, 20, 100 }, editor.ExportMotion().CameraKeyframes[0].Interpolation.Skip(16).Take(4));
        var clip = editor.CopySelection([new(SceneTrackKind.Camera, 2)]);
        editor.Apply(editor.PreparePaste(clip, 20));
        editor.Apply(editor.PrepareRegister(new() { LightKeyframes = [new() { Frame = 30, Color = Vector3.One }] }));
        editor.Apply(editor.PrepareDelete([new(SceneTrackKind.Camera, 2), new(SceneTrackKind.Light, 30)]));
        Assert.Equal(20u, Assert.Single(editor.ExportMotion().CameraKeyframes).Frame);
        Assert.Empty(editor.ExportMotion().LightKeyframes);
        Assert.True(editor.Undo()); Assert.Single(editor.ExportMotion().LightKeyframes);
        Assert.True(editor.Undo()); Assert.Empty(editor.ExportMotion().LightKeyframes);
        Assert.True(editor.Undo()); Assert.Single(editor.ExportMotion().CameraKeyframes);
        Assert.True(editor.Undo()); Assert.Equal(Linear(), editor.ExportMotion().CameraKeyframes[0].Interpolation);
    }

    [Fact]
    public void InvalidMoveAndCrossEditorPlanCannotChangeStateOrHistory()
    {
        var editor = new SceneTrackEditor(new() { CameraKeyframes = [Camera(0, 0)] });
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PrepareMove([new(SceneTrackKind.Camera, 0)], -1));
        Assert.Throws<ArgumentException>(() => editor.PrepareDelete([new(SceneTrackKind.Light, 0)]));
        Assert.Throws<InvalidOperationException>(() => new SceneTrackEditor(new()).Apply(editor.PrepareDelete([new(SceneTrackKind.Camera, 0)])));
        Assert.False(editor.CanUndo); Assert.Equal(0ul, editor.Revision);
    }

    [Fact]
    public void EditedSceneMotionSurvivesVmdBinaryRoundTripWithCharacterSections()
    {
        var editor = new SceneTrackEditor(new() { ModelName = "scene", CameraKeyframes = [Camera(0, 0)],
            MorphKeyframes = [new() { MorphName = "smile", Frame = 3, Weight = .25f }] });
        editor.Apply(editor.PrepareRegister(new() { SelfShadowKeyframes = [new() { Frame = 20, Mode = 2, Distance = .03f }] }));
        using var bytes = new MemoryStream(VmdWriter.ToBytes(editor.ExportMotion()));
        var restored = VmdReader.Read(bytes);
        Assert.Equal(.25f, Assert.Single(restored.MorphKeyframes).Weight);
        Assert.Equal((byte)2, Assert.Single(restored.SelfShadowKeyframes).Mode);
        Assert.Equal(-10, new VmdSceneTracks(restored).CameraAt(4)!.Value.Distance);
    }

    [Fact]
    public void MoveSelectedDestinationsDoesNotOverwriteOtherSelectedRecordsAndClipboardIsDetached()
    {
        var source = new List<VmdLightKeyframe> { new() { Frame = 2, Color = Vector3.One }, new() { Frame = 4, Color = new(2) } };
        var editor = new SceneTrackEditor(new() { LightKeyframes = source }); source.Clear();
        var clip = editor.CopySelection([new(SceneTrackKind.Light, 2), new(SceneTrackKind.Light, 4)]);
        var move = editor.PrepareMove([new(SceneTrackKind.Light, 2), new(SceneTrackKind.Light, 4)], 2);
        Assert.Empty(move.Conflicts); editor.Apply(move);
        Assert.Equal(new uint[] { 4, 6 }, editor.ExportMotion().LightKeyframes.Select(k => k.Frame));
        editor.Apply(editor.PrepareDelete([new(SceneTrackKind.Light, 4), new(SceneTrackKind.Light, 6)]));
        editor.Apply(editor.PreparePaste(clip, 10));
        Assert.Equal(new uint[] { 10, 12 }, editor.ExportMotion().LightKeyframes.Select(k => k.Frame));
        Assert.Equal(Vector3.One, editor.ExportMotion().LightKeyframes[0].Color);
    }

    [Theory]
    [InlineData(long.MinValue)] [InlineData(long.MaxValue)]
    public void ExtremeFrameDeltaCannotWrapOrMutateHistory(long delta)
    {
        var editor = new SceneTrackEditor(new() { CameraKeyframes = [Camera(4, 0)] });
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PrepareMove([new(SceneTrackKind.Camera, 4)], delta));
        Assert.False(editor.CanUndo); Assert.Equal(4u, editor.ExportMotion().CameraKeyframes[0].Frame);
    }
}
