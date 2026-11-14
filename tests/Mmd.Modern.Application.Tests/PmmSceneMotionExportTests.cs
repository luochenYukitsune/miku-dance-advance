using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmSceneMotionExportTests
{
    [Fact]
    public void AsymmetricSixCameraCurvesAndSceneValuesSurviveWriterReaderAndSampling()
    {
        var source = PmmEditedMotionExporterTests.Document();
        byte[] before = PmmWriter.ToBytes(source);
        var motion = Motion();
        var result = PmmSceneMotionExporter.ReplaceSceneMotion(source, motion, 7);
        var reread = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(result)));
        var expected = new VmdSceneTracks(motion); var actual = new PmmSceneTracks(reread);
        foreach (double f in new[] { 0d, .25, 2, 5, 7, 9.999, 10, 10.5, 11, 12, 100 })
        {
            var a = expected.CameraAt(f)!.Value; var b = actual.CameraAt(f);
            Assert.InRange(Vector3.Distance(a.LookAt, b.LookAt), 0, 2e-5f);
            Assert.InRange(Vector3.Distance(a.Angle, b.Angle), 0, 2e-5f);
            Assert.Equal(a.Distance, b.Distance, 4); Assert.Equal(a.Fov, b.Fov, 4);
            Assert.Equal(a.Perspective, b.Perspective); Assert.Equal(-1, b.ParentModelIndex);
            Assert.Equal(expected.LightAt(f)!.Value, actual.LightAt(f));
            Assert.Equal(expected.SelfShadowAt(f)!.Value.Mode, actual.SelfShadowAt(f).Mode);
            Assert.Equal(expected.SelfShadowAt(f)!.Value.Distance, actual.SelfShadowAt(f).Distance);
        }
        Assert.Equal(before, PmmWriter.ToBytes(source));
        Assert.Same(source.Models, result.Models); Assert.Same(source.Gravity, result.Gravity);
        Assert.Same(source.Accessories, result.Accessories); Assert.Equal(source.UnknownTail, reread.UnknownTail);
        Assert.Equal(7, reread.CurrentFrame);
        Assert.Equal(expected.CameraAt(7)!.Value with { ParentBoneIndex = -1 }, actual.SavedCamera);
        Assert.Equal(expected.LightAt(7)!.Value, actual.SavedLight);
        Assert.Equal(new PmmSelfShadowSavedState(true, .01f), actual.SavedSelfShadow);
    }

    [Fact]
    public void FirstKeyAfterZeroClampsToFirstEditedValuesAndDuplicateLastWins()
    {
        var motion = new VmdMotion { CameraKeyframes = [Camera(8, 1), Camera(8, 3), Camera(20, 8)],
            LightKeyframes = [new() { Frame = 8, Color = new(.2f), Direction = Vector3.UnitY }],
            SelfShadowKeyframes = [new() { Frame = 8, Mode = 2, Distance = .03f }] };
        var changed = PmmSceneMotionExporter.ReplaceSceneMotion(PmmEditedMotionExporterTests.Document(), motion, 0);
        Assert.Equal(new[] { 0, 8, 20 }, new[] { changed.Camera.InitialKeyframe }.Concat(changed.Camera.Keyframes).Select(k => k.Frame));
        Assert.Equal(3, changed.Camera.InitialKeyframe.LookAt.X);
        var tracks = new PmmSceneTracks(changed); var expected = new VmdSceneTracks(motion);
        foreach (double f in new[] { 0, 4, 8, 10, 20 })
        {
            var a = expected.CameraAt(f)!.Value; var b = tracks.CameraAt(f);
            Assert.InRange(Vector3.Distance(a.LookAt, b.LookAt), 0, 2e-5f);
            Assert.InRange(Vector3.Distance(a.Angle, b.Angle), 0, 2e-5f);
            Assert.Equal(a.Distance, b.Distance, 4); Assert.Equal(a.Fov, b.Fov, 4);
            Assert.Equal(a.Perspective, b.Perspective);
        }
        Assert.Equal(.2f, tracks.LightAt(0).Color.X); Assert.Equal((byte)2, tracks.SelfShadowAt(0).Mode);
        Assert.Equal(0, changed.Camera.InitialKeyframe.ObjectIndex);
        Assert.Equal(1, changed.Camera.InitialKeyframe.NextIndex);
        Assert.Equal(0, changed.Camera.Keyframes[0].PreviousIndex);
        Assert.Equal(2, changed.Camera.Keyframes[0].NextIndex);
        Assert.Equal(1, changed.Camera.Keyframes[1].PreviousIndex);
        Assert.Equal(0, changed.Camera.Keyframes[1].NextIndex);
    }

    [Fact]
    public void EmptyTracksPreserveSourceAndExplicitSavedPoseOptOutPreservesEditorState()
    {
        var source = PmmEditedMotionExporterTests.Document();
        var unchanged = PmmSceneMotionExporter.ReplaceSceneMotion(source, new());
        Assert.Same(source.Camera, unchanged.Camera); Assert.Same(source.Light, unchanged.Light);
        Assert.Same(source.SelfShadow, unchanged.SelfShadow); Assert.Same(source.Settings, unchanged.Settings);
        var changed = PmmSceneMotionExporter.ReplaceSceneMotion(source, Motion(), refreshSavedPose: false);
        Assert.Equal(source.Camera.LookAt, changed.Camera.LookAt); Assert.Equal(source.Camera.Position, changed.Camera.Position);
        Assert.Equal(source.CameraFov, changed.CameraFov); Assert.Same(source.Settings, changed.Settings);
        Assert.Equal(source.Light.Color, changed.Light.Color); Assert.Equal(source.SelfShadow.Enabled, changed.SelfShadow.Enabled);
    }

    [Theory]
    [InlineData(-1)] [InlineData(.5)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(2147483648d)]
    public void InvalidEditorFrameCannotBeSilentlyRounded(double frame)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PmmSceneMotionExporter.ReplaceSceneMotion(PmmEditedMotionExporterTests.Document(), new(), frame));

    [Fact]
    public void InvalidDuplicateAndUnrepresentableCameraValuesAreRejectedBeforeReplacement()
    {
        var source = PmmEditedMotionExporterTests.Document(); byte[] before = PmmWriter.ToBytes(source);
        var invalids = new VmdMotion[] {
            new() { CameraKeyframes = [new() { Frame = uint.MaxValue }] },
            new() { CameraKeyframes = [new() { ViewAngle = uint.MaxValue }] },
            new() { CameraKeyframes = [new() { Interest = new(float.NaN, 0, 0) }, Camera(0, 1)] },
            new() { LightKeyframes = [new() { Frame = uint.MaxValue }] },
            new() { SelfShadowKeyframes = [new() { Frame = uint.MaxValue }] },
            new() { SelfShadowKeyframes = [new() { Mode = 3 }] },
        };
        foreach (var motion in invalids) Assert.ThrowsAny<ArgumentException>(() => PmmSceneMotionExporter.ReplaceSceneMotion(source, motion));
        Assert.Equal(before, PmmWriter.ToBytes(source));
        Assert.Throws<NotSupportedException>(() => PmmSceneMotionExporter.ReplaceSceneMotion(source, new() { BoneKeyframes = [new() { BoneName = "A" }] }));
    }

    [Fact]
    public void CameraReplacementClearsSavedParentWithoutLosingUnrelatedSettings()
    {
        var source = PmmEditedMotionExporterTests.Document();
        var changed = PmmSceneMotionExporter.ReplaceSceneMotion(source, new() { CameraKeyframes = [Camera(0, 4)] });
        Assert.Equal(-1, changed.Settings.CameraLookAtModelIndex); Assert.Equal(-1, changed.Settings.CameraLookAtBoneIndex);
        Assert.Equal(source.Settings.AudioPath, changed.Settings.AudioPath);
        Assert.Equal(source.Settings.UnknownMatrix, changed.Settings.UnknownMatrix);
        Assert.Equal(source.Settings.SelectionTrailerFlag, changed.Settings.SelectionTrailerFlag);
    }

    internal static VmdMotion Motion() => new()
    {
        CameraKeyframes = [Camera(0, 0), Camera(10, 10), Camera(11, 20)],
        LightKeyframes = [new() { Color = new(.2f, .4f, .8f), Direction = new(1, 2, 3) }, new() { Frame = 10, Color = new(.7f, .1f, .3f), Direction = new(-1, -3, 2) }],
        SelfShadowKeyframes = [new() { Mode = 2, Distance = .01f }, new() { Frame = 10, Mode = 0, Distance = .02f }],
    };
    static VmdCameraKeyframe Camera(uint frame, float value) => new()
    {
        Frame = frame, Interest = new(value, value * 2, value * 3), Rotation = new(value * .01f, value * .03f, -value * .02f),
        Distance = -30 - value, ViewAngle = (uint)(30 + value), Perspective = frame != 11,
        Interpolation = Enumerable.Range(0, 6).SelectMany(c => new byte[] { (byte)(5 + c * 5), (byte)(90 + c * 3), (byte)(80 - c * 7), (byte)(120 - c * 2) }).ToArray(),
    };
}
