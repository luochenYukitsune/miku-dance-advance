using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class ScenePmmCameraParentTests
{
    [Fact]
    public void ModelReplacementPausesMissingCameraParentAndRequiresConfirmationBeforeReactivation()
    {
        var f = Fixture(); var source = f.Motions[0];
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, sceneMotion: f.Scene, pmmControls: f.Controls);
        var smaller = Model(["unused"]); string path = Path.Combine(directory, "smaller.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(smaller));
        var bytes = f.Controls.Capture().DocumentBytes;
        project.ApplyModelReplacement(project.PrepareModelReplacement(source.ObjectId, path, new(smaller, Rest(smaller)), Token), cancellationToken: Token);
        Assert.Null(project.PmmControls!.CameraParentAt(0)); Assert.NotEmpty(project.PmmControls.DetachedDataNotices);
        Assert.Equal(bytes, project.PmmControls.Capture().DocumentBytes);
        string restored = Path.Combine(directory, "restored.pmx"); File.WriteAllBytes(restored, PmxWriter.ToBytes(source.Model));
        project.ApplyModelReplacement(project.PrepareModelReplacement(source.ObjectId, restored, new(source.Model, Rest(source.Model)), Token), cancellationToken: Token);
        Assert.Null(project.PmmControls.CameraParentAt(0));
        var same = project.PrepareOrphanRestoration(source.ObjectId, source.ObjectId);
        Assert.True(same.RecoverableCount > 0); project.ApplyOrphanRestoration(same, true, Token);
        Assert.Equal(source.ObjectId, project.PmmControls.CameraParentAt(0)!.ObjectId);
        Assert.True(project.Undo(Token)); Assert.Null(project.PmmControls.CameraParentAt(0));
        Assert.True(project.Redo(Token)); Assert.Equal(source.ObjectId, project.PmmControls.CameraParentAt(0)!.ObjectId);
    }

    [Fact]
    public void LaterImportedModelCanExplicitlyOwnMissingPmmCameraBindingsAndPersistThem()
    {
        var f = Fixture(); var source = f.Motions[0];
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, sceneMotion: f.Scene, pmmControls: f.Controls);
        var smaller = Model(["unused"]); string path = Path.Combine(directory, "smaller.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(smaller));
        project.ApplyModelReplacement(project.PrepareModelReplacement(source.ObjectId, path, new(smaller, Rest(smaller)), Token), cancellationToken: Token);
        var targetModel = Model(["target"]); string targetPath = Path.Combine(directory, "target.pmx"); File.WriteAllBytes(targetPath, PmxWriter.ToBytes(targetModel));
        Guid target = Guid.Empty;
        project.Edit(e =>
        {
            target = e.Composition.Add("explicit recovery target", targetPath, ModelRole.Character, Rest(targetModel)).Id;
            var empty = SceneMotionAsset.FromMotion(new()); e.Motions.Add(new(target, targetModel, empty.Motion, PhysicsEnabled: false, Asset: empty));
        }, Token);
        Assert.Null(project.PmmControls!.CameraParentAt(0));
        var plan = project.PrepareOrphanRestoration(source.ObjectId, target);
        Assert.Contains(plan.Notices, n => n.Kind == SceneModelDataKind.PmmControl && n.CanRestore);
        project.ApplyOrphanRestoration(plan, true, Token);
        Assert.Equal(target, project.PmmControls.CameraParentAt(0)!.ObjectId);
        Assert.Equal(0, project.PmmControls.CameraParentAt(0)!.BoneIndex);
        var payload = project.PmmControls.Capture();
        var retained = ScenePmmControlState.Restore(payload, project.CaptureAuthoredContent().Motions.ToDictionary(m => m.ObjectId, m => m.Model));
        Assert.Equal(target, retained.CameraParentAt(0)!.ObjectId);
        Assert.False(PmaPmmCompatibility.Inspect(project).CanExport);
        string package = Path.Combine(directory, "restored-parent.pma"); PmaProjectStore.Save(package, project, cancellationToken: Token);
        using var loaded = PmaProjectStore.Load(package, Path.Combine(directory, "restored-cache"), (file, token) =>
        { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(file); var model = PmxReader.Read(input); return new(model, Rest(model)); }, provider, cancellationToken: Token);
        Assert.Equal(target, loaded.Session.PmmControls!.CameraParentAt(0)!.ObjectId);
        Assert.Equal(project.PmmControls.Capture().DocumentBytes, loaded.Session.PmmControls.Capture().DocumentBytes);
    }

    [Fact]
    public void PausedRedirectUsesCurrentTargetNamesAndRequiresAnotherConfirmation()
    {
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        var fixture = Fixture();
        var source = fixture.Motions[0];
        using var project = new SceneProjectSession(fixture.Layout, new(), fixture.Motions, provider, sceneMotion: fixture.Scene, pmmControls: fixture.Controls);
        string smallerPath = Path.Combine(directory, "redirect-source.pmx"); var smaller = Model(["unused"]);
        File.WriteAllBytes(smallerPath, PmxWriter.ToBytes(smaller));
        project.ApplyModelReplacement(project.PrepareModelReplacement(source.ObjectId, smallerPath, new(smaller, Rest(smaller)), Token), cancellationToken: Token);
        var targetModel = Model(["target"]); string targetPath = Path.Combine(directory, "redirect-target.pmx");
        File.WriteAllBytes(targetPath, PmxWriter.ToBytes(targetModel));
        Guid target = Guid.Empty;
        project.Edit(edit =>
        {
            target = edit.Composition.Add("redirect target", targetPath, ModelRole.Character, Rest(targetModel)).Id;
            var empty = SceneMotionAsset.FromMotion(new());
            edit.Motions.Add(new(target, targetModel, empty.Motion, PhysicsEnabled: false, Asset: empty));
        }, Token);
        project.ApplyOrphanRestoration(project.PrepareOrphanRestoration(source.ObjectId, target), true, Token);
        Assert.Equal(target, project.PmmControls!.CameraParentAt(0)!.ObjectId);
        byte[] original = project.PmmControls.Capture().DocumentBytes;

        string missingPath = Path.Combine(directory, "redirect-missing.pmx"); var missing = Model(["other"]);
        File.WriteAllBytes(missingPath, PmxWriter.ToBytes(missing));
        project.ApplyModelReplacement(project.PrepareModelReplacement(target, missingPath, new(missing, Rest(missing)), Token), cancellationToken: Token);
        Assert.Null(project.PmmControls.CameraParentAt(0));
        Assert.Contains(project.PmmControls.DetachedDataNotices, notice => notice.Name == "target" && !notice.CanRestore);

        var preview = project.PrepareModelReplacement(target, targetPath, new(targetModel, Rest(targetModel)), Token);
        Assert.Contains(preview.Notices, notice => notice.Kind == SceneModelDataKind.PmmControl && notice.Name == "target" && notice.CanRestore);
        project.ApplyModelReplacement(preview, cancellationToken: Token);
        Assert.Null(project.PmmControls.CameraParentAt(0));
        Assert.Contains(project.PmmControls.DetachedDataNotices, notice => notice.Name == "target" && notice.CanRestore);
        var originalSource = project.PrepareModelReplacement(source.ObjectId, smallerPath, new(smaller, Rest(smaller)), Token);
        Assert.DoesNotContain(originalSource.Notices, notice => notice.Kind == SceneModelDataKind.PmmControl);
        var unrelated = project.PrepareOrphanRestoration(source.ObjectId, source.ObjectId);
        Assert.Equal(0, unrelated.RecoverableCount);
        var unchanged = project.Frame; project.ApplyOrphanRestoration(unrelated, true, Token); Assert.Same(unchanged, project.Frame);
        var confirmed = project.PrepareOrphanRestoration(target, target);
        Assert.True(confirmed.RecoverableCount > 0);
        Assert.Throws<InvalidOperationException>(() => project.ApplyOrphanRestoration(confirmed, false, Token));
        Assert.Null(project.PmmControls.CameraParentAt(0));
        project.ApplyOrphanRestoration(confirmed, true, Token);
        Assert.Equal(target, project.PmmControls.CameraParentAt(0)!.ObjectId);
        Assert.Equal(original, project.PmmControls.Capture().DocumentBytes);
        Assert.True(project.Undo(Token)); Assert.Null(project.PmmControls.CameraParentAt(0));
        Assert.True(project.Redo(Token)); Assert.Equal(target, project.PmmControls.CameraParentAt(0)!.ObjectId);
    }

    [Theory]
    [InlineData("null-binding")]
    [InlineData("empty-source")]
    [InlineData("unknown-source")]
    [InlineData("empty-target")]
    [InlineData("same-owner")]
    [InlineData("negative-ordinal")]
    [InlineData("large-ordinal")]
    [InlineData("attached-ordinal")]
    [InlineData("null-target-names")]
    [InlineData("null-target-name")]
    [InlineData("large-target-table")]
    [InlineData("large-target-name")]
    [InlineData("missing-active-name")]
    [InlineData("ambiguous-active-name")]
    [InlineData("duplicate-binding")]
    [InlineData("large-redirect-table")]
    [InlineData("missing-source-names")]
    [InlineData("missing-detached-ordinals")]
    [InlineData("duplicate-source-name")]
    public void MalformedRedirectsRejectDuringPayloadValidationBeforeModelAccess(string corruption)
    {
        var f = RedirectedControlPayload(); var payload = f.Payload;
        var binding = Assert.Single(payload.RestoredBindings!);
        binding = corruption switch
        {
            "null-binding" => null!,
            "empty-source" => binding with { SourceObjectId = Guid.Empty },
            "unknown-source" => binding with { SourceObjectId = Guid.NewGuid() },
            "empty-target" => binding with { TargetObjectId = Guid.Empty },
            "same-owner" => binding with { TargetObjectId = binding.SourceObjectId },
            "negative-ordinal" => binding with { BoneOrdinal = -1 },
            "large-ordinal" => binding with { BoneOrdinal = int.MaxValue },
            "attached-ordinal" => binding with { BoneOrdinal = 1 },
            "null-target-names" => binding with { TargetBoneNames = null! },
            "null-target-name" => binding with { TargetBoneNames = [null!] },
            "large-target-table" => binding with { TargetBoneNames = new string[100_001] },
            "large-target-name" => binding with { TargetBoneNames = [new string('x', 4097)] },
            "missing-active-name" => binding with { TargetBoneNames = ["other"] },
            "ambiguous-active-name" => binding with { TargetBoneNames = ["target", "target"] },
            _ => binding
        };
        payload = payload with { RestoredBindings = corruption switch
        {
            "duplicate-binding" => [binding, binding],
            "large-redirect-table" => new ScenePmmOrphanRedirection[100_001],
            _ => [binding]
        } };
        if (corruption == "missing-source-names")
            payload = payload with { Actors = payload.Actors.Select(actor => actor.ObjectId == f.Source
                ? actor with { NativeBindingNames = null } : actor).ToArray() };
        if (corruption == "missing-detached-ordinals")
            payload = payload with { Actors = payload.Actors.Select(actor => actor.ObjectId == f.Source
                ? actor with { DetachedBoneOrdinals = null } : actor).ToArray() };
        if (corruption == "duplicate-source-name")
            payload = payload with { Actors = payload.Actors.Select(actor => actor.ObjectId == f.Source
                ? actor with { NativeBindingNames = ["target", "target"] } : actor).ToArray() };
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.ValidatePayload(payload));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.ValidatePayload(payload, f.Models.Keys.ToArray()));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(payload, f.Models));
    }

    [Fact]
    public void RedirectTargetsMustBelongToProjectWhileIndependentActorsRemainAllowed()
    {
        var f = RedirectedControlPayload();
        f.Models.Add(Guid.NewGuid(), Model(["independent"]));
        ScenePmmControlState.ValidatePayload(f.Payload, f.Models.Keys.ToArray());
        var state = ScenePmmControlState.Restore(f.Payload, f.Models);
        Assert.Equal(f.Payload.Actors.Select(actor => (actor.ObjectId, actor.PmmModelIndex)),
            state.Capture().Actors.Select(actor => (actor.ObjectId, actor.PmmModelIndex)));
        Assert.Equal(f.Target, state.CameraParentAt(0)!.ObjectId);
        var unknown = f.Payload with { RestoredBindings = [f.Payload.RestoredBindings![0] with { TargetObjectId = Guid.NewGuid() }] };
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.ValidatePayload(unknown, f.Models.Keys.ToArray()));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(unknown, f.Models));
        var targetModel = f.Models[f.Target]; f.Models[f.Target] = null!;
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(f.Payload, f.Models));
        f.Models[f.Target] = targetModel;
        f.Models.Remove(f.Source);
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.ValidatePayload(f.Payload, f.Models.Keys.ToArray()));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(f.Payload, f.Models));
    }

    [Fact]
    public void RedirectPayloadAndCapturedArraysCannotMutateRestoredState()
    {
        var f = RedirectedControlPayload();
        var state = ScenePmmControlState.Restore(f.Payload, f.Models);
        byte[] expected = state.Capture().DocumentBytes;
        foreach (var payload in new[] { f.Payload, state.Capture() })
        {
            payload.DocumentBytes[0] ^= 1;
            var actor = payload.Actors.Single(actor => actor.ObjectId == f.Source);
            actor.BoneIndices[0] = 99; actor.NativeBindingNames![0] = "changed"; actor.DetachedBoneOrdinals![0] = 99;
            payload.RestoredBindings![0].TargetBoneNames[0] = "changed";
            payload.RestoredBindings[0] = null!;
            payload.Actors[0] = null!;
        }
        Assert.Equal(f.Target, state.CameraParentAt(0)!.ObjectId);
        Assert.Empty(state.DetachedDataNotices);
        Assert.Equal(expected, state.Capture().DocumentBytes);
        state.ValidateActors(f.Models);
    }

    [Fact]
    public void RedirectsCannotOverwriteAnExistingNativePmmBinding()
    {
        var fixture = Fixture(); var source = fixture.Motions[0]; var target = fixture.Motions[1];
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(fixture.Layout, new(), fixture.Motions, provider, sceneMotion: fixture.Scene, pmmControls: fixture.Controls);
        var smaller = Model(["unused"]); string path = Path.Combine(directory, "occupied-source.pmx");
        File.WriteAllBytes(path, PmxWriter.ToBytes(smaller));
        project.ApplyModelReplacement(project.PrepareModelReplacement(source.ObjectId, path, new(smaller, Rest(smaller)), Token), cancellationToken: Token);
        var before = project.PmmControls!; var plan = project.PrepareOrphanRestoration(source.ObjectId, target.ObjectId);
        Assert.Contains(plan.Notices, notice => notice.Kind == SceneModelDataKind.PmmControl && notice.Name == "target" && !notice.CanRestore);
        project.ApplyOrphanRestoration(plan, true, Token);
        Assert.Null(project.PmmControls!.CameraParentAt(0));
        Assert.Equal(before.Capture().DocumentBytes, project.PmmControls.Capture().DocumentBytes);
        var payload = before.Capture() with { RestoredBindings = [new(source.ObjectId, 0, target.ObjectId)
            { TargetBoneNames = target.Model.Bones.Select(b => b.Name).ToArray() }] };
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.ValidatePayload(payload));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(payload, project.CaptureAuthoredContent().Motions.ToDictionary(m => m.ObjectId, m => m.Model)));
        Assert.Same(before, project.PmmControls);
    }

    [Fact]
    public void TwoOrphansCannotOccupyTheSameRedirectTargetBone()
    {
        var fixture = Fixture(); var source = fixture.Motions[0].ObjectId; var second = fixture.Motions[1].ObjectId;
        Guid target = Guid.NewGuid(); var payload = fixture.Controls.Capture();
        payload = payload with
        {
            Actors = payload.Actors.Select(actor => actor.ObjectId == source
                ? actor with { BoneIndices = [-1, 0], NativeBindingNames = ["target", "unused"], DetachedBoneOrdinals = [0] }
                : actor with { BoneIndices = [0, -1], NativeBindingNames = ["unused", "target"], DetachedBoneOrdinals = [1] }).ToArray(),
            RestoredBindings = [new(source, 0, target) { TargetBoneNames = ["target"] }, new(second, 1, target) { TargetBoneNames = ["target"] }]
        };
        var models = new Dictionary<Guid, PmxModel> { [source] = Model(["unused"]), [second] = Model(["unused"]), [target] = Model(["target"]) };
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.ValidatePayload(payload));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Restore(payload, models));
        // Both raw orphan records remain valid when neither is active.
        payload = payload with { RestoredBindings = payload.RestoredBindings!.Select(binding => binding with { IsDetached = true }).ToArray() };
        var paused = ScenePmmControlState.Restore(payload, models);
        Assert.Null(paused.CameraParentAt(0)); Assert.Equal(2, paused.DetachedDataNotices.Count);
        Assert.All(paused.DetachedDataNotices, notice => Assert.False(notice.CanRestore));
    }

    [Fact]
    public void UnrelatedPmmOrphansNeverAppearInAnotherModelsReplacementOrSameTargetRestoration()
    {
        var fixture = Fixture(); var source = fixture.Motions[0]; var unrelated = fixture.Motions[1];
        var payload = fixture.Controls.Capture();
        payload = payload with { Actors = payload.Actors.Select(actor => actor.ObjectId == source.ObjectId
            ? actor with { BoneIndices = [-1, 0], NativeBindingNames = ["target", "unused"], DetachedBoneOrdinals = [0] } : actor).ToArray() };
        var controls = ScenePmmControlState.Restore(payload, fixture.Motions.ToDictionary(actor => actor.ObjectId, actor => actor.Model));
        var notice = Assert.Single(controls.DetachedDataNotices); Assert.True(notice.CanRestore);
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(fixture.Layout, new(), fixture.Motions, provider, sceneMotion: fixture.Scene, pmmControls: controls);
        string path = fixture.Layout.Objects.Single(obj => obj.Id == unrelated.ObjectId).SourcePath;
        var replacement = project.PrepareModelReplacement(unrelated.ObjectId, path, new(unrelated.Model, Rest(unrelated.Model)), Token);
        Assert.DoesNotContain(replacement.Notices, item => item.Kind == SceneModelDataKind.PmmControl);
        var restore = project.PrepareOrphanRestoration(unrelated.ObjectId, unrelated.ObjectId);
        Assert.Empty(restore.Notices); Assert.Equal(0, restore.RecoverableCount);
        var frame = project.Frame;
        project.ApplyOrphanRestoration(restore, true, Token);
        Assert.False(project.CanUndo); Assert.Same(frame, project.Frame); Assert.Same(controls, project.PmmControls);
        project.ApplyModelReplacement(replacement, true, Token);
        Assert.Null(project.PmmControls!.CameraParentAt(0));
        Assert.True(Assert.Single(project.PmmControls.DetachedDataNotices).CanRestore);
        var matching = project.PrepareOrphanRestoration(source.ObjectId, source.ObjectId);
        Assert.Equal(1, matching.RecoverableCount);
        project.ApplyOrphanRestoration(matching, true, Token);
        Assert.Equal(source.ObjectId, project.PmmControls.CameraParentAt(0)!.ObjectId);
        Assert.Empty(project.PmmControls.DetachedDataNotices);
        Assert.Equal(payload.DocumentBytes, project.PmmControls.Capture().DocumentBytes);
    }

    [Fact]
    public void ConflictingPausedNativeAndRedirectClaimsRemainPausedAfterConfirmedReplacement()
    {
        var fixture = Fixture(); var source = fixture.Motions[0]; var target = fixture.Motions[1];
        var smaller = Model(["unused"]); var returned = Model(["unused", "target"]);
        var payload = fixture.Controls.Capture();
        payload = payload with
        {
            Actors = payload.Actors.Select(actor => actor.ObjectId == source.ObjectId
                ? actor with { BoneIndices = [-1, 0], NativeBindingNames = ["target", "unused"], DetachedBoneOrdinals = [0] }
                : actor with { BoneIndices = [0, -1], NativeBindingNames = ["unused", "target"], DetachedBoneOrdinals = [1] }).ToArray(),
            RestoredBindings = [new(source.ObjectId, 0, target.ObjectId) { TargetBoneNames = ["unused", "target"], IsDetached = true }]
        };
        var motions = fixture.Motions.Select(actor => actor.ObjectId == source.ObjectId ? actor with { Model = smaller }
            : actor with { Model = returned }).ToArray();
        var controls = ScenePmmControlState.Restore(payload, motions.ToDictionary(actor => actor.ObjectId, actor => actor.Model));
        Assert.All(controls.DetachedDataNotices, notice => Assert.False(notice.CanRestore));
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(fixture.Layout, new(), motions, provider, pmmControls: controls);
        string path = Path.Combine(directory, "conflicting-return.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(returned));
        var replacement = project.PrepareModelReplacement(target.ObjectId, path, new(returned, Rest(returned)), Token);
        var notices = replacement.Notices.Where(notice => notice.Kind == SceneModelDataKind.PmmControl).ToArray();
        Assert.Equal(2, notices.Length); Assert.All(notices, notice => Assert.False(notice.CanRestore));
        project.ApplyModelReplacement(replacement, true, Token);
        Assert.Null(project.PmmControls!.CameraParentAt(0)); Assert.Null(project.PmmControls.CameraParentAt(5));
        Assert.All(project.PmmControls.DetachedDataNotices, notice => Assert.False(notice.CanRestore));
        Assert.Equal(payload.DocumentBytes, project.PmmControls.Capture().DocumentBytes);
    }

    [Fact]
    public void ReturningOriginalSourceCannotReclaimItsExplicitRedirect()
    {
        var f = RedirectedControlPayload(); var state = ScenePmmControlState.Restore(f.Payload, f.Models);
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        var layout = new SceneCompositionEditor(); var identities = new Dictionary<Guid, Guid>();
        foreach (var actor in f.Models)
        {
            string path = Path.Combine(directory, "returning-" + actor.Key + ".pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(actor.Value));
            identities[actor.Key] = layout.Add("actor", path, ModelRole.Character, Rest(actor.Value)).Id;
        }
        // Rebase only the detached storage identities to this independently constructed scene.
        var rebased = f.Payload with
        {
            Actors = f.Payload.Actors.Select(actor => actor with { ObjectId = identities[actor.ObjectId] }).ToArray(),
            RestoredBindings = [f.Payload.RestoredBindings![0] with { SourceObjectId = identities[f.Source], TargetObjectId = identities[f.Target] }]
        };
        var motions = f.Models.Select(actor => new SceneActorMotion(identities[actor.Key], actor.Value, new(), PhysicsEnabled: false)).ToArray();
        var models = motions.ToDictionary(actor => actor.ObjectId, actor => actor.Model);
        using var project = new SceneProjectSession(layout, new(), motions, provider, pmmControls: ScenePmmControlState.Restore(rebased, models));
        var returned = Model(["unused", "target"]); string returnPath = Path.Combine(directory, "returned-original.pmx");
        File.WriteAllBytes(returnPath, PmxWriter.ToBytes(returned));
        project.ApplyModelReplacement(project.PrepareModelReplacement(identities[f.Source], returnPath, new(returned, Rest(returned)), Token), true, Token);
        Assert.Equal(identities[f.Target], project.PmmControls!.CameraParentAt(0)!.ObjectId);
        Assert.Contains(0, project.PmmControls.Capture().Actors.Single(actor => actor.ObjectId == identities[f.Source]).DetachedBoneOrdinals!);
        Assert.Equal(state.Capture().DocumentBytes, project.PmmControls.Capture().DocumentBytes);
    }

    [Fact]
    public void NormalPmmCaptureKeepsVersionEightButDetachedPayloadRequiresVersionNine()
    {
        var fixture = Fixture();
        Assert.False(fixture.Controls.HasReplacementMetadata);
        Assert.All(fixture.Controls.Capture().Actors, actor => { Assert.Null(actor.NativeBindingNames); Assert.Null(actor.DetachedBoneOrdinals); });
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(fixture.Layout, new(), fixture.Motions, provider, sceneMotion: fixture.Scene, pmmControls: fixture.Controls);
        string path = Path.Combine(directory, "pmm-version.mmdscene"); project.Save(path, cancellationToken: Token);
        var normal = JsonNode.Parse(File.ReadAllText(path))!; Assert.Equal(8, normal["version"]!.GetValue<int>());
        bool loaded = false;
        var smaller = Model(["unused"]); string smallerPath = Path.Combine(directory, "version-smaller.pmx");
        File.WriteAllBytes(smallerPath, PmxWriter.ToBytes(smaller));
        project.ApplyModelReplacement(project.PrepareModelReplacement(fixture.Motions[0].ObjectId, smallerPath, new(smaller, Rest(smaller)), Token), cancellationToken: Token);
        project.Save(path, cancellationToken: Token);
        var detached = JsonNode.Parse(File.ReadAllText(path))!; Assert.Equal(9, detached["version"]!.GetValue<int>());
        detached["version"] = 8;
        Assert.Throws<JsonException>(() => SceneAnimationProjectStore.Prepare(detached.ToJsonString(), path,
            (_, _) => { loaded = true; throw new InvalidOperationException("Model access must follow metadata validation."); }, provider, Token));
        Assert.False(loaded);
    }

    [Fact]
    public void ExplicitEncodingBindingSurvivesReplacementThatAddsTheOtherLegalMeaning()
    {
        var baseDocument = Document();
        var document = new PmmDocument
        {
            AnsiCodePage = 936, PanelStates = baseDocument.PanelStates, OutputWidth = 640, OutputHeight = 480, CameraFov = 35,
            Models = [Track(42, ["unused", "target"]), Track(7, ["中", "unused"])],
            Camera = baseDocument.Camera, Settings = baseDocument.Settings, Gravity = baseDocument.Gravity
        };
        Guid source = Guid.NewGuid(), second = Guid.NewGuid();
        var original = Model(["unused", "中"]); var other = Model(["target", "unused"]);
        var state = ScenePmmControlState.Create(document, [new(source, 7, original), new(second, 42, other)]);
        var name = state.ExportDocument().Models.Single(model => model.Index == 7).BoneNames[0];
        var meanings = PmmNameResolver.DecodeCandidates(name).Select(candidate => candidate.Value).Distinct().ToArray();
        Assert.Equal(2, meanings.Length);
        var models = new Dictionary<Guid, PmxModel> { [source] = Model(["unused", .. meanings]), [second] = other };
        var payload = state.Capture();
        payload = payload with { Actors = payload.Actors.Select(actor => actor.ObjectId == source
            ? actor with { NativeBindingNames = ["中", "unused"], BoneIndices = [Array.IndexOf(meanings, "中") + 1, 0] } : actor).ToArray() };
        var replaced = ScenePmmControlState.Restore(payload, models);
        Assert.True(replaced.HasReplacementMetadata);
        Assert.Equal(["中", "unused"], Assert.IsType<string[]>(replaced.Capture().Actors.Single(actor => actor.ObjectId == source).NativeBindingNames));
        Assert.Equal(replaced.CameraParentAt(0), ScenePmmControlState.Restore(replaced.Capture(), models).CameraParentAt(0));
        Assert.Equal(state.Capture().DocumentBytes, replaced.Capture().DocumentBytes);
    }

    private (ScenePmmControlPayload Payload, Dictionary<Guid, PmxModel> Models, Guid Source, Guid Target) RedirectedControlPayload()
    {
        var fixture = Fixture(); var source = fixture.Motions[0].ObjectId; var target = Guid.NewGuid();
        var payload = fixture.Controls.Capture();
        payload = payload with
        {
            Actors = payload.Actors.Select(actor => actor.ObjectId == source
                ? actor with { BoneIndices = [-1, 0], NativeBindingNames = ["target", "unused"], DetachedBoneOrdinals = [0] } : actor).ToArray(),
            RestoredBindings = [new(source, 0, target) { TargetBoneNames = ["target"] }]
        };
        var models = fixture.Motions.ToDictionary(actor => actor.ObjectId, actor => actor.Model);
        models[source] = Model(["unused"]); models[target] = Model(["target"]);
        return (payload, models, source, target);
    }
}
