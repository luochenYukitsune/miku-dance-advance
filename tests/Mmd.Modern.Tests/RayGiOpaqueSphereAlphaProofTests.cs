using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiOpaqueSphereAlphaProofTests
{
    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void AdapterProvesOnlyDiffuseAndKeepsTransparentSphereVisibility(OriginalSphereMode mode)
    {
        var proved = RayGiScene.FromOriginal(Style(mode));
        Assert.Equal(new RayGiUInt4(2, 0, 0, 0), proved.Materials[0].Flags);
        Assert.False(proved.AcceptCandidate(0, new(.25f), -Vector3.UnitZ));

        // The only changed diffuse texel is not selected by UV0. It must invalidate the whole-view proof.
        var cutoutDiffuse = RayGiScene.FromOriginal(Style(mode, diffuseLastAlpha: 254));
        Assert.Equal(default, cutoutDiffuse.Materials[0].Flags);
        Assert.Equal(proved.Materials[0].SphereTexture, cutoutDiffuse.Materials[0].SphereTexture);
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void PublicProofRoundTripChecksActualDiffuseViewButAllowsTransparentSphere(OriginalSphereMode mode)
    {
        var source = RayGiScene.FromOriginal(Style(mode));
        var flagged = WithFlags(source.Materials[0], new(2, 0, 0, 0));
        var pixels = source.Texels.ToArray();
        pixels[0] = 0x00ffffff; // An unrelated atlas entry need not be opaque.
        var copy = Copy(source, [flagged], pixels);
        Assert.Equal(flagged, copy.Materials[0]);
        Assert.Equal(source.Triangles.ToArray(), copy.Triangles.ToArray());
        Assert.Equal(pixels, copy.Texels.ToArray());
        Assert.False(copy.AcceptCandidate(0, new(.25f), -Vector3.UnitZ));

        int lastDiffuse = checked((int)(flagged.DiffuseTexture.X + flagged.DiffuseTexture.Y * flagged.DiffuseTexture.Z - 1));
        pixels[lastDiffuse] = (pixels[lastDiffuse] & 0x00ffffff) | 0xfe000000;
        Assert.Throws<ArgumentException>(() => Copy(source, [flagged], pixels));
        Assert.Equal(default, Copy(source, [WithFlags(flagged, default)], pixels).Materials[0].Flags);
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void MissingDiffuseMapProvesActualFallbackAndCannotClaimTheNoneSphereFlag(OriginalSphereMode mode)
    {
        var source = RayGiScene.FromOriginal(Style(mode, missingDiffuse: true));
        var material = source.Materials[0];
        Assert.Equal(0u, material.DiffuseTexture.X);
        Assert.Equal(new RayGiUInt4(2, 0, 0, 0), material.Flags);
        var pixels = source.Texels.ToArray();
        Assert.Equal(material, Copy(source, [material], pixels).Materials[0]);
        pixels[0] = 0x00ffffff;
        Assert.Throws<ArgumentException>(() => Copy(source, [WithFlags(material, new(2, 0, 0, 0))], pixels));
        Assert.Throws<ArgumentException>(() => Copy(source, [WithFlags(material, new(1, 0, 0, 0))], source.Texels.ToArray()));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(0x80000000u)]
    public void CombinedAndUnknownFlagsRemainInvalid(uint flag)
    {
        var source = RayGiScene.FromOriginal(Style(OriginalSphereMode.Add));
        Assert.Throws<ArgumentException>(() => Copy(source,
            [WithFlags(source.Materials[0], new(flag, 0, 0, 0))], source.Texels.ToArray()));
    }

    [Fact]
    public void ActiveSphereFlagCannotBeAppliedToNoneAndSubTextureStillNeedsCompleteUv1()
    {
        var none = RayGiScene.FromOriginal(Style(OriginalSphereMode.None));
        Assert.Equal(new RayGiUInt4(1, 0, 0, 0), none.Materials[0].Flags);
        Assert.Throws<ArgumentException>(() => Copy(none,
            [WithFlags(none.Materials[0], new(2, 0, 0, 0))], none.Texels.ToArray()));

        var sub = RayGiScene.FromOriginal(Style(OriginalSphereMode.SubTexture));
        var flagged = WithFlags(sub.Materials[0], new(2, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new RayGiScene(sub.Triangles, [flagged], sub.Texels, sub.DiffuseTextureFactors));
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void ImmutableDiffuseProofSurvivesCameraAndColourMorphButRechecksNewOwner(OriginalSphereMode mode)
    {
        var style = Style(mode);
        var first = RayGiScene.FromOriginal(style);
        var factors = OriginalMaterialTextureFactors.Identity with
        {
            Diffuse = new(new(2, .5f, .75f, 0), new(.1f, .2f, 0, 0)),
            Sphere = new(new(.5f, 2, .75f, 0), new(.1f, 0, .2f, 0))
        };
        var moved = new OriginalStyleScene(style.Snapshot.WithCamera(style.Snapshot.Camera with { Eye = new(0, 0, -7) }),
            [style.Materials[0] with { TextureFactors = factors }], style.Profile);
        var morphed = RayGiScene.FromOriginal(moved);
        Assert.Equal(first.TexelIdentity, morphed.TexelIdentity);
        Assert.Equal(new RayGiUInt4(2, 0, 0, 0), morphed.Materials[0].Flags);
        Assert.NotEmpty(morphed.DiffuseTextureFactors.ToArray());

        var mesh = style.Snapshot;
        OriginalStyleScene Replace(SceneTexture diffuse, SceneTexture sphere) => new(new SceneSnapshot(mesh.Vertices,
            mesh.Indices, mesh.Materials, [diffuse, sphere], mesh.World, mesh.Camera, mesh.LightDirection, mesh.AdditionalUvs),
            style.Materials, style.Profile);
        var replacedDiffuse = RayGiScene.FromOriginal(Replace(Diffuse(0), mesh.Textures[1]));
        Assert.NotEqual(first.TexelIdentity, replacedDiffuse.TexelIdentity);
        Assert.Equal(default, replacedDiffuse.Materials[0].Flags);
        // Changing sphere alpha does not invalidate a proof about diffuse alpha; it remains sampled.
        var replacedSphere = RayGiScene.FromOriginal(Replace(mesh.Textures[0], Sphere(255)));
        Assert.NotEqual(first.TexelIdentity, replacedSphere.TexelIdentity);
        Assert.Equal(new RayGiUInt4(2, 0, 0, 0), replacedSphere.Materials[0].Flags);
        Assert.True(replacedSphere.AcceptCandidate(0, new(.25f), -Vector3.UnitZ));
        Assert.False(first.AcceptCandidate(0, new(.25f), -Vector3.UnitZ));
    }

    private static RayGiScene Copy(RayGiScene source, RayGiMaterialGpu[] materials, uint[] pixels)
        => new(source.Triangles, materials, pixels, source.DiffuseTextureFactors, source.SubTextureUvs);

    private static RayGiMaterialGpu WithFlags(RayGiMaterialGpu source, RayGiUInt4 flags)
        => new(source.DiffuseFactor, source.Emission, source.DiffuseTexture, source.SphereTexture, source.Parameters, flags);

    private static SceneTexture Diffuse(byte lastAlpha)
        => new("same-name-diffuse", 2, 2, [5, 29, 233, 255, 239, 37, 13, 255, 53, 211, 67, 255, 101, 83, 191, lastAlpha]);

    private static SceneTexture Sphere(byte lastAlpha)
        => new("same-name-sphere", 4, 1, [128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, lastAlpha]);

    private static OriginalStyleScene Style(OriginalSphereMode mode, byte diffuseLastAlpha = 255, bool missingDiffuse = false)
    {
        var normal = new Vector3(.5f, 0, .8660254f); // Sphere projection X=.75 selects the final texel.
        SceneVertex[] vertices = [new(new(-1, -1, 0), normal, new(.25f)), new(new(1, -1, 0), normal, new(.25f)),
            new(new(0, 1, 0), normal, new(.25f))];
        var uv1 = new SceneAdditionalUvs(1, Enumerable.Repeat(
            new SceneAdditionalUvVertex(new(.75f, .5f, 0, 0), default, default, default), 3).ToArray());
        var mesh = new SceneSnapshot(vertices, [0, 1, 2], [new("surface", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1,
            DiffuseTexture: missingDiffuse ? -1 : 0, AlphaCutoff: .5f)], [Diffuse(diffuseLastAlpha), Sphere(0)], additionalUvs: uv1);
        return new(mesh, [new(Vector3.One, mode == OriginalSphereMode.None ? -1 : 1, mode)],
            OriginalStyleProfile.SelectedReference with { TextureSampling = OriginalTextureSampling.PointClamp });
    }
}
