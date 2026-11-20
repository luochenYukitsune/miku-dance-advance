using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmTrackEditorExtendedTests
{
    [Fact]
    public void AccessoryPoseOpacityParentAndSavedStateRoundTripAndSampleWithoutGeometry()
    {
        var editor = new PmmTrackEditor(Document());
        var plan = editor.PrepareRegister([new(new(PmmTrackKind.Accessory, 7, "", 10), new PmmAccessoryKeyframe
        { Translation = new(10, 20, 30), Rotation = new(1, 2, 3), Scale = 20, OpacityAndVisibility = PmmTrackSampling.PackOpacity(.5f, false), ParentModelIndex = 1, ParentBoneIndex = 0, ShadowEnabled = false })]);
        editor.Apply(plan); var result = editor.ExportDocument();
        var pose = PmmTrackSampling.AccessoryAt(result, 7, 5);
        Assert.Equal(new Vector3(5, 10, 15), pose.Translation); Assert.Equal(new Vector3(.5f, 1, 1.5f), pose.Rotation);
        Assert.Equal(15, pose.Scale); Assert.Equal(.75f, pose.Opacity); Assert.True(pose.Visible); Assert.True(pose.ShadowEnabled);
        Assert.Equal(-1, pose.ParentModelIndex); // discrete parent changes at frame 10, never halfway
        Assert.Equal(pose, PmmTrackSampling.AccessoryAt(result, 7, 5, saved: true));
        var end = PmmTrackSampling.AccessoryAt(result, 7, 10); Assert.Equal(1, end.ParentModelIndex); Assert.False(end.Visible); Assert.False(end.ShadowEnabled);
        Assert.Equal(.5f, end.Opacity); Assert.Equal(new byte[] { 222, 173 }, result.UnknownTail);
        var reread = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(result)));
        Assert.Equal(end, PmmTrackSampling.AccessoryAt(reread, 7, 100));
    }

    [Fact]
    public void OutsideParentCanAddMissingSlotRetainsUnchangedIkAndSupportsUndo()
    {
        var source = Document(); var editor = new PmmTrackEditor(source); byte[] before = PmmWriter.ToBytes(editor.ExportDocument());
        // Remove the old future dependency before introducing the reverse model dependency.
        editor.Apply(editor.PrepareRegister([new(new(PmmTrackKind.Model, 0, "", 12), new PmmModelKeyframe
        { Visible = true, ConstraintEnabled = [true], OutsideParents = [new(0, -1, -1)] })]), true);
        editor.Apply(editor.PrepareOutsideParent(1, "parent", 3, 0, "A"));
        var result = editor.ExportDocument(); var model = result.Models.Single(model => model.Index == 1);
        Assert.Equal(new[] { 0 }, model.OutsideParentSubjectBoneIndices);
        Assert.Equal(-1, model.InitialModelKeyframe.OutsideParents[0].ModelIndex);
        Assert.Equal(0, PmmTrackSampling.OutsideParentsAt(result, 1, 5)[0].ModelIndex);
        Assert.Equal(3, model.OutsideParentStates[0].BeginFrame); Assert.Equal(int.MaxValue, model.OutsideParentStates[0].EndFrame);
        Assert.Empty(model.InitialModelKeyframe.ConstraintEnabled); Assert.Single(result.Models[0].ConstraintStates);
        Assert.True(editor.Undo()); Assert.True(editor.Undo()); Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument()));
    }

    [Fact]
    public void KnownOutsideParentCyclesIncludingFutureKeysAreRejectedAtomically()
    {
        var editor = new PmmTrackEditor(Document()); byte[] before = PmmWriter.ToBytes(editor.ExportDocument());
        // actor B is bound to model 1 parent at frame 12; this would create a cycle only in the future.
        Assert.Throws<InvalidDataException>(() => editor.PrepareOutsideParent(1, "parent", 3, 0, "B"));
        Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument())); Assert.False(editor.CanUndo);
        Assert.Throws<InvalidDataException>(() => editor.PrepareOutsideParent(0, "B", 3, 0, "B"));
        // Bone A is not itself an outside-parent subject; the direct bone graph is acyclic,
        // but the whole-model playback dependency still cycles at frame 12.
        Assert.Throws<InvalidDataException>(() => editor.PrepareOutsideParent(1, "parent", 3, 0, "A"));
    }

    [Fact]
    public void DeletingOrMovingLastKeyRecomputesDurationAcrossRemainingModelChannels()
    {
        var editor = new PmmTrackEditor(Document());
        editor.Apply(editor.PrepareDelete([new(PmmTrackKind.Model, 0, "", 12)]));
        Assert.Equal(10, editor.ExportDocument().Models.Single(m => m.Index == 0).LastFrame);
        editor.Apply(editor.PrepareMove([new(PmmTrackKind.Bone, 0, "A", 10)], -7));
        Assert.Equal(3, editor.ExportDocument().Models.Single(m => m.Index == 0).LastFrame);
        Assert.True(editor.Undo()); Assert.Equal(10, editor.ExportDocument().Models.Single(m => m.Index == 0).LastFrame);
        Assert.True(editor.Undo()); Assert.Equal(12, editor.ExportDocument().Models.Single(m => m.Index == 0).LastFrame);
    }

    [Fact]
    public void StoredModelIndexSurvivesListReorderingAndClipboardRebindsByExactBoneName()
    {
        var editor = new PmmTrackEditor(Document(reverseModels: true));
        var plan = editor.PrepareRegister([new(new(PmmTrackKind.Bone, 0, "B", 2), new PmmBoneKeyframe
        { Rotation = Quaternion.Identity, Translation = new(10, 0, 0), PhysicsDisabled = true, Interpolation = new byte[16] })]);
        editor.Apply(plan); var result = editor.ExportDocument();
        Assert.Equal(1, result.Models[0].Index); Assert.Single(result.Models.Single(model => model.Index == 0).BoneKeyframes, key => key.Frame == 2);
        var clipboard = editor.CopySelection([new(PmmTrackKind.Bone, 0, "B", 2)]);
        editor.Apply(editor.PreparePaste(clipboard, 5));
        Assert.Equal(10, editor.ExportDocument().Models.Single(model => model.Index == 0).BoneKeyframes.Single(key => key.Frame == 5).Translation.X);
    }

    [Fact]
    public void GravityNoiseMetadataIsRetainedAndSavedStateUsesOriginalLinearSampling()
    {
        var editor = new PmmTrackEditor(Document());
        editor.Apply(editor.PrepareRegister([
            new(new(PmmTrackKind.Gravity, 0, "", 2), new PmmGravityKeyframe { Acceleration = 3, Direction = new(0, -2, 0), NoiseEnabled = true, Noise = 42 }),
            new(new(PmmTrackKind.Gravity, 0, "", 8), new PmmGravityKeyframe { Acceleration = 5, Direction = Vector3.UnitX })]));
        var result = editor.ExportDocument(); var controls = new PmmPhysicsControlTracks(result);
        float weight = 5f / 6f;
        Assert.Equal(3f + 2f * weight, controls.GravityAt(7).Acceleration);
        Assert.Equal(new Vector3(weight, -2f + 2f * weight, 0), controls.GravityAt(7).Direction);
        Assert.True(controls.GravityAt(7).NoiseEnabled); Assert.Equal(7, controls.GravityAt(7).Noise);
        Assert.Equal(controls.GravityAt(5), controls.SavedGravity); Assert.Equal(new Vector3(50, 0, 0), controls.GravityAt(8).WorldGravity);
        Assert.Contains(controls.Diagnostics, diagnostic => diagnostic.Contains("PmmGravityNoise", StringComparison.Ordinal));
    }

    [Fact]
    public void RegistrationDuplicateWrongTypeOwnerAndInvalidOpacityAreRejectedBeforePublishing()
    {
        var editor = new PmmTrackEditor(Document());
        var address = new PmmTrackAddress(PmmTrackKind.Gravity, 0, "", 4); var record = new PmmTrackRecord(address, new PmmGravityKeyframe());
        Assert.Throws<ArgumentException>(() => editor.PrepareRegister([record, record]));
        Assert.Throws<ArgumentException>(() => editor.PrepareRegister([new(address, new PmmModelKeyframe())]));
        Assert.Throws<ArgumentException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Accessory, 0, "", 4), new PmmAccessoryKeyframe())]));
        Assert.Throws<ArgumentException>(() => editor.PrepareOutsideParent(0, "B", 4, 1));
        Assert.Throws<InvalidDataException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Model, 0, "", 4), new PmmModelKeyframe
        { ConstraintEnabled = [true], OutsideParents = [new(0, 1, -1)] })]));
        Assert.Throws<InvalidDataException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Accessory, 7, "", 4), new PmmAccessoryKeyframe { OpacityAndVisibility = 255 })]));
        Assert.Throws<InvalidDataException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Bone, 0, "B", 4), new PmmBoneKeyframe { Rotation = default, Interpolation = new byte[16] })]));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void ForeignPlanAndUndoRevisionAreRejectedAndNewEditClearsRedo()
    {
        var editor = new PmmTrackEditor(Document(), undoLimit: 1); var other = new PmmTrackEditor(Document());
        var record = new PmmTrackRecord(new(PmmTrackKind.Gravity, 0, "", 4), new PmmGravityKeyframe { Acceleration = 9, Direction = -Vector3.UnitY });
        var oldPlan = editor.PrepareRegister([record]); Assert.Throws<InvalidOperationException>(() => other.Apply(oldPlan));
        editor.Apply(oldPlan); editor.Undo(); Assert.Throws<InvalidOperationException>(() => editor.Apply(oldPlan));
        editor.Apply(editor.PrepareRegister([record])); Assert.False(editor.CanRedo);
        editor.Apply(editor.PrepareDelete([record.Address])); Assert.True(editor.Undo()); Assert.False(editor.Undo());
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void OpacityPackingNeverWrapsInvalidValues(float opacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PmmTrackSampling.PackOpacity(opacity, true));

    [Theory]
    [InlineData(0f, false, 200)]
    [InlineData(1f, true, 1)]
    [InlineData(.5f, true, 101)]
    public void OpacityPackingUsesPmmPercentAndVisibilityBits(float opacity, bool visible, int expected)
        => Assert.Equal(expected, PmmTrackSampling.PackOpacity(opacity, visible));

    [Fact]
    public void V1EditsKnownBoneChannelsButRejectsUnavailablePhysicsAndGravityFields()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "pmm-v1-one-model.pmm"));
        var source = PmmReader.Read(stream, new() { V1ModelMetadataResolver = _ => new(["bone"], ["morph"], [0]) });
        var editor = new PmmTrackEditor(source);
        editor.Apply(editor.PrepareRegister([new(new(PmmTrackKind.Bone, 7, "bone", 5), new PmmBoneKeyframe
        { Translation = new(7, 8, 9), Rotation = Quaternion.Identity, Interpolation = new byte[16] })]));
        var result = editor.ExportDocument(); Assert.Equal(1, result.Version);
        Assert.Equal(new Vector3(7, 8, 9), result.Models[0].BoneKeyframes.Single(key => key.Frame == 5).Translation);
        byte[] before = PmmWriter.ToBytes(result);
        Assert.Throws<ArgumentException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Gravity, 0, "", 5), new PmmGravityKeyframe())]));
        Assert.Throws<NotSupportedException>(() => editor.PrepareOutsideParent(7, "bone", 5, -1));
        Assert.ThrowsAny<Exception>(() => editor.PrepareRegister([new(new(PmmTrackKind.Bone, 7, "bone", 6), new PmmBoneKeyframe
        { PhysicsDisabled = true, Rotation = Quaternion.Identity, Interpolation = new byte[16] })]));
        Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument()));
    }

    [Fact]
    public void CurveBytesAreValidatedBeforeCandidateEvenWhenCurrentFrameDoesNotSampleThem()
    {
        var editor = new PmmTrackEditor(Document()); byte[] curve = new byte[16]; curve[15] = 255;
        Assert.Throws<InvalidDataException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Bone, 0, "B", 1000), new PmmBoneKeyframe
        { Rotation = Quaternion.Identity, Interpolation = curve })]));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void PrepareOutsideParentSupportsGroundWithRoundTripUndoRedoAndControlSampling()
    {
        var source = Document();
        var editor = new PmmTrackEditor(source);
        byte[] before = PmmWriter.ToBytes(editor.ExportDocument());

        // Add a missing slot on model 1 bound to ground (-2) at frame 3.
        editor.Apply(editor.PrepareOutsideParent(1, "parent", 3, -2));
        var afterModel1 = editor.ExportDocument();
        var model1 = afterModel1.Models.Single(model => model.Index == 1);
        Assert.Equal(new[] { 0 }, model1.OutsideParentSubjectBoneIndices);
        Assert.Equal(new PmmOutsideParent(0, -1, -1), model1.InitialModelKeyframe.OutsideParents[0] with { Offset = 0 });
        Assert.Equal(new PmmOutsideParent(0, -2, -1), model1.ModelKeyframes.Single(k => k.Frame == 3).OutsideParents[0] with { Offset = 0 });
        Assert.Equal(3, model1.OutsideParentStates[0].BeginFrame);
        Assert.Equal(int.MaxValue, model1.OutsideParentStates[0].EndFrame);
        Assert.Equal(new PmmOutsideParent(0, -2, -1), model1.OutsideParentStates[0].Parent with { Offset = 0 });

        // Update an existing slot on model 0 ("B") to ground (-2) at frame 3 while frame 12 remains bound to model 1.
        editor.Apply(editor.PrepareOutsideParent(0, "B", 3, -2));
        var result = editor.ExportDocument();
        var model0 = result.Models.Single(model => model.Index == 0);
        Assert.Equal(3, model0.OutsideParentStates[0].BeginFrame);
        Assert.Equal(11, model0.OutsideParentStates[0].EndFrame);
        Assert.Equal(new PmmOutsideParent(0, -2, -1), model0.OutsideParentStates[0].Parent with { Offset = 0 });

        var reread = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(result)));
        Assert.Equal(new PmmOutsideParent(0, -2, -1), PmmTrackSampling.OutsideParentsAt(reread, 0, 5)[0] with { Offset = 0 });
        Assert.Equal(new PmmOutsideParent(0, -2, -1), PmmTrackSampling.OutsideParentsAt(reread, 1, 5)[0] with { Offset = 0 });

        Guid actor0 = Guid.NewGuid(), actor1 = Guid.NewGuid();
        var controls = ScenePmmControlState.Create(reread, [
            new(actor0, 0, PmmMotionAdapterTests.Pmx(["A", "B"])),
            new(actor1, 1, PmmMotionAdapterTests.Pmx(["parent"])),
        ]);
        var at0 = controls.Sample(0);
        Assert.Empty(at0.GroundOutsideParents);
        Assert.Empty(at0.OutsideParents);
        var at5 = controls.Sample(5);
        Assert.Equal([new(actor0, 1), new(actor1, 0)], at5.GroundOutsideParents.OrderBy(item => item.ObjectId == actor0 ? 0 : 1).ToArray());
        Assert.Empty(at5.OutsideParents);
        var at12 = controls.Sample(12);
        Assert.Equal([new(actor1, 0)], at12.GroundOutsideParents);
        var parent12 = Assert.Single(at12.OutsideParents);
        Assert.Equal((actor0, 1, actor1, 0), (parent12.ObjectId, parent12.BoneIndex, parent12.ParentObjectId, parent12.ParentBoneIndex));

        Assert.True(editor.Undo());
        Assert.True(editor.Undo());
        Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument()));
        Assert.True(editor.Redo());
        Assert.True(editor.Redo());
        Assert.Equal(PmmWriter.ToBytes(result), PmmWriter.ToBytes(editor.ExportDocument()));
    }

    [Fact]
    public void GroundOutsideParentIgnoresStaleBoneIndexInSameParentAndRejectsInvalidInputsAtomically()
    {
        var baseDoc = Document();
        var editor = new PmmTrackEditor(baseDoc);
        byte[] before = PmmWriter.ToBytes(editor.ExportDocument());

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PrepareOutsideParent(0, "B", 4, -3));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.PrepareOutsideParent(0, "B", 4, -3, "parent"));
        Assert.Throws<ArgumentException>(() => editor.PrepareOutsideParent(0, "B", 4, -2, "parent"));
        Assert.Throws<InvalidDataException>(() => editor.PrepareRegister([new(new(PmmTrackKind.Model, 0, "", 4), new PmmModelKeyframe
        { Visible = true, ConstraintEnabled = [true], OutsideParents = [new(0, -3, -1)] })]));
        Assert.False(editor.CanUndo);
        Assert.Equal(before, PmmWriter.ToBytes(editor.ExportDocument()));

        // Whole-model (-1) slot cannot target ground (-2).
        var actor0 = baseDoc.Models[0];
        var wholeModel = new PmmModel
        {
            Offset = actor0.Offset, Index = actor0.Index, Name = actor0.Name, EnglishName = actor0.EnglishName, Path = actor0.Path,
            FixedTrackCount = actor0.FixedTrackCount, BoneNames = actor0.BoneNames, MorphNames = actor0.MorphNames,
            ConstraintBoneIndices = actor0.ConstraintBoneIndices, OutsideParentSubjectBoneIndices = [-1],
            DrawOrderIndex = actor0.DrawOrderIndex, Visible = actor0.Visible, SelectedBoneIndex = actor0.SelectedBoneIndex,
            SelectedMorphIndices = actor0.SelectedMorphIndices, ExpansionStates = actor0.ExpansionStates, VerticalScroll = actor0.VerticalScroll,
            LastFrame = actor0.LastFrame, InitialBoneKeyframes = actor0.InitialBoneKeyframes, BoneKeyframes = actor0.BoneKeyframes,
            InitialMorphKeyframes = actor0.InitialMorphKeyframes, MorphKeyframes = actor0.MorphKeyframes,
            InitialModelKeyframe = actor0.InitialModelKeyframe, ModelKeyframes = actor0.ModelKeyframes,
            BoneStates = actor0.BoneStates, MorphStates = actor0.MorphStates, ConstraintStates = actor0.ConstraintStates,
            OutsideParentStates = actor0.OutsideParentStates, BlendEnabled = actor0.BlendEnabled, EdgeWidth = actor0.EdgeWidth,
            SelfShadowEnabled = actor0.SelfShadowEnabled, TransformOrderIndex = actor0.TransformOrderIndex,
        };
        var wholeDoc = new PmmDocument
        {
            Version = baseDoc.Version, AnsiCodePage = baseDoc.AnsiCodePage, PanelStates = baseDoc.PanelStates,
            OutputWidth = baseDoc.OutputWidth, OutputHeight = baseDoc.OutputHeight, CameraFov = baseDoc.CameraFov,
            Models = [wholeModel, baseDoc.Models[1]], CurrentFrame = baseDoc.CurrentFrame, Camera = baseDoc.Camera,
            Light = baseDoc.Light, Gravity = baseDoc.Gravity, SelfShadow = baseDoc.SelfShadow,
            Settings = baseDoc.Settings, UnknownTail = baseDoc.UnknownTail, Accessories = baseDoc.Accessories,
            AccessoryNames = baseDoc.AccessoryNames,
        };
        var wholeEditor = new PmmTrackEditor(wholeDoc);
        Assert.Throws<InvalidDataException>(() => wholeEditor.PrepareRegister([new(new(PmmTrackKind.Model, 0, "", 4), new PmmModelKeyframe
        { Visible = true, ConstraintEnabled = [true], OutsideParents = [new(0, -2, -1)] })]));
        Assert.False(wholeEditor.CanUndo);

        // Stale BoneIndex on (-2, 7) matches (-2, -1) in SameParent, keeping BeginFrame at 0.
        editor.Apply(editor.PrepareRegister([new(new(PmmTrackKind.Model, 0, "", 0), new PmmModelKeyframe
        { Visible = true, ConstraintEnabled = [true], OutsideParents = [new(0, -2, 7)] })]), confirmOverwrite: true);
        editor.Apply(editor.PrepareOutsideParent(0, "B", 3, -2));
        var state = editor.ExportDocument().Models.Single(m => m.Index == 0).OutsideParentStates[0];
        Assert.Equal(0, state.BeginFrame);
        Assert.Equal(11, state.EndFrame);
        Assert.Equal(new PmmOutsideParent(0, -2, -1), state.Parent with { Offset = 0 });
    }
    internal static PmmDocument Document(bool reverseModels = false)
    {
        var source = PmmEditedMotionExporterTests.Document();
        var accessory = new PmmAccessory { Index = 7, Name = new(0, "prop", []), Path = new(0, "prop.x", []),
            InitialKeyframe = new() { ParentModelIndex = -1, ParentBoneIndex = -1, Scale = 10, OpacityAndVisibility = 1, ShadowEnabled = true },
            ParentModelIndex = -1, ParentBoneIndex = -1, Scale = 10, OpacityAndVisibility = 1, ShadowEnabled = true };
        return new() { Version = source.Version, AnsiCodePage = source.AnsiCodePage, PanelStates = source.PanelStates, OutputWidth = source.OutputWidth,
            OutputHeight = source.OutputHeight, CameraFov = source.CameraFov, Models = reverseModels ? source.Models.Reverse().ToArray() : source.Models,
            CurrentFrame = source.CurrentFrame, Camera = source.Camera, Light = source.Light, Gravity = source.Gravity, SelfShadow = source.SelfShadow,
            Settings = source.Settings, UnknownTail = source.UnknownTail, Accessories = [accessory], AccessoryNames = [accessory.Name] };
    }
}


