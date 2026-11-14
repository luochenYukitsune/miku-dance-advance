using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterDirectLightPaletteShaderTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    public void DirectPaletteCompilesASeparateConsumedNativeVariantWhileLegacyBytecodeStaysExact(
        bool gi, bool morph, bool subTexture, bool characterToon)
    {
        string entry = gi ? "PSSurfaceGiNative" : "PSSurfaceNative";
        var before = Legacy();
        var selected = OriginalFrameShaders.CompileCharacterDirectPalette(entry, gi, morph, subTexture, characterToon);

        Assert.True(selected.Length > 32);
        Assert.Equal("DXBC"u8.ToArray(), selected[..4]);
        Assert.False(before.AsSpan().SequenceEqual(selected), "Opt-in direct palette must change the consumed pixel program.");
        Assert.Equal(before, Legacy());

        byte[] Legacy() => characterToon ? OriginalFrameShaders.CompileCharacterToon(entry, true, gi, morph, subTexture)
            : subTexture ? OriginalFrameShaders.CompileSubTexture(entry, false, true, gi)
            : morph ? OriginalFrameShaders.CompileTextureMorph(entry, true, gi)
            : OriginalFrameShaders.CompileNative(entry, false);
    }
}
