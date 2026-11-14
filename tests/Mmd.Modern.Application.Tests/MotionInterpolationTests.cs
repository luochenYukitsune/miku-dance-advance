using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class MotionInterpolationTests
{
    [Fact]
    public void EditingYUpdatesOriginalRuntimeAndCanonicalSlotsButPreservesPhysicsMarkerAndOtherCurves()
    {
        var source = Motion(); var before = source.BoneKeyframes[0].Interpolation.ToArray();
        var editor = new MotionEditor(source); var curve = new VmdBezierControl(5, 19, 97, 116);
        var plan = editor.PrepareSetInterpolation([KeyframeAddress.Bone("arm", 10)], BoneInterpolationChannel.Y, curve);
        Assert.Empty(plan.Conflicts); Assert.Equal(before, editor.ExportMotion().BoneKeyframes[0].Interpolation);
        editor.Apply(plan);
        var after = editor.ExportMotion(); var bytes = after.BoneKeyframes[0].Interpolation;
        Assert.Equal(new byte[] { 5, 19, 97, 116 }, new[] { bytes[16], bytes[20], bytes[24], bytes[28] });
        Assert.Equal(new byte[] { 5, 19, 97, 116 }, new[] { bytes[1], bytes[5], bytes[9], bytes[13] });
        Assert.Equal(99, bytes[2]); Assert.Equal(15, bytes[3]); Assert.Equal(233, bytes[63]);
        var a = MmdBoneMotionAdapter.Convert(source).Tracks[0].Keys.Single(k => k.Frame == 10).Curves;
        var b = MmdBoneMotionAdapter.Convert(after).Tracks[0].Keys.Single(k => k.Frame == 10).Curves;
        Assert.Equal(a.X, b.X); Assert.Equal(a.Z, b.Z); Assert.Equal(a.Rotation, b.Rotation);
        Assert.Equal(5 / 127f, b.Y.X1); Assert.Equal(19 / 127f, b.Y.Y1);
        Assert.Equal(97 / 127f, b.Y.X2); Assert.Equal(116 / 127f, b.Y.Y2);
        Assert.Equal(before, source.BoneKeyframes[0].Interpolation);
        Assert.Equal(1ul, editor.Revision); Assert.True(editor.Undo());
        Assert.Equal(before, editor.ExportMotion().BoneKeyframes[0].Interpolation);
        Assert.True(editor.Redo()); Assert.Equal(bytes, editor.ExportMotion().BoneKeyframes[0].Interpolation);
    }

    [Theory]
    [InlineData(BoneInterpolationChannel.Z)]
    [InlineData(BoneInterpolationChannel.Rotation)]
    public void EditingZX1OrRotationX1DoesNotOverwriteTheOriginalPhysicsMarker(BoneInterpolationChannel channel)
    {
        var editor = new MotionEditor(Motion());
        editor.Apply(editor.PrepareSetInterpolation([KeyframeAddress.Bone("arm", 10)], channel, new(9, 27, 91, 123)));
        var bytes = editor.ExportMotion().BoneKeyframes[0].Interpolation;
        Assert.Equal(99, bytes[2]); Assert.Equal(15, bytes[3]);
        Assert.Equal(9, bytes[16 * (int)channel]);
    }

    [Fact]
    public void DuplicateAddressesEditAllDuplicateRecordsAsOneUndoableTransaction()
    {
        var motion = Motion();
        var editor = new MotionEditor(new() { BoneKeyframes = [motion.BoneKeyframes[0], motion.BoneKeyframes[0], new() { BoneName = "other", Frame = 10 }] });
        var curve = new VmdBezierControl(5, 6, 7, 8);
        editor.Apply(editor.PrepareSetInterpolation([KeyframeAddress.Bone("arm", 10), KeyframeAddress.Bone("arm", 10)], BoneInterpolationChannel.X, curve));
        var after = editor.ExportMotion();
        Assert.Equal(3, after.BoneKeyframes.Count);
        Assert.Equal(5, after.BoneKeyframes[0].Interpolation[0]); Assert.Equal(5, after.BoneKeyframes[1].Interpolation[0]);
        Assert.Equal(0, after.BoneKeyframes[2].Interpolation[0]);
        Assert.True(editor.Undo()); Assert.Equal(motion.BoneKeyframes[0].Interpolation, editor.ExportMotion().BoneKeyframes[0].Interpolation);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void EmptyAndIdenticalEditsDoNotCreateHistoryAndOldPlansExpire()
    {
        var editor = new MotionEditor(Motion());
        editor.Apply(editor.PrepareSetInterpolation([], BoneInterpolationChannel.X, new(1, 2, 3, 4)));
        Assert.Equal(0ul, editor.Revision); Assert.False(editor.CanUndo);
        var selection = new[] { KeyframeAddress.Bone("arm", 10) };
        editor.Apply(editor.PrepareSetInterpolation(selection, BoneInterpolationChannel.X, new(5, 6, 7, 8)));
        var stale = editor.PrepareSetInterpolation(selection, BoneInterpolationChannel.Y, new(4, 3, 2, 1));
        editor.Apply(editor.PrepareSetInterpolation(selection, BoneInterpolationChannel.X, new(5, 6, 7, 8)));
        Assert.Equal(1ul, editor.Revision);
        editor.Apply(editor.PrepareSetInterpolation(selection, BoneInterpolationChannel.Y, new(1, 2, 3, 4)));
        Assert.Throws<InvalidOperationException>(() => editor.Apply(stale));
        var other = new MotionEditor(Motion());
        Assert.Throws<InvalidOperationException>(() => other.Apply(editor.PrepareSetInterpolation(selection, BoneInterpolationChannel.X, new(2, 3, 4, 5))));
    }

    [Fact]
    public void InvalidChannelsSelectionsAndControlsFailWithoutMutation()
    {
        var editor = new MotionEditor(Motion());
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmdBezierControl(128, 0, 127, 127));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmdBezierControl(0, 128, 127, 127));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmdBezierControl(0, 0, 128, 127));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmdBezierControl(0, 0, 127, 128));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PrepareSetInterpolation([], (BoneInterpolationChannel)4, new(1, 2, 3, 4)));
        Assert.Throws<ArgumentException>(() => editor.PrepareSetInterpolation([KeyframeAddress.Morph("smile", 1)], BoneInterpolationChannel.X, new(1, 2, 3, 4)));
        Assert.Throws<ArgumentException>(() => editor.PrepareSetInterpolation([KeyframeAddress.Bone("missing", 10)], BoneInterpolationChannel.X, new(1, 2, 3, 4)));
        Assert.Equal(0ul, editor.Revision); Assert.False(editor.CanUndo);
    }

    private static VmdMotion Motion()
    {
        byte[] raw = PmmMotionAdapter.ConvertBoneInterpolation(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        raw[2] = 99; raw[3] = 15; raw[63] = 233;
        return new() { BoneKeyframes = [new() { BoneName = "arm", Frame = 10, Interpolation = raw }],
            MorphKeyframes = [new() { MorphName = "smile", Frame = 1 }] };
    }
}
