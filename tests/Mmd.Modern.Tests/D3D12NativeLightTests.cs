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
        Assert.Equal(expectedFaces, session.NativeLightStatistics!.ShadowFaces);
        Assert.Equal(expectedFaces, session.NativeLightStatistics.ShadowDrawCalls);
        Assert.False(session.NativeLightStatistics.NativeLightsContributeToGi);
        var transparentCaster = session.Capture(WithLight(Fixture(transparentCaster: true), [light]), 129, 97);
        var noShadow = session.Capture(WithLight(Fixture(), [light with { CastShadows = false }]), 129, 97);
        int center = (48 * 129 + 64) * 4;
        Assert.True(noShadow.Rgba[center] > shadow.Rgba[center] + 40, $"{kind}: lit={noShadow.Rgba[center]}, shadow={shadow.Rgba[center]}");
        Assert.Equal(noShadow.Rgba, transparentCaster.Rgba);
        D3D12OriginalFrameTests.Save("native-" + kind + "-shadow", shadow);
        D3D12OriginalFrameTests.Save("native-" + kind + "-alpha-hole", transparentCaster);
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

    private static NativeSceneLight Light(SceneLightKind kind) => new(Guid.NewGuid(), "test " + kind, kind, new(0, 0, -2),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), new(1, .3f, .1f), kind == SceneLightKind.Directional ? 1 : 4, 8);

    private static OriginalFrameScene WithLight(OriginalFrameScene frame, IEnumerable<NativeSceneLight> lights) =>
        new(frame.Surface, options: frame.Options, materials: frame.Materials, lighting: new(lights));

    private static OriginalFrameScene Fixture(bool transparentCaster = false)
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
