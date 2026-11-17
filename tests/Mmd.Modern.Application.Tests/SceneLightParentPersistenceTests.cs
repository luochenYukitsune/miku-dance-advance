using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightParentPersistenceTests
{
    private static NativeSceneLight Light(SceneLightParent? parent = null) => new(Guid.NewGuid(), "局部补光",
        SceneLightKind.Spot, new(2, -3, 4), Quaternion.CreateFromYawPitchRoll(.2f, -.3f, .4f),
        new(.25f, .5f, 2), Range: 17, Parent: parent);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(13)]
    public void SidecarPreservesParentLocalTransformRangeAndGroupAuthoring(int? boneIndex)
    {
        var parent = new SceneLightParent(Guid.NewGuid(), boneIndex);
        var group = new SceneLightGroup(Guid.NewGuid(), "父级灯组", false, 2.5f);
        var light = Light(parent) with { GroupId = group.Id, Intensity = 3 };
        var editor = new SceneLightingEditor();
        editor.ReplaceSnapshot(new([light], [group]), light.Id);
        string json = editor.SerializeSidecar();
        Assert.Equal(SceneLightingEditor.SidecarVersion, JsonNode.Parse(json)!["version"]!.GetValue<int>());
        var restored = new SceneLightingEditor();
        Assert.True(restored.TryLoadSidecar(json, out var error), error);
        Assert.Equal(light, Assert.Single(restored.Lights));
        Assert.Equal(group, Assert.Single(restored.Groups));
        var effective = Assert.Single(restored.Snapshot.EffectiveLights);
        Assert.Equal(parent, effective.Parent);
        Assert.Equal(light.Position, effective.Position);
        Assert.Equal(light.Rotation, effective.Rotation);
        Assert.Equal(17, effective.Range);
        Assert.Equal(7.5f, effective.Intensity);
        Assert.False(effective.Enabled);
        Assert.False(restored.CanUndo);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void LegacySidecarsRejectParentFieldsEvenWhenExplicitlyNull(int version, bool nullParent)
    {
        var editor = new SceneLightingEditor();
        editor.Add(Light(new(Guid.NewGuid(), 0)));
        var json = JsonNode.Parse(editor.SerializeSidecar())!;
        json["version"] = version;
        if (version == 1) json.AsObject().Remove("groups");
        if (nullParent) json["lights"]![0]!["parent"] = null;
        editor.BeginEdit();
        editor.Update(editor.Lights[0] with { Intensity = 4 });
        string before = editor.SerializeSidecar();
        Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out var error));
        Assert.Contains("version 3", error);
        Assert.Equal(before, editor.SerializeSidecar());
        Assert.True(editor.IsEditing);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacySidecarsWithoutParentsStillLoad(int version)
    {
        var editor = new SceneLightingEditor();
        var light = Light(); editor.Add(light);
        var json = JsonNode.Parse(editor.SerializeSidecar())!;
        json["version"] = version;
        if (version == 1) json.AsObject().Remove("groups");
        var restored = new SceneLightingEditor();
        Assert.True(restored.TryLoadSidecar(json.ToJsonString(), out var error), error);
        Assert.Equal(light, Assert.Single(restored.Lights));
        Assert.Null(restored.Lights[0].Parent);
    }

    [Theory]
    [InlineData("empty-object")]
    [InlineData("negative-bone")]
    [InlineData("missing-object")]
    [InlineData("unknown-parent-field")]
    public void InvalidParentSidecarDoesNotReplaceActiveState(string defect)
    {
        var editor = new SceneLightingEditor();
        editor.Add(Light(new(Guid.NewGuid(), 0)));
        string before = editor.SerializeSidecar();
        var json = JsonNode.Parse(before)!;
        var parent = json["lights"]![0]!["parent"]!.AsObject();
        switch (defect)
        {
            case "empty-object": parent["objectId"] = Guid.Empty; break;
            case "negative-bone": parent["boneIndex"] = -1; break;
            case "missing-object": parent.Remove("objectId"); break;
            case "unknown-parent-field": parent["worldPosition"] = new JsonArray(1, 2, 3); break;
        }
        Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(before, editor.SerializeSidecar());
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public void SnapshotAndInspectorRejectInvalidParentIdentityOrBone()
    {
        foreach (var parent in new[] { new SceneLightParent(Guid.Empty), new SceneLightParent(Guid.NewGuid(), -1) })
        {
            var light = Light(parent);
            Assert.Throws<ArgumentException>(() => SceneLightingSnapshot.ValidateLight(light));
            Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([light]));
        }
        var valid = Light(new(Guid.NewGuid(), 0));
        SceneLightingSnapshot.ValidateLight(valid);
        Assert.Equal(valid, Assert.Single(new SceneLightingSnapshot([valid]).Lights));
    }
}
