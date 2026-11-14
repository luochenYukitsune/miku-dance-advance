using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class MotionEditingTests
{
    [Fact]
    public void MoveRequiresExplicitOverwriteAndUndoRestoresEveryOriginalRecordInOrder()
    {
        var source = new VmdMotion
        {
            ModelName = "model",
            BoneKeyframes = [Bone("arm", 10, 1), Bone("unrelated", 3, 9), Bone("arm", 20, 2), Bone("arm", 20, 3)],
            MorphKeyframes = [new() { MorphName = "smile", Frame = 7, Weight = .5f }],
            IkKeyframes = [new() { Frame = 4, Visible = false, Switches = [new("ik", false)] }],
            Diagnostics = ["source diagnostic"]
        };
        var editor = new MotionEditor(source);
        var plan = editor.PrepareMove([KeyframeAddress.Bone("arm", 10)], 10);

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(KeyframeAddress.Bone("arm", 20), conflict.Address);
        Assert.Equal(2, conflict.ExistingRecordCount);
        Assert.Equal(0ul, plan.Revision);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        Assert.Equal(0ul, editor.Revision);
        Assert.False(editor.CanUndo);
        Assert.Equal(new[] { 1f, 9f, 2f, 3f }, editor.ExportMotion().BoneKeyframes.Select(k => k.Translation.X));

        editor.Apply(plan, confirmOverwrite: true);
        Assert.Equal(new[] { "arm", "unrelated" }, editor.ExportMotion().BoneKeyframes.Select(k => k.BoneName));
        Assert.Equal(20u, editor.ExportMotion().BoneKeyframes[0].Frame);
        Assert.Equal(.5f, Assert.Single(editor.ExportMotion().MorphKeyframes).Weight);
        Assert.False(Assert.Single(editor.ExportMotion().IkKeyframes).Visible);
        Assert.Equal("source diagnostic", Assert.Single(editor.ExportMotion().Diagnostics));

        Assert.True(editor.Undo());
        Assert.Equal(new[] { 1f, 9f, 2f, 3f }, editor.ExportMotion().BoneKeyframes.Select(k => k.Translation.X));
        Assert.False(editor.CanUndo);
        Assert.True(editor.Redo());
        Assert.Equal(2, editor.ExportMotion().BoneKeyframes.Count);
        Assert.False(editor.CanRedo);
    }

    private static VmdBoneKeyframe Bone(string name, uint frame, float x)
        => new() { BoneName = name, Frame = frame, Translation = new Vector3(x, 0, 0) };

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-11L)]
    [InlineData(4294967296L)]
    public void MoveRejectsOutOfRangeWithoutMutation(long delta)
    {
        var editor = new MotionEditor(new() { BoneKeyframes = [Bone("arm", 10, 1)] });
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PrepareMove([KeyframeAddress.Bone("arm", 10)], delta));
        Assert.Equal(0ul, editor.Revision); Assert.False(editor.CanUndo);
    }

    [Fact]
    public void SelectedDestinationIsMovedAwayInsteadOfOverwrittenAndDuplicatesTravelTogether()
    {
        var editor = new MotionEditor(new() { BoneKeyframes = [Bone("arm", 10, 1), Bone("arm", 10, 2), Bone("arm", 20, 3)] });
        var plan = editor.PrepareMove([KeyframeAddress.Bone("arm", 10), KeyframeAddress.Bone("arm", 20)], 10);
        Assert.Empty(plan.Conflicts); editor.Apply(plan);
        Assert.Equal(new uint[] { 20, 20, 30 }, editor.ExportMotion().BoneKeyframes.Select(k => k.Frame));
        Assert.Equal(new[] { 1f, 2f, 3f }, editor.ExportMotion().BoneKeyframes.Select(k => k.Translation.X));
    }

    [Fact]
    public void MorphAndIkMoveAreOneUndoableBatchWithSeparateTrackConflicts()
    {
        var editor = new MotionEditor(new()
        {
            MorphKeyframes = [new() { MorphName = "smile", Frame = 5, Weight = .3f }, new() { MorphName = "smile", Frame = 15, Weight = .8f }],
            IkKeyframes = [new() { Frame = 5, Visible = false, Switches = [new("knee", false)] }, new() { Frame = 15 }]
        });
        var plan = editor.PrepareMove([KeyframeAddress.Morph("smile", 5), KeyframeAddress.Ik(5)], 10);
        Assert.Equal(2, plan.Conflicts.Count); editor.Apply(plan, true);
        Assert.Equal(.3f, Assert.Single(editor.ExportMotion().MorphKeyframes).Weight);
        var ik = Assert.Single(editor.ExportMotion().IkKeyframes);
        Assert.Equal(15u, ik.Frame); Assert.False(ik.Visible); Assert.False(Assert.Single(ik.Switches).Enabled);
        Assert.True(editor.Undo()); Assert.Equal(2, editor.ExportMotion().IkKeyframes.Count);
        Assert.Equal(2, editor.ExportMotion().MorphKeyframes.Count);
    }

    [Fact]
    public void PlansAreOwnerBoundAndBecomeStaleAfterCommitOrUndo()
    {
        var source = new VmdMotion { BoneKeyframes = [Bone("arm", 10, 1)] };
        var editor = new MotionEditor(source); var other = new MotionEditor(source);
        var plan = editor.PrepareMove([KeyframeAddress.Bone("arm", 10)], 1);
        Assert.Throws<InvalidOperationException>(() => other.Apply(plan));
        editor.Apply(plan);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        Assert.True(editor.Undo()); Assert.Equal(2ul, editor.Revision);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
    }

    [Fact]
    public void DeletePreservesDuplicatesForUndoAndNewCommitClearsRedo()
    {
        var editor = new MotionEditor(new() { BoneKeyframes = [Bone("arm", 10, 1), Bone("arm", 10, 2), Bone("arm", 20, 3)] });
        editor.Apply(editor.PrepareDelete([KeyframeAddress.Bone("arm", 10)]));
        Assert.Single(editor.ExportMotion().BoneKeyframes);
        Assert.True(editor.Undo()); Assert.True(editor.CanRedo);
        editor.Apply(editor.PrepareMove([KeyframeAddress.Bone("arm", 20)], 1));
        Assert.False(editor.CanRedo); Assert.False(editor.Redo());
        Assert.Equal(3, editor.ExportMotion().BoneKeyframes.Count);
    }

    [Fact]
    public void InputArraysCannotMutateEditorAndExportIsReadOnly()
    {
        var keys = new[] { Bone("arm", 10, 1) }; var diagnostics = new[] { "original" };
        var editor = new MotionEditor(new() { BoneKeyframes = keys, Diagnostics = diagnostics });
        keys[0] = Bone("changed", 1, 0); diagnostics[0] = "changed";
        var exported = editor.ExportMotion();
        Assert.Equal("arm", exported.BoneKeyframes[0].BoneName); Assert.Equal("original", exported.Diagnostics[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<VmdBoneKeyframe>)exported.BoneKeyframes)[0] = keys[0]);
    }

    [Fact]
    public void NoOpCreatesNoHistoryAndDoesNotClearRedo()
    {
        var editor = new MotionEditor(new() { BoneKeyframes = [Bone("arm", 10, 1)] });
        editor.Apply(editor.PrepareMove([KeyframeAddress.Bone("arm", 10)], 1)); editor.Undo();
        editor.Apply(editor.PrepareMove([KeyframeAddress.Bone("arm", 10)], 0));
        editor.Apply(editor.PrepareDelete([]));
        Assert.Equal(2ul, editor.Revision); Assert.True(editor.CanRedo); Assert.False(editor.CanUndo);
    }

    [Fact]
    public void MissingAddressIsAnErrorAndUndoLimitIsBounded()
    {
        var editor = new MotionEditor(new() { BoneKeyframes = [Bone("arm", 10, 1)] }, undoLimit: 1);
        Assert.Throws<ArgumentException>(() => editor.PrepareDelete([KeyframeAddress.Bone("absent", 10)]));
        editor.Apply(editor.PrepareMove([KeyframeAddress.Bone("arm", 10)], 1));
        editor.Apply(editor.PrepareMove([KeyframeAddress.Bone("arm", 11)], 1));
        Assert.True(editor.Undo()); Assert.False(editor.Undo());
        Assert.Equal(11u, editor.ExportMotion().BoneKeyframes[0].Frame);
    }
}
