using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightingCapacityTests
{
    [Fact]
    public void AuthoringCapacityRetainsDisabledLightsAndRoundTripsAllValues()
    {
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxLights)
            .Select(index => Light(index < SceneLightingSnapshot.MaxActiveLights)).ToArray();
        var editor = new SceneLightingEditor();
        editor.ReplaceSnapshot(new(lights));
        var restored = new SceneLightingEditor();
        Assert.True(restored.TryLoadSidecar(editor.SerializeSidecar(), out string? error), error);
        Assert.Equal(128, restored.Lights.Count);
        Assert.Equal(lights, restored.Lights);
        Assert.Equal(32, restored.Snapshot.EffectiveLights.Count(light => light.Enabled));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot(lights.Append(Light(false))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveEnableBudgetCountsZeroIntensityAndBlackLights(bool black)
    {
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxActiveLights)
            .Select(_ => Light(true) with { Intensity = black ? 1 : 0, Color = black ? Vector3.Zero : Vector3.One }).ToArray();
        Assert.Equal(32, new SceneLightingSnapshot(lights).EffectiveLights.Count(light => light.Enabled));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot(lights.Append(Light(true))));
    }

    [Fact]
    public void DisabledGroupsKeepAuthoredSwitchesAndOverBudgetEnableIsAtomic()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "备用灯", Enabled: false);
        var active = Enumerable.Range(0, SceneLightingSnapshot.MaxActiveLights).Select(_ => Light(true));
        var grouped = Enumerable.Range(0, SceneLightingSnapshot.MaxLights - SceneLightingSnapshot.MaxActiveLights)
            .Select(_ => Light(true) with { GroupId = group.Id });
        var snapshot = new SceneLightingSnapshot(active.Concat(grouped), [group]);
        Assert.All(snapshot.Lights, light => Assert.True(light.Enabled));
        Assert.Equal(32, snapshot.EffectiveLights.Count(light => light.Enabled));
        var editor = new SceneLightingEditor();
        editor.ReplaceSnapshot(snapshot);
        bool canUndo = editor.CanUndo;
        Assert.Throws<ArgumentException>(() => editor.UpdateGroup(group with { Enabled = true }));
        Assert.Same(snapshot, editor.Snapshot);
        Assert.Equal(canUndo, editor.CanUndo);
        Assert.False(Assert.Single(editor.Groups).Enabled);
    }

    private static NativeSceneLight Light(bool enabled) => new(Guid.NewGuid(), "capacity light", SceneLightKind.Point,
        Vector3.Zero, Quaternion.Identity, Vector3.One, Enabled: enabled);
}
