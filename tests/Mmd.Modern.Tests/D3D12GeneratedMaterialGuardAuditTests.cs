using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
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
            int pSlot = profile.Materials[0].PluginBindings[0].TextureSlots!["p"];
            var p = profile.Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == pSlot);
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
            int pSlot = profile.Materials[0].PluginBindings[0].TextureSlots!["p"];
            var p = profile.Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == pSlot);
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
        var atlas = row.PluginBindings[0];
        int lightmapSlot = atlas.TextureSlots!["lightmap"];
        int atlasSlot = atlas.TextureSlots!["atlas"];
        var lightmapGuard = row.TextureGuards.Single(g => g.SourceSceneTextureOrdinal == lightmapSlot);
        var atlasGuard = row.TextureGuards.Single(g => g.SourceSceneTextureOrdinal == atlasSlot);
        var selected = mapName == "lightmap" ? lightmapGuard : atlasGuard;

        var profile = Profile(prepared.Profile, row with
        {
            PluginBindings =
            [
                atlas,
                TestStylePlugins.GenshinHair(lightmapSlot, multiplier: .17f),
                TestStylePlugins.GenshinMetallic(lightmapSlot, atlasSlot, "LinearRgb",
                    new(.4f), Vector3.One, new(.8f), hasSpecular: true,
                    specularColor: Vector3.One, specularSharpColor: new(.7f), specularRampSlot: atlasSlot),
            ]
        });
        var restored = D3D12GeneratedStyleMaterialPreparation.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
        var edited = restored.WithEditedMap(0, mapName, Edited(restored.SourceRest, selected));
        var actual = edited.Profile.Materials[0];
        var actualAtlas = actual.PluginBindings.Single(b => b.ModeId == "hair-atlas");
        var actualHair = actual.PluginBindings.Single(b => b.ModeId == "hair");
        var actualMetallic = actual.PluginBindings.Single(b => b.ModeId == "metallic");
        Assert.Equal(lightmapSlot, actualHair.TextureSlots!["texture"]);
        Assert.Equal(lightmapSlot, actualMetallic.TextureSlots!["lightmap"]);
        Assert.Equal(atlasSlot, actualMetallic.TextureSlots!["matcap"]);
        Assert.Equal(atlasSlot, actualMetallic.TextureSlots!["specularRamp"]);
        Assert.Equal(.17, actualHair.NumberValues!["multiplier"], 6);
        var updatedGuard = actual.TextureGuards.Single(g => g.SourceSceneTextureOrdinal == selected.SourceSceneTextureOrdinal);
        Assert.NotEqual(selected, updatedGuard);
        Assert.Equal(updatedGuard, D3D12MaterialBindingLayout.Capture(edited.SourceRest).CaptureTexture(selected.SourceSceneTextureOrdinal));
        ResolveBoth(edited.SourceRest, edited.Profile, "genshin");
        Assert.Equal(edited.ManifestBytes, D3D12GeneratedStyleMaterialPreparation.Restore(
            edited.SourceRest, edited.Profile, edited.ManifestBytes).ManifestBytes);
        Assert.Equal(lightmapGuard, row.TextureGuards.Single(g => g.SourceSceneTextureOrdinal == lightmapSlot));
    }

    [Fact]
    public void EditingGenshinMetallicMatcapRefreshesOptionalSpecularRamp()
    {
        var prepared = Prepare(Source(), "genshin", D3D12GeneratedPmxSurfaceKind.Metal);
        var row = prepared.Profile.Materials[0];
        var metallic = row.PluginBindings[0];
        int lightmapSlot = metallic.TextureSlots!["lightmap"];
        int matcapSlot = metallic.TextureSlots!["matcap"];
        var matcapGuard = row.TextureGuards.Single(g => g.SourceSceneTextureOrdinal == matcapSlot);
        var withSpec = TestStylePlugins.GenshinMetallic(lightmapSlot, matcapSlot, "LinearRgb",
            new(.7f), Vector3.One, new(.85f), enableMetallics: .3f,
            hasSpecular: true, specularColor: Vector3.One, specularSharpColor: Vector3.One, specularRampSlot: matcapSlot);
        var profile = Profile(prepared.Profile, row with { PluginBindings = [withSpec] });
        var restored = D3D12GeneratedStyleMaterialPreparation.Restore(prepared.SourceRest, profile, prepared.ManifestBytes);
        var edited = restored.WithEditedMap(0, "matcap", Edited(restored.SourceRest, matcapGuard));
        var updatedGuard = edited.Profile.Materials[0].TextureGuards.Single(g => g.SourceSceneTextureOrdinal == matcapSlot);
        Assert.NotEqual(matcapGuard, updatedGuard);
        Assert.Equal(matcapSlot, edited.Profile.Materials[0].PluginBindings[0].TextureSlots!["specularRamp"]);
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
                pluginBindings: [TestStylePlugins.FaceSdf(pipeline, 0, secondary ? 0 : -1,
                    encoding: secondary ? "GenshinSoftFace" : "GrayRed",
                    worldForward: Vector3.UnitZ, worldRight: Vector3.UnitX)],
                textureGuards: [old])
            : new D3D12MaterialBindingProfileRow(0, D3D12CharacterMaterialRole.Hair,
                pluginBindings: [TestStylePlugins.StarMcHair(family, 0, 0, intensity: .23f)],
                textureGuards: [old]);
        row.Validate();
        var changed = D3D12GeneratedEndfieldMaterialPreparation.ReplaceTextures(source, [Edited(source, old)]);
        var layout = D3D12MaterialBindingLayout.Capture(changed);
        var refreshed = D3D12GeneratedStyleMaterialResult.RefreshGuards(row, layout);
        var expected = layout.CaptureTexture(0);
        Assert.NotEqual(old, expected);
        Assert.Equal(expected, Assert.Single(refreshed.TextureGuards));
        Assert.Equal(old, Assert.Single(row.TextureGuards));
        if (family == "face")
        {
            Assert.Equal(0, refreshed.PluginBindings[0].TextureSlots!["primary"]);
            Assert.Equal(secondary, refreshed.PluginBindings[0].TextureSlots!.ContainsKey("secondary"));
        }
        else
        {
            Assert.Equal(0, refreshed.PluginBindings[0].TextureSlots!["highlight"]);
            Assert.Equal(0, refreshed.PluginBindings[0].TextureSlots!["shadow"]);
            Assert.Equal(.23, refreshed.PluginBindings[0].NumberValues!["intensity"], 6);
        }
        var profile = new D3D12MaterialBindingProfile("standalone-guard-audit", "Guard audit", layout.Fingerprint, [refreshed]);
        ResolveBoth(changed, profile, pipeline);
    }

    private static D3D12MaterialBindingProfileRow CoupleEndfield(D3D12MaterialBindingProfileRow row)
    {
        var maps = row.PluginBindings[0];
        int normalSlot = maps.TextureSlots!["normal"];
        int pSlot = maps.TextureSlots!["p"];
        int matcapSlot = maps.TextureSlots!["matcap"];
        var bc = maps.VectorValues!["baseColor"];
        var baseColor = new Vector3(bc[0], bc[1], bc[2]);
        var hc = maps.VectorValues!["highlightColor"];
        var highlightColor = new Vector3(hc[0], hc[1], hc[2]);
        var lcd = maps.VectorValues!["lightControlDirection"];
        var lightCtrl = new Vector3(lcd[0], lcd[1], lcd[2]);
        var spec = new EndfieldMatcapSpec(
            normalSlot, pSlot, matcapSlot,
            maps.StringValues!["matcapEncoding"],
            (int)maps.NumberValues!["uvChannel"],
            baseColor, highlightColor,
            (float)maps.NumberValues!["normalStrength"],
            (float)maps.NumberValues!["rotationRadians"],
            (float)maps.NumberValues!["highlightCoefficient"],
            (float)maps.NumberValues!["shadowHardness"],
            lightCtrl,
            (float)maps.NumberValues!["baselineStrength"],
            CoupleEndfieldPalette: true,
            GeneratorRevision: maps.StringValues!.GetValueOrDefault("generatorRevision"),
            Palette: new EndfieldPaletteSpec(Vector3.Zero,
                EmissionInputMode: T8dTestHelpers.ModeTexture, EmissionSlot: pSlot,
                ReceivedLightInputMode: T8dTestHelpers.ModeTexture, ReceivedLightSlot: pSlot,
                BaseImage: new EndfieldBaseImageSpec(Vector3.One, Vector3.One, new(.5f), Vector3.One,
                    BaseInputMode: T8dTestHelpers.ModeTexture, BaseSlot: pSlot,
                    DetailInputMode: T8dTestHelpers.ModeTexture, DetailSlot: pSlot,
                    ShadowInputMode: T8dTestHelpers.ModeTexture, ShadowSlot: pSlot)));
        return row with
        {
            PluginBindings =
            [
                spec.ToBinding(),
                T8dTestHelpers.EndfieldHair(pSlot, baseColor, brightness: .15f),
            ]
        };
    }

    private static void AssertCoupledEndfield(OriginalFrameScene frame, D3D12MaterialBindingProfile profile,
        D3D12MaterialBindingProfileTexture previous)
    {
        var row = profile.Materials[0];
        var matcap = row.PluginBindings.Single(b => b.ModeId == "matcap");
        var hair = row.PluginBindings.Single(b => b.ModeId == "hair");
        var current = D3D12MaterialBindingLayout.Capture(frame).CaptureTexture(previous.SourceSceneTextureOrdinal);
        Assert.NotEqual(previous, current);
        Assert.Equal(current, row.TextureGuards.Single(g => g.SourceSceneTextureOrdinal == previous.SourceSceneTextureOrdinal));
        int pSlot = previous.SourceSceneTextureOrdinal;
        Assert.Equal(pSlot, matcap.TextureSlots!["p"]);
        Assert.Equal(pSlot, hair.TextureSlots!["texture"]);
        Assert.Equal(pSlot, matcap.TextureSlots!["paletteEmission"]);
        Assert.Equal(pSlot, matcap.TextureSlots!["paletteReceivedLight"]);
        Assert.Equal(pSlot, matcap.TextureSlots!["baseImage"]);
        Assert.Equal(pSlot, matcap.TextureSlots!["baseImageDetail"]);
        Assert.Equal(pSlot, matcap.TextureSlots!["baseImageShadow"]);
        Assert.Equal(.15, hair.NumberValues!["brightness"], 6);
        ResolveBoth(frame, profile, "endfield");
    }

    private static void ResolveBoth(OriginalFrameScene frame, D3D12MaterialBindingProfile profile, string pipeline)
    {
        var active = D3D12MaterialBindingProfileResolver.Resolve(profile, frame, Guid.NewGuid(), pipeline, TestStylePlugins.AllViewCatalog);
        Assert.NotEmpty(active.PluginBindings);
        var dormant = D3D12MaterialBindingProfileResolver.Resolve(profile, frame, Guid.NewGuid(), "mmd", TestStylePlugins.AllViewCatalog);
        Assert.Empty(dormant.PluginBindings);
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
