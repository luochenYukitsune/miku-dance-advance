using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12GeneratedMaterialGuardAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingGeneratedPRefreshesLegalCoupledRadialHairAndPalette(bool legacy)
    {
        var source = Source();
        if (legacy)
        {
            var prepared = D3D12GeneratedEndfieldMaterialPreparation.Prepare(source,
                [new(0, new(D3D12GeneratedPmxSurfaceKind.Hair, 16))], "guard-audit", "Guard audit",
                TestContext.Current.CancellationToken);
            var profile = Profile(prepared.Profile, CoupleEndfield(prepared.Profile.Materials[0]));
            var restored = D3D12GeneratedMaterialRestore.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
            var p = profile.Materials[0].EndfieldMatcap!.P;
            var edited = restored.WithEditedMaps(0, p: Edited(restored.SourceRest, p));
            AssertCoupledEndfield(edited.SourceRest, edited.Profile, p);
            Assert.Equal(edited.ManifestBytes, D3D12GeneratedMaterialRestore.Restore(
                edited.SourceRest, edited.Profile, edited.ManifestBytes).ManifestBytes);
        }
        else
        {
            var prepared = Prepare(source, "endfield", D3D12GeneratedPmxSurfaceKind.Hair);
            var profile = Profile(prepared.Profile, CoupleEndfield(prepared.Profile.Materials[0]));
            var restored = D3D12GeneratedStyleMaterialPreparation.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
            var p = profile.Materials[0].EndfieldMatcap!.P;
            var edited = restored.WithEditedMap(0, "p", Edited(restored.SourceRest, p));
            AssertCoupledEndfield(edited.SourceRest, edited.Profile, p);
            Assert.Equal(edited.ManifestBytes, D3D12GeneratedStyleMaterialPreparation.Restore(
                edited.SourceRest, edited.Profile, edited.ManifestBytes).ManifestBytes);
        }
    }

    [Theory]
    [InlineData("lightmap")]
    [InlineData("atlas")]
    public void EditingGenshinAtlasRefreshesLegalHairMetallicAndSpecularAliases(string mapName)
    {
        var prepared = Prepare(Source(), "genshin", D3D12GeneratedPmxSurfaceKind.Hair);
        var row = prepared.Profile.Materials[0];
        var atlas = row.GenshinHairAtlas!;
        var profile = Profile(prepared.Profile, row with
        {
            GenshinHair = new(atlas.Lightmap, Multiplier: .17f),
            GenshinMetallic = new(atlas.Lightmap, atlas.Atlas, D3D12MatcapColorEncoding.LinearRgb,
                new(.4f), Vector3.One, new(.8f), Specular: new(Vector3.One, new(.7f), Ramp: atlas.Atlas))
        });
        var restored = D3D12GeneratedStyleMaterialPreparation.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
        var selected = mapName == "lightmap" ? atlas.Lightmap : atlas.Atlas;
        var edited = restored.WithEditedMap(0, mapName, Edited(restored.SourceRest, selected));
        var actual = edited.Profile.Materials[0];
        Assert.Equal(actual.GenshinHairAtlas!.Lightmap, actual.GenshinHair!.Texture);
        Assert.Equal(actual.GenshinHairAtlas.Lightmap, actual.GenshinMetallic!.Lightmap);
        Assert.Equal(actual.GenshinHairAtlas.Atlas, actual.GenshinMetallic.Matcap);
        Assert.Equal(actual.GenshinHairAtlas.Atlas, actual.GenshinMetallic.Specular!.Ramp);
        Assert.Equal(.17f, actual.GenshinHair.Multiplier);
        Assert.NotEqual(selected, D3D12MaterialBindingLayout.Capture(edited.SourceRest).CaptureTexture(selected.SourceSceneTextureOrdinal));
        ResolveBoth(edited.SourceRest, edited.Profile, "genshin");
        Assert.Equal(edited.ManifestBytes, D3D12GeneratedStyleMaterialPreparation.Restore(
            edited.SourceRest, edited.Profile, edited.ManifestBytes).ManifestBytes);
        Assert.Equal(atlas.Lightmap, row.GenshinHairAtlas!.Lightmap);
    }

    [Fact]
    public void EditingGenshinMetallicMatcapRefreshesOptionalSpecularRamp()
    {
        var prepared = Prepare(Source(), "genshin", D3D12GeneratedPmxSurfaceKind.Metal);
        var row = prepared.Profile.Materials[0];
        var metallic = row.GenshinMetallic!;
        var profile = Profile(prepared.Profile, row with
        {
            GenshinMetallic = metallic with { Specular = new(Vector3.One, Vector3.One, Ramp: metallic.Matcap) }
        });
        var restored = D3D12GeneratedStyleMaterialPreparation.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
        var edited = restored.WithEditedMap(0, "matcap", Edited(restored.SourceRest, metallic.Matcap));
        Assert.Equal(edited.Profile.Materials[0].GenshinMetallic!.Matcap,
            edited.Profile.Materials[0].GenshinMetallic!.Specular!.Ramp);
        ResolveBoth(edited.SourceRest, edited.Profile, "genshin");
        _ = D3D12GeneratedStyleMaterialPreparation.Restore(edited.SourceRest, edited.Profile, edited.ManifestBytes);
    }

    [Theory]
    [InlineData("face", false)]
    [InlineData("face", true)]
    [InlineData("star-rail", false)]
    [InlineData("wuthering-waves", false)]
    public void SharedRefreshCoversStandaloneFaceAndStarMcHairSelectors(string family, bool secondary)
    {
        var source = Source();
        var old = D3D12MaterialBindingLayout.Capture(source).CaptureTexture(0);
        string pipeline = family == "face" ? secondary ? "genshin" : "star-rail" : family;
        var row = family == "face"
            ? new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Face,
                Face: new(pipeline, old, secondary ? old : null, Vector3.UnitZ, Vector3.UnitX,
                    Encoding: secondary ? D3D12FaceSdfEncoding.GenshinSoftFace : D3D12FaceSdfEncoding.GrayRed))
            : new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
                StarMcHair: new(family, old, old, Intensity: .23f));
        row.Validate();
        var changed = D3D12GeneratedEndfieldMaterialPreparation.ReplaceTextures(source, [Edited(source, old)]);
        var layout = D3D12MaterialBindingLayout.Capture(changed);
        var refreshed = D3D12GeneratedStyleMaterialResult.RefreshGuards(row, layout);
        var expected = layout.CaptureTexture(0);
        Assert.NotEqual(old, expected);
        if (family == "face")
        {
            Assert.Equal(expected, refreshed.Face!.Primary);
            Assert.Equal(secondary ? expected : null, refreshed.Face.Secondary);
            Assert.Equal(old, row.Face!.Primary);
        }
        else
        {
            Assert.Equal(expected, refreshed.StarMcHair!.Highlight);
            Assert.Equal(expected, refreshed.StarMcHair.Shadow);
            Assert.Equal(.23f, refreshed.StarMcHair.Intensity);
            Assert.Equal(old, row.StarMcHair!.Highlight);
        }
        var profile = new D3D12MaterialBindingProfile("standalone-guard-audit", "Guard audit", layout.Fingerprint, [refreshed]);
        ResolveBoth(changed, profile, pipeline);
    }

    private static D3D12MaterialBindingProfileRow CoupleEndfield(D3D12MaterialBindingProfileRow row)
    {
        var maps = row.EndfieldMatcap!;
        return row with
        {
            EndfieldHair = new(maps.P, maps.BaseColor, Brightness: .15f),
            EndfieldMatcap = maps with
            {
                CoupleEndfieldPalette = true,
                Palette = new(Vector3.Zero,
                    EmissionInputMode: D3D12EndfieldPaletteInputMode.Texture, EmissionTexture: maps.P,
                    ReceivedLightInputMode: D3D12EndfieldPaletteInputMode.Texture, ReceivedLightTexture: maps.P,
                    BaseImage: new(Vector3.One, Vector3.One, new(.5f), Vector3.One,
                        BaseInputMode: D3D12EndfieldPaletteInputMode.Texture, BaseTexture: maps.P,
                        DetailInputMode: D3D12EndfieldPaletteInputMode.Texture, DetailTexture: maps.P,
                        ShadowInputMode: D3D12EndfieldPaletteInputMode.Texture, ShadowTexture: maps.P))
            }
        };
    }

    private static void AssertCoupledEndfield(OriginalFrameScene frame, D3D12MaterialBindingProfile profile,
        D3D12MaterialBindingProfileTexture previous)
    {
        var row = profile.Materials[0];
        var maps = row.EndfieldMatcap!;
        var current = D3D12MaterialBindingLayout.Capture(frame).CaptureTexture(previous.SourceSceneTextureOrdinal);
        Assert.NotEqual(previous, current);
        Assert.Equal(current, maps.P);
        Assert.Equal(current, row.EndfieldHair!.Texture);
        Assert.Equal(current, maps.Palette!.EmissionTexture);
        Assert.Equal(current, maps.Palette.ReceivedLightTexture);
        Assert.Equal(current, maps.Palette.BaseImage!.BaseTexture);
        Assert.Equal(current, maps.Palette.BaseImage.DetailTexture);
        Assert.Equal(current, maps.Palette.BaseImage.ShadowTexture);
        Assert.Equal(.15f, row.EndfieldHair.Brightness);
        ResolveBoth(frame, profile, "endfield");
    }

    private static void ResolveBoth(OriginalFrameScene frame, D3D12MaterialBindingProfile profile, string pipeline)
    {
        _ = D3D12MaterialBindingProfileResolver.Resolve(profile, frame, Guid.NewGuid(), pipeline);
        var dormant = D3D12MaterialBindingProfileResolver.Resolve(profile, frame, Guid.NewGuid(), "mmd");
        Assert.Empty(dormant.FaceBindings);
        Assert.Empty(dormant.GenshinHairBindings);
        Assert.Empty(dormant.StarMcHairBindings);
        Assert.Empty(dormant.EndfieldHairBindings);
        Assert.Empty(dormant.EndfieldMatcapBindings);
    }

    private static D3D12MaterialBindingProfile Profile(D3D12MaterialBindingProfile original, D3D12MaterialBindingProfileRow row)
        => new(original.StableId, original.Name, original.LayoutFingerprint, [row], original.LayoutVersion);

    private static D3D12GeneratedStyleMaterialResult Prepare(OriginalFrameScene source, string pipeline,
        D3D12GeneratedPmxSurfaceKind kind)
        => D3D12GeneratedStyleMaterialPreparation.Prepare(source, [new(0, new(kind, 16))], pipeline,
            "guard-audit", "Guard audit", TestContext.Current.CancellationToken);

    private static SceneTexture Edited(OriginalFrameScene frame, D3D12MaterialBindingProfileTexture selector)
    {
        var texture = frame.Surface.Snapshot.Textures[selector.SourceSceneTextureOrdinal];
        byte[] pixels = texture.Rgba.ToArray(); pixels[0] ^= 127;
        return new("edited generated map", texture.Width, texture.Height, pixels);
    }

    private static OriginalFrameScene Source()
    {
        SceneVertex[] vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)];
        return new(new(new SceneSnapshot(vertices, [0, 1, 2],
            [new("explicit", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1, 0)],
            [new SceneTexture("original", 1, 1, [80, 120, 160, 255])],
            rawRadialSeeds: new([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ])), [new(Vector3.One)]));
    }
}
