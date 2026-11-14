using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmTrackEditorTests
{
    [Fact]
    public void GravityConflictRequiresConfirmationThenRoundTripsAndUndoRestoresExactBytes()
    {
        var source = PmmEditedMotionExporterTests.Document(); byte[] before = PmmWriter.ToBytes(source);
        var editor = new PmmTrackEditor(source);
        var plan = editor.PrepareRegister([new(new(PmmTrackKind.Gravity, 0, "", 0), new PmmGravityKeyframe { Acceleration = 9.8f, Direction = -Vector3.UnitY })]);
        Assert.Single(plan.Conflicts); Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument()));
        editor.Apply(plan, true);
        var result = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(editor.ExportDocument())));
        Assert.Equal(new Vector3(0, -98f, 0), new PmmPhysicsControlTracks(result).GravityAt(5).WorldGravity);
        Assert.True(editor.Undo()); Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument())); Assert.True(editor.Redo());
    }

    [Fact]
    public void ModelOutsideParentAndIkEditsRefreshSavedStateAndPreserveBoneAndRawTail()
    {
        var editor = new PmmTrackEditor(PmmEditedMotionExporterTests.Document());
        var plan = editor.PrepareRegister([new(new(PmmTrackKind.Model, 0, "", 3), new PmmModelKeyframe { Visible = false, ConstraintEnabled = [false], OutsideParents = [new(0, 1, 0)] })]);
        editor.Apply(plan); var result = editor.ExportDocument();
        Assert.False(result.Models[0].Visible); Assert.False(result.Models[0].ConstraintStates[0].Enabled);
        Assert.Equal(1, result.Models[0].OutsideParentStates[0].Parent.ModelIndex);
        Assert.Equal(new byte[] { 222, 173 }, result.UnknownTail);
        Assert.Equal(2, result.Models[0].InitialBoneKeyframes.Count);
        Assert.Equal(3, result.Models[0].ModelKeyframes[0].Frame);
    }

    [Fact]
    public void BonePhysicsUsesExactNameAndDoesNotAliasCallerOrExportedMutableKeys()
    {
        var source = PmmEditedMotionExporterTests.Document(); var editor = new PmmTrackEditor(source);
        var key = new PmmBoneKeyframe { Frame = 999, PhysicsDisabled = true, Translation = new(2, 0, 0), Rotation = Quaternion.Identity, Interpolation = new byte[16] };
        var plan = editor.PrepareRegister([new(new(PmmTrackKind.Bone, 0, "B", 4), key)]); key.Frame = 700;
        editor.Apply(plan); var result = editor.ExportDocument(); result.Models[0].BoneKeyframes.Single(k => k.Frame == 4).Frame = 777;
        Assert.Contains(1, new PmmPhysicsControlTracks(editor.ExportDocument()).DisabledBonesAt(0, 5));
        Assert.DoesNotContain(editor.ExportDocument().Models[0].BoneKeyframes, k => k.Frame == 700 || k.Frame == 777);
        Assert.Throws<ArgumentException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Bone, 0, "b", 4), key)]));
        Assert.Equal(12, source.Models[0].ModelKeyframes[0].Frame);
    }

    [Fact]
    public void MoveCopyDeleteConflictStalePlanAndInitialFrameInvariant()
    {
        var editor = new PmmTrackEditor(PmmEditedMotionExporterTests.Document());
        var a = new PmmTrackAddress(PmmTrackKind.Model, 0, "", 12);
        var copy = editor.CopySelection([a]); var stale = editor.PreparePaste(copy, 24);
        editor.Apply(editor.PrepareMove([a], 8));
        Assert.Throws<InvalidOperationException>(() => editor.Apply(stale));
        editor.Apply(editor.PreparePaste(copy, 30));
        Assert.Equal(new[] { 20, 30 }, editor.ExportDocument().Models[0].ModelKeyframes.Select(k => k.Frame));
        editor.Apply(editor.PrepareDelete([new(PmmTrackKind.Model, 0, "", 20)]));
        Assert.Single(editor.ExportDocument().Models[0].ModelKeyframes);
        Assert.Throws<InvalidOperationException>(() => editor.PrepareDelete([new(PmmTrackKind.Gravity, 0, "", 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PrepareMove([new(PmmTrackKind.Model, 0, "", 30)], long.MaxValue));
    }

    [Fact]
    public void InvalidParentAndNonfiniteGravityCannotMutateCurrentOrUndo()
    {
        var editor = new PmmTrackEditor(PmmEditedMotionExporterTests.Document()); byte[] before = PmmWriter.ToBytes(editor.ExportDocument());
        Assert.ThrowsAny<Exception>(() => editor.PrepareRegister([new(new(PmmTrackKind.Model, 0, "", 7), new PmmModelKeyframe { Visible = true, ConstraintEnabled = [true], OutsideParents = [new(0, 99, 0)] })]));
        Assert.ThrowsAny<Exception>(() => editor.PrepareRegister([new(new(PmmTrackKind.Gravity, 0, "", 7), new PmmGravityKeyframe { Acceleration = float.NaN })]));
        Assert.False(editor.CanUndo); Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument()));
    }
}

