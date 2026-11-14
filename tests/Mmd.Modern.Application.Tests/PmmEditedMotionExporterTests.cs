using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmEditedMotionExporterTests
{
    [Fact]
    public void EditedNamedTracksSaveReopenAndRefreshRawEditorPoseWithoutTouchingOriginal()
    {
        var source = Document();
        byte[] before = PmmWriter.ToBytes(source);
        var edited = PmmEditedMotionExporter.ReplaceModelMotion(source, 0, PmmMotionAdapterTests.Pmx(["B", "A"], ["smile"]), Motion());
        var reopened = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(edited)));
        var restored = PmmMotionAdapter.Convert(reopened.Models[0], PmmMotionAdapterTests.Pmx(["A", "B"], ["smile"]));
        Assert.Equal(new uint[] { 0, 10, 20 }, restored.BoneKeyframes.Where(k => k.BoneName == "A").Select(k => k.Frame));
        Assert.Equal(20, restored.BoneKeyframes.Single(k => k.BoneName == "A" && k.Frame == 20).Translation.X);
        Assert.Equal(5, reopened.Models[0].BoneStates[0].Translation.X, 4);
        Assert.Equal(.5f, reopened.Models[0].MorphStates[0].Weight, 4);
        Assert.Equal(77, reopened.Models[0].BoneStates[1].Translation.X);
        Assert.False(reopened.Models[0].Visible);
        Assert.False(reopened.Models[0].ConstraintStates[0].Enabled);
        Assert.Equal(before, PmmWriter.ToBytes(source));
        Assert.Equal(source.UnknownTail, reopened.UnknownTail);
        Assert.Equal(20, reopened.Models[0].LastFrame);
    }

    [Fact]
    public void BoneCurvesUseRuntimeRedundantBlocksAndRetainPerBonePhysicsAtTransition()
    {
        byte[] raw = new byte[64];
        for (int c = 0; c < 4; c++) for (int i = 0; i < 4; i++) raw[16 * c + 4 * i] = (byte)(10 + c * 20 + i);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "A", Frame = 0, Interpolation = raw }, new() { BoneName = "A", Frame = 10, Interpolation = raw }] };
        var result = PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, PmmMotionAdapterTests.Pmx(["A", "B"], ["smile"]), motion);
        for (int c = 0; c < 4; c++) for (int i = 0; i < 4; i++) Assert.Equal(raw[16 * c + 4 * i], result.Models[0].InitialBoneKeyframes[0].Interpolation[c * 4 + i]);
        Assert.True(result.Models[0].BoneKeyframes.Single(k => k.Frame == 10).PhysicsDisabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsymmetricCurvesAndConflictingRedundantBytesPreserveActualAnimationEvaluation(bool conflicting)
    {
        var pmmCurves = new byte[] { 10, 80, 100, 120, 30, 5, 90, 115, 50, 100, 110, 120, 60, 10, 100, 80 };
        byte[] raw = PmmMotionAdapter.ConvertBoneInterpolation(pmmCurves);
        if (conflicting) { raw[1] = 120; raw[5] = 2; raw[9] = 124; raw[13] = 3; }
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "A", Frame = 0 }, new() { BoneName = "A", Frame = 10,
            Translation = new(10, 20, 30), Rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), 1.1f), Interpolation = raw }] };
        var pmx = PmmMotionAdapterTests.Pmx(["A", "B"]);
        var changed = PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, pmx, motion);
        var reread = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(changed)));
        var reimported = PmmMotionAdapter.Convert(reread.Models[0], pmx);
        var rig = new CharacterRig([new("A", Vector3.Zero, -1), new("B", Vector3.Zero, -1)], [], []);
        var before = new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(motion));
        var after = new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(reimported));
        foreach (double frame in new[] { 0d, .25, 2.5, 5, 8.75, 10 })
        {
            var a = before.Sample(frame).BoneWorldMatrices[0]; var b = after.Sample(frame).BoneWorldMatrices[0];
            foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                Assert.InRange(Vector3.Distance(Vector3.Transform(point, a), Vector3.Transform(point, b)), 0, 2e-5f);
        }
    }

    [Fact]
    public void PhysicsSwitchCannotDisappearWhenReplacingBoneTimeline()
    {
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "A", Frame = 20 }] };
        Assert.Contains("physics", Assert.Throws<NotSupportedException>(() => PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, PmmMotionAdapterTests.Pmx(["A", "B"]), motion)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutsideParentTimelineAndUneditedModelMetadataSurviveInsertedIkKeys()
    {
        var source = Document();
        var result = PmmEditedMotionExporter.ReplaceModelMotion(source, 0, PmmMotionAdapterTests.Pmx(["A", "B"]), new() { IkKeyframes = [new() { Frame = 5, Visible = false, Switches = [new("A", false)] }] });
        Assert.Equal(new[] { 5, 12 }, result.Models[0].ModelKeyframes.Select(k => k.Frame));
        Assert.Equal(-1, result.Models[0].ModelKeyframes[0].OutsideParents[0].ModelIndex);
        Assert.Equal(1, result.Models[0].ModelKeyframes[1].OutsideParents[0].ModelIndex);
        Assert.False(result.Models[0].ModelKeyframes[1].ConstraintEnabled[0]);
        Assert.Same(source.Models[0].OutsideParentStates, result.Models[0].OutsideParentStates);
        Assert.Same(source.Camera, result.Camera);
        Assert.Same(source.Models[1], result.Models[1]);
        Assert.Equal(source.Models[0].EdgeWidth, result.Models[0].EdgeWidth);
    }

    [Fact]
    public void SavedEditorStateCanExplicitlyRemainUntouched()
    {
        var source = Document();
        var result = PmmEditedMotionExporter.ReplaceModelMotion(source, 0, PmmMotionAdapterTests.Pmx(["A", "B"], ["smile"]), Motion(), refreshSavedPose: false);
        Assert.Same(source.Models[0].BoneStates, result.Models[0].BoneStates);
        Assert.Same(source.Models[0].MorphStates, result.Models[0].MorphStates);
        Assert.True(result.Models[0].Visible);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("a")]
    public void UnknownOrCaseMismatchedTrackNeverGuessesIndex(string name)
        => Assert.Throws<InvalidDataException>(() => PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, PmmMotionAdapterTests.Pmx(["A", "B"]), new() { BoneKeyframes = [new() { BoneName = name }] }));

    [Fact]
    public void AmbiguousModelBoneNameAndNonConstraintIkAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, PmmMotionAdapterTests.Pmx(["A", "A", "B"]), Motion()));
        Assert.Throws<InvalidDataException>(() => PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, PmmMotionAdapterTests.Pmx(["A", "B"]), new() { IkKeyframes = [new() { Switches = [new("B", false)] }] }));
    }

    [Fact]
    public void SceneSectionsAndNonFiniteOrOversizedFramesAreRejectedRatherThanDropped()
    {
        var pmx = PmmMotionAdapterTests.Pmx(["A", "B"]);
        foreach (var motion in new VmdMotion[] { new() { CameraKeyframes = [new()] }, new() { LightKeyframes = [new()] }, new() { SelfShadowKeyframes = [new()] } })
            Assert.Throws<NotSupportedException>(() => PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, pmx, motion));
        Assert.Throws<InvalidDataException>(() => PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, pmx, new() { BoneKeyframes = [new() { BoneName = "B", Frame = uint.MaxValue }] }));
        Assert.Throws<InvalidDataException>(() => PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, pmx, new() { BoneKeyframes = [new() { BoneName = "B", Translation = new(float.NaN, 0, 0) }] }));
    }

    [Fact]
    public void SparseOriginalLinksRebuildIntoValidOwnedChainsAndDuplicateEditedFrameUsesLast()
    {
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "B", Frame = 2, Translation = new(1, 0, 0) }, new() { BoneName = "B", Frame = 2, Translation = new(9, 0, 0) }] };
        var result = PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, PmmMotionAdapterTests.Pmx(["A", "B"]), motion);
        var saved = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(result)));
        Assert.Equal(9, saved.Models[0].BoneKeyframes.Single(k => k.Frame == 2).Translation.X);
        Assert.Equal(1, saved.Models[0].BoneKeyframes.Single(k => k.Frame == 2).PreviousIndex);
    }

    [Fact]
    public void BoneReplacementWithoutFrameZeroStartsAtRestInsteadOfOldPmmPose()
    {
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "A", Frame = 10, Translation = new(10, 0, 0) }] };
        var result = PmmEditedMotionExporter.ReplaceModelMotion(Document(oldBoneX: 77), 0, PmmMotionAdapterTests.Pmx(["A", "B"]), motion);
        Assert.Equal(Vector3.Zero, result.Models[0].InitialBoneKeyframes[0].Translation);
        Assert.Equal(5, result.Models[0].BoneStates[0].Translation.X, 4);
        Assert.True(result.Models[0].BoneKeyframes.Single(k => k.Frame == 10).PhysicsDisabled);
    }

    [Fact]
    public void MorphReplacementWithoutFrameZeroClampsToFirstEditedValueInsteadOfOldPmmWeight()
    {
        var motion = new VmdMotion { MorphKeyframes = [new() { MorphName = "smile", Frame = 10, Weight = 1 }] };
        var result = PmmEditedMotionExporter.ReplaceModelMotion(Document(), 0, PmmMotionAdapterTests.Pmx(["A", "B"], ["smile"]), motion);
        Assert.Equal(1, result.Models[0].InitialMorphKeyframes[0].Weight);
        Assert.Equal(1, result.Models[0].MorphStates[0].Weight);
    }

    [Fact]
    public void BackwardsSourceFrameChainRejectsBeforeCandidateCanBePublished()
    {
        var source = Document(); source.Models[0].InitialBoneKeyframes[0].Frame = 11;
        Assert.Throws<InvalidDataException>(() => PmmEditedMotionExporter.ReplaceModelMotion(source, 0, PmmMotionAdapterTests.Pmx(["A", "B"]), new()));
        Assert.Equal(11, source.Models[0].InitialBoneKeyframes[0].Frame);
    }

    static VmdMotion Motion() => new()
    {
        BoneKeyframes = new uint[] { 0, 10, 20 }.Select(f => new VmdBoneKeyframe { BoneName = "A", Frame = f, Translation = new(f, 0, 0), Interpolation = PmmMotionAdapter.ConvertBoneInterpolation(Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(x => x).ToArray()) }).ToArray(),
        MorphKeyframes = [new() { MorphName = "smile", Frame = 0 }, new() { MorphName = "smile", Frame = 10, Weight = 1 }],
        IkKeyframes = [new() { Frame = 0, Visible = false, Switches = [new("A", false)] }],
    };

    internal static PmmDocument Document(float oldBoneX = 0)
    {
        PmmString S(string s) => new(0, s, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(x => x).ToArray();
        var actor = new PmmModel
        {
            Index = 0, Name = S("actor"), Path = S("actor.pmx"), BoneNames = [S("A"), S("B")], MorphNames = [S("smile")], ConstraintBoneIndices = [0], OutsideParentSubjectBoneIndices = [1],
            Visible = true, SelectedMorphIndices = [0, 0, 0, 0], LastFrame = 12, EdgeWidth = 1.25f,
            InitialBoneKeyframes = [new() { RecordIndex = 0, ObjectIndex = 0, NextIndex = 400, Translation = new(oldBoneX, 0, 0), Rotation = Quaternion.Identity, Interpolation = linear }, new() { RecordIndex = 1, ObjectIndex = 1, Rotation = Quaternion.Identity, Interpolation = linear }],
            BoneKeyframes = [new() { RecordIndex = 2, ObjectIndex = 400, PreviousIndex = 0, Frame = 10, Rotation = Quaternion.Identity, Interpolation = linear, PhysicsDisabled = true }],
            InitialMorphKeyframes = [new() { ObjectIndex = 0, Weight = .25f }],
            InitialModelKeyframe = new() { ObjectIndex = 0, NextIndex = 800, Visible = true, ConstraintEnabled = [true], OutsideParents = [new(0, -1, -1)] },
            ModelKeyframes = [new() { RecordIndex = 1, ObjectIndex = 800, PreviousIndex = 0, Frame = 12, Visible = true, ConstraintEnabled = [true], OutsideParents = [new(0, 1, 0)] }],
            BoneStates = [new(0, new(99, 0, 0), Quaternion.Identity, true, false, 2), new(0, new(77, 0, 0), Quaternion.Identity, false, false, 1)],
            MorphStates = [new(0, .25f)], ConstraintStates = [new(0, true)], OutsideParentStates = [new(0, 0, 20, new(0, -1, -1))],
        };
        return new()
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 1280, OutputHeight = 720, CameraFov = 30,
            Models = [actor, new() { Index = 1, SelectedMorphIndices = [0, 0, 0, 0], InitialModelKeyframe = new() { Visible = true },
                BoneNames = [S("parent")], InitialBoneKeyframes = [new() { Rotation = Quaternion.Identity, Interpolation = linear }],
                BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)] }], CurrentFrame = 5,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 }, UnknownTail = new byte[] { 222, 173 },
        };
    }
}
