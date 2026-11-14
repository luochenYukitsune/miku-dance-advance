using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ScenePmmControlsTests
{
    static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Parent = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static ScenePmmActorSource[] Sources() => [new(Parent, 1, PmmMotionAdapterTests.Pmx(["parent"])), new(Actor, 0, PmmMotionAdapterTests.Pmx(["B", "A"], ["smile"]))];
    static ScenePmmControlState State() => ScenePmmControlState.Create(PmmEditedMotionExporterTests.Document(), Sources());
    static PmmDocument Read(byte[] bytes) => PmmReader.Read(new MemoryStream(bytes), new() { AnsiCodePage = 932 });

    [Fact]
    public void StoredModelIdsAndNativeNamesMapDespiteActorAndBoneReordering()
    {
        byte[] bytes = PmmWriter.ToBytes(PmmEditedMotionExporterTests.Document()); var source = Read(bytes);
        bytes[(int)source.Models[0].Offset] = 7; bytes[(int)source.Models[1].Offset] = 42;
        // Saved camera model ID precedes bone/64-byte matrix/three flags/frame text/trailer in the known tail.
        BitConverter.TryWriteBytes(bytes.AsSpan((int)source.ParsedLength - 80, 4), 7);
        BitConverter.TryWriteBytes(bytes.AsSpan((int)source.Models[0].ModelKeyframes[0].OutsideParents[0].Offset, 4), 42);
        var state = ScenePmmControlState.Create(Read(bytes), Sources().Select(s => s with { PmmModelIndex = s.PmmModelIndex == 0 ? (byte)7 : (byte)42 }));
        Assert.Empty(state.Sample(9.9).DisabledBoneIndices[Actor]);
        Assert.Equal(new[] { 1 }, state.Sample(10).DisabledBoneIndices[Actor]);
        var parent = Assert.Single(state.Sample(12).OutsideParents);
        Assert.Equal(Actor, parent.ObjectId); Assert.Equal(0, parent.BoneIndex);
        Assert.Equal(Parent, parent.ParentObjectId); Assert.Equal(0, parent.ParentBoneIndex);
        Assert.Equal(Matrix4x4.Identity, parent.Offset);
        Assert.Empty(state.Sample(11.99).OutsideParents);
    }

    [Fact]
    public void SourceDocumentExportAndPayloadMutationCannotChangeCapturedState()
    {
        var source = PmmEditedMotionExporterTests.Document(); var state = ScenePmmControlState.Create(source, Sources());
        byte[] expected = PmmWriter.ToBytes(state.ExportDocument());
        source.Models[0].BoneKeyframes[0].Frame = 100;
        state.ExportDocument().Models[0].BoneKeyframes[0].Frame = 200;
        var payload = state.Capture(); payload.DocumentBytes[0] ^= 1; payload.Actors[0].BoneIndices[0] = 999;
        Assert.Equal(expected, PmmWriter.ToBytes(state.ExportDocument()));
        Assert.Equal(new[] { 1 }, state.Sample(10).DisabledBoneIndices[Actor]);
    }

    [Fact]
    public void PayloadRoundTripVerifiesDigestAndExplicitSkeletonMapping()
    {
        var state = State(); var models = Sources().ToDictionary(s => s.ObjectId, s => s.Model);
        var restored = ScenePmmControlState.Restore(state.Capture(), models);
        Assert.Equal(state.Sha256, restored.Sha256); Assert.Equal(state.OriginSha256, restored.OriginSha256);
        Assert.Equal(PmmWriter.ToBytes(state.ExportDocument()), PmmWriter.ToBytes(restored.ExportDocument()));
        var corrupt = state.Capture(); corrupt.DocumentBytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(corrupt, models));
        var incorrect = state.Capture(); incorrect.Actors.Single(a => a.ObjectId == Actor).BoneIndices[0] = 0;
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(incorrect, models));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(state.Capture() with { DocumentBytes = new byte[ScenePmmControlState.MaxPayloadBytes + 1] }, models));
    }

    [Fact]
    public void MissingDuplicateOrAmbiguousBindingsRejectWithoutGuessing()
    {
        var doc = PmmEditedMotionExporterTests.Document(); var sources = Sources();
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(doc, sources.Take(1)));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(doc, [sources[0], sources[1] with { ObjectId = Parent }]));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(doc, [sources[0], sources[1] with { Model = PmmMotionAdapterTests.Pmx(["A", "A", "B"]) }]));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(doc, [sources[0], sources[1] with { Model = PmmMotionAdapterTests.Pmx(["a", "B"]) }]));
    }

    [Fact]
    public void DetachedExistingEditorPreservesOriginAndConflictFailurePreservesPriorState()
    {
        var before = State();
        PmmTrackEditPlan Plan(PmmTrackEditor e) => e.PrepareRegister([new(new(PmmTrackKind.Gravity, 0, "", 0), new PmmGravityKeyframe { Acceleration = 5, Direction = -Vector3.UnitY })]);
        Assert.Throws<InvalidOperationException>(() => before.Edit(Plan));
        var after = before.Edit(Plan, confirmOverwrite: true);
        Assert.Equal(before.OriginSha256, after.OriginSha256); Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal(new Vector3(0, -50, 0), after.Sample(0).Gravity.WorldGravity);
        Assert.Equal(before.Sha256, State().Sha256);
    }

    [Fact]
    public void FractionalGravityAndSavedStateRemainSeparate()
    {
        var state = State().Edit(e => e.PrepareRegister([new(new(PmmTrackKind.Gravity, 0, "", 10), new PmmGravityKeyframe { Acceleration = 20, Direction = -Vector3.UnitY, NoiseEnabled = true, Noise = 20 })]));
        Assert.Equal(new PmmPhysicsControlTracks(state.ExportDocument()).GravityAt(5.25), state.Sample(5.25).Gravity);
        Assert.False(state.Sample(5.25).Gravity.NoiseEnabled);
        Assert.True(state.Sample(10).Gravity.NoiseEnabled);
        Assert.Empty(state.Sample(10, saved: true).DisabledBoneIndices[Actor]);
        Assert.Equal(new[] { 1 }, state.Sample(10).DisabledBoneIndices[Actor]);
    }

    [Fact]
    public void FutureParentCycleIsRejectedBeforeTimelineReachesIt()
    {
        byte[] bytes = PmmWriter.ToBytes(PmmEditedMotionExporterTests.Document()); var source = Read(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan((int)source.Models[0].ModelKeyframes[0].OutsideParents[0].Offset, 4), 0);
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(Read(bytes), Sources()));
    }

    [Fact]
    public void WholeModelOutsideParentSubjectRemainsMinusOne()
    {
        byte[] bytes = PmmWriter.ToBytes(PmmEditedMotionExporterTests.Document()); var source = Read(bytes);
        var model = source.Models[0]; var lastMorph = model.MorphNames[^1];
        // Variable string prefix/payload, constraint count/table, then outside-subject count/table.
        int subjectOffset = (int)lastMorph.Offset + 1 + lastMorph.RawBytes.Count + 4 + 4 * model.ConstraintBoneIndices.Count + 4;
        BitConverter.TryWriteBytes(bytes.AsSpan(subjectOffset, 4), -1);
        var state = ScenePmmControlState.Create(Read(bytes), Sources());
        Assert.Equal(-1, Assert.Single(state.Sample(12).OutsideParents).BoneIndex);
        Assert.Equal(-1, Assert.Single(ScenePmmControlState.Restore(state.Capture(), Sources().ToDictionary(s => s.ObjectId, s => s.Model)).Sample(12).OutsideParents).BoneIndex);
    }

    [Fact]
    public void RuntimeBindingAllowsIndependentAddedActorButRejectsReorderedOrMissingProvenanceActor()
    {
        var state = State(); var models = Sources().ToDictionary(s => s.ObjectId, s => s.Model);
        state.ValidateActors(models);
        models[Actor] = PmmMotionAdapterTests.Pmx(["A", "B"]);
        Assert.Throws<InvalidDataException>(() => state.ValidateActors(models));
        models = Sources().ToDictionary(s => s.ObjectId, s => s.Model);
        models[Guid.NewGuid()] = PmmMotionAdapterTests.Pmx(["extra"]);
        state.ValidateActors(models);
        Assert.Equal(state.Capture().DocumentBytes, ScenePmmControlState.Restore(state.Capture(), models).Capture().DocumentBytes);
        models.Remove(Actor);
        Assert.Throws<InvalidDataException>(() => state.ValidateActors(models));
    }
}
