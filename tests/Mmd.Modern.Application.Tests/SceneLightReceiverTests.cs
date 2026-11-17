using System.Collections;
using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightReceiverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NativeSceneLight Light(SceneLightReceivers? receivers = null) => new(Guid.NewGuid(), "target",
        SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One, Receivers: receivers);

    [Fact]
    public void FiltersAreImmutableValueSelectionsWithExplicitEmptyIncludeAndExcludeMeaning()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var ids = new[] { a, b };
        var include = new SceneLightReceivers(SceneLightReceiverMode.Include, ids); ids[0] = Guid.NewGuid();
        Assert.True(include.Affects(a)); Assert.False(include.Affects(null)); Assert.False(include.Affects(Guid.NewGuid()));
        Assert.Equal(include, new SceneLightReceivers(SceneLightReceiverMode.Include, [b, a]));
        Assert.Throws<NotSupportedException>(() => ((IList)include.ObjectIds)[0] = Guid.NewGuid());
        Assert.False(new SceneLightReceivers(SceneLightReceiverMode.Include, []).Affects(a));
        Assert.True(new SceneLightReceivers(SceneLightReceiverMode.Exclude, []).Affects(a));
        Assert.False(new SceneLightReceivers(SceneLightReceiverMode.Exclude, [a]).Affects(a));
        Assert.True(new SceneLightReceivers(SceneLightReceiverMode.All, []).Affects(null));
        foreach (var invalid in new[] { new[] { Guid.Empty }, new[] { a, a } })
            Assert.Throws<ArgumentException>(() => new SceneLightReceivers(SceneLightReceiverMode.Include, invalid));
        Assert.Throws<ArgumentException>(() => new SceneLightReceivers((SceneLightReceiverMode)99, []));
        Assert.Throws<ArgumentException>(() => new SceneLightReceivers(SceneLightReceiverMode.All, [a]));
    }

    [Fact]
    public void MaterialMasksFollowContributingLightOrderThroughGroupsAndIncludeBitThirtyOne()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var group = new SceneLightGroup(Guid.NewGuid(), "off", false);
        var lights = Enumerable.Range(0, 32).Select(index => Light(new(SceneLightReceiverMode.Include, [index == 31 ? b : a]))).ToArray();
        var snapshot = new SceneLightingSnapshot(new[] { Light() with { GroupId = group.Id }, Light() with { Intensity = 0, Enabled = false } }.Concat(lights), [group]);
        Assert.Equal(0x7fffffffu, snapshot.ReceiverMask(a)); Assert.Equal(0x80000000u, snapshot.ReceiverMask(b));
        Assert.Equal(0u, snapshot.ReceiverMask(null));
        Assert.Equal(1u, new SceneLightingSnapshot([Light()]).ReceiverMask(null));
    }

    [Theory]
    [InlineData(SceneLightReceiverMode.All)]
    [InlineData(SceneLightReceiverMode.Include)]
    [InlineData(SceneLightReceiverMode.Exclude)]
    public void SidecarRoundTripAndLegacyVersionGuardsPreserveReceiverSelection(SceneLightReceiverMode mode)
    {
        var receivers = new SceneLightReceivers(mode, mode == SceneLightReceiverMode.All ? [] : [Guid.NewGuid()]);
        var editor = new SceneLightingEditor(); editor.Add(Light(receivers));
        string json = editor.SerializeSidecar(); var restored = new SceneLightingEditor();
        Assert.True(restored.TryLoadSidecar(json, out var error), error); Assert.Equal(editor.Lights, restored.Lights);
        for (int version = 1; version <= 3; version++)
        {
            var old = JsonNode.Parse(json)!; old["version"] = version;
            if (version == 1) old.AsObject().Remove("groups");
            Assert.False(restored.TryLoadSidecar(old.ToJsonString(), out error));
            old["lights"]![0]!["receivers"] = null;
            Assert.False(restored.TryLoadSidecar(old.ToJsonString(), out error)); Assert.Equal(editor.Lights, restored.Lights);
        }
    }

    [Fact]
    public void SceneMaterialsRetainStableReceiverIdsAndRemovedModelsPruneFiltersWithHistory()
    {
        var model = EditedMotionProjectTests.Model(); var frame = EditedMotionProjectTests.Rest(model);
        var layout = new SceneCompositionEditor(); var a = layout.Add("a", Path.GetFullPath("a.pmx"), ModelRole.Prop, frame);
        var b = layout.Add("b", Path.GetFullPath("b.pmx"), ModelRole.Prop, frame);
        var light = Light(new(SceneLightReceiverMode.Include, [a.Id])); var lights = new SceneLightingEditor(); lights.Add(light);
        var physics = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        using var project = new SceneProjectSession(layout, lights, [], physics, cancellationToken: Token);
        Assert.Equal(new Guid?[] { a.Id, b.Id }, project.Frame.Materials.Select(flags => flags.LightReceiverObjectId));
        Assert.Throws<ArgumentException>(() => project.Edit(edit => edit.Lighting.Update(light with { Receivers = new(SceneLightReceiverMode.Include, [Guid.NewGuid()]) }), Token));
        Assert.False(project.CanUndo);
        project.Edit(edit => edit.RemoveObject(a.Id), Token);
        Assert.Empty(Assert.Single(project.Lighting.Lights).Receivers!.ObjectIds);
        Assert.Equal(0u, project.Lighting.ReceiverMask(b.Id));
        Assert.True(project.Undo(Token)); Assert.Equal(light, Assert.Single(project.Lighting.Lights));
        Assert.Equal(new Guid?[] { a.Id, b.Id }, project.Frame.Materials.Select(flags => flags.LightReceiverObjectId));
    }
}
