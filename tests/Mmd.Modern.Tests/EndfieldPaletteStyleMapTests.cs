using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldPaletteStyleMapTests
{
    private static readonly EndfieldPaletteSpec Palette = new(new(.1f, .2f, .3f), .5f, .7f, .6f, .1f,
        T8dTestHelpers.ModeTexture, 3, T8dTestHelpers.ModeTexture, 4);

    private static StyleMaterialBindingData ParentMatcap(
        int uvChannel = 0,
        Vector3? baseColor = null,
        Vector3? highlightColor = null,
        float baselineStrength = 1f,
        bool couple = true,
        EndfieldPaletteSpec? palette = null)
        => T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingLinearRgb, uvChannel,
            baseColor ?? new(.2f), highlightColor ?? Vector3.Zero, baselineStrength: baselineStrength,
            coupleEndfieldPalette: couple, palette: palette);

    [Fact]
    public void PaletteRequiresExplicitCoupledParentAndUsesOnlyItsEffectiveSelectedFamily()
    {
        var frame = Frame();
        var map = Map(frame: frame); map.Validate(frame);
        Assert.Equal("matcap-palette", map.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.Equal("matcap", Map(ParentMatcap(palette: null), frame: frame).CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.False(T8dTestHelpers.ValidateEndfield([ParentMatcap(couple: false), Palette.ToBinding()]).IsValid);
        Assert.Throws<ArgumentException>(() => Map(ParentMatcap(couple: false, palette: Palette), frame: frame, validate: true));
        Assert.Throws<NotSupportedException>(() => new D3D12SurfaceStyleMap(["mmd"], [map.CharacterMaterials[0]], TestStylePlugins.AllPresetCatalog).Validate(frame));
        foreach (var family in new[] { "genshin", "star-rail", "wuthering-waves" })
        {
            var wrong = Map(ParentMatcap(palette: Palette), family, frame);
            Assert.NotEqual("matcap-palette", wrong.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void EverySelectedInputMustRemainAnInRangeImageEvenWhenDormant(bool dormant)
    {
        var authored = dormant ? Palette with { EmissionStrength = 0, LightInfluence = -1 } : Palette;
        var frame = Frame();
        foreach (bool emission in new[] { false, true })
        {
            var badIndex = emission ? authored with { EmissionSlot = 5 }
                : authored with { ReceivedLightSlot = 5 };
            Assert.False(T8dTestHelpers.ValidateEndfield(ParentMatcap(palette: badIndex), StyleMaterialRole.Body, 5).IsValid);
            if (!dormant)
                Assert.Throws<ArgumentOutOfRangeException>(() => Map(ParentMatcap(palette: badIndex), frame: frame).Validate(frame));
            var badKind = Frame([Texture(), Texture(), Texture(), Texture(), Texture()]);
            var guard = D3D12MaterialBindingLayout.Capture(badKind).CaptureTexture(emission ? 3 : 4);
            var textures = Textures();
            textures[emission ? 3 : 4] = SceneTexture.ProceduralToon();
            Assert.Throws<ArgumentException>(() => D3D12MaterialBindingLayout.Capture(Frame(textures)).ValidateTexture(guard));
        }
    }

    [Fact]
    public void DetachedHistoryCapturesBoundTexturesAndIsolatesSnapshots()
    {
        var frame = Frame();
        var map = Map(frame: frame); var first = Capture(map, frame);
        var renamed = Textures();
        renamed[3] = new("renamed 3", 1, 1, renamed[3].Rgba);
        renamed[4] = new("renamed 4", 1, 1, renamed[4].Rgba);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(map, Frame(renamed))));
        foreach ((int slot, int channel) in new[] { (3, 2), (4, 0), (4, 1), (4, 2) })
        {
            var changed = Textures(); var rgba = changed[slot].Rgba.ToArray(); rgba[channel]++;
            changed[slot] = new("changed selected numerical input", 1, 1, rgba);
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(map, Frame(changed))));
        }
        // Capturing a subsequent frame never overwrites the detached prior input snapshot.
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(map, Frame())));
    }

    [Fact]
    public void DormantInputContentAndModesDoNotInvalidateHistoryButReenabledInputsDo()
    {
        var dormantPalette = Palette with { EmissionStrength = 0, LightInfluence = -2 };
        var dormant = ParentMatcap(palette: dormantPalette);
        var frame = Frame();
        var first = Capture(Map(dormant, frame: frame), frame);
        var changed = Textures(); changed[3] = Texture(1, 2, 3, 4); changed[4] = Texture(5, 6, 7, 8);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(dormant, frame: Frame(changed)), Frame(changed))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(ParentMatcap(palette: dormantPalette with { EmissionStrength = .5f }), frame: frame), frame)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(ParentMatcap(palette: dormantPalette with { LightInfluence = .5f }), frame: frame), frame)));
    }

    [Fact]
    public void DetachedHistoryRebasesSelectedTextureSlotsUsingConsumedBytes()
    {
        var textures = Textures(); textures[3] = Texture(10, 20, 30, 40); textures[4] = Texture(60, 70, 80, 90);
        var firstFrame = Frame(textures);
        var first = Capture(Map(frame: firstFrame), firstFrame);
        (textures[3], textures[4]) = (textures[4], textures[3]);
        var swappedFrame = Frame(textures);
        var rebased = ParentMatcap(palette: Palette with { EmissionSlot = 4, ReceivedLightSlot = 3 });
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(rebased, frame: swappedFrame), swappedFrame)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(frame: firstFrame), Map(rebased, frame: swappedFrame)));
    }

    [Theory, InlineData(312.5f, 32f), InlineData(-312.5f, 32f), InlineData(312.5f, -32f)]
    public void WaveDensityIncludesSignedBoundaryAndRequiresEffectiveUvChannel(float u, float density)
        => Map(ParentMatcap(palette: Palette with { Density = density }), frame: Frame(uv: new(u, -.3f))).Validate(Frame(uv: new(u, -.3f)));

    [Theory, InlineData(32.01f), InlineData(-32.01f), InlineData(float.NaN)]
    public void WaveDensityRejectsOutOfRangeValuesBeforeRecording(float density)
        => Assert.False(T8dTestHelpers.ValidateEndfield(ParentMatcap(palette: Palette with { Density = density }), StyleMaterialRole.Body, 5).IsValid);

    [Fact]
    public void EffectiveUvChannelRequiresMatchingAdditionalUvStreamForMikkGeneration()
    {
        var selected = ParentMatcap(uvChannel: 1, palette: Palette with { Density = 32 });
        var withUv1 = Frame(uv: new(1e7f, 0), additionalU: .25f);
        Map(selected, frame: withUv1).Validate(withUv1);
        var withoutUv1 = Frame(uv: new(.25f, 0));
        Assert.Throws<ArgumentException>(() => Map(selected, frame: withoutUv1).Validate(withoutUv1));
    }

    [Fact]
    public void SignedHdrAdapterInputsRemainSupportedInsideTheFp16Domain()
    {
        var signed = new EndfieldPaletteSpec(new(-2, 3, .25f),
            EmissionStrength: 2, UniformEmissionBlue: -1.5f, LightInfluence: .8f, Density: .1f);
        var hdr = ParentMatcap(baseColor: new(1.2f, .8f, .5f), highlightColor: new(2, 1, 1),
            baselineStrength: 2, palette: signed);
        Assert.True(T8dTestHelpers.ValidateEndfield(hdr, StyleMaterialRole.Body, 5).IsValid);
        Map(hdr, frame: Frame(), validate: true).Validate(Frame());
    }

    [Fact]
    public void CombinedUniformEmissionStrengthCannotOverflowTheFp16Adapter()
    {
        // Every scalar independently passes single-field checks, yet their
        // composition exceeds 60000 before conversion into the FP16 target.
        var extremePalette = new EndfieldPaletteSpec(Vector3.Zero, EmissionStrength: 32, UniformEmissionBlue: 16);
        var baseOnly = ParentMatcap(baseColor: new(16), baselineStrength: 32, palette: null);
        Assert.True(T8dTestHelpers.ValidateEndfield(baseOnly, StyleMaterialRole.Body, 5).IsValid);
        Assert.True(T8dTestHelpers.ValidateEndfield([ParentMatcap(couple: true), extremePalette.ToBinding()], StyleMaterialRole.Body, 5).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([baseOnly, extremePalette.ToBinding()], StyleMaterialRole.Body, 5).IsValid);
        Assert.Throws<ArgumentException>(() => Map(baseOnly, frame: Frame(), validate: true, extra: extremePalette.ToBinding()));
        Map(baseOnly, frame: Frame(), validate: true).Validate(Frame());
    }

    [Fact]
    public void CombinedTextureAndRimStrengthCannotOverflowTheFp16Adapter()
    {
        var extreme = ParentMatcap(baseColor: new(16), highlightColor: new(16), baselineStrength: 32, palette: Palette with { EmissionStrength = 32, LightInfluence = 1 });
        var rim = T8dTestHelpers.EndfieldRim(new(16), new(16), edgeStrength: 32, rangeStrength: 32);
        Assert.True(T8dTestHelpers.ValidateEndfield(extreme, StyleMaterialRole.Body, 5).IsValid);
        Assert.True(T8dTestHelpers.ValidateEndfield(rim, StyleMaterialRole.Body, 5).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([extreme, rim], StyleMaterialRole.Body, 5).IsValid);
        Assert.Throws<ArgumentException>(() => Map(extreme, frame: Frame(), validate: true, extra: rim));
    }

    private static SceneTexture Texture(byte r = 120, byte g = 70, byte b = 40, byte a = 230)
        => new("authored numerical image", 1, 1, [r, g, b, a]);
    private static SceneTexture[] Textures() => [Texture(), Texture(), Texture(), Texture(), Texture()];
    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding = null,
        string family = "endfield", OriginalFrameScene? frame = null, bool validate = false, params StyleMaterialBindingData[] extra)
        => T8dTestHelpers.CreateDirectSurfaceStyleMap(frame ?? Frame(), [family], [new(D3D12CharacterMaterialRole.Body)],
            [[binding ?? ParentMatcap(palette: Palette), .. extra]], validatePlugin: validate);
    private static D3D12SurfaceStyleMap Capture(D3D12SurfaceStyleMap map, OriginalFrameScene frame)
    {
        map.Validate(frame); return map.CaptureResolvedInputs(frame);
    }
    private static OriginalFrameScene Frame(SceneTexture[]? textures = null, Vector2? uv = null, float? additionalU = null)
    {
        var original = D3D12EndfieldHairBindingTests.Frame(textures: textures ?? Textures());
        if (uv is null && additionalU is null) return original;
        var old = original.Surface.Snapshot;
        var vertices = old.Vertices.ToArray();
        if (uv is { } selected) for (int i = 0; i < vertices.Length; i++) vertices[i] = vertices[i] with { Uv = selected };
        var additional = additionalU is { } u ? new SceneAdditionalUvs(1,
            Enumerable.Repeat(new SceneAdditionalUvVertex(new(u, .3f, 0, 0), default, default, default), vertices.Length).ToArray()) : null;
        var snapshot = new SceneSnapshot(vertices, old.Indices, old.Materials, old.Textures,
            world: old.World, camera: old.Camera, lightDirection: old.LightDirection, additionalUvs: additional);
        return new(new(snapshot, original.Surface.Materials, original.Surface.Profile), options: original.Options, materials: original.Materials);
    }
}
