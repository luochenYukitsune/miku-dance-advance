using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeLightCapacityTests
{
    [Theory]
    [InlineData(SceneLightKind.Directional, 32, 6, 6)]
    [InlineData(SceneLightKind.Spot, 32, 6, 6)]
    [InlineData(SceneLightKind.Point, 192, 14, 14)]
    public void FullActiveBudgetPacksActualFacesWithoutReducingResolution(SceneLightKind kind, int faces, int columns, int rows)
    {
        var lighting = new SceneLightingSnapshot(Enumerable.Range(0, SceneLightingSnapshot.MaxLights)
            .Select(index => Light(kind) with { Enabled = index < SceneLightingSnapshot.MaxActiveLights }));
        var layout = NativeLightShadowAtlasLayout.Create(lighting);
        Assert.Equal(faces, layout.FaceCount);
        Assert.Equal(columns, layout.Columns);
        Assert.Equal(rows, layout.Rows);
        Assert.Equal(columns * 512, layout.Width);
        Assert.Equal(rows * 512, layout.Height);
        Assert.InRange(layout.Width, 1, 16384);
        Assert.InRange(layout.Height, 1, 16384);
        var occupied = Enumerable.Range(0, faces).Select(layout.TileOrigin).ToArray();
        Assert.Equal(faces, occupied.Distinct().Count());
        Assert.All(occupied, origin =>
        {
            Assert.InRange(origin.X, 0, layout.Width - 512);
            Assert.InRange(origin.Y, 0, layout.Height - 512);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.TileOrigin(faces));
    }

    [Fact]
    public void OnlyContributingShadowFacesAllocateTilesAndGroupMaskIsApplied()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "off", Enabled: false);
        NativeSceneLight[] lights = [Light(SceneLightKind.Spot), Light(SceneLightKind.Point),
            Light(SceneLightKind.Point) with { CastShadows = false },
            Light(SceneLightKind.Point) with { Enabled = false },
            Light(SceneLightKind.Point) with { Intensity = 0 },
            Light(SceneLightKind.Point) with { Color = Vector3.Zero },
            Light(SceneLightKind.Point) with { GroupId = group.Id }];
        var layout = NativeLightShadowAtlasLayout.Create(new(lights, [group]));
        Assert.Equal(7, layout.FaceCount);
        Assert.Equal((0, 0), layout.TileOrigin(0));
        Assert.Equal((512, 0), layout.TileOrigin(1));
        Assert.Equal((0, 1024), layout.TileOrigin(6));
        Assert.Equal(1536, layout.Width);
        Assert.Equal(1536, layout.Height);
        Assert.Equal(default, NativeLightShadowAtlasLayout.Create(new([lights[2]])));
    }

    [Fact]
    public void SecondaryGiTableRetainsTheLastActiveLightAndFiltersAuthoringOnlyLights()
    {
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxLights)
            .Select(index => Light((SceneLightKind)(index % 3)) with
            { Enabled = index < SceneLightingSnapshot.MaxActiveLights, Position = new(index, 1, 2) }).ToArray();
        var frame = NativeLightGiContractTests.Fixture(lights);
        var values = NativeRayGiLights.From(frame).Values;
        Assert.Equal(SceneLightingSnapshot.MaxActiveLights, values.Length);
        Assert.Equal(new Vector4(lights[31].Position, (float)lights[31].Kind), values[^1].PositionKind);
        Assert.True(NativeRayGiLights.IrradianceBound(frame) > 0);
    }

    private static NativeSceneLight Light(SceneLightKind kind) => new(Guid.NewGuid(), "capacity", kind,
        Vector3.Zero, Quaternion.Identity, Vector3.One);
}
