using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiOpaqueAlphaProofTests
{
    [Theory]
    [InlineData(OriginalTextureSampling.PointClamp)]
    [InlineData(OriginalTextureSampling.LinearClamp)]
    [InlineData(OriginalTextureSampling.PointWrap)]
    [InlineData(OriginalTextureSampling.LinearWrap)]
    public void AdapterProvesEveryRawAlphaRatherThanRgbOrTheCurrentlySampledTexel(OriginalTextureSampling sampling)
    {
        var opaque = RayGiScene.FromOriginal(Style(ColorTexture(255), sampling: sampling));
        Assert.Equal(new RayGiUInt4(1, 0, 0, 0), opaque.Materials[0].Flags);

        // Only the final texel changes. UV0 samples the first opaque texel, and RGB is unchanged.
        var cutout = RayGiScene.FromOriginal(Style(ColorTexture(254), sampling: sampling));
        Assert.Equal(default, cutout.Materials[0].Flags);
        Assert.Equal(1, cutout.SampleTexture(cutout.Materials[0].DiffuseTexture, new(.25f, .25f)).W);
        Assert.Equal(opaque.Texels[0], cutout.Texels[0]);
        Assert.Equal(0xffffffffu, opaque.Texels[0]);
    }

    [Fact]
    public void MissingMapUsesTheActualWhiteFallbackAndProofSurvivesPublicRoundTrip()
    {
        var source = RayGiScene.FromOriginal(Style());
        Assert.Equal(0u, source.Materials[0].DiffuseTexture.X);
        Assert.Equal(new RayGiUInt4(1, 0, 0, 0), source.Materials[0].Flags);
        var copy = Copy(source, source.Materials.ToArray(), source.Texels.ToArray());
        Assert.Equal(source.Materials.ToArray(), copy.Materials.ToArray());
        Assert.Equal(source.Triangles.ToArray(), copy.Triangles.ToArray());
        Assert.Equal(source.Texels.ToArray(), copy.Texels.ToArray());

        var alteredFallback = source.Texels.ToArray();
        alteredFallback[0] = 0x00ffffff;
        Assert.Throws<ArgumentException>(() => Copy(source, [WithFlags(source.Materials[0], new(1, 0, 0, 0))], alteredFallback));
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void ActiveSphereUsesDiffuseProofButCannotClaimTheSphereNoneFlag(OriginalSphereMode sphere)
    {
        var source = RayGiScene.FromOriginal(Style(ColorTexture(255), sphere));
        Assert.Equal(new RayGiUInt4(2, 0, 0, 0), source.Materials[0].Flags);
        Assert.Throws<ArgumentException>(() => Copy(source, [WithFlags(source.Materials[0], new(1, 0, 0, 0))], source.Texels.ToArray()));
    }

    [Fact]
    public void PublicProofValidationChecksTheReferencedViewIncludingItsLastPixel()
    {
        var source = RayGiScene.FromOriginal(Style(ColorTexture(255)));
        var flagged = WithFlags(source.Materials[0], new(1, 0, 0, 0));
        var pixels = source.Texels.ToArray();

        // Unrelated atlas entries need not be opaque. A view proof must not scan the entire atlas.
        pixels[0] = 0x00ffffff;
        var valid = Copy(source, [flagged], pixels);
        Assert.Equal(flagged, valid.Materials[0]);

        var view = flagged.DiffuseTexture;
        int last = checked((int)(view.X + view.Y * view.Z - 1));
        pixels[last] = (pixels[last] & 0x00ffffff) | 0xfe000000;
        Assert.Throws<ArgumentException>(() => Copy(source, [flagged], pixels));

        // Existing explicit zero-flag scenes remain valid, including cutout views.
        var legacy = Copy(source, [WithFlags(flagged, default)], pixels);
        Assert.Equal(default, legacy.Materials[0].Flags);
    }

    [Theory]
    [InlineData(2u, 0u, 0u, 0u)]
    [InlineData(0x80000000u, 0u, 0u, 0u)]
    [InlineData(1u, 1u, 0u, 0u)]
    [InlineData(1u, 0u, 1u, 0u)]
    [InlineData(1u, 0u, 0u, 1u)]
    public void PublicConstructorContinuesRejectingEveryUnknownFlag(uint x, uint y, uint z, uint w)
    {
        var source = RayGiScene.FromOriginal(Style(ColorTexture(255)));
        Assert.Throws<ArgumentException>(() => Copy(source, [WithFlags(source.Materials[0], new(x, y, z, w))], source.Texels.ToArray()));
    }

    [Fact]
    public void TextureOwnerReuseKeepsProofButReplacingSameNamedPixelsInvalidatesIt()
    {
        var style = Style(ColorTexture(255));
        var first = RayGiScene.FromOriginal(style);
        var moved = new OriginalStyleScene(style.Snapshot.WithCamera(style.Snapshot.Camera with { Eye = new(0, 0, -7) }), style.Materials, style.Profile);
        var changedFactors = style.Materials[0] with
        {
            TextureFactors = OriginalMaterialTextureFactors.Identity with { Diffuse = new(new(2, .5f, .75f, 0), new(.1f, .2f, 0, 0)) }
        };
        var morphed = RayGiScene.FromOriginal(new OriginalStyleScene(moved.Snapshot, [changedFactors], moved.Profile));
        Assert.Equal(first.TexelIdentity, morphed.TexelIdentity);
        Assert.Equal(first.Materials.ToArray(), morphed.Materials.ToArray());
        Assert.NotEmpty(morphed.DiffuseTextureFactors.ToArray());

        var mesh = style.Snapshot;
        var replacement = new SceneSnapshot(mesh.Vertices, mesh.Indices, mesh.Materials,
            [ColorTexture(0), mesh.Textures[1]], mesh.World, mesh.Camera, mesh.LightDirection, mesh.AdditionalUvs);
        var replaced = RayGiScene.FromOriginal(new OriginalStyleScene(replacement, style.Materials, style.Profile));
        Assert.NotEqual(first.TexelIdentity, replaced.TexelIdentity);
        Assert.Equal(default, replaced.Materials[0].Flags);
        Assert.Equal(new RayGiUInt4(1, 0, 0, 0), first.Materials[0].Flags);
    }

    private static RayGiScene Copy(RayGiScene source, RayGiMaterialGpu[] materials, uint[] pixels)
        => new(source.Triangles, materials, pixels, source.DiffuseTextureFactors, source.SubTextureUvs);

    private static RayGiMaterialGpu WithFlags(RayGiMaterialGpu source, RayGiUInt4 flags)
        => new(source.DiffuseFactor, source.Emission, source.DiffuseTexture, source.SphereTexture, source.Parameters, flags);

    private static SceneTexture ColorTexture(byte lastAlpha)
        => new("same-name-opaque-proof", 2, 2, [5, 29, 233, 255, 239, 37, 13, 255, 53, 211, 67, 255, 101, 83, 191, lastAlpha]);

    private static OriginalStyleScene Style(SceneTexture? diffuse = null, OriginalSphereMode sphere = OriginalSphereMode.None,
        OriginalTextureSampling sampling = OriginalTextureSampling.LinearWrap)
    {
        SceneVertex[] vertices = [new(new(-1, -1, 0), Vector3.UnitZ, new(.25f)), new(new(1, -1, 0), Vector3.UnitZ, new(.25f)), new(new(0, 1, 0), Vector3.UnitZ, new(.25f))];
        SceneTexture[] textures = [diffuse ?? ColorTexture(255), new("opaque-sphere", 1, 1, [17, 59, 197, 255])];
        var uv1 = new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .5f, 0, 0), default, default, default), 3).ToArray());
        var mesh = new SceneSnapshot(vertices, [0, 1, 2], [new("surface", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1,
            DiffuseTexture: diffuse is null ? -1 : 0, AlphaCutoff: .5f)], textures, additionalUvs: uv1);
        return new(mesh, [new(Vector3.One, sphere == OriginalSphereMode.None ? -1 : 1, sphere)],
            OriginalStyleProfile.SelectedReference with { TextureSampling = sampling });
    }
}
