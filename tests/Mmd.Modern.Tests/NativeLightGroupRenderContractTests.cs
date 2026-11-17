using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeLightGroupRenderContractTests
{
    [Fact]
    public void GroupStateControlsDirectEligibilityGiTableAndRadianceBoundWithoutChangingAuthoring()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "人物补光", Intensity: 3);
        var member = NativeLightGiContractTests.Light(SceneLightKind.Directional) with { GroupId = group.Id, Intensity = 2 };
        var authoredOff = member with { Id = Guid.NewGuid(), Enabled = false, Intensity = 8 };
        var on = Frame(new([member, authoredOff], [group]));
        Assert.True(D3D12OriginalStyleRenderer.HasNativeLights(on));
        Assert.Equal(6, Assert.Single(NativeRayGiLights.From(on).Values).ColorIntensity.W);
        Assert.Equal(6, NativeRayGiLights.IrradianceBound(on));
        Assert.Equal(2, on.Lighting!.Lights[0].Intensity); Assert.True(on.Lighting.Lights[0].Enabled);
        var off = Frame(new(on.Lighting.Lights, [group with { Enabled = false }]));
        Assert.False(D3D12OriginalStyleRenderer.HasNativeLights(off));
        Assert.Empty(NativeRayGiLights.From(off).Values); Assert.Equal(0, NativeRayGiLights.IrradianceBound(off));
        var zero = Frame(new(on.Lighting.Lights, [group with { Intensity = 0 }]));
        Assert.False(D3D12OriginalStyleRenderer.HasNativeLights(zero)); Assert.Empty(NativeRayGiLights.From(zero).Values);
        // The original immutable render capture remains independently lit.
        Assert.Equal(6, Assert.Single(NativeRayGiLights.From(on).Values).ColorIntensity.W);
    }

    [Fact]
    public void GroupLightingChangesInvalidateTemporalHistoryWhileRenamingDoesNot()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "窗光", Intensity: 2);
        var light = NativeLightGiContractTests.Light(SceneLightKind.Point) with { GroupId = group.Id };
        var frame = Frame(new([light], [group]));
        var history = new D3D12TemporalHistory(); var options = new D3D12TemporalUpscalingOptions();
        history.Commit(frame, options, 0, 8);
        Assert.Equal("None", history.ResetReason(Frame(new([light], [group with { Name = "室内窗光" }])), options));
        Assert.Equal("SceneStructureChanged", history.ResetReason(Frame(new([light], [group with { Intensity = 3 }])), options));
        Assert.Equal("SceneStructureChanged", history.ResetReason(Frame(new([light], [group with { Enabled = false }])), options));
        Assert.Equal("None", history.ResetReason(frame, options));
    }

    private static OriginalFrameScene Frame(SceneLightingSnapshot lighting)
    {
        var frame = NativeLightGiContractTests.Fixture([]);
        return new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options, frame.Materials, lighting, frame.EditorOverlay);
    }
}
