using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightingPresetTests
{
    private static NativeSceneLight Light(Guid? group = null) => new(Guid.NewGuid(), "Fill", SceneLightKind.Spot,
        new(3, 4, 5), Quaternion.Identity, new(.9f, .8f, .7f), 2, 30, GroupId: group);

    [Fact]
    public void PortableCaptureResolvesParentsAndExplicitlyDropsProjectReceiverReferences()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "Portrait", false, 3);
        var parent = new SceneLightParent(Guid.NewGuid(), 2);
        var light = Light(group.Id) with { Parent = parent, Receivers = new(SceneLightReceiverMode.Include, [Guid.NewGuid()]) };
        var source = new SceneLightingSnapshot([light], [group]);
        Assert.Throws<ArgumentException>(() => SceneLightingPreset.Capture("Single", source, [light.Id]));
        var preset = SceneLightingPreset.Capture("Single", source, [light.Id], resolveParent: _ => Matrix4x4.CreateTranslation(10, 0, 0));
        var captured = Assert.Single(preset.Lighting.Lights);
        Assert.Equal(new(13, 4, 5), captured.Position);
        Assert.Null(captured.Parent); Assert.Null(captured.Receivers); Assert.Null(captured.GroupId);
        Assert.False(captured.Enabled); Assert.Equal(6, captured.Intensity); Assert.Empty(preset.Lighting.Groups);
        Assert.Equal(parent, light.Parent); Assert.NotNull(light.Receivers); Assert.True(light.Enabled);
        var fullGroup = SceneLightingPreset.Capture("Group", source, [], [group.Id], _ => Matrix4x4.Identity);
        Assert.Equal(group, Assert.Single(fullGroup.Lighting.Groups));
        Assert.Equal(2, Assert.Single(fullGroup.Lighting.Lights).Intensity);
    }

    [Fact]
    public void RoundTripAndRepeatedApplyHaveFreshIdentitiesAndKeepUnselectedLightsAndGroups()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "Saved", true, 2);
        var member = Light(group.Id);
        var saved = SceneLightingPreset.Capture("My lighting", new([member], [group]), [], [group.Id]);
        var loaded = SceneLightingPreset.Deserialize(saved.Serialize());
        Assert.Equal(saved.Name, loaded.Name); Assert.Equal(saved.Lighting.Lights, loaded.Lighting.Lights);
        Assert.Equal(saved.Lighting.Groups, loaded.Lighting.Groups);
        var retained = Light(); var selected = Light(); var untouchedGroup = new SceneLightGroup(Guid.NewGuid(), "Empty");
        var source = new SceneLightingSnapshot([retained, selected], [untouchedGroup]);
        var first = loaded.Apply(source, SceneLightingPresetApplyMode.Add);
        var second = loaded.Apply(first.Snapshot, SceneLightingPresetApplyMode.ReplaceSelected, [selected.Id]);
        Assert.Equal(3, second.Snapshot.Lights.Count); Assert.Contains(retained, second.Snapshot.Lights);
        Assert.DoesNotContain(second.Snapshot.Lights, light => light.Id == selected.Id);
        Assert.Contains(untouchedGroup, second.Snapshot.Groups);
        Assert.NotEqual(Assert.Single(first.AddedLightIds), Assert.Single(second.AddedLightIds));
        Assert.NotEqual(group.Id, Assert.Single(first.AddedGroupIds));
        Assert.DoesNotContain(second.Snapshot.Lights, light => light.Id == member.Id);
        var editor = new SceneLightingEditor(); editor.ReplaceSnapshot(source, selected.Id);
        editor.ReplaceSnapshot(second.Snapshot, second.AddedLightIds[0]);
        Assert.True(editor.Undo()); Assert.Equal(source.Lights, editor.Lights); Assert.Equal(source.Groups, editor.Groups);
        Assert.True(editor.Redo()); Assert.Equal(second.Snapshot.Lights, editor.Lights);
    }

    [Fact]
    public void ReplacingSelectedGroupRemovesAllMembersAndRejectsUnknownOrEmptySelection()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "Old"); var member = Light(group.Id); var other = Light();
        var source = new SceneLightingSnapshot([member, other], [group]);
        var seed = Light(); var preset = SceneLightingPreset.Capture("One", new([seed]), [seed.Id]);
        var result = preset.Apply(source, SceneLightingPresetApplyMode.ReplaceSelected, [], [group.Id]);
        Assert.Equal(2, result.Snapshot.Lights.Count); Assert.Contains(other, result.Snapshot.Lights);
        Assert.Empty(result.Snapshot.Groups); Assert.DoesNotContain(member, result.Snapshot.Lights);
        Assert.Throws<ArgumentException>(() => preset.Apply(source, SceneLightingPresetApplyMode.ReplaceSelected));
        Assert.Throws<ArgumentException>(() => preset.Apply(source, SceneLightingPresetApplyMode.ReplaceSelected, [Guid.NewGuid()]));
        Assert.Throws<ArgumentNullException>(() => SceneLightingPreset.Capture("One", source, null!));
    }

    [Fact]
    public void LoadingRejectsForeignVersionsProjectReferencesInvalidValuesAndOversizeWithoutMutation()
    {
        var seed = Light(); var preset = SceneLightingPreset.Capture("One", new([seed]), [seed.Id]);
        string json = Encoding.UTF8.GetString(preset.Serialize());
        foreach (var change in new Action<JsonObject>[]
        {
            root => root["version"] = 99,
            root => root["format"] = "MDA_LIGHTING_SIDECAR",
            root => root["lights"]![0]!["parent"] = null,
            root => root["lights"]![0]!["receivers"] = null,
            root => root["lights"]![0]!["range"] = -1,
            root => root["lights"]![0]!["kind"] = "Unknown",
            root => root["groups"] = null,
        })
        {
            var root = JsonNode.Parse(json)!.AsObject(); change(root);
            Assert.ThrowsAny<ArgumentException>(() => SceneLightingPreset.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => SceneLightingPreset.Deserialize(new byte[SceneLightingPreset.MaxFileBytes + 1]));
        Assert.Throws<ArgumentException>(() => SceneLightingPreset.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"version\": 2", "\"version\": 2, \"version\": 2"))));
        // Empty selections never accidentally export an entire project.
        Assert.Throws<ArgumentException>(() => SceneLightingPreset.Capture("One", new([Light()]), []));
    }

    [Fact]
    public void BuiltinsAndCapacityChecksUseTheNormalSceneBudgets()
    {
        foreach (var kind in Enum.GetValues<SceneLightingBuiltinPreset>())
        {
            var preset = SceneLightingPreset.CreateBuiltin(kind);
            Assert.NotEmpty(preset.Lighting.Lights); Assert.All(preset.Lighting.Lights, light => Assert.Null(light.Parent));
            var area = Assert.Single(preset.Lighting.Lights, light => light.Kind == SceneLightKind.Area);
            Assert.True(area.AreaSize!.Value.X > 0); Assert.True(Vector3.Dot(area.Direction, Vector3.Normalize(new Vector3(0, 10, 0) - area.Position)) > .9999f);
            var loaded = SceneLightingPreset.Deserialize(preset.Serialize());
            Assert.Equal(preset.Lighting.Lights, loaded.Lighting.Lights);
            var full = new SceneLightingSnapshot(Enumerable.Range(0, SceneLightingSnapshot.MaxActiveLights).Select(_ => Light()));
            Assert.Throws<ArgumentException>(() => preset.Apply(full, SceneLightingPresetApplyMode.Add));
            Assert.Equal(SceneLightingSnapshot.MaxActiveLights, full.Lights.Count);
        }
    }

    [Fact]
    public void AtomicFilesRoundTripAndDisabledLightBudgetStillApplies()
    {
        var seed = Light(); var preset = SceneLightingPreset.Capture("Reusable", new([seed]), [seed.Id]);
        string directory = Path.Combine(Environment.GetEnvironmentVariable("MDA_LIGHTING_PRESET_EVIDENCE") ?? Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "rig.json"); preset.Save(path); preset.Save(path);
            Assert.Equal(preset.Lighting.Lights, SceneLightingPreset.Load(path).Lighting.Lights);
            Assert.Single(Directory.GetFiles(directory));
            var source = new SceneLightingSnapshot(Enumerable.Range(0, SceneLightingSnapshot.MaxLights).Select(_ => Light() with { Enabled = false }));
            Assert.Throws<ArgumentException>(() => preset.Apply(source, SceneLightingPresetApplyMode.Add));
            var replacement = preset.Apply(source, SceneLightingPresetApplyMode.ReplaceSelected, [source.Lights[0].Id]);
            Assert.Equal(SceneLightingSnapshot.MaxLights, replacement.Snapshot.Lights.Count);
            Assert.Throws<ArgumentException>(() => preset.Save(Path.Combine(directory, "rig.pma")));
            File.WriteAllBytes(path, [.. new byte[] { 0xef, 0xbb, 0xbf }, .. preset.Serialize()]);
            Assert.Equal(preset.Name, SceneLightingPreset.Load(path).Name);
            File.WriteAllBytes(path, new byte[SceneLightingPreset.MaxFileBytes + 1]);
            Assert.Throws<ArgumentException>(() => SceneLightingPreset.Load(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
