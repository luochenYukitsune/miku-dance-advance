using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightingSettingsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NativeSceneLight Light() => new(Guid.NewGuid(), "fill", SceneLightKind.Point,
        Vector3.One, Quaternion.Identity, Vector3.One);

    [Fact]
    public void SettingsAreFiniteBoundedAndLegacySnapshotsUseNeutralDefaults()
    {
        Assert.Equal(new SceneLightingSettings(0, 0), SceneLightingSettings.Default);
        Assert.Same(SceneLightingSettings.Default, SceneLightingSnapshot.Empty.Settings);
        Assert.Equal(SceneLightingSettings.Default, new SceneLightingSnapshot([Light()]).Settings);
        foreach (float invalid in new[] { -1f, 4.0001f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new SceneLightingSettings(invalid));
        foreach (float invalid in new[] { -8.0001f, 8.0001f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new SceneLightingSettings(0, invalid));
        foreach (var settings in new[] { new SceneLightingSettings(0, -8), new SceneLightingSettings(4, 8) })
            Assert.Same(settings, new SceneLightingSnapshot([], settings: settings).Settings);
    }

    [Fact]
    public void SettingsHaveGestureHistoryAndEveryLightAndGroupEditPreservesThem()
    {
        var editor = new SceneLightingEditor(); var first = new SceneLightingSettings(.25f, -2);
        editor.BeginEdit(); editor.UpdateSettings(first); editor.UpdateSettings(new(2, 3)); editor.CommitEdit();
        Assert.Equal(new SceneLightingSettings(2, 3), editor.Settings);
        Assert.True(editor.Undo()); Assert.Equal(SceneLightingSettings.Default, editor.Settings); Assert.False(editor.CanUndo);
        Assert.True(editor.Redo()); editor.BeginEdit(); editor.UpdateSettings(first); editor.CancelEdit();
        var retained = editor.Settings; var light = Light(); var group = new SceneLightGroup(Guid.NewGuid(), "group");
        editor.Add(light); editor.Update(light with { Intensity = 2 }); editor.AddGroup(group);
        editor.AssignToGroup(light.Id, group.Id); editor.UpdateGroup(group with { Intensity = 3 });
        Assert.Same(retained, editor.Settings); editor.RemoveGroup(group.Id); editor.Remove(light.Id);
        Assert.Same(retained, editor.Settings); Assert.Empty(editor.Lights); Assert.Empty(editor.Groups);
        var snapshot = editor.Snapshot.WithLights([light]).WithGroups([group]);
        Assert.Same(retained, snapshot.Settings);
        Assert.Equal(first, snapshot.WithSettings(first).Settings);
    }

    [Fact]
    public void ParentResolutionAndPresetApplicationRetainTargetSettingsButPresetCaptureOmitsThem()
    {
        var settings = new SceneLightingSettings(1.234567f, -3.25f); var parent = new SceneLightParent(Guid.NewGuid());
        var light = Light() with { Parent = parent }; var source = new SceneLightingSnapshot([light], settings: settings);
        var world = source.ResolveParents(_ => Matrix4x4.CreateTranslation(3, 4, 5));
        Assert.Same(settings, world.Settings); Assert.Same(settings, world.AuthoredLighting.Settings);
        var preset = SceneLightingPreset.Capture("selection", source, [light.Id], resolveParent: _ => Matrix4x4.Identity);
        Assert.Equal(SceneLightingSettings.Default, preset.Lighting.Settings);
        foreach (var mode in new[] { SceneLightingPresetApplyMode.Add, SceneLightingPresetApplyMode.ReplaceSelected })
            Assert.Same(settings, preset.Apply(source, mode, [light.Id]).Snapshot.Settings);
        Assert.DoesNotContain("settings", System.Text.Encoding.UTF8.GetString(preset.Serialize()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SidecarSixRoundTripsSettingsAndMissingSettingsRemainNeutral()
    {
        var editor = new SceneLightingEditor(); editor.UpdateSettings(new(3.75f, -7.5f));
        var json = JsonNode.Parse(editor.SerializeSidecar())!; Assert.Equal(6, json["version"]!.GetValue<int>());
        var loaded = new SceneLightingEditor(); Assert.True(loaded.TryLoadSidecar(json.ToJsonString(), out var error), error);
        Assert.Equal(editor.Settings, loaded.Settings); Assert.False(loaded.CanUndo);
        json.AsObject().Remove("settings"); Assert.True(loaded.TryLoadSidecar(json.ToJsonString(), out error), error);
        Assert.Equal(SceneLightingSettings.Default, loaded.Settings);
        Assert.Null(JsonNode.Parse(loaded.SerializeSidecar())!["settings"]);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void OldSidecarsRejectSettingsIncludingNullAndNoSettingsStillLoad(int version)
    {
        var editor = new SceneLightingEditor(); editor.UpdateSettings(new(1, 2)); editor.BeginEdit();
        string before = editor.SerializeSidecar(); var json = JsonNode.Parse(before)!; json["version"] = version;
        if (version == 1) json.AsObject().Remove("groups");
        Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out var error)); Assert.Contains("version 6", error);
        json["settings"] = null; Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out error)); Assert.Contains("version 6", error);
        Assert.Equal(before, editor.SerializeSidecar()); Assert.True(editor.IsEditing);
        json.AsObject().Remove("settings"); Assert.True(editor.TryLoadSidecar(json.ToJsonString(), out error), error);
        Assert.Equal(SceneLightingSettings.Default, editor.Settings);
    }

    [Theory]
    [InlineData("null")] [InlineData("missing")] [InlineData("range")] [InlineData("unknown")]
    public void InvalidSettingsRejectAtomically(string defect)
    {
        var editor = new SceneLightingEditor(); editor.UpdateSettings(new(1, 2)); string before = editor.SerializeSidecar();
        var json = JsonNode.Parse(before)!;
        switch (defect)
        {
            case "null": json["settings"] = null; break;
            case "missing": json["settings"]!.AsObject().Remove("exposureEv"); break;
            case "range": json["settings"]!["environmentStrength"] = 5; break;
            case "unknown": json["settings"]!["color"] = "white"; break;
        }
        Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out _)); Assert.Equal(before, editor.SerializeSidecar());
    }

    [Fact]
    public void NonDefaultSettingsSelectLayoutElevenAndOlderLayoutsRejectTheirPresence()
    {
        var composition = new SceneCompositionEditor();
        composition.Add("fixture", Path.Combine(AppContext.BaseDirectory, "fixtures/rich-w1-uv4.pmx"), ModelRole.Prop, EngineShowcaseScene.Create().Frame);
        var editor = new SceneLightingEditor(); string path = Path.GetFullPath("global-settings.mmdscene");
        Assert.Equal(1, JsonNode.Parse(SceneProjectStore.Serialize(path, composition, editor))!["version"]!.GetValue<int>());
        editor.UpdateSettings(new(1.234567f, -3.25f)); string text = SceneProjectStore.Serialize(path, composition, editor);
        Assert.Equal(11, JsonNode.Parse(text)!["version"]!.GetValue<int>());
        var loaded = SceneProjectStore.Prepare(text, path, (_, _) => EngineShowcaseScene.Create().Frame, Token);
        Assert.Equal(editor.Settings, loaded.Lighting.Settings);
        for (int version = 1; version <= 10; version++)
        {
            var json = JsonNode.Parse(text)!; json["version"] = version;
            foreach (bool useNull in new[] { false, true })
            {
                if (useNull) json["settings"] = null;
                var error = Assert.Throws<JsonException>(() => SceneProjectStore.Prepare(json.ToJsonString(), path,
                    (_, _) => throw new InvalidOperationException("must reject before asset loading"), Token));
                Assert.Contains("version 11 or 12", error.Message);
            }
        }
        foreach (bool omit in new[] { false, true })
        {
            var json = JsonNode.Parse(text)!;
            if (omit) json.AsObject().Remove("settings"); else json["settings"] = null;
            Assert.Throws<JsonException>(() => SceneProjectStore.Prepare(json.ToJsonString(), path, (_, _) => throw new InvalidOperationException(), Token));
        }
        editor.UpdateSettings(SceneLightingSettings.Default);
        Assert.Equal(1, JsonNode.Parse(SceneProjectStore.Serialize(path, composition, editor))!["version"]!.GetValue<int>());
    }

    [Fact]
    public void AnimationSaveRejectsSettingsThatNoLongerMatchItsCapturedInputs()
    {
        var composition = new SceneCompositionEditor();
        composition.Add("fixture", Path.Combine(AppContext.BaseDirectory, "fixtures/rich-w1-uv4.pmx"), ModelRole.Prop, EngineShowcaseScene.Create().Frame);
        var editor = new SceneLightingEditor();
        using var animation = new SceneAnimationSession(composition.Objects, [],
            _ => throw new InvalidOperationException("No solver expected"), editor.Snapshot, Token);
        editor.UpdateSettings(new(.5f, 2));
        Assert.Throws<InvalidOperationException>(() => SceneAnimationProjectStore.Save(Path.GetFullPath("stale-lighting.mmdscene"),
            composition, editor, animation, new() { CopyModelAssets = false }, Token));
    }
}
