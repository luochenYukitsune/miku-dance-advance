using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneModelReplacementTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-replacement-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public SceneModelReplacementTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void DefaultModelNameFollowsReplacementAndUndoRedoAtomically()
    {
        var original = Asset(["root"], [], "Original");
        using var project = Project(original, out var id, "Original");
        var replacement = Asset(["root"], [], "Replacement");
        string path = Write("replacement", replacement);
        var plan = project.PrepareModelReplacement(id, path, replacement, Token);
        Assert.Equal("Original", project.Objects[0].Name);
        project.ApplyModelReplacement(plan, cancellationToken: Token);
        Assert.Equal("Replacement", project.Objects[0].Name);
        Assert.Equal(path, project.Objects[0].SourcePath);
        Assert.True(project.Undo(Token));
        Assert.Equal("Original", project.Objects[0].Name);
        Assert.True(project.Redo(Token));
        Assert.Equal("Replacement", project.Objects[0].Name);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("model")]
    public void LegacyDefaultNameAndBlankReplacementUseTheChosenFileName(string oldName)
    {
        var original = Asset(["root"], []);
        using var project = Project(original, out var id, oldName);
        var replacement = Asset(["root"], [], "  ");
        project.ApplyModelReplacement(project.PrepareModelReplacement(id, Write("New file", replacement), replacement, Token), cancellationToken: Token);
        Assert.Equal("New file", project.Objects[0].Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitAliasEqualToOldModelNameSurvivesPortableSaveAndReplacement(bool portable)
    {
        var original = Asset(["root"], []);
        using var project = Project(original, out var id);
        // It is deliberately an alias even though its final text equals the PMX name.
        project.Edit(edit => edit.Composition.SetObjectMetadata(id, "model", ModelRole.Character), Token);
        var replacement = Asset(["root"], [], "Replacement");
        string path = Path.Combine(directory, portable ? "alias.pma" : "alias.mmdscene");
        if (portable)
        {
            PmaProjectStore.Save(path, project, cancellationToken: Token);
            using var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "alias-cache"), Load, new NoPhysics(), cancellationToken: Token);
            loaded.Session.ApplyModelReplacement(loaded.Session.PrepareModelReplacement(id, Write("alias-new", replacement), replacement, Token), cancellationToken: Token);
            Assert.Equal("model", loaded.Session.Objects[0].Name);
        }
        else
        {
            project.Save(path, cancellationToken: Token);
            using var loaded = SceneProjectSession.Load(path, Load, new NoPhysics(), Token);
            loaded.ApplyModelReplacement(loaded.PrepareModelReplacement(id, Write("alias-new", replacement), replacement, Token), cancellationToken: Token);
            Assert.Equal("model", loaded.Objects[0].Name);
        }
    }

    [Fact]
    public void AutomaticFileNameRemainsAutomaticAfterPortablePathRewriting()
    {
        var original = Asset(["root"], [], "");
        using var project = Project(original, out var id, "old");
        var blank = Asset(["root"], [], "");
        project.ApplyModelReplacement(project.PrepareModelReplacement(id, Write("First file", blank), blank, Token), cancellationToken: Token);
        string path = Path.Combine(directory, "automatic.pma");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        using var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "automatic-cache"), Load, new NoPhysics(), cancellationToken: Token);
        var replacement = Asset(["root"], [], "Final model");
        loaded.Session.ApplyModelReplacement(loaded.Session.PrepareModelReplacement(id, Write("final", replacement), replacement, Token), cancellationToken: Token);
        Assert.Equal("Final model", loaded.Session.Objects[0].Name);
    }

    [Fact]
    public void ReorderedBonesKeepIdentityPlacementMotionAndUndoWithoutIndexBinding()
    {
        var original = Asset(["root", "hand"], ["smile"]);
        using var project = Project(original, out var id);
        var before = project.Frame;
        var replacement = Asset(["hand", "root"], ["smile"]);
        var plan = project.PrepareModelReplacement(id, Write("new", replacement), replacement, Token);
        Assert.Same(before, project.Frame); Assert.False(project.CanUndo);
        Assert.Equal(1, Assert.Single(plan.Mappings, m => m.Kind == SceneModelDataKind.Bone && m.Name == "root").TargetIndex);
        project.ApplyModelReplacement(plan, cancellationToken: Token);
        Assert.Equal(id, Assert.Single(project.Objects).Id);
        Assert.Equal("instance", project.Objects[0].Name);
        Assert.Equal(new Vector3(4, 0, 0), project.Objects[0].Position);
        Assert.Same(replacement.Model, Assert.Single(project.CaptureAuthoredContent().Motions).Model);
        Assert.Equal(new Vector3(6, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.True(project.Undo(Token)); Assert.Same(original.Model, project.CaptureAuthoredContent().Motions[0].Model);
        Assert.True(project.Redo(Token)); Assert.Same(replacement.Model, project.CaptureAuthoredContent().Motions[0].Model);
        Assert.Throws<InvalidOperationException>(() => project.ApplyModelReplacement(plan, cancellationToken: Token));
    }

    [Fact]
    public void MissingTracksStayAuthoredInactiveAndNeverSilentlyReactivate()
    {
        var old = Asset(["root", "hand"], ["smile"]); using var project = Project(old, out var id);
        var smaller = Asset(["other"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(id, Write("small", smaller), smaller, Token), cancellationToken: Token);
        var detached = Assert.Single(project.CaptureAuthoredContent().Motions);
        Assert.Contains("root", detached.Detachment!.BoneNames); Assert.Contains("smile", detached.Detachment.MorphNames);
        Assert.Equal(2, detached.Motion.BoneKeyframes.Count); Assert.Equal(30u, project.DurationFrames);
        Assert.Equal(new Vector3(4, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
        var back = project.PrepareModelReplacement(id, Write("back", old), old, Token);
        Assert.True(back.RecoverableCount >= 2);
        project.ApplyModelReplacement(back, cancellationToken: Token);
        Assert.NotNull(project.CaptureAuthoredContent().Motions[0].Detachment);
        Assert.Equal(new Vector3(4, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
        var confirmed = project.PrepareModelReplacement(id, Write("confirmed", old), old, Token);
        project.ApplyModelReplacement(confirmed, confirmRestoreOrphans: true, cancellationToken: Token);
        Assert.Null(project.CaptureAuthoredContent().Motions[0].Detachment);
        Assert.Equal(new Vector3(6, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public void ImportThenExplicitRestoreMovesOnlyUnownedChannelsAndRejectsStalePlans()
    {
        var original = Asset(["root"], ["smile"]); using var project = Project(original, out var source);
        var smaller = Asset(["other"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(source, Write("small", smaller), smaller, Token), cancellationToken: Token);
        var target = Add(project, original);
        var plan = project.PrepareOrphanRestoration(source, target);
        Assert.Equal(2, plan.RecoverableCount); Assert.Empty(project.CaptureAuthoredContent().Motions.Single(m => m.ObjectId == target).Motion.BoneKeyframes);
        Assert.Throws<InvalidOperationException>(() => project.ApplyOrphanRestoration(plan, false, Token));
        project.ApplyOrphanRestoration(plan, true, Token);
        var targetActor = project.CaptureAuthoredContent().Motions.Single(m => m.ObjectId == target);
        Assert.Equal(2, targetActor.Motion.BoneKeyframes.Count); Assert.Single(targetActor.Motion.MorphKeyframes);
        Assert.Empty(project.CaptureAuthoredContent().Motions.Single(m => m.ObjectId == source).Motion.BoneKeyframes);
        Assert.True(project.Undo(Token)); Assert.NotNull(project.CaptureAuthoredContent().Motions.Single(m => m.ObjectId == source).Detachment);
        Assert.True(project.Redo(Token));
        Assert.Throws<InvalidOperationException>(() => project.ApplyOrphanRestoration(plan, true, Token));
    }

    [Fact]
    public void ExistingTargetTracksAreNeverOverwrittenAndMissingDataRemainsInPortablePma()
    {
        var original = Asset(["root"], ["smile"]); using var project = Project(original, out var source);
        var smaller = Asset(["other"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(source, Write("small", smaller), smaller, Token), cancellationToken: Token);
        var target = Add(project, original, withMotion: true);
        var plan = project.PrepareOrphanRestoration(source, target);
        Assert.Equal(0, plan.RecoverableCount); Assert.Equal(2, plan.ConflictCount);
        string package = Path.Combine(directory, "retained.pma"); PmaProjectStore.Save(package, project, cancellationToken: Token);
        foreach (string file in Directory.GetFiles(directory, "*.pmx")) File.Delete(file);
        using var loaded = PmaProjectStore.Load(package, Path.Combine(directory, "cache"), Load, new NoPhysics(), cancellationToken: Token);
        var retained = loaded.Session.CaptureAuthoredContent().Motions.Single(m => m.ObjectId == source);
        Assert.Equal(new[] { "root" }, retained.Detachment!.BoneNames); Assert.Equal(new[] { "smile" }, retained.Detachment.MorphNames);
        Assert.Equal(2, retained.Motion.BoneKeyframes.Count);
        Assert.False(PmaPmmCompatibility.Inspect(loaded.Session).CanExport);
    }

    [Fact]
    public void OutsideParentNamesRemapAndRemainRetainedUntilConfirmed()
    {
        var original = Asset(["root", "hand"], []); using var project = Project(original, out var child);
        var parent = Add(project, original);
        project.Edit(e => e.OutsideParents.Add(new(child, -1, parent, 1, Matrix4x4.Identity)), Token);
        var smaller = Asset(["root"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(parent, Write("parent-small", smaller), smaller, Token), cancellationToken: Token);
        Assert.Empty(project.OutsideParents);
        Assert.Single(project.CaptureAuthoredContent().Motions.SelectMany(m => m.Detachment?.OutsideParents ?? []));
        var returning = Asset(["hand", "root"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(parent, Write("parent-return", returning), returning, Token), cancellationToken: Token);
        Assert.Empty(project.OutsideParents);
        project.ApplyModelReplacement(project.PrepareModelReplacement(parent, Write("parent-confirm", returning), returning, Token), true, Token);
        Assert.Equal(0, Assert.Single(project.OutsideParents).ParentBoneIndex);
    }

    [Fact]
    public void CancelAndFailedPreparationKeepBothProjectAndHistoryUntouched()
    {
        var asset = Asset(["root"], []); using var project = Project(asset, out var id);
        var frame = project.Frame; var objects = project.Objects;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => project.PrepareModelReplacement(id, Write("cancel", asset), asset, cancelled.Token));
        Assert.Throws<ArgumentException>(() => project.PrepareModelReplacement(id, "relative.pmx", asset, Token));
        Assert.Same(frame, project.Frame); Assert.Equal(objects, project.Objects); Assert.False(project.CanUndo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateMorphNamesAtEitherEndpointRemainDetached(bool duplicateSource)
    {
        var original = Asset(["root"], duplicateSource ? ["smile", "smile"] : ["smile"]);
        using var project = Project(original, out var id);
        var replacement = Asset(["root"], duplicateSource ? ["smile"] : ["smile", "smile"]);
        var plan = project.PrepareModelReplacement(id, Write("duplicates", replacement), replacement, Token);
        Assert.All(plan.Mappings.Where(mapping => mapping.Kind == SceneModelDataKind.Morph), mapping => Assert.Equal(-1, mapping.TargetIndex));
        project.ApplyModelReplacement(plan, confirmRestoreOrphans: true, cancellationToken: Token);
        var actor = Assert.Single(project.CaptureAuthoredContent().Motions);
        Assert.Empty(actor.Detachment!.BoneNames);
        Assert.Equal(new[] { "smile" }, actor.Detachment.MorphNames);
        Assert.Equal(2, actor.Motion.BoneKeyframes.Count);
        Assert.Equal(new Vector3(6, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public void AmbiguousSourceBoneMetadataDoesNotSilentlyBindToAUniqueTarget()
    {
        var original = Asset(["root", "hand"], []); using var project = Project(original, out var id);
        // Imported model DTOs expose their collections; simulate ambiguous metadata discovered after binding.
        ((PmxBone[])original.Model.Bones)[1] = original.Model.Bones[0];
        var replacement = Asset(["root"], []);
        var plan = project.PrepareModelReplacement(id, Write("unique-target", replacement), replacement, Token);
        Assert.All(plan.Mappings, mapping => Assert.Equal(-1, mapping.TargetIndex));
        project.ApplyModelReplacement(plan, confirmRestoreOrphans: true, cancellationToken: Token);
        Assert.Equal(new[] { "root" }, Assert.Single(project.CaptureAuthoredContent().Motions).Detachment!.BoneNames);
        Assert.Equal(new Vector3(4, 0, 0), project.Frame.Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public void DuplicateBoneTargetNeverBindsAndKeepsExistingProjectOnRuntimeRejection()
    {
        var original = Asset(["root"], []); using var project = Project(original, out var id);
        var before = project.Frame; var replacement = Asset(["root", "root"], []);
        var plan = project.PrepareModelReplacement(id, Write("duplicate-bones", replacement), replacement, Token);
        Assert.Equal(-1, Assert.Single(plan.Mappings).TargetIndex);
        Assert.Contains(plan.Notices, notice => notice.Kind == SceneModelDataKind.Bone && !notice.CanRestore);
        var error = Assert.Throws<SceneActorAnimationException>(() => project.ApplyModelReplacement(plan, cancellationToken: Token));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Same(before, project.Frame); Assert.False(project.CanUndo);
        Assert.Same(original.Model, Assert.Single(project.CaptureAuthoredContent().Motions).Model);
    }

    [Fact]
    public void ReplacingOneParentEndpointPreservesTheOtherIndexedIdentityAndOffset()
    {
        var original = Asset(["root", "hand"], []); using var project = Project(original, out var child);
        var parent = Add(project, Asset(["root", "hand"], []));
        var offset = Matrix4x4.CreateTranslation(0, 2, 0);
        project.Edit(edit => edit.OutsideParents.Add(new(child, 1, parent, 1, offset)), Token);
        var reordered = Asset(["hand", "root"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(child, Write("child-reordered", reordered), reordered, Token), cancellationToken: Token);
        var binding = Assert.Single(project.OutsideParents);
        Assert.Equal(child, binding.ObjectId); Assert.Equal(0, binding.BoneIndex);
        Assert.Equal(parent, binding.ParentObjectId); Assert.Equal(1, binding.ParentBoneIndex);
        Assert.Equal(offset, binding.Offset);
    }

    [Fact]
    public void ReorderedParentSubjectsReserveTheirFinalSlotsTogether()
    {
        var original = Asset(["root", "hand"], []); using var project = Project(original, out var child);
        var parent = Add(project, original);
        project.Edit(edit =>
        {
            edit.OutsideParents.Add(new(child, 0, parent, 0, Matrix4x4.Identity));
            edit.OutsideParents.Add(new(child, 1, parent, 1, Matrix4x4.Identity));
        }, Token);
        var reordered = Asset(["hand", "root"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(child, Write("two-subjects", reordered), reordered, Token), cancellationToken: Token);
        Assert.Equal(2, project.OutsideParents.Count);
        Assert.Contains(project.OutsideParents, binding => binding.BoneIndex == 1 && binding.ParentBoneIndex == 0);
        Assert.Contains(project.OutsideParents, binding => binding.BoneIndex == 0 && binding.ParentBoneIndex == 1);
        Assert.Null(project.CaptureAuthoredContent().Motions.Single(actor => actor.ObjectId == child).Detachment);
    }

    [Fact]
    public void NewlyAmbiguousOutsideParentStaysDetachedEvenWhenRestoreWasConfirmed()
    {
        var original = Asset(["root", "other", "hand"], []); using var project = Project(original, out var child);
        var parent = Add(project, Asset(["root", "hand"], []));
        project.Edit(edit => edit.OutsideParents.Add(new(child, 2, parent, 1, Matrix4x4.Identity)), Token);
        ((PmxBone[])original.Model.Bones)[1] = original.Model.Bones[2];
        var unique = Asset(["root", "hand"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(child, Write("child-unique", unique), unique, Token), true, Token);
        Assert.Empty(project.OutsideParents);
        var detached = Assert.Single(project.CaptureAuthoredContent().Motions.SelectMany(actor => actor.Detachment?.OutsideParents ?? []));
        Assert.Equal(child, detached.ObjectId); Assert.Equal(parent, detached.ParentObjectId);
        Assert.Equal("hand", detached.BoneName); Assert.Equal("hand", detached.ParentBoneName);
    }

    [Fact]
    public void ParentRecoveryPreservesOtherOrphansAndRejectsOccupiedSlotsAndCycles()
    {
        var original = Asset(["root", "hand"], []); using var project = Project(original, out var source);
        var parent = Add(project, original); var target = Add(project, original);
        var orphan = new SceneDetachedOutsideParent(source, null, parent, "hand", Matrix4x4.Identity);
        project.Edit(edit =>
        {
            int index = edit.Motions.FindIndex(actor => actor.ObjectId == source);
            edit.Motions[index] = edit.Motions[index] with { Detachment = new([], [], [], [orphan]) };
            edit.OutsideParents.Add(new(parent, -1, target, 1, Matrix4x4.Identity));
        }, Token);
        var cycle = project.PrepareOrphanRestoration(source, target);
        Assert.Equal(0, cycle.RecoverableCount);
        Assert.Contains(cycle.Notices, notice => notice.Kind == SceneModelDataKind.OutsideParent && !notice.CanRestore);
        var replacement = project.PrepareModelReplacement(target, Write("unrelated", original), original, Token);
        project.ApplyModelReplacement(replacement, confirmRestoreOrphans: true, cancellationToken: Token);
        Assert.Single(project.CaptureAuthoredContent().Motions.Single(actor => actor.ObjectId == source).Detachment!.OutsideParents);
        Assert.Null(project.CaptureAuthoredContent().Motions.Single(actor => actor.ObjectId == target).Detachment);
        project.Edit(edit =>
        {
            edit.OutsideParents.Clear();
            edit.OutsideParents.Add(new(target, -1, parent, 0, Matrix4x4.Identity));
        }, Token);
        var occupied = project.PrepareOrphanRestoration(source, target);
        Assert.Equal(0, occupied.RecoverableCount);
        Assert.Contains(occupied.Notices, notice => notice.Kind == SceneModelDataKind.OutsideParent && !notice.CanRestore);
        Assert.Single(project.CaptureAuthoredContent().Motions.Single(actor => actor.ObjectId == source).Detachment!.OutsideParents);
    }

    [Fact]
    public void DetachedMasksCopyInputsAndExposeReadOnlyCollections()
    {
        string[] bones = ["root"], morphs = ["smile"], ik = ["leg"];
        SceneDetachedOutsideParent[] parents = [new(Guid.NewGuid(), null, Guid.NewGuid(), "hand", Matrix4x4.Identity)];
        var state = new SceneMotionDetachment(bones, morphs, ik, parents);
        bones[0] = "changed"; morphs[0] = "changed"; ik[0] = "changed"; parents[0] = parents[0] with { ParentBoneName = "changed" };
        Assert.Equal("root", state.BoneNames[0]); Assert.Equal("smile", state.MorphNames[0]); Assert.Equal("leg", state.IkNames[0]);
        Assert.Equal("hand", state.OutsideParents[0].ParentBoneName);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)state.BoneNames)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<SceneDetachedOutsideParent>)state.OutsideParents).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IkRecoveryPreservesTargetSwitchesAndVisibility(bool occupied)
    {
        var original = Asset(["root", "leg"], []);
        ((PmxBone[])original.Model.Bones)[1] = new()
        {
            Name = "leg", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0,
            Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 0, IkIterationCount = 1, IkLimit = 1
        };
        using var project = Project(original, out var source);
        project.Edit(edit =>
        {
            var actor = edit.Motions.Single(item => item.ObjectId == source);
            var motion = SceneMotionAsset.FromMotion(new()
            {
                BoneKeyframes = actor.Motion.BoneKeyframes,
                IkKeyframes = [new() { Frame = 20, Visible = true, Switches = [new() { BoneName = "leg", Enabled = false }] }]
            });
            edit.Motions[edit.Motions.FindIndex(item => item.ObjectId == source)] = actor with { Motion = motion.Motion, Asset = motion };
        }, Token);
        var smaller = Asset(["root"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(source, Write("ik-small", smaller), smaller, Token), cancellationToken: Token);
        var target = Add(project, original);
        project.Edit(edit =>
        {
            int index = edit.Motions.FindIndex(item => item.ObjectId == target);
            var motion = SceneMotionAsset.FromMotion(new()
            {
                IkKeyframes = [new() { Visible = true }, new() { Visible = false, Switches = occupied ? [new() { BoneName = "leg", Enabled = true }] : [] }]
            });
            edit.Motions[index] = edit.Motions[index] with { Motion = motion.Motion, Asset = motion };
        }, Token);
        var plan = project.PrepareOrphanRestoration(source, target);
        Assert.Equal(!occupied, Assert.Single(plan.Notices, notice => notice.Kind == SceneModelDataKind.Ik).CanRestore);
        project.ApplyOrphanRestoration(plan, true, Token);
        var restored = project.CaptureAuthoredContent().Motions.Single(actor => actor.ObjectId == target).Motion;
        Assert.Equal(occupied ? 2 : 3, restored.IkKeyframes.Count);
        Assert.False(new MmdModelVisibilityTrack(restored).Evaluate(30));
        Assert.Equal(occupied, Assert.Single(restored.IkKeyframes.SelectMany(key => key.Switches)).Enabled);
        Assert.Equal(occupied, project.CaptureAuthoredContent().Motions.Single(actor => actor.ObjectId == source).Detachment is not null);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void OrphanMetadataRejectsOlderProjectVersionsBeforeLoadingAnyModel(int version)
    {
        var original = Asset(["root"], ["smile"]); using var project = Project(original, out var id);
        var smaller = Asset(["other"], []);
        project.ApplyModelReplacement(project.PrepareModelReplacement(id, Write("version-small", smaller), smaller, Token), cancellationToken: Token);
        string path = Path.Combine(directory, "versioned.mmdscene"); project.Save(path, cancellationToken: Token);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(9, document["version"]!.GetValue<int>());
        document["version"] = version;
        if (version < 7) { document.AsObject().Remove("settings"); document.AsObject().Remove("media"); }
        bool loaded = false;
        Assert.Throws<JsonException>(() => SceneAnimationProjectStore.Prepare(document.ToJsonString(), path,
            (file, token) => { loaded = true; return Load(file, token); }, new NoPhysics(), Token));
        Assert.False(loaded);
    }

    private SceneProjectSession Project(SceneAnimationModelAsset asset, out Guid id, string name = "instance")
    {
        var layout = new SceneCompositionEditor(); var obj = layout.Add(name, Write("old", asset), ModelRole.Character, asset.Source);
        id = obj.Id; layout.SetTransform(id, new(4, 0, 0), Quaternion.Identity, 1);
        var motion = Motion(asset.Model);
        return new(layout, new(), [new(id, asset.Model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics(), initialFrame: 30);
    }
    private Guid Add(SceneProjectSession project, SceneAnimationModelAsset asset, bool withMotion = false)
    {
        Guid id = Guid.Empty;
        project.Edit(e =>
        {
            id = e.Composition.Add("target", Write(Guid.NewGuid().ToString("N"), asset), ModelRole.Character, asset.Source).Id;
            var motion = withMotion ? Motion(asset.Model) : SceneMotionAsset.FromMotion(new());
            e.Motions.Add(new(id, asset.Model, motion.Motion, PhysicsEnabled: false, Asset: motion));
        }, Token); return id;
    }
    private static SceneMotionAsset Motion(PmxModel model) => SceneMotionAsset.FromMotion(new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = new(2, 0, 0) }],
        MorphKeyframes = model.Morphs.Count == 0 ? [] : [new() { MorphName = "smile", Frame = 30, Weight = .5f }]
    });
    private string Write(string name, SceneAnimationModelAsset asset)
    { string path = Path.Combine(directory, name + ".pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(asset.Model)); return path; }
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var file = File.OpenRead(path); return Rest(PmxReader.Read(file)); }
    private static SceneAnimationModelAsset Asset(string[] bones, string[] morphs, string name = "model")
    {
        var basis = PmmMotionAdapterTests.Pmx(bones, morphs);
        var model = new PmxModel
        {
            Header = basis.Header, Name = name, EnglishName = "", Comment = "", EnglishComment = "", Bones = basis.Bones,
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex { Position = p, Normal = Vector3.UnitZ,
                Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [Array.IndexOf(bones, "root") is var index && index >= 0 ? index : 0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.One,
                EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Morphs = basis.Morphs, DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        }; return Rest(model);
    }
    private static SceneAnimationModelAsset Rest(PmxModel model) => new(model, new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model)));
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException("Unexpected physics.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException("Unexpected shared world.");
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
