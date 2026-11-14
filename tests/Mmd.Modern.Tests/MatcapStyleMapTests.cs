using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MatcapStyleMapTests
{
    private static readonly D3D12EndfieldMatcapBinding Binding = new(0, 1, 2,
        D3D12MatcapColorEncoding.SrgbRgb, 0, new(.2f), Vector3.Zero);
    private static SceneTexture Texture(byte r = 120, byte g = 70, byte b = 40, byte a = 230)
        => new("authored", 1, 1, [r, g, b, a]);
    private static OriginalFrameScene Frame(SceneTexture[]? textures = null, Matrix4x4? world = null)
        => D3D12EndfieldHairBindingTests.Frame(textures: textures ?? [Texture(), Texture(), Texture()], world: world);
    private static D3D12SurfaceStyleMap Map(D3D12EndfieldMatcapBinding? binding,
        D3D12SurfaceStyle family = D3D12SurfaceStyle.EndfieldPmx)
        => new([family], [new(EndfieldMatcap: binding)]);

    [Fact]
    public void FullPackedNormalBindingRequiresItsExplicitFamilyTexturesAndEffectiveUv()
    {
        var frame = Frame(); var map = Map(Binding);
        map.Validate(frame);
        Assert.True(map.HasConsumedEndfieldMatcap);
        Assert.Same(Binding, map.EffectiveEndfieldMatcap(0));
        Assert.Equal(frame.Surface.Snapshot.Materials[0].IndexCount, map.EndfieldMatcapGeometry(0, frame).Corners.Length);
        foreach (var family in new[] { D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx })
            Assert.Throws<NotSupportedException>(() => Map(Binding, family).Validate(frame));
        foreach (var bad in new[] { Binding with { NormalCombinedTextureIndex = 3 }, Binding with { PCombinedTextureIndex = 3 }, Binding with { MatcapCombinedTextureIndex = 3 } })
            Assert.Throws<ArgumentOutOfRangeException>(() => Map(bad).Validate(frame));
        Assert.Throws<ArgumentException>(() => Map(Binding with { UvChannel = 1 }).Validate(frame));
        Assert.False(Map(null).HasConsumedEndfieldMatcap);
    }

    [Fact]
    public void CoupledPaletteRequiresExplicitSharedColorsAndExactlyOneDynamicRadialStrength()
    {
        var matcap = Binding with { CoupleEndfieldPalette = true };
        var hair = new D3D12EndfieldHairBinding(1, Binding.BaseColor);
        var rim = new D3D12EndfieldRimBinding(Binding.BaseColor, new(.3f), EdgeStrength: .4f);
        var style = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            EndfieldHair: hair, EndfieldRim: rim, EndfieldMatcap: matcap);
        style.Validate();
        foreach (var bad in new[] { style with { EndfieldHair = hair with { PaletteColor = new(.5f) } },
            style with { EndfieldRim = rim with { BaseColor = new(.5f) } },
            style with { EndfieldMatcap = matcap with { BaselineStrength = 2 } },
            style with { EndfieldRim = rim with { RadialStrength = .2f }, EndfieldMatcap = matcap with { BaselineStrength = 1.2f } } })
            Assert.Throws<ArgumentException>(bad.Validate);
        (style with { EndfieldHair = null, EndfieldRim = rim with { RadialStrength = .2f }, EndfieldMatcap = matcap with { BaselineStrength = 1.2f } }).Validate();
        foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(role, EndfieldMatcap: Binding).Validate());
        Assert.Throws<ArgumentException>(() => new D3D12CharacterMaterialStyle(EndfieldMatcap: Binding, PmxMaterialTreatment: new()).Validate());
    }

    [Fact]
    public void SameTopologyWrongObjectPoseOrUvIsRejectedBeforeRecording()
    {
        var frame = Frame();
        var moved = Frame(world: Matrix4x4.CreateTranslation(.1f, 0, 0));
        Assert.Throws<ArgumentException>(() => Map(Binding with { Geometry = new(moved.Surface.Snapshot, 0) }).Validate(frame));
        var snapshot = frame.Surface.Snapshot;
        var changed = snapshot.Vertices.ToArray(); changed[0] = changed[0] with { Uv = changed[0].Uv + new Vector2(.01f, 0) };
        var changedUvs = new SceneSnapshot(changed, snapshot.Indices, snapshot.Materials, snapshot.Textures, snapshot.World, snapshot.Camera);
        Assert.Throws<ArgumentException>(() => Map(Binding with { Geometry = new(changedUvs, 0) }).Validate(frame));
        var normalVertices = snapshot.Vertices.ToArray();
        normalVertices[0] = normalVertices[0] with { Normal = Vector3.Normalize(new Vector3(.3f, 0, -1)) };
        var normalSource = new SceneSnapshot(normalVertices, snapshot.Indices, snapshot.Materials, snapshot.Textures, snapshot.World, snapshot.Camera);
        Assert.Throws<ArgumentException>(() => Map(Binding with { Geometry = new(normalSource, 0) }).Validate(frame));
        Map(Binding with { Geometry = new(snapshot, 0) }).Validate(frame);
    }

    [Fact]
    public void DetachedHistoryConsumesOnlyNrgPraAndColorRgbaAndRebasesActualSlots()
    {
        var map = Map(Binding); var first = map.CaptureFaceTextures(Frame());
        Assert.NotSame(map, first);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame())));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame([Texture(b: 255, a: 0), Texture(g: 255, b: 255), Texture()]))));
        foreach (var changed in new[] { new[] { Texture(r: 1), Texture(), Texture() }, new[] { Texture(g: 1), Texture(), Texture() },
            new[] { Texture(), Texture(r: 1), Texture() }, new[] { Texture(), Texture(a: 1), Texture() },
            new[] { Texture(), Texture(), Texture(r: 1) }, new[] { Texture(), Texture(), Texture(g: 1) },
            new[] { Texture(), Texture(), Texture(b: 1) }, new[] { Texture(), Texture(), Texture(a: 1) } })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame(changed))));
        var rebased = Map(Binding with { NormalCombinedTextureIndex = 2, PCombinedTextureIndex = 0, MatcapCombinedTextureIndex = 1 })
            .CaptureFaceTextures(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureFaceTextures(Frame())));
    }

    [Fact]
    public void HistoryUsesConsumedAppearanceAndIgnoresPureWorldTranslationOrDescriptorIdentity()
    {
        var frame = Frame(); var snapshot = frame.Surface.Snapshot;
        var first = Map(Binding with { Geometry = new(snapshot, 0) }).CaptureFaceTextures(frame);
        var copy = Map(Binding with { Geometry = new(snapshot, 0) }).CaptureFaceTextures(frame);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, copy));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(Binding).CaptureFaceTextures(Frame(world: Matrix4x4.CreateTranslation(1, 2, 3)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(Binding).CaptureFaceTextures(Frame(world: Matrix4x4.CreateRotationZ(.3f)))));
        foreach (var changed in new[] { Binding with { NormalStrength = 1 }, Binding with { RotationRadians = 1 },
            Binding with { HighlightColor = Vector3.One }, Binding with { HighlightCoefficient = 1 },
            Binding with { ShadowHardness = .8f }, Binding with { BaselineStrength = 2 },
            Binding with { MatcapEncoding = D3D12MatcapColorEncoding.LinearRgb }, Binding with { LightControlDirection = Vector3.UnitX } })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Binding), Map(changed)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Binding), Map(null)));
    }
}
