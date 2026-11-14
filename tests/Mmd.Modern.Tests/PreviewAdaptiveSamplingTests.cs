using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PreviewAdaptiveSamplingTests
{
    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 2, 1 })]
    [InlineData(4, new[] { 4, 2, 1 })]
    [InlineData(8, new[] { 8, 4, 2, 1 })]
    [InlineData(16, new[] { 16, 8, 4, 2, 1 })]
    [InlineData(32, new[] { 32, 16, 8, 4, 2, 1 })]
    public void BudgetRejectionsVisitOnlySupportedLowerSampleCounts(int requested, int[] expected)
    {
        var planner = new D3D12PreviewAdaptiveSampling();
        planner.Prepare(1400, 621, new(SpatialSamples: requested), Scene(requested));
        var observed = new List<int> { planner.SpatialSamples };
        while (planner.Reduce("GI reservation exceeds available memory.")) observed.Add(planner.SpatialSamples);
        Assert.Equal(expected, observed);
        Assert.Equal(1, planner.SpatialSamples);
    }

    [Fact]
    public void PreviewCopiesOnlySpatialSamplesAndPreservesRequestedAndExportQuality()
    {
        var quality = new ViewQualityOptions(Gi: new(LightingMode.HardwareRayTracing, SamplesPerPixel: 16,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial),
            Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 16);
        var request = new ViewRenderRequest(1400, 621, quality);
        var original = Scene(16);
        var planner = new D3D12PreviewAdaptiveSampling();
        planner.Prepare(request.Width, request.Height, quality, original);
        Assert.Same(original, planner.ApplyTo(original));
        Assert.True(planner.Reduce("budget"));
        Assert.True(planner.Reduce("budget"));
        var adapted = planner.ApplyTo(original);
        Assert.Equal(4, adapted.Options!.SpatialSamples);
        Assert.Equal(original.Options! with { SpatialSamples = 4 }, adapted.Options);
        Assert.Same(original.Surface, adapted.Surface);
        Assert.Same(original.Outline, adapted.Outline);
        Assert.Same(original.Lighting, adapted.Lighting);
        Assert.Same(original.EditorOverlay, adapted.EditorOverlay);
        Assert.Equal(16, original.Options.SpatialSamples);
        Assert.Equal(16, quality.SpatialSamples);
        Assert.Same(quality, request.Quality);
        Assert.Equal(1400, request.Width);
        Assert.Equal(621, request.Height);
    }

    [Fact]
    public void AcceptedChoiceSurvivesCameraAndPoseFramesButQualityAndSizeReattemptRequested()
    {
        var planner = new D3D12PreviewAdaptiveSampling();
        var quality = new ViewQualityOptions(SpatialSamples: 16);
        var original = Scene(16);
        planner.Prepare(1400, 621, quality, original);
        planner.Reduce("budget"); planner.Reduce("budget");
        planner.CommitPresentation();
        var cameraFrame = Scene(16, snapshot: original.Surface.Snapshot.WithCamera(SceneCamera.Default with { Eye = new(2, 9, -30) }));
        planner.Prepare(1400, 621, quality with { }, cameraFrame);
        Assert.Equal(4, planner.SpatialSamples);
        planner.Prepare(1400, 621, quality with { SmaaQuality = SmaaQuality.Ultra }, cameraFrame);
        Assert.Equal(16, planner.SpatialSamples);
        planner.Reduce("budget");
        planner.Prepare(1401, 621, quality with { SmaaQuality = SmaaQuality.Ultra }, cameraFrame);
        Assert.Equal(16, planner.SpatialSamples);
    }

    [Fact]
    public void ResourceChangesAndExplicitResetReattemptRequestedWithoutChangingLastPresentation()
    {
        var planner = new D3D12PreviewAdaptiveSampling();
        var quality = new ViewQualityOptions(SpatialSamples: 8);
        planner.Prepare(1400, 621, quality, Scene(8));
        planner.Reduce("first rejection"); planner.CommitPresentation();
        Assert.Equal(8, planner.LastRequestedSpatialSamples);
        Assert.Equal(4, planner.LastEffectiveSpatialSamples);
        Assert.Equal("first rejection", planner.LastAdjustmentReason);
        var light = new NativeSceneLight(Guid.NewGuid(), "light", SceneLightKind.Point, Vector3.Zero, Quaternion.Identity, Vector3.One);
        planner.Prepare(1400, 621, quality, Scene(8, lighting: new([light])));
        Assert.Equal(8, planner.SpatialSamples);
        Assert.Equal(4, planner.LastEffectiveSpatialSamples);
        planner.Reduce("next rejection");
        planner.Reset();
        planner.Prepare(1400, 621, quality, Scene(8, lighting: new([light])));
        Assert.Equal(8, planner.SpatialSamples);
        planner.CommitPresentation();
        Assert.Equal(8, planner.LastEffectiveSpatialSamples);
        Assert.Null(planner.LastAdjustmentReason);
    }

    [Fact]
    public void TextureAndSurfaceResourceChangesResetCacheButEquivalentMapsDoNot()
    {
        var planner = new D3D12PreviewAdaptiveSampling();
        var quality = new ViewQualityOptions(SpatialSamples: 16);
        var first = Triangle(new("first", 1, 1, new byte[] { 255, 255, 255, 255 }));
        var map = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.Mmd]);
        planner.Prepare(1400, 621, quality, Scene(16, snapshot: first), map);
        planner.Reduce("budget");
        planner.Prepare(1400, 621, quality, Scene(16, snapshot: first.WithCamera(SceneCamera.Default)),
            new D3D12SurfaceStyleMap([D3D12SurfaceStyle.Mmd]));
        Assert.Equal(8, planner.SpatialSamples);
        var second = Scene(16, snapshot: Triangle(new("second", 1, 1, new byte[] { 255, 255, 255, 255 })));
        planner.Prepare(1400, 621, quality, second, map);
        Assert.Equal(16, planner.SpatialSamples);
        planner.Reduce("budget");
        planner.Prepare(1400, 621, quality, second,
            new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx]));
        Assert.Equal(16, planner.SpatialSamples);
    }

    [Fact]
    public void AnimatedVertexContentsReuseAdmissionWhileTopologyChangesRestoreRequested()
    {
        var planner = new D3D12PreviewAdaptiveSampling();
        var quality = new ViewQualityOptions(SpatialSamples: 8);
        var geometry = Triangle(new("image", 1, 1, new byte[] { 255, 255, 255, 255 }));
        var firstPose = geometry.WithDeformedVertices(geometry.Vertices.ToArray());
        planner.Prepare(1400, 621, quality, Scene(8, snapshot: firstPose));
        planner.Reduce("budget");
        var moved = geometry.Vertices.ToArray();
        moved[0] = moved[0] with { Position = new(-2, 0, 0) };
        planner.Prepare(1400, 621, quality, Scene(8, snapshot: geometry.WithDeformedVertices(moved)));
        Assert.Equal(4, planner.SpatialSamples);
        planner.Prepare(1400, 621, quality, Scene(8));
        Assert.Equal(8, planner.SpatialSamples);
    }

    [Fact]
    public void InheritedSamplesAreResolvedWithoutIntroducingAnAuthoredOverride()
    {
        var planner = new D3D12PreviewAdaptiveSampling();
        var quality = new ViewQualityOptions();
        planner.Prepare(1400, 621, quality, Scene(4));
        planner.Reduce("budget"); planner.CommitPresentation();
        Assert.Equal(4, planner.LastRequestedSpatialSamples);
        Assert.Equal(2, planner.LastEffectiveSpatialSamples);
        Assert.Null(quality.SpatialSamples);
    }

    private static OriginalFrameScene Scene(int samples, SceneSnapshot? snapshot = null, SceneLightingSnapshot? lighting = null)
    {
        snapshot ??= SceneSnapshot.CreateEmpty();
        return new(new(snapshot, Enumerable.Repeat(new OriginalStyleMaterialBinding(Vector3.Zero), snapshot.Materials.Count)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, Antialiasing: FrameAntialiasing.Smaa1X,
                SmaaQuality: SmaaQuality.Ultra, SpatialSamples: samples), lighting: lighting);
    }

    private static SceneSnapshot Triangle(SceneTexture texture) => new(
        [new(new(-1, 0, 0), Vector3.UnitZ, Vector2.Zero), new(new(1, 0, 0), Vector3.UnitZ, Vector2.UnitX),
            new(new(0, 1, 0), Vector3.UnitZ, Vector2.UnitY)], [0u, 1u, 2u],
        [new("triangle", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0, DiffuseTexture: 0)],
        [texture], Matrix4x4.Identity, SceneCamera.Default, Vector3.UnitY);
}
