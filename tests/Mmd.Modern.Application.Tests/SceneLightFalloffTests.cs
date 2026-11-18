using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightFalloffTests
{
    private static NativeSceneLight Spot(float intensity = 1, float range = 30, float? aim = 10) => new(Guid.NewGuid(), "key",
        SceneLightKind.Spot, new(0, 10, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2), Vector3.One,
        intensity, range, AimDistance: aim);

    [Fact]
    public void IntensityIsTheBrightnessReceivedAtTheAimAndLightEndsAtRange()
    {
        var light = Spot(.75f);
        Assert.Equal(.75f, SceneLightFalloff.Received(light, 10), 5);
        Assert.Equal(0, SceneLightFalloff.Received(light, 30));
        Assert.Equal(0, SceneLightFalloff.Received(light, 31));
        // Gentle near the light: no 1/d^2 hot spot.
        Assert.InRange(SceneLightFalloff.Received(light, .01f) / SceneLightFalloff.Received(light, 10), 1, 1.3f);
        Assert.True(SceneLightFalloff.Received(light, 20) < SceneLightFalloff.Received(light, 10));
        Assert.Equal(new Vector3(0, 0, 0), light.Target, new Vector3Comparer());
        var directional = light with { Kind = SceneLightKind.Directional };
        Assert.Equal(.75f, SceneLightFalloff.RenderIntensity(directional));
        Assert.Equal(.75f, SceneLightFalloff.Received(directional, 1000));
        // Without an explicit aim the reference is one sixth of the range; beyond the range it is clamped inside.
        Assert.Equal(5, Spot(aim: null).EffectiveAimDistance);
        Assert.True(float.IsFinite(SceneLightFalloff.RenderIntensity(Spot(aim: 100))));
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => SceneLightingSnapshot.ValidateLight(Spot(aim: invalid)));
    }

    [Fact]
    public void LegacyLightsKeepTheirBrightnessAtTheDefaultAim()
    {
        // Saved before aims: I / d^2 inside a quartic window. A placement light of 0.5 * d^2 gave 0.5 at its subject.
        var legacy = Spot(intensity: .5f * 25, range: 30, aim: null);
        var converted = SceneLightFalloff.FromLegacy(legacy);
        float d = legacy.EffectiveAimDistance, ratio = d / legacy.Range;
        Assert.Equal(legacy.Intensity * (1 - ratio * ratio * ratio * ratio) / (d * d), SceneLightFalloff.Received(converted, d), 4);
        Assert.Null(converted.AimDistance);
        var sun = legacy with { Kind = SceneLightKind.Directional, Intensity = .3f };
        Assert.Same(sun, SceneLightFalloff.FromLegacy(sun));
    }

    [Fact]
    public void AimingTurnsTowardTheTargetKeepsRollAndGrowsRangeWhenNeeded()
    {
        var area = Spot() with { Kind = SceneLightKind.Area, AreaSize = new(2, 4) };
        var target = new Vector3(8, 0, 3);
        var aimed = SceneLightAim.AimAt(area, target);
        Assert.Equal(target, aimed.Target, new Vector3Comparer());
        Assert.Equal(Vector3.Distance(area.Position, target), aimed.AimDistance!.Value, 4);
        // Shortest arc: the emitter's right axis stays perpendicular to the old/new plane's normal drift, not rolled.
        var right = Vector3.Transform(Vector3.UnitX, aimed.Rotation);
        Assert.InRange(MathF.Abs(Vector3.Dot(right, aimed.Direction)), 0, 1e-4f);
        Assert.InRange(Vector3.Dot(right, Vector3.Transform(Vector3.UnitX, area.Rotation)), .7f, 1.0001f);
        var farther = SceneLightAim.AimAt(area, new(0, 10, -60));
        Assert.True(farther.Range >= 60 / SceneLightFalloff.MaxReferenceFraction - 1e-3f);
        Assert.Equal(area.Intensity, farther.Intensity);
        var point = Spot() with { Kind = SceneLightKind.Point };
        Assert.Equal(point.Rotation, SceneLightAim.AimAt(point, target).Rotation);
        Assert.Throws<ArgumentException>(() => SceneLightAim.AimAt(area, area.Position));
    }

    [Fact]
    public void ParentingKeepsTheAimInSceneUnits()
    {
        var light = Spot();
        var parent = new SceneLightParent(Guid.NewGuid());
        var transform = Matrix4x4.CreateScale(3) * Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(4, 0, 2);
        var local = SceneLightTransform.ToLocal(light, parent, transform);
        Assert.Equal(light.AimDistance, local.AimDistance);
        var world = SceneLightTransform.ToWorld(local, transform);
        Assert.Equal(light.Target, world.Target, new Vector3Comparer(1e-3f));
    }

    [Fact]
    public void SidecarsAndPresetsConvertLegacyIntensitiesOnceAndKeepAims()
    {
        var editor = new SceneLightingEditor();
        var light = Spot(.6f);
        editor.Add(light);
        var json = JsonNode.Parse(editor.SerializeSidecar())!;
        Assert.Equal(7, json["version"]!.GetValue<int>());
        Assert.Equal(10, json["lights"]![0]!["aimDistance"]!.GetValue<float>());
        var loaded = new SceneLightingEditor();
        Assert.True(loaded.TryLoadSidecar(json.ToJsonString(), out var error), error);
        Assert.Equal(light, loaded.Lights.Single());
        // A version 6 document has no aims and stores the old units.
        json["version"] = 6;
        Assert.False(loaded.TryLoadSidecar(json.ToJsonString(), out error));
        json["lights"]![0]!.AsObject().Remove("aimDistance");
        json["lights"]![0]!["intensity"] = 30f;
        Assert.True(loaded.TryLoadSidecar(json.ToJsonString(), out error), error);
        var converted = loaded.Lights.Single();
        Assert.Null(converted.AimDistance);
        Assert.Equal(SceneLightFalloff.FromLegacy(light with { AimDistance = null, Intensity = 30 }).Intensity, converted.Intensity);
        // The converted light saves in the current version and round-trips unchanged.
        var again = new SceneLightingEditor();
        Assert.True(again.TryLoadSidecar(loaded.SerializeSidecar(), out error), error);
        Assert.Equal(converted, again.Lights.Single());

        var preset = SceneLightingPreset.CreateBuiltin(SceneLightingBuiltinPreset.PortraitSoft);
        Assert.All(preset.Lighting.Lights, item => Assert.InRange(item.Intensity, .05f, 2));
        var document = JsonNode.Parse(preset.Serialize())!;
        Assert.Equal(2, document["version"]!.GetValue<int>());
        Assert.Equal(preset.Lighting.Lights.Select(item => item.Intensity),
            SceneLightingPreset.Deserialize(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())).Lighting.Lights.Select(item => item.Intensity));
        document["version"] = 1;
        Assert.Throws<ArgumentException>(() => SceneLightingPreset.Deserialize(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    private sealed class Vector3Comparer(float tolerance = 1e-4f) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.Distance(x, y) <= tolerance;
        public int GetHashCode(Vector3 obj) => 0;
    }
}
