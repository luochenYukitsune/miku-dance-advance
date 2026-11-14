using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldPaletteStyleMapTests
{
    private static readonly D3D12EndfieldPaletteBinding Palette = new(new(.1f, .2f, .3f), .5f, .7f, .6f, .1f,
        D3D12EndfieldPaletteInputMode.Texture, 3, D3D12EndfieldPaletteInputMode.Texture, 4);
    private static readonly D3D12EndfieldMatcapBinding Parent = new(0, 1, 2,
        D3D12MatcapColorEncoding.LinearRgb, 0, new(.2f), Vector3.Zero, CoupleEndfieldPalette: true, Palette: Palette);

    [Fact]
    public void PaletteRequiresExplicitCoupledParentAndUsesOnlyItsEffectiveSelectedFamily()
    {
        var map = Map(); map.Validate(Frame());
        Assert.True(map.HasConsumedEndfieldPalette);
        Assert.False(Map(Parent with { Palette = null }).HasConsumedEndfieldPalette);
        Assert.Throws<ArgumentException>(() => Map(Parent with { CoupleEndfieldPalette = false }).Validate(Frame()));
        foreach (var family in new[] { D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx })
        {
            var wrong = Map(family: family);
            Assert.False(wrong.HasConsumedEndfieldPalette);
            Assert.Throws<NotSupportedException>(() => wrong.Validate(Frame()));
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void EverySelectedInputMustRemainAnInRangeImageEvenWhenDormant(bool dormant)
    {
        var authored = dormant ? Palette with { EmissionStrength = 0, LightInfluence = -1 } : Palette;
        foreach (bool emission in new[] { false, true })
        {
            var badIndex = emission ? authored with { EmissionCombinedTextureIndex = 5 }
                : authored with { ReceivedLightCombinedTextureIndex = 5 };
            Assert.Throws<ArgumentOutOfRangeException>(() => Map(Parent with { Palette = badIndex }).Validate(Frame()));
            var textures = Textures();
            textures[emission ? 3 : 4] = SceneTexture.ProceduralToon();
            Assert.Throws<ArgumentException>(() => Map(Parent with { Palette = authored }).Validate(Frame(textures)));
        }
    }

    [Fact]
    public void DetachedHistoryConsumesEmissionBlueAndReceivedLightRgbWithoutAlphaOrOtherEmissionChannels()
    {
        var map = Map(); var first = Capture(map, Frame());
        var harmless = Textures(); harmless[3] = Texture(1, 2, 40, 4); harmless[4] = Texture(120, 70, 40, 0);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(map, Frame(harmless))));
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
        var dormant = Parent with { Palette = Palette with { EmissionStrength = 0, LightInfluence = -2 } };
        var first = Capture(Map(dormant), Frame());
        var changed = Textures(); changed[3] = Texture(1, 2, 3, 4); changed[4] = Texture(5, 6, 7, 8);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(dormant), Frame(changed))));
        var uniform = dormant with { Palette = dormant.Palette! with {
            EmissionInputMode = D3D12EndfieldPaletteInputMode.Uniform, EmissionCombinedTextureIndex = null,
            ReceivedLightInputMode = D3D12EndfieldPaletteInputMode.Uniform, ReceivedLightCombinedTextureIndex = null,
            UniformEmissionBlue = -4, ReceivedLightLinearRgb = new(-2), LightInfluence = 0 } };
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(uniform), Frame(changed))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(dormant with { Palette = dormant.Palette! with { EmissionStrength = .5f } }), Frame())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(dormant with { Palette = dormant.Palette! with { LightInfluence = .5f } }), Frame())));
    }

    [Fact]
    public void DetachedHistoryRebasesSelectedTextureSlotsUsingConsumedChannels()
    {
        var textures = Textures(); textures[3] = Texture(10, 20, 30, 40); textures[4] = Texture(60, 70, 80, 90);
        var first = Capture(Map(), Frame(textures));
        (textures[3], textures[4]) = (textures[4], textures[3]);
        var rebased = Parent with { Palette = Palette with { EmissionCombinedTextureIndex = 4, ReceivedLightCombinedTextureIndex = 3 } };
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Capture(Map(rebased), Frame(textures))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(), Map(rebased)));
    }

    [Theory, InlineData(312.5f, 32f), InlineData(-312.5f, 32f), InlineData(312.5f, -32f)]
    public void WaveDomainIncludesItsSignedBoundary(float u, float density)
        => Map(Parent with { Palette = Palette with { Density = density } }).Validate(Frame(uv: new(u, -.3f)));

    [Theory, InlineData(312.51f, 32f), InlineData(-312.51f, 32f), InlineData(312.51f, -32f), InlineData(1e7f, 1f)]
    public void WaveDomainRejectsFiniteOutOfRangePhaseBeforeRecording(float u, float density)
        => Assert.Throws<ArgumentException>(() => Map(Parent with { Palette = Palette with { Density = density } }).Validate(Frame(uv: new(u, -.3f))));

    [Fact]
    public void WaveGuardReadsOnlyTheParentSelectedCurrentEffectiveUvAndDensityZeroSkipsPhaseLimit()
    {
        Map(Parent with { Palette = Palette with { Density = 0 } }).Validate(Frame(uv: new(1e7f, 1e7f)));
        var selected = Parent with { UvChannel = 1, Palette = Palette with { Density = 32 } };
        Map(selected).Validate(Frame(uv: new(1e7f, 0), additionalU: .25f));
        Assert.Throws<ArgumentException>(() => Map(selected).Validate(Frame(uv: new(.25f, 0), additionalU: 312.51f)));
        Assert.Throws<ArgumentException>(() => Map(selected).Validate(Frame(uv: new(.25f, 0))));
        var source = Frame(uv: new(.25f, 0), additionalU: .25f);
        var stale = selected with { Geometry = new(source.Surface.Snapshot, 0) };
        Assert.Throws<ArgumentException>(() => Map(stale).Validate(Frame(uv: new(.25f, 0), additionalU: .3f)));
    }

    [Fact]
    public void SignedHdrAdapterInputsRemainSupportedInsideTheFp16Domain()
    {
        var signed = new D3D12EndfieldPaletteBinding(new(-2, 3, .25f),
            EmissionStrength: 2, UniformEmissionBlue: -1.5f, LightInfluence: .8f, Density: .1f);
        var hdr = Parent with { BaseColor = new(1.2f, .8f, .5f), HighlightColor = new(2, 1, 1),
            BaselineStrength = 2, Palette = signed };
        Map(hdr).Validate(Frame());
    }

    [Fact]
    public void CombinedUniformEmissionStrengthCannotOverflowTheFp16Adapter()
    {
        // Every scalar independently passes its public binding range, but their
        // composition would exceed 524k before conversion into the FP16 target.
        var extreme = Parent with { BaseColor = new(16), BaselineStrength = 32,
            Palette = new(Vector3.Zero, EmissionStrength: 32, UniformEmissionBlue: 16) };
        extreme.Validate();
        Assert.Throws<ArgumentException>(() => Map(extreme).Validate(Frame()));
        Map(extreme with { Palette = null }).Validate(Frame());
    }

    [Fact]
    public void CombinedTextureAndRimStrengthCannotOverflowTheFp16Adapter()
    {
        var extreme = Parent with { BaseColor = new(16), HighlightColor = new(16), BaselineStrength = 32,
            Palette = Palette with { EmissionStrength = 32, LightInfluence = 1 } };
        var rim = new D3D12EndfieldRimBinding(extreme.BaseColor, new(16), EdgeStrength: 32, RangeStrength: 32);
        var map = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.EndfieldPmx],
            [new(EndfieldMatcap: extreme, EndfieldRim: rim)]);
        extreme.Validate(); rim.Validate();
        Assert.Throws<ArgumentException>(() => map.Validate(Frame()));
    }

    private static SceneTexture Texture(byte r = 120, byte g = 70, byte b = 40, byte a = 230)
        => new("authored numerical image", 1, 1, [r, g, b, a]);
    private static SceneTexture[] Textures() => [Texture(), Texture(), Texture(), Texture(), Texture()];
    private static D3D12SurfaceStyleMap Map(D3D12EndfieldMatcapBinding? binding = null,
        D3D12SurfaceStyle family = D3D12SurfaceStyle.EndfieldPmx)
        => new([family], [new(EndfieldMatcap: binding ?? Parent)]);
    private static D3D12SurfaceStyleMap Capture(D3D12SurfaceStyleMap map, OriginalFrameScene frame)
    {
        map.Validate(frame); return map.CaptureFaceTextures(frame);
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
