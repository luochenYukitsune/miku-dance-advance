using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneAreaLightAuthoringTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NativeSceneLight Area() => new(Guid.NewGuid(), "柔光箱", SceneLightKind.Area,
        new(2, 3, 4), Quaternion.CreateFromYawPitchRoll(.2f, -.3f, .1f), Vector3.One,
        Range: 17, AreaSize: new(8.123456f, 12.654321f));

    [Fact]
    public void AreaSizeIsRequiredPositiveAndFiniteAndCanRemainOnOtherKinds()
    {
        Assert.Equal(3, (int)SceneLightKind.Area);
        foreach (Vector2? size in new Vector2?[] { null, new(0, 1), new(1, -1), new(float.NaN, 1), new(1, float.PositiveInfinity) })
        {
            Assert.Throws<ArgumentException>(() => SceneLightingSnapshot.ValidateLight(Area() with { AreaSize = size }));
            if (size is not null)
                Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([Area() with { Kind = SceneLightKind.Point, AreaSize = size }]));
        }
        var point = Area() with { Kind = SceneLightKind.Point };
        Assert.Equal(point, Assert.Single(new SceneLightingSnapshot([point]).Lights));
        SceneLightingSnapshot.ValidateLight(point with { AreaSize = null });
    }

    [Fact]
    public void ParentingDoesNotScaleAreaDimensionsOrRangeAndHistoryRetainsThem()
    {
        var light = Area(); var parent = new SceneLightParent(Guid.NewGuid(), 0);
        var transform = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(3, 4, 5);
        var local = SceneLightTransform.ToLocal(light, parent, transform);
        Assert.Equal(light.AreaSize, local.AreaSize); Assert.Equal(light.Range, local.Range);
        var world = SceneLightTransform.ToWorld(local, transform);
        Assert.Equal(light.AreaSize, world.AreaSize); Assert.Equal(light.Range, world.Range);
        var editor = new SceneLightingEditor(); editor.Add(local);
        editor.Update(local with { AreaSize = new(3, 7) });
        Assert.True(editor.Undo()); Assert.Equal(local, Assert.Single(editor.Lights));
        Assert.True(editor.Redo()); Assert.Equal(new Vector2(3, 7), Assert.Single(editor.Lights).AreaSize);
    }

    [Fact]
    public void SidecarFivePreservesSizesOnAreaAndTemporarilyChangedKinds()
    {
        var editor = new SceneLightingEditor(); editor.Add(Area()); editor.Add(Area() with { Kind = SceneLightKind.Spot });
        var json = editor.SerializeSidecar(); Assert.Equal(SceneLightingEditor.SidecarVersion, JsonNode.Parse(json)!["version"]!.GetValue<int>());
        var loaded = new SceneLightingEditor(); Assert.True(loaded.TryLoadSidecar(json, out var error), error);
        Assert.Equal(editor.Lights, loaded.Lights);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void OldSidecarsRejectAreaKindOrAreaSizeEvenNullWithoutReplacingState(int version)
    {
        var editor = new SceneLightingEditor(); editor.Add(Area()); string before = editor.SerializeSidecar();
        var json = JsonNode.Parse(before)!; json["version"] = version;
        if (version == 1) json.AsObject().Remove("groups");
        Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out _));
        json["lights"]![0]!.AsObject().Remove("areaSize");
        Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out _));
        json["lights"]![0]!["kind"] = "Point"; json["lights"]![0]!["areaSize"] = null;
        Assert.False(editor.TryLoadSidecar(json.ToJsonString(), out _));
        Assert.Equal(before, editor.SerializeSidecar());
        json["lights"]![0]!.AsObject().Remove("areaSize");
        Assert.True(editor.TryLoadSidecar(json.ToJsonString(), out var error), error);
        Assert.Null(Assert.Single(editor.Lights).AreaSize);
    }

    [Fact]
    public void CurrentStaticLayoutRetainsAreaAndRejectsOlderIdentityEvenWithNullSize()
    {
        var composition = new SceneCompositionEditor();
        composition.Add("fixture", Path.Combine(AppContext.BaseDirectory, "fixtures/rich-w1-uv4.pmx"), ModelRole.Prop, EngineShowcaseScene.Create().Frame);
        var editor = new SceneLightingEditor(); editor.Add(Area()); string path = Path.GetFullPath("area.mmdscene");
        string text = SceneProjectStore.Serialize(path, composition, editor);
        Assert.Equal(SceneProjectStore.LightingFalloffVersion, JsonNode.Parse(text)!["version"]!.GetValue<int>());
        var loaded = SceneProjectStore.Prepare(text, path, (_, _) => EngineShowcaseScene.Create().Frame, Token);
        Assert.Equal(editor.Lights, loaded.Lighting.Lights);
        for (int version = 1; version <= 8; version++)
        {
            var json = JsonNode.Parse(text)!; json["version"] = version;
            if (version <= 2) json.AsObject().Remove("groups");
            if (version % 2 == 0) json["portableModels"] = new JsonObject();
            Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(json.ToJsonString(), path, (_, _) => throw new InvalidOperationException("must reject before assets"), Token));
            json["lights"]![0]!["kind"] = "Point"; json["lights"]![0]!["areaSize"] = null;
            Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(json.ToJsonString(), path, (_, _) => throw new InvalidOperationException("must reject before assets"), Token));
        }
    }
}
