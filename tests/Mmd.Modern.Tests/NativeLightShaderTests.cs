using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeLightShaderTests
{
    [Theory]
    [InlineData("VSNativeDepth", true)]
    [InlineData("PSNativeDepth", false)]
    [InlineData("PSSurfaceNative", false)]
    [InlineData("PSSurfaceGiNative", false)]
    public void NativeLightVariantsCompileWithoutChangingTheAcceptedOffSource(string entry, bool vertex)
    {
        var before = OriginalFrameShaders.Compile("PSSurface", false);
        var compiled = OriginalFrameShaders.CompileNative(entry, vertex);
        Assert.Equal("DXBC"u8.ToArray(), compiled[..4]);
        Assert.Equal(before, OriginalFrameShaders.Compile("PSSurface", false));
    }
}
