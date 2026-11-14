using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12PmxMaterialTreatmentBudgetTests
{
    [Fact]
    public void HsvRaisedChannelCannotHideAnOverflowBehindAnOriginallyZeroChannel()
    {
        var frame = Frame(new(1e30f, 0, 0), new(0, 1e20f, 0), 1);
        Map(null).Validate(frame, null);
        Map(new(1, 1.1f)).Validate(frame, null); // Width-only preserves original RGB bounds.
        Assert.Throws<NotSupportedException>(() => Map(new(0)).Validate(frame, null));
    }

    [Fact]
    public void FiniteColoredProductsRemainSupportedAndNoLightPathKeepsItsExistingDomain()
    {
        Map(new(.9f, .9f)).Validate(Frame(new(.7f, .2f, .1f), new(.1f, 1, .3f), 4), null);
        var source = Frame(new(1e30f, 0, 0), new(0, 1e20f, 0), 1);
        var unlit = new OriginalFrameScene(source.Surface, options: source.Options);
        Map(new(0)).Validate(unlit, null);
    }

    [Fact]
    public void SignedPassThroughAndSignedSphereCannotEscapeTheConservativeRange()
    {
        var source = Frame(new(1e30f, 0, 0), new(1e20f, 0, 0), 1);
        var snapshot = source.Surface.Snapshot;
        var binding = source.Surface.Materials[0] with
        {
            SphereTexture = 0, SphereMode = OriginalSphereMode.Multiply,
            TextureFactors = OriginalMaterialTextureFactors.Identity with
            {
                Diffuse = new(new(-1, 0, 0, 1), Vector4.Zero),
                Sphere = new(new(-1, 0, 0, 1), Vector4.Zero),
            },
        };
        var scene = new SceneSnapshot(snapshot.Vertices, snapshot.Indices,
            [snapshot.Materials[0] with { DiffuseTexture = 0 }], [new("numeric signed ranges", 1, 1, [255, 255, 255, 255])],
            camera: snapshot.Camera, lightDirection: snapshot.LightDirection);
        var frame = new OriginalFrameScene(new(scene, [binding], source.Surface.Profile), options: source.Options,
            lighting: source.Lighting);
        Assert.Throws<NotSupportedException>(() => Map(new(0)).Validate(frame, null));
    }

    [Fact]
    public void ContrastBrighteningKeepsZeroChannelsAndUntouchedHdrWithinTheExistingFiniteDomain()
    {
        var brighten = Map(new(ValueContrast: -.5f));
        brighten.Validate(Frame(new(1e30f, 0, 0), new(0, 1e20f, 0), 1), null);
        brighten.Validate(Frame(new(3e38f, 0, 0), new(1, 0, 0), 1), null);
        brighten.Validate(Frame(new(.5f, 0, 0), new(1, 0, 0), float.MaxValue), null);
    }

    private static D3D12SurfaceStyleMap Map(D3D12PmxMaterialTreatment? treatment)
        => new([D3D12SurfaceStyle.StarRailPmx], [new(PmxMaterialTreatment: treatment)]);

    private static OriginalFrameScene Frame(Vector3 diffuse, Vector3 color, float intensity)
    {
        var scene = new SceneSnapshot([new(new(-1, -1, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, 0), -Vector3.UnitZ, Vector2.UnitX), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.UnitY)],
            [0, 2, 1], [new("finite-budget witness", 0, 3, new(diffuse, 1), Vector3.Zero, Vector3.Zero, 1)]);
        return new(new(scene, [new(new(.2f))], new(Vector3.One, Vector3.Zero, Matrix4x4.Identity)),
            options: new(OriginalAlphaBlendMode.Normal, true, false),
            lighting: new([new(Guid.NewGuid(), "cross-channel bound", SceneLightKind.Directional,
                Vector3.Zero, Quaternion.Identity, color, intensity, CastShadows: false)]));
    }
}
