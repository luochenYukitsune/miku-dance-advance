using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class MotionClipboardTests
{
    [Fact]
    public void CopyPreservesAllSelectedDuplicateRecordsInSourceOrderWithoutChangingHistory()
    {
        var editor = new MotionEditor(Example());
        var clipboard = editor.CopySelection(Selection());
        Assert.Equal(5u, clipboard.SourceStartFrame);
        Assert.False(clipboard.IsEmpty);
        Assert.Equal(new uint[] { 10, 10, 20 }, clipboard.BoneKeyframes.Select(k => k.Frame));
        Assert.Equal(new[] { 1f, 2f, 3f }, clipboard.BoneKeyframes.Select(k => k.Translation.X));
        Assert.Equal(5u, Assert.Single(clipboard.MorphKeyframes).Frame);
        Assert.Equal(new uint[] { 8, 8 }, clipboard.IkKeyframes.Select(k => k.Frame));
        Assert.Equal(0ul, editor.Revision);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void PasteUsesEarliestFrameAcrossTracksAndPreservesEveryPayloadAndDuplicate()
    {
        var source = new MotionEditor(Example());
        var clipboard = source.CopySelection(Selection());
        var target = new MotionEditor(new() { ModelName = "target", Diagnostics = ["target diagnostic"] });
        var plan = target.PreparePaste(clipboard, 100);
        Assert.Empty(plan.Conflicts);
        Assert.False(target.CanUndo);
        target.Apply(plan);
        var pasted = target.ExportMotion();
        Assert.Equal("target", pasted.ModelName);
        Assert.Equal("target diagnostic", Assert.Single(pasted.Diagnostics));
        Assert.Equal(new uint[] { 105, 105, 115 }, pasted.BoneKeyframes.Select(k => k.Frame));
        Assert.Equal(100u, Assert.Single(pasted.MorphKeyframes).Frame);
        Assert.Equal(new uint[] { 103, 103 }, pasted.IkKeyframes.Select(k => k.Frame));
        Assert.Equal(clipboard.BoneKeyframes[0].Rotation, pasted.BoneKeyframes[0].Rotation);
        Assert.Equal(clipboard.BoneKeyframes[0].Interpolation, pasted.BoneKeyframes[0].Interpolation);
        Assert.Equal(.25f, pasted.MorphKeyframes[0].Weight);
        Assert.False(pasted.IkKeyframes[0].Visible);
        Assert.Equal(clipboard.IkKeyframes[0].Switches, pasted.IkKeyframes[0].Switches);
        AssertMotion(Example(), source.ExportMotion());
    }

    [Fact]
    public void ClipboardIsDeepDetachedReadOnlyAndSurvivesSourceEdits()
    {
        var source = Example();
        var editor = new MotionEditor(source);
        var clipboard = editor.CopySelection(Selection());
        Assert.NotSame(source.BoneKeyframes[0], clipboard.BoneKeyframes[0]);
        Assert.NotSame(source.BoneKeyframes[0].Interpolation, clipboard.BoneKeyframes[0].Interpolation);
        Assert.NotSame(source.MorphKeyframes[0], clipboard.MorphKeyframes[0]);
        Assert.NotSame(source.IkKeyframes[0], clipboard.IkKeyframes[0]);
        Assert.NotSame(source.IkKeyframes[0].Switches, clipboard.IkKeyframes[0].Switches);
        Assert.Throws<NotSupportedException>(() => ((IList<VmdBoneKeyframe>)clipboard.BoneKeyframes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VmdMorphKeyframe>)clipboard.MorphKeyframes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VmdIkKeyframe>)clipboard.IkKeyframes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)clipboard.BoneKeyframes[0].Interpolation)[0] = 99);
        Assert.Throws<NotSupportedException>(() => ((IList<VmdIkSwitch>)clipboard.IkKeyframes[0].Switches).Clear());
        editor.Apply(editor.PrepareDelete(Selection()));
        editor.Apply(editor.PreparePaste(clipboard, 0));
        Assert.Equal(new uint[] { 5, 5, 15 }, editor.ExportMotion().BoneKeyframes.TakeLast(3).Select(k => k.Frame));
    }

    [Fact]
    public void PasteConflictsCountExistingRecordsAndRequireConfirmationBeforeAnyMutation()
    {
        var source = Example();
        var editor = new MotionEditor(source);
        var clipboard = editor.CopySelection(Selection());
        var plan = editor.PreparePaste(clipboard, 5);
        Assert.Equal(4, plan.Conflicts.Count);
        Assert.Equal(2, plan.Conflicts.Single(c => c.Address == KeyframeAddress.Bone("arm", 10)).ExistingRecordCount);
        Assert.Equal(1, plan.Conflicts.Single(c => c.Address == KeyframeAddress.Bone("arm", 20)).ExistingRecordCount);
        Assert.Equal(1, plan.Conflicts.Single(c => c.Address == KeyframeAddress.Morph("smile", 5)).ExistingRecordCount);
        Assert.Equal(2, plan.Conflicts.Single(c => c.Address == KeyframeAddress.Ik(8)).ExistingRecordCount);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        Assert.Equal(0ul, editor.Revision);
        Assert.False(editor.CanUndo);
        AssertMotion(source, editor.ExportMotion());
        editor.Apply(plan, confirmOverwrite: true);
        Assert.Equal(4, editor.ExportMotion().BoneKeyframes.Count);
        Assert.Equal(2, editor.ExportMotion().IkKeyframes.Count);
    }

    [Fact]
    public void ConfirmedPasteUndoRedoRestoresExactOrderPayloadAndOverwrittenDuplicates()
    {
        var source = Example();
        var editor = new MotionEditor(source);
        var clipboard = editor.CopySelection([KeyframeAddress.Bone("arm", 10), KeyframeAddress.Ik(8)]);
        editor.Apply(editor.PreparePaste(clipboard, 18), true);
        var pasted = editor.ExportMotion();
        Assert.Equal(new[] { 1f, 9f, 2f, 1f, 2f }, pasted.BoneKeyframes.Select(k => k.Translation.X));
        Assert.Equal(new uint[] { 8, 8, 18, 18 }, pasted.IkKeyframes.Select(k => k.Frame));
        Assert.True(editor.Undo());
        AssertMotion(source, editor.ExportMotion());
        Assert.True(editor.Redo());
        AssertMotion(pasted, editor.ExportMotion());
        Assert.Equal(3ul, editor.Revision);
    }

    [Fact]
    public void PastePlansAreOwnerAndRevisionBoundIncludingAfterUndoRedo()
    {
        var editor = new MotionEditor(Example());
        var clipboard = editor.CopySelection(Selection());
        var plan = editor.PreparePaste(clipboard, 100);
        Assert.Throws<InvalidOperationException>(() => new MotionEditor(Example()).Apply(plan));
        editor.Apply(plan);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        var beforeUndo = editor.PreparePaste(clipboard, 200);
        editor.Undo();
        Assert.Throws<InvalidOperationException>(() => editor.Apply(beforeUndo));
        var beforeRedo = editor.PreparePaste(clipboard, 200);
        editor.Redo();
        Assert.Throws<InvalidOperationException>(() => editor.Apply(beforeRedo));
    }

    [Fact]
    public void EmptyClipboardPasteIsNoOpAndDoesNotClearRedo()
    {
        var editor = new MotionEditor(Example());
        var clipboard = editor.CopySelection([]);
        Assert.True(clipboard.IsEmpty);
        Assert.Null(clipboard.SourceStartFrame);
        editor.Apply(editor.PrepareDelete([KeyframeAddress.Bone("arm", 10)]));
        editor.Undo();
        var plan = editor.PreparePaste(clipboard, uint.MaxValue);
        Assert.Empty(plan.Conflicts);
        editor.Apply(plan);
        Assert.Equal(2ul, editor.Revision);
        Assert.True(editor.CanRedo);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void FrameRangeChecksAllTracksAtomicallyAndAllowsBothEndpoints()
    {
        var editor = new MotionEditor(new()
        {
            BoneKeyframes = [Bone("arm", 0, 1)],
            MorphKeyframes = [new() { MorphName = "smile", Frame = uint.MaxValue, Weight = .5f }]
        });
        var all = editor.CopySelection([KeyframeAddress.Bone("arm", 0), KeyframeAddress.Morph("smile", uint.MaxValue)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PreparePaste(all, 1));
        Assert.Equal(0ul, editor.Revision);
        Assert.False(editor.CanUndo);
        var target = new MotionEditor(new());
        target.Apply(target.PreparePaste(all, 0));
        Assert.Equal(uint.MaxValue, Assert.Single(target.ExportMotion().MorphKeyframes).Frame);
        var single = editor.CopySelection([KeyframeAddress.Morph("smile", uint.MaxValue)]);
        target.Apply(target.PreparePaste(single, 0));
        Assert.Equal(new uint[] { uint.MaxValue, 0 }, target.ExportMotion().MorphKeyframes.Select(k => k.Frame));
    }

    [Fact]
    public void CopyValidatesMissingAndMalformedAddressesAndNullArguments()
    {
        var editor = new MotionEditor(Example());
        Assert.Throws<ArgumentNullException>(() => editor.CopySelection(null!));
        Assert.Throws<ArgumentException>(() => editor.CopySelection([KeyframeAddress.Bone("missing", 10)]));
        Assert.Throws<ArgumentException>(() => editor.CopySelection([new((KeyframeTrackKind)42, "arm", 10)]));
        Assert.Throws<ArgumentException>(() => editor.CopySelection([new(KeyframeTrackKind.Ik, "named", 8)]));
        Assert.Throws<ArgumentNullException>(() => editor.PreparePaste(null!, 0));
        Assert.Equal(0ul, editor.Revision);
    }

    [Fact]
    public void RegisterUsesTheSameConflictAndUndoContractWithAbsoluteFramesAndDetachedInput()
    {
        var editor = new MotionEditor(Example());
        var records = new[] { Bone("arm", 10, 77), Bone("arm", 10, 88), Bone("new", 0, 99) };
        var plan = editor.PrepareRegister(new()
        {
            ModelName = "ignored", Diagnostics = ["ignored"], BoneKeyframes = records,
            MorphKeyframes = [new() { MorphName = "smile", Frame = 5, Weight = .9f }],
            IkKeyframes = [new() { Frame = 8, Visible = true, Switches = [new("ik", true)] }]
        });
        records[0] = Bone("changed", 100, 0);
        Assert.Equal(3, plan.Conflicts.Count);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        editor.Apply(plan, true);
        var result = editor.ExportMotion();
        Assert.Equal(new[] { 9f, 3f, 77f, 88f, 99f }, result.BoneKeyframes.Select(k => k.Translation.X));
        Assert.Equal(new uint[] { 3, 20, 10, 10, 0 }, result.BoneKeyframes.Select(k => k.Frame));
        Assert.Equal(.9f, Assert.Single(result.MorphKeyframes).Weight);
        Assert.True(Assert.Single(result.IkKeyframes).Visible);
        Assert.Equal("model", result.ModelName);
        Assert.Equal("original diagnostic", Assert.Single(result.Diagnostics));
        Assert.True(editor.Undo());
        AssertMotion(Example(), editor.ExportMotion());
        Assert.True(editor.Redo());
        AssertMotion(result, editor.ExportMotion());
        Assert.Throws<ArgumentNullException>(() => editor.PrepareRegister(null!));
    }

    [Theory]
    [InlineData("bone-record")]
    [InlineData("morph-record")]
    [InlineData("ik-record")]
    [InlineData("bone-name")]
    [InlineData("morph-name")]
    [InlineData("ik-switch-name")]
    [InlineData("bone-list")]
    [InlineData("morph-list")]
    [InlineData("ik-list")]
    public void RegisterRejectsMissingRecordsAndNamesAtomically(string invalid)
    {
        var records = invalid switch
        {
            "bone-record" => new VmdMotion { BoneKeyframes = [null!] },
            "morph-record" => new VmdMotion { MorphKeyframes = [null!] },
            "ik-record" => new VmdMotion { IkKeyframes = [null!] },
            "bone-name" => new VmdMotion { BoneKeyframes = [new() { BoneName = null! }] },
            "morph-name" => new VmdMotion { MorphKeyframes = [new() { MorphName = null! }] },
            "ik-switch-name" => new VmdMotion { IkKeyframes = [new() { Switches = [new(null!, true)] }] },
            "bone-list" => new VmdMotion { BoneKeyframes = null! },
            "morph-list" => new VmdMotion { MorphKeyframes = null! },
            "ik-list" => new VmdMotion { IkKeyframes = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        AssertRegistrationRejected(records);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RegisterRejectsEveryNonFiniteBoneComponentAndMorphWeightAtomically(float invalid)
    {
        for (var component = 0; component < 7; component++)
        {
            float[] values = [1, 2, 3, .1f, .2f, .3f, .4f];
            values[component] = invalid;
            AssertRegistrationRejected(new()
            {
                BoneKeyframes = [new()
                {
                    BoneName = "arm", Frame = 20,
                    Translation = new(values[0], values[1], values[2]),
                    Rotation = new(values[3], values[4], values[5], values[6])
                }]
            });
        }
        AssertRegistrationRejected(new() { MorphKeyframes = [new() { MorphName = "smile", Weight = invalid }] });
    }

    [Fact]
    public void RegisterRejectsExactlyZeroQuaternionAtomically()
    {
        AssertRegistrationRejected(new() { BoneKeyframes = [new() { BoneName = "arm", Rotation = new(0, -0f, 0, -0f) }] });
    }

    [Fact]
    public void RegisterPreservesFiniteRawQuaternionsAndUnclampedMorphWeights()
    {
        var editor = new MotionEditor(new());
        var records = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "tiny", Rotation = new(float.Epsilon, 0, 0, 0) },
                new() { BoneName = "large", Translation = new(float.MaxValue, -float.MaxValue, float.Epsilon),
                    Rotation = new(float.MaxValue, -float.MaxValue, 0, 1) }
            ],
            MorphKeyframes = [new() { MorphName = "negative", Weight = -3 }, new() { MorphName = "large", Weight = float.MaxValue }]
        };
        editor.Apply(editor.PrepareRegister(records));
        AssertMotion(records, editor.ExportMotion());
        Assert.True(editor.Undo());
        Assert.Empty(editor.ExportMotion().BoneKeyframes);
        Assert.True(editor.Redo());
        AssertMotion(records, editor.ExportMotion());
    }

    private static void AssertRegistrationRejected(VmdMotion records)
    {
        var editor = new MotionEditor(Example());
        editor.Apply(editor.PrepareDelete([KeyframeAddress.Bone("arm", 10)]));
        editor.Apply(editor.PrepareDelete([KeyframeAddress.Bone("arm", 20)]));
        editor.Undo();
        var before = editor.ExportMotion();
        var revision = editor.Revision;
        Assert.True(editor.CanUndo);
        Assert.True(editor.CanRedo);
        Assert.Throws<ArgumentException>(() => editor.PrepareRegister(records));
        Assert.Equal(revision, editor.Revision);
        Assert.True(editor.CanUndo);
        Assert.True(editor.CanRedo);
        AssertMotion(before, editor.ExportMotion());
        Assert.True(editor.Undo());
        AssertMotion(Example(), editor.ExportMotion());
        Assert.True(editor.Redo());
        AssertMotion(before, editor.ExportMotion());
    }

    private static KeyframeAddress[] Selection() =>
        [KeyframeAddress.Bone("arm", 20), KeyframeAddress.Morph("smile", 5), KeyframeAddress.Ik(8),
         KeyframeAddress.Bone("arm", 10), KeyframeAddress.Bone("arm", 10)];

    private static VmdMotion Example() => new()
    {
        ModelName = "model", Diagnostics = ["original diagnostic"],
        BoneKeyframes = [Bone("arm", 10, 1), Bone("unrelated", 3, 9), Bone("arm", 10, 2), Bone("arm", 20, 3)],
        MorphKeyframes = [new() { MorphName = "smile", Frame = 5, Weight = .25f }],
        IkKeyframes = [new() { Frame = 8, Visible = false, Switches = [new("ik", false), new("ik", true)] }, new() { Frame = 8 }]
    };

    private static VmdBoneKeyframe Bone(string name, uint frame, float x) => new()
    {
        BoneName = name, Frame = frame, Translation = new Vector3(x, -x, 2 * x),
        Rotation = new Quaternion(.1f, .2f, .3f, -.4f),
        Interpolation = Enumerable.Range(0, 64).Select(i => (byte)(i + (int)x)).ToArray()
    };

    private static void AssertMotion(VmdMotion expected, VmdMotion actual)
    {
        Assert.Equal(expected.ModelName, actual.ModelName);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        Assert.Equal(expected.BoneKeyframes.Count, actual.BoneKeyframes.Count);
        for (var i = 0; i < expected.BoneKeyframes.Count; i++)
        {
            var e = expected.BoneKeyframes[i]; var a = actual.BoneKeyframes[i];
            Assert.Equal((e.BoneName, e.Frame, e.Translation, e.Rotation), (a.BoneName, a.Frame, a.Translation, a.Rotation));
            Assert.Equal(e.Interpolation, a.Interpolation);
        }
        Assert.Equal(expected.MorphKeyframes.Select(k => (k.MorphName, k.Frame, k.Weight)), actual.MorphKeyframes.Select(k => (k.MorphName, k.Frame, k.Weight)));
        Assert.Equal(expected.IkKeyframes.Count, actual.IkKeyframes.Count);
        for (var i = 0; i < expected.IkKeyframes.Count; i++)
        {
            Assert.Equal(expected.IkKeyframes[i].Frame, actual.IkKeyframes[i].Frame);
            Assert.Equal(expected.IkKeyframes[i].Visible, actual.IkKeyframes[i].Visible);
            Assert.Equal(expected.IkKeyframes[i].Switches, actual.IkKeyframes[i].Switches);
        }
    }
}
