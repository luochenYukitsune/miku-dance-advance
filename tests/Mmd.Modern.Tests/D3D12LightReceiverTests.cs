using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeLightReceiverContractTests
{
    [Fact]
    public void SecondaryReceiverMasksBelongToTheExactMaterialOwnerWithoutChangingGeometryAbi()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var light = NativeLightGiContractTests.Light(SceneLightKind.Point) with { Receivers = new(SceneLightReceiverMode.Include, [a]) };
        var template = NativeLightGiContractTests.Fixture([light]);
        var first = WithOwners(template, a, b); var swapped = WithOwners(template, b, a);
        var table = NativeRayGiLights.From(first); var changed = NativeRayGiLights.From(swapped);
        Assert.Same(table, NativeRayGiLights.From(first)); Assert.NotSame(table, changed);
        Assert.Single(table.Values); Assert.True(table.HasReceiverMasks); Assert.Equal(3, table.UploadRows.Length);
        Assert.Equal(1u, BitConverter.SingleToUInt32Bits(table.UploadRows[1].PositionKind.X));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(table.UploadRows[2].PositionKind.X));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(changed.UploadRows[1].PositionKind.X));
        Assert.Equal(RayGiScene.FromOriginal(first).GeometryIdentity, RayGiScene.FromOriginal(swapped).GeometryIdentity);
        Assert.Equal(RayGiScene.FromOriginal(first).MaterialIdentity, RayGiScene.FromOriginal(swapped).MaterialIdentity);
    }
    internal static OriginalFrameScene WithOwners(OriginalFrameScene frame, Guid a, Guid b, SceneLightingSnapshot? lighting = null) =>
        new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options, frame.Materials.Select((flags, index) => flags with
        { LightReceiverObjectId = index == 0 ? a : b }), lighting ?? frame.Lighting);
}

[Trait("Category", "Gpu")]
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12LightReceiverTests
{
    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void IncludeExcludeAndThirtySecondMaskBitSelectTheRealRasterReceiver(string family)
    {
        using var session = new D3D12FrameSession(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var original = D3D12NativeLightTests.Fixture();
        // Leave real palette contrast for the opt-in styles' fill light to spend. A fully
        // lit primary toon band intentionally receives no extra diffuse in those styles.
        var dark = new OriginalFrameScene(new(original.Surface.Snapshot.WithLighting(Vector3.UnitZ),
            original.Surface.Materials, original.Surface.Profile), options: original.Options, materials: original.Materials);
        var frame = NativeLightReceiverContractTests.WithOwners(dark, a, b);
        var styles = new D3D12SurfaceStyleMap([family, "mmd"], null, TestStylePlugins.AllPresetCatalog);
        var light = D3D12NativeLightTests.Light(SceneLightKind.Point) with { CastShadows = false };
        OriginalFrameScene With(params NativeSceneLight[] lights) => NativeLightReceiverContractTests.WithOwners(frame, a, b, new(lights));
        var baseline = session.Capture(With(), 129, 97, surfaceStyles: styles);
        var all = session.Capture(With(light), 129, 97, surfaceStyles: styles);
        Assert.NotEqual(baseline.Rgba, all.Rgba);
        Assert.Equal(all.Rgba, session.Capture(With(light with { Receivers = new(SceneLightReceiverMode.Include, [a]) }), 129, 97, surfaceStyles: styles).Rgba);
        Assert.Equal(baseline.Rgba, session.Capture(With(light with { Receivers = new(SceneLightReceiverMode.Exclude, [a]) }), 129, 97, surfaceStyles: styles).Rgba);
        var many = Enumerable.Range(0, 32).Select(index => light with { Id = Guid.NewGuid(),
            Receivers = new(SceneLightReceiverMode.Include, [index == 31 ? a : b]) }).ToArray();
        Assert.Equal(all.Rgba, session.Capture(With(many), 129, 97, surfaceStyles: styles).Rgba);
        Assert.Equal(32, session.NativeLightStatistics!.EnabledLights);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void FilteringTheSecondaryBounceModelRemovesItsNativeLightContribution(LightingMode mode)
    {
        using var session = new D3D12FrameSession(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var light = NativeLightGiContractTests.Light(SceneLightKind.Point);
        var source = NativeLightGiContractTests.Fixture([light]); var gi = NativeLightGiContractTests.Gi(mode);
        OriginalFrameScene With(SceneLightReceivers? receivers) => NativeLightReceiverContractTests.WithOwners(source, a, b, new([light with { Receivers = receivers }]));
        var baseline = session.Capture(NativeLightReceiverContractTests.WithOwners(source, a, b, SceneLightingSnapshot.Empty), 79, 59, rayGiOptions: gi);
        var secondaryOnly = session.Capture(With(new(SceneLightReceiverMode.Include, [b])), 79, 59, rayGiOptions: gi);
        Assert.Equal(1, session.RayGiStatistics!.SecondaryNativeLights);
        int center = (29 * 79 + 39) * 4;
        Assert.True(secondaryOnly.Rgba[center] > baseline.Rgba[center] + 5, "The primary receiver cannot see this light directly; the selected secondary wall supplies the red bounce.");
        var excluded = session.Capture(With(new(SceneLightReceiverMode.Include, [])), 79, 59, rayGiOptions: gi);
        Assert.Equal(baseline.Rgba, excluded.Rgba);
        Assert.Equal(secondaryOnly.Rgba, session.Capture(With(new(SceneLightReceiverMode.Exclude, [a])), 79, 59, rayGiOptions: gi).Rgba);
        var many = Enumerable.Range(0, 32).Select(index => light with { Id = Guid.NewGuid(),
            Receivers = new(SceneLightReceiverMode.Include, [index == 31 ? b : a]) }).ToArray();
        Assert.Equal(secondaryOnly.Rgba, session.Capture(NativeLightReceiverContractTests.WithOwners(source, a, b, new(many)),
            79, 59, rayGiOptions: gi).Rgba);
        Assert.Equal(32, session.RayGiStatistics!.SecondaryNativeLights);
    }

    [Fact]
    public void ChangingReceiverSelectionResetsGiHistoryAndMatchesFreshInput()
    {
        using var session = new D3D12FrameSession(); using var fresh = new D3D12FrameSession();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var light = NativeLightGiContractTests.Light(SceneLightKind.Point);
        var source = NativeLightGiContractTests.Fixture([light]);
        var gi = NativeLightGiContractTests.Gi(LightingMode.SoftwareRayTracing) with { Denoiser = RayGiDenoiser.TemporalSpatial };
        var before = NativeLightReceiverContractTests.WithOwners(source, a, b, new([light with { Receivers = new(SceneLightReceiverMode.Include, [b]) }]));
        session.Capture(before, 79, 59, rayGiOptions: gi); session.Capture(before, 79, 59, rayGiOptions: gi);
        Assert.Equal(2UL, session.RayGiStatistics!.TemporalHistoryFrames);
        var changed = NativeLightReceiverContractTests.WithOwners(source, a, b, new([light with { Receivers = new(SceneLightReceiverMode.Include, []) }]));
        var result = session.Capture(changed, 79, 59, rayGiOptions: gi);
        Assert.True(session.RayGiStatistics.TemporalResetHistory); Assert.Equal("LightingChanged", session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal(fresh.Capture(changed, 79, 59, rayGiOptions: gi).Rgba, result.Rgba);
    }
}
