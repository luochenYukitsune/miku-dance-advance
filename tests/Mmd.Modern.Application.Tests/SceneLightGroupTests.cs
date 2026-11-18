using System.Collections;
using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightGroupTests
{
    private static NativeSceneLight Light(Guid? groupId = null) =>
        new(Guid.NewGuid(), "补光", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One, GroupId: groupId);
    private static SceneLightGroup Group() => new(Guid.NewGuid(), "人物柔光");

    [Fact]
    public void SnapshotCopiesBothCollectionsAndResolvesGroupsWithoutChangingAuthoredLights()
    {
        var group = Group() with { Enabled = false, Intensity = 3 };
        var member = Light(group.Id) with { Intensity = 2 };
        var disabled = Light(group.Id) with { Enabled = false };
        var ungrouped = Light() with { Intensity = 4 };
        var lights = new[] { member, disabled, ungrouped };
        var groups = new[] { group };
        var snapshot = new SceneLightingSnapshot(lights, groups);
        lights[0] = Light(); groups[0] = Group();
        Assert.Equal(member, snapshot.Lights[0]); Assert.Equal(group, Assert.Single(snapshot.Groups));
        Assert.False(snapshot.EffectiveLights[0].Enabled); Assert.Equal(6, snapshot.EffectiveLights[0].Intensity);
        Assert.False(snapshot.EffectiveLights[1].Enabled); Assert.Equal(ungrouped, snapshot.EffectiveLights[2]);
        Assert.True(snapshot.Lights[0].Enabled); Assert.Equal(2, snapshot.Lights[0].Intensity);
        Assert.Throws<NotSupportedException>(() => ((IList)snapshot.Groups)[0] = Group());
        Assert.Throws<NotSupportedException>(() => ((IList)snapshot.EffectiveLights)[0] = Light());
        var enabled = new SceneLightingSnapshot(snapshot.Lights, [group with { Enabled = true }]);
        Assert.True(enabled.EffectiveLights[0].Enabled); Assert.False(enabled.EffectiveLights[1].Enabled);
        Assert.Empty(SceneLightingSnapshot.Empty.Groups);
        Assert.Equal(ungrouped, Assert.Single(new SceneLightingSnapshot([ungrouped]).EffectiveLights));
    }

    [Fact]
    public void SnapshotValidatesGroupsMembershipAndMultiplicationEvenWhenDisabled()
    {
        var group = Group();
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([], [null!]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([], [group, group]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([], [group with { Id = Guid.Empty }]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([], [group with { Name = " " }]));
        foreach (float value in new[] { -1f, float.NaN, float.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([], [group with { Intensity = value }]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([Light(Guid.Empty)]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([Light(Guid.NewGuid())], [group]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot(
            [Light(group.Id) with { Intensity = float.MaxValue, Enabled = false }], [group with { Enabled = false, Intensity = 2 }]));
        Assert.Equal(0, Assert.Single(new SceneLightingSnapshot([Light(group.Id)], [group with { Intensity = 0 }]).EffectiveLights).Intensity);
        // Inspector validation is independent of the full scene's membership check.
        SceneLightingSnapshot.ValidateLight(Light(group.Id));
        Assert.Throws<ArgumentException>(() => SceneLightingSnapshot.ValidateLight(Light(group.Id) with { Range = 0 }));
        Assert.Throws<ArgumentException>(() => SceneLightingSnapshot.ValidateLight(Light(Guid.Empty)));
    }

    [Fact]
    public void GroupEditsMembershipAndDissolveAreAtomicUndoableAndKeepPerLightState()
    {
        var editor = new SceneLightingEditor(); var group = Group();
        var enabled = Light(); var disabled = Light() with { Enabled = false, Intensity = 4 };
        editor.Add(enabled); editor.Add(disabled); editor.SetModelRole("model:1", ModelRole.Environment, true);
        editor.AddGroup(group); editor.AssignToGroup([enabled.Id, disabled.Id], group.Id);
        editor.UpdateGroup(group with { Enabled = false, Intensity = 2 });
        Assert.All(editor.Snapshot.EffectiveLights, light => Assert.False(light.Enabled));
        Assert.True(editor.Lights[0].Enabled); Assert.False(editor.Lights[1].Enabled);
        editor.UpdateGroup(group);
        Assert.True(editor.Snapshot.EffectiveLights[0].Enabled); Assert.False(editor.Snapshot.EffectiveLights[1].Enabled);
        string before = editor.SerializeSidecar();
        Assert.Throws<ArgumentException>(() => editor.AssignToGroup([enabled.Id, Guid.NewGuid()], null));
        Assert.Throws<ArgumentException>(() => editor.AssignToGroup([enabled.Id], Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => editor.UpdateGroup(group with { Intensity = float.NaN }));
        Assert.Equal(before, editor.SerializeSidecar());
        Assert.True(editor.RemoveGroup(group.Id)); Assert.False(editor.RemoveGroup(group.Id));
        Assert.Empty(editor.Groups); Assert.All(editor.Lights, light => Assert.Null(light.GroupId));
        Assert.Equal(enabled.Enabled, editor.Lights[0].Enabled); Assert.Equal(disabled.Intensity, editor.Lights[1].Intensity);
        Assert.Equal(disabled.Id, editor.SelectedId); Assert.Single(editor.ModelRoles);
        Assert.True(editor.Undo()); Assert.Equal(before, editor.SerializeSidecar());
        Assert.True(editor.Redo()); Assert.Empty(editor.Groups);
    }

    [Fact]
    public void GroupGestureCancelAndSnapshotReplacementKeepHistorySelectionAndRoles()
    {
        var editor = new SceneLightingEditor(); var group = Group(); var light = Light(group.Id);
        editor.SetModelRole("model:1", ModelRole.Character);
        editor.ReplaceSnapshot(new([light], [group]), light.Id);
        editor.BeginEdit();
        editor.UpdateGroup(group with { Intensity = 2 }); editor.UpdateGroup(group with { Intensity = 3 });
        editor.CommitEdit(); Assert.True(editor.Undo()); Assert.Equal(group, Assert.Single(editor.Groups));
        Assert.True(editor.CanRedo); editor.BeginEdit(); editor.RemoveGroup(group.Id); editor.CancelEdit();
        Assert.True(editor.CanRedo); Assert.Equal(light.Id, editor.SelectedId);
        Assert.True(editor.Redo()); Assert.Equal(3, Assert.Single(editor.Groups).Intensity);
        editor.ReplaceSnapshot(SceneLightingSnapshot.Empty); Assert.Null(editor.SelectedId); Assert.Single(editor.ModelRoles);
        Assert.True(editor.Undo()); Assert.Equal(light.Id, editor.SelectedId); Assert.Equal(light, Assert.Single(editor.Lights));
        Assert.Throws<ArgumentException>(() => editor.ReplaceSnapshot(SceneLightingSnapshot.Empty, light.Id));
        Assert.Equal(light, Assert.Single(editor.Lights));
    }

    [Fact]
    public void SidecarRoundTripPreservesGroupAuthoringAndLoadsLegacyVersionOne()
    {
        var editor = new SceneLightingEditor(); var group = Group() with { Enabled = false, Intensity = 2.5f };
        var light = Light(group.Id) with { Intensity = 3 };
        editor.ReplaceSnapshot(new([light], [group]), light.Id); editor.SetModelRole("model:1", ModelRole.Character);
        var restored = new SceneLightingEditor();
        Assert.True(restored.TryLoadSidecar(editor.SerializeSidecar(), out var error), error);
        Assert.Equal(editor.SerializeSidecar(), restored.SerializeSidecar()); Assert.False(restored.CanUndo);
        restored.UpdateGroup(group with { Enabled = true }); Assert.False(Assert.Single(editor.Groups).Enabled);
        var legacyEditor = new SceneLightingEditor(); var legacyLight = Light(); legacyEditor.Add(legacyLight);
        legacyEditor.SetModelRole("legacy:1", ModelRole.Environment, true);
        var legacy = JsonNode.Parse(legacyEditor.SerializeSidecar())!;
        legacy["version"] = 1; legacy.AsObject().Remove("groups");
        Assert.True(restored.TryLoadSidecar(legacy.ToJsonString(), out error), error); Assert.Empty(restored.Groups);
        // Version 1 stored I / d^2 intensities, converted once on load.
        Assert.Equal(SceneLightFalloff.FromLegacy(legacyLight), Assert.Single(restored.Lights)); Assert.Equal(legacyLight.Id, restored.SelectedId);
        Assert.Equal(new(ModelRole.Environment, true), restored.GetModelRole("legacy:1"));
        Assert.False(restored.CanUndo); Assert.False(restored.CanRedo);
    }

    [Theory]
    [InlineData("missing-groups")]
    [InlineData("null-group")]
    [InlineData("duplicate-group")]
    [InlineData("missing-member-group")]
    [InlineData("legacy-groups")]
    [InlineData("legacy-membership")]
    [InlineData("duplicate-property")]
    public void InvalidGroupSidecarLeavesTheActiveGestureAndHistoryUntouched(string defect)
    {
        var editor = new SceneLightingEditor(); var group = Group(); var light = Light(group.Id);
        editor.ReplaceSnapshot(new([light], [group]), light.Id);
        var json = JsonNode.Parse(editor.SerializeSidecar())!;
        switch (defect)
        {
            case "missing-groups": json.AsObject().Remove("groups"); break;
            case "null-group": json["groups"]![0] = null; break;
            case "duplicate-group": json["groups"]!.AsArray().Add(json["groups"]![0]!.DeepClone()); break;
            case "missing-member-group": json["groups"] = new JsonArray(); break;
            case "legacy-groups": json["version"] = 1; break;
            case "legacy-membership": json["version"] = 1; json.AsObject().Remove("groups"); break;
        }
        string invalid = defect == "duplicate-property" ? json.ToJsonString().Replace("\"intensity\":1", "\"intensity\":2,\"intensity\":1") : json.ToJsonString();
        editor.BeginEdit(); editor.UpdateGroup(group with { Intensity = 4 }); string prior = editor.SerializeSidecar();
        Assert.False(editor.TryLoadSidecar(invalid, out var error)); Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(prior, editor.SerializeSidecar()); Assert.True(editor.IsEditing);
        editor.CancelEdit(); Assert.True(editor.Undo()); Assert.Empty(editor.Groups);
    }
}
