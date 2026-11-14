using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterDirectLightPaletteHistoryTests
{
    [Fact]
    public void DirectPaletteChangesInvalidateColorHistoryButEqualDetachedMetadataReusesIt()
    {
        var source = D3D12RayGiTests.Bounce();
        var history = new D3D12TemporalHistory();
        var options = new D3D12TemporalUpscalingOptions();
        history.Commit(source, options, 0, 8);
        var selected = Copy(source, true);

        Assert.Equal("SceneStructureChanged", history.ResetReason(selected, options));
        history.Commit(selected, options, 0, 8);
        Assert.Equal("None", history.ResetReason(Copy(selected, true), options));
        Assert.Equal("SceneStructureChanged", history.ResetReason(Copy(selected, false), options));
    }

    private static OriginalFrameScene Copy(OriginalFrameScene frame, bool selected) => new(frame.Surface, frame.Outline,
        frame.OutlineOptions, frame.Options, frame.Materials.Select(flags => flags with
        { PreserveDirectLightPalette = selected }), frame.Lighting, frame.EditorOverlay);
}
