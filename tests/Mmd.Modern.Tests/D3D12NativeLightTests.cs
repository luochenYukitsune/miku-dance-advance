using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12NativeLightTests
{
    [Fact]
    public void EmptyDisabledZeroAndBlackLightsRestoreTheExactOriginalFrame()
    {
        using var session = new D3D12FrameSession();
        var fixture = Fixture();
        var original = session.Capture(fixture, 129, 97);
        var light = Light(SceneLightKind.Point);
        foreach (var input in new[] { Array.Empty<NativeSceneLight>(), [light with { Enabled = false }], [light with { Intensity = 0 }], [light with { Color = Vector3.Zero }] })
        {
            var frame = WithLight(fixture, input);
            Assert.Equal(original.Rgba, session.Capture(frame, 129, 97).Rgba);
            Assert.Null(session.NativeLightStatistics);
        }
        session.Capture(WithLight(fixture, [light]), 129, 97);
        Assert.Equal(original.Rgba, session.Capture(fixture, 129, 97).Rgba);
        Assert.Null(session.NativeLightStatistics);
    }

    [Theory]
    [InlineData(SceneLightKind.Directional, 1)]
    [InlineData(SceneLightKind.Point, 6)]
    [InlineData(SceneLightKind.Spot, 1)]
    public void RealGeometryOccludesEachLightTypeAndTextureAlphaCreatesAHole(SceneLightKind kind, int expectedFaces)
    {
        using var session = new D3D12FrameSession();
        var light = Light(kind);
        var shadow = session.Capture(WithLight(Fixture(), [light]), 129, 97);
        var stats = session.NativeLightStatistics!;
        Assert.Equal(expectedFaces, stats.ShadowFaces);
        Assert.Equal(expectedFaces, stats.ShadowCasterDraws);
        Assert.Equal(kind == SceneLightKind.Point ? 1 : expectedFaces, stats.ShadowDrawCalls);
        Assert.Equal(expectedFaces, stats.ShadowDrawCalls + stats.ShadowCulledDraws);
        Assert.False(stats.NativeLightsContributeToGi);
        var transparentCaster = session.Capture(WithLight(Fixture(transparentCaster: true), [light]), 129, 97);
        var noShadow = session.Capture(WithLight(Fixture(), [light with { CastShadows = false }]), 129, 97);
        int center = (48 * 129 + 64) * 4;
        Assert.True(noShadow.Rgba[center] > shadow.Rgba[center] + 40, $"{kind}: lit={noShadow.Rgba[center]}, shadow={shadow.Rgba[center]}");
        Assert.Equal(noShadow.Rgba, transparentCaster.Rgba);
        D3D12OriginalFrameTests.Save("native-" + kind + "-shadow", shadow);
        D3D12OriginalFrameTests.Save("native-" + kind + "-alpha-hole", transparentCaster);
    }

    [Fact]
    public void OverlappingBrightLightsKeepAShadowInsteadOfClippingBothToWhite()
    {
        // Several bright lights on one spot used to saturate the 8-bit target, so the
        // shadowed pixel ("all lights but one") and the lit pixel were both 255.
        using var session = new D3D12FrameSession();
        var fixture = Fixture();
        int center = (48 * 129 + 64) * 4;
        // The same receiver brightness as the original fixture: caster 2, each fill about 1.3 (aim-normalized units).
        var caster = Light(SceneLightKind.Spot) with { Color = Vector3.One, Intensity = 2, AimDistance = 2 };
        var fills = Enumerable.Range(0, 3).Select(i => Light(SceneLightKind.Point) with
            { Color = Vector3.One, Position = new(i - 1, 1.5f, -2), CastShadows = false, Intensity = 1.28f, AimDistance = 2.5f }).ToArray();
        var shadow = session.Capture(WithLight(fixture, [caster, .. fills]), 129, 97);
        var lit = session.Capture(WithLight(fixture, [caster with { CastShadows = false }, .. fills]), 129, 97);
        Assert.True(lit.Rgba[center] < 255, $"lit={lit.Rgba[center]}");
        Assert.True(lit.Rgba[center] >= shadow.Rgba[center] + 10, $"lit={lit.Rgba[center]}, shadow={shadow.Rgba[center]}");
        // Below the shoulder the accepted additive result is unchanged: a dim light adds exactly.
        var dim = Light(SceneLightKind.Spot) with { Color = Vector3.One, Intensity = .125f, CastShadows = false, AimDistance = 2 };
        var single = session.Capture(WithLight(fixture, [dim]), 129, 97);
        var doubled = session.Capture(WithLight(fixture, [dim, dim with { Id = Guid.NewGuid() }]), 129, 97);
        var original = session.Capture(fixture, 129, 97);
        Assert.InRange(doubled.Rgba[center] - original.Rgba[center] - 2 * (single.Rgba[center] - original.Rgba[center]), -1, 1);
    }

    [Fact]
    public void ResolvedParentLightsMatchEquivalentWorldLightsThroughTheRenderingBoundary()
    {
        using var session = new D3D12FrameSession(); var fixture = Fixture();
        var transform = Matrix4x4.CreateTranslation(4, 2, 0);
        var parent = new SceneLightParent(Guid.NewGuid(), 0);
        foreach (var kind in Enum.GetValues<SceneLightKind>())
        {
            var world = Light(kind);
            var local = SceneLightTransform.ToLocal(world, parent, transform);
            var resolved = new SceneLightingSnapshot([local]).ResolveParents(_ => transform);
            var input = new OriginalFrameScene(fixture.Surface, options: fixture.Options, materials: fixture.Materials, lighting: resolved);
            Assert.Equal(session.Capture(WithLight(fixture, [world]), 129, 97).Rgba,
                session.Capture(input, 129, 97).Rgba);
            Assert.Equal(kind == SceneLightKind.Area ? 24 : kind == SceneLightKind.Point ? 6 : 1, session.NativeLightStatistics!.ShadowFaces);
        }
    }

    [Fact]
    public void MovingPointAndRotatingSpotChangesOnlyTheirActualWorldSpaceInfluence()
    {
        using var session = new D3D12FrameSession();
        var fixture = Fixture();
        var baseline = session.Capture(fixture, 129, 97);
        var point = Light(SceneLightKind.Point) with { CastShadows = false };
        var lit = session.Capture(WithLight(fixture, [point]), 129, 97);
        Assert.NotEqual(baseline.Rgba, lit.Rgba);
        Assert.Equal(baseline.Rgba, session.Capture(WithLight(fixture, [point with { Position = new(0, 0, -20) }]), 129, 97).Rgba);
        var spot = Light(SceneLightKind.Spot) with { CastShadows = false };
        Assert.NotEqual(baseline.Rgba, session.Capture(WithLight(fixture, [spot]), 129, 97).Rgba);
        Assert.Equal(baseline.Rgba, session.Capture(WithLight(fixture, [spot with { Rotation = Quaternion.Identity }]), 129, 97).Rgba);
        Assert.Equal(lit.Rgba, session.Capture(WithLight(fixture, [point]), 129, 97).Rgba);
    }

    internal static NativeSceneLight Light(SceneLightKind kind) => new(Guid.NewGuid(), "test " + kind, kind, new(0, 0, -2),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), new(1, .3f, .1f), kind == SceneLightKind.Directional ? 1 : 4, 8,
        AreaSize: kind == SceneLightKind.Area ? new Vector2(2, 2) : null);

    internal static OriginalFrameScene WithLight(OriginalFrameScene frame, IEnumerable<NativeSceneLight> lights) =>
        new(frame.Surface, options: frame.Options, materials: frame.Materials, lighting: new(lights));

    internal static OriginalFrameScene Fixture(bool transparentCaster = false)
    {
        SceneVertex[] Quad(float z, float half) => [new(new(-half, -half, z), -Vector3.UnitZ, Vector2.Zero),
            new(new(half, -half, z), -Vector3.UnitZ, Vector2.Zero), new(new(half, half, z), -Vector3.UnitZ, Vector2.Zero),
            new(new(-half, half, z), -Vector3.UnitZ, Vector2.Zero)];
        var mesh = new SceneSnapshot(Quad(0, 1.4f).Concat(Quad(-1, .35f)).ToArray(), [0u, 2u, 1u, 0u, 3u, 2u, 4u, 6u, 5u, 4u, 7u, 6u],
            [new("receiver", 0, 6, new(.3f, .3f, .3f, 1), Vector3.Zero, Vector3.Zero, 1),
             new("caster", 6, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0)],
            [new("caster alpha", 1, 1, [255, 255, 255, transparentCaster ? (byte)0 : (byte)255])],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 20), lightDirection: -Vector3.UnitZ);
        var surface = new OriginalStyleScene(mesh, [new(new(.2f)), new(new(.2f))], new(new(.1f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp));
        return new(surface, options: new(OriginalAlphaBlendMode.Normal, true, false),
            materials: [new(false, true, false), new(true, false, false, SurfaceEnabled: false, ShadowDoubleSided: true)]);
    }
}
