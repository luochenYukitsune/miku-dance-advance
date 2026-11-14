using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12StylizedLightBudgetValidationTests
{
    public static TheoryData<D3D12SurfaceStyle> Families => new()
    {
        D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx,
        D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx,
    };

    [Theory, MemberData(nameof(Families))]
    public void EachFamilyAcceptsOrdinaryNativeAndPaletteInputs(D3D12SurfaceStyle family)
    {
        var frame = Frame();
        var map = Map(family);
        map.Validate(frame, null);
        map.Validate(frame, Palette);
        map.Validate(frame, Palette with { ToonResponse = RayGiToonResponse.PreservePaletteBands });
    }

    [Theory, MemberData(nameof(Families))]
    public void NewBudgetRequiresNonnegativeFiniteEndpointsAndUnitToon(D3D12SurfaceStyle family)
    {
        var map = Map(family);
        foreach (var frame in new[]
        {
            Frame(toon: new(-.1f, .2f, .3f)),
            Frame(toon: new(.1f, 1.1f, .3f)),
            Frame(diffuse: new(-1, 1, 1, 1)),
            // Ambient makes visible Eg positive, but NativeDiffuse is still negative.
            Frame(diffuse: new(-1, 1, 1, 1), ambient: new(2)),
            Frame(factors: OriginalMaterialTextureFactors.Identity with
            {
                Toon = new(new Vector4(float.MaxValue), new Vector4(float.MaxValue)),
            }),
        })
        {
            Assert.Throws<NotSupportedException>(() => map.Validate(frame, null));
            Assert.Throws<NotSupportedException>(() => map.Validate(frame, Palette));
        }
    }

    [Fact]
    public void DefaultMmdGiOnlyAndActiveAdditiveKeepTheirExistingDomains()
    {
        var unsupported = Frame(toon: new(2));
        Map(D3D12SurfaceStyle.Mmd).Validate(unsupported, null);
        // The original single-argument API remains structural validation.
        Map().Validate(unsupported);
        Map().Validate(Frame(toon: new(2), lights: []), Palette);
        Map().Validate(unsupported, Palette with { Composition = RayGiComposition.Additive });
        // Enabled zero-strength GI still records the established Additive entrypoint.
        Map().Validate(unsupported, Palette with { Composition = RayGiComposition.Additive, Strength = 0 });
        Assert.Throws<NotSupportedException>(() => Map().Validate(unsupported,
            Palette with { Composition = RayGiComposition.Additive, Mode = LightingMode.Legacy }));
    }

    [Fact]
    public void EmbeddedGiOptionsAndExplicitOffSelectTheActualComposition()
    {
        var original = Frame(toon: new(2));
        var frame = new OriginalFrameScene(original.Surface,
            options: original.Options! with { RayGi = Palette with { Composition = RayGiComposition.Additive } },
            lighting: original.Lighting);
        Map().Validate(frame, null);
        Assert.Throws<NotSupportedException>(() => Map().Validate(frame, RayGiOptions.Off));
    }

    [Fact]
    public void InactiveLightsAndUndrawnMaterialsDoNotAcquireTheNewDomain()
    {
        foreach (var light in new[] { Light() with { Enabled = false }, Light() with { Intensity = 0 }, Light() with { Color = Vector3.Zero } })
            Map().Validate(Frame(toon: new(2), lights: [light]), null);
        Map().Validate(Frame(toon: new(2), surfaceEnabled: false), null);

        var frame = Frame();
        var original = frame.Surface.Snapshot;
        var first = original.Materials[0];
        // A newly partitioned fixture needs its own snapshot; WithMaterials intentionally
        // preserves the original material topology during ordinary material edits.
        var scene = new SceneSnapshot(original.Vertices, original.Indices,
            [first with { IndexCount = 0 }, first with { Name = "draw", StartIndex = 0 }], original.Textures,
            original.World, original.Camera, original.LightDirection, original.AdditionalUvs);
        var surface = new OriginalStyleScene(scene, [new(new Vector3(2)), new(new Vector3(.2f))], frame.Surface.Profile);
        new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd])
            .Validate(new(surface, options: frame.Options, lighting: frame.Lighting), null);
        // Only the selected family draw is checked in a mixed map.
        surface = new(scene, [new(new Vector3(2)), new(new Vector3(.2f))], frame.Surface.Profile);
        new D3D12SurfaceStyleMap([D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx])
            .Validate(new(surface, options: frame.Options, lighting: frame.Lighting), null);
    }

    [Fact]
    public void AbsentDiffuseAndSphereSlotsKeepTheirWhiteAndUnusedFactorContracts()
    {
        var dangerous = new OriginalTextureFactor(new(float.MaxValue), new(float.MaxValue));
        var factors = new OriginalMaterialTextureFactors(dangerous, dangerous, OriginalTextureFactor.Identity);
        Map().Validate(Frame(factors: factors, diffuseTexture: false), null);
        // A disabled sphere does not evaluate its stored morph factor.
        Map().Validate(Frame(factors: OriginalMaterialTextureFactors.Identity with { Sphere = dangerous }), null);
        Assert.Throws<NotSupportedException>(() => Map().Validate(Frame(factors: factors), null));
    }

    [Fact]
    public void EffectiveMorphDomainIsCheckedInsteadOfRejectingEveryNegativeAccumulator()
    {
        var negativeButNeutral = new OriginalTextureFactor(new(-2, -3, -4, 0), Vector4.Zero);
        var factors = OriginalMaterialTextureFactors.Identity with { Diffuse = negativeButNeutral, Sphere = negativeButNeutral };
        Map().Validate(Frame(factors: factors, sphere: OriginalSphereMode.Multiply), null);
        var negative = factors with { Sphere = new(new(-1, -1, -1, 1), Vector4.Zero) };
        Assert.Throws<NotSupportedException>(() => Map().Validate(Frame(factors: negative, sphere: OriginalSphereMode.Multiply), null));
        // Additive sphere is part of the original surface, outside the new diffuse budget.
        Map().Validate(Frame(factors: negative, sphere: OriginalSphereMode.Add), null);
        Map().Validate(Frame(factors: negative, sphere: OriginalSphereMode.SubTexture, additiveSubTexture: true), null);
        Assert.Throws<NotSupportedException>(() => Map().Validate(Frame(factors: negative,
            sphere: OriginalSphereMode.SubTexture, additiveSubTexture: true, diffuseTexture: false), null));
    }

    [Fact]
    public void IntermediateMorphAndNativeProductsCannotBeHiddenByZeroBudgetOrLaterFactors()
    {
        var factors = OriginalMaterialTextureFactors.Identity with
        {
            Diffuse = new(new(float.MaxValue, float.MaxValue, float.MaxValue, 0), new(float.MaxValue, float.MaxValue, float.MaxValue, 0)),
        };
        Assert.Throws<NotSupportedException>(() => Map().Validate(Frame(factors: factors, toon: Vector3.One), null));
        factors = OriginalMaterialTextureFactors.Identity with { Diffuse = new(new(2, 2, 2, 1), Vector4.Zero) };
        // Host Eg is capped to one, independently of the unbounded NativeDiffuse product.
        Assert.Throws<NotSupportedException>(() => Map().Validate(Frame(diffuse: new(float.MaxValue, float.MaxValue, float.MaxValue, 1),
            factors: factors, toon: Vector3.One), null));
        var colorFirstOverflow = Frame(diffuse: new(2, 2, 2, 1),
            lights: [Light() with { Color = new(float.MaxValue), Intensity = 1e-38f }], toon: Vector3.Zero);
        Assert.Throws<NotSupportedException>(() => Map().Validate(colorFirstOverflow, null));
    }

    [Fact]
    public void IntensityAttenuationAndNativeAccumulationAreFiniteBeforeTheCap()
    {
        var point = Light() with { Kind = SceneLightKind.Point, Intensity = 1e38f, Color = new(1e-20f) };
        Assert.Throws<NotSupportedException>(() => Map().Validate(Frame(lights: [point], toon: Vector3.One), null));
        var light = Light() with { Intensity = float.MaxValue * .3f };
        Map().Validate(Frame(diffuse: Vector4.One, lights: [light], toon: Vector3.Zero), null);
        Assert.Throws<NotSupportedException>(() => Map().Validate(Frame(diffuse: Vector4.One,
            lights: Enumerable.Range(0, 4).Select(_ => light with { Id = Guid.NewGuid() }).ToArray(), toon: Vector3.Zero), null));
    }

    [Fact]
    public void NativePlusGiTotalIsCheckedBeforeSharedScalingAndUsesTheSelectedGiMaximum()
    {
        var factors = OriginalMaterialTextureFactors.Identity with
        {
            Diffuse = new(new(float.MaxValue * .75f, float.MaxValue * .75f, float.MaxValue * .75f, 1), Vector4.Zero),
        };
        var frame = Frame(diffuse: new(1e-38f, 1e-38f, 1e-38f, 1), ambient: Vector3.One, toon: Vector3.Zero,
            factors: factors, lights: [Light() with { Intensity = float.MaxValue * .2f }]);
        Map().Validate(frame, null);
        Assert.Throws<NotSupportedException>(() => Map().Validate(frame, Palette));
        Map().Validate(frame, Palette with { ToonResponse = RayGiToonResponse.PreservePaletteBands });
        var flags = frame.Materials.Select(value => value with { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands });
        Map().Validate(new(frame.Surface, options: frame.Options, lighting: frame.Lighting, materials: flags), Palette);
    }

    [Fact]
    public void TinyColoredHugeDirectionalLightAndUnboundedAuthoredSpecularRemainSupported()
    {
        var light = Light() with { Color = new(1e-20f), Intensity = 1e38f };
        Map().Validate(Frame(lights: [light]), null);
        var frame = Frame(diffuse: new(0, 0, 0, 1), toon: Vector3.One);
        var material = frame.Surface.Snapshot.Materials[0] with { Specular = new(4) };
        Map().Validate(new(new(frame.Surface.Snapshot.WithMaterials([material]), frame.Surface.Materials, frame.Surface.Profile),
            options: frame.Options, lighting: frame.Lighting), null);
    }

    private static D3D12SurfaceStyleMap Map(D3D12SurfaceStyle family = D3D12SurfaceStyle.GenshinPmx) => new([family]);
    private static RayGiOptions Palette => new(Mode: LightingMode.SoftwareRayTracing, Composition: RayGiComposition.ToonPalette);
    private static NativeSceneLight Light() => new(Guid.NewGuid(), "budget", SceneLightKind.Directional,
        Vector3.Zero, Quaternion.Identity, Vector3.One, CastShadows: false);

    private static OriginalFrameScene Frame(Vector4? diffuse = null, Vector3? ambient = null, Vector3? toon = null,
        OriginalMaterialTextureFactors? factors = null, NativeSceneLight[]? lights = null, bool diffuseTexture = true,
        OriginalSphereMode sphere = OriginalSphereMode.None, bool additiveSubTexture = false, bool surfaceEnabled = true)
    {
        var quad = D3D12MeshTests.Quad();
        var material = new SceneMaterial("budget", 0, 6, diffuse ?? new(.4f, .24f, .16f, 1), Vector3.Zero,
            ambient ?? Vector3.Zero, 1, diffuseTexture ? 0 : -1);
        var uv = sphere == OriginalSphereMode.SubTexture
            ? new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(default, default, default, default), quad.Vertices.Length).ToArray()) : null;
        var snapshot = new SceneSnapshot(quad.Vertices, quad.Indices, [material], [new("white", 1, 1, [255, 255, 255, 255])],
            camera: quad.Camera, additionalUvs: uv);
        var binding = new OriginalStyleMaterialBinding(toon ?? new(.2f, .3f, .4f), sphere == OriginalSphereMode.None ? -1 : 0, sphere)
        {
            TextureFactors = factors ?? OriginalMaterialTextureFactors.Identity,
            SubTextureAdditive = additiveSubTexture,
        };
        return new(new(snapshot, [binding], new(new(.5f), Vector3.One, Matrix4x4.Identity)),
            options: new(OriginalAlphaBlendMode.Normal, true, false), lighting: new(lights ?? [Light()]),
            materials: [new(false, false, false, SurfaceEnabled: surfaceEnabled)]);
    }
}
