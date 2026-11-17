using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12SsaaGiBudgetTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void EstimateIncludesEverySampleAndAllThreeRecordingSlots(int samples)
    {
        var estimate = D3D12SsaaGiMemoryBudget.FromResourceSizes(100, 75, samples);
        Assert.Equal(100UL, estimate.PerSampleBytes);
        Assert.Equal((ulong)(samples * 100 + 75), estimate.PerFrameBytes);
        Assert.Equal(estimate.PerFrameBytes * 3, estimate.InFlightBytes);
    }

    [Fact]
    public void BudgetLeavesHalfForOtherResourcesAndHonorsCurrentProcessUsage()
    {
        Assert.Equal(500UL, new D3D12SsaaGiMemoryBudget.Budget(1000, 0, true).AvailableBytes);
        Assert.Equal(250UL, new D3D12SsaaGiMemoryBudget.Budget(1000, 750, true).AvailableBytes);
        Assert.Equal(0UL, new D3D12SsaaGiMemoryBudget.Budget(1000, 1000, true).AvailableBytes);
        Assert.Equal(0UL, new D3D12SsaaGiMemoryBudget.Budget(1000, 1001, true).AvailableBytes);
        Assert.Equal(D3D12SsaaGiMemoryBudget.FallbackReservationBytes,
            new D3D12SsaaGiMemoryBudget.Budget(0, 0, false).AvailableBytes);
    }

    [Fact]
    public void ExactBoundaryPassesAndNextByteRejectsWithoutChangingQuality()
    {
        var budget = new D3D12SsaaGiMemoryBudget.Budget(2000, 0, true);
        D3D12SsaaGiMemoryBudget.RequireFits(new(0, 333, 1000), budget, 17, 13, 32);
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() =>
            D3D12SsaaGiMemoryBudget.RequireFits(new(0, 334, 1001), budget, 17, 13, 32));
        Assert.Contains("32 spatial samples at 17x13", error.Message);
        Assert.Contains("3 in-flight frames", error.Message);
        Assert.Contains("Quality was not changed", error.Message);
        Assert.True(error.IsPreSubmit);
        Assert.Equal(32, error.SpatialSamples);
        Assert.Equal(1001UL, error.RequiredBytes);
        Assert.Equal(1000UL, error.AvailableBytes);
    }

    [Fact]
    public void MissingResidencyInterfaceNeverMeansUnlimitedMemory()
    {
        var fallback = new D3D12SsaaGiMemoryBudget.Budget(ulong.MaxValue, 0, false);
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => D3D12SsaaGiMemoryBudget.RequireFits(
            new(0, D3D12SsaaGiMemoryBudget.FallbackReservationBytes, D3D12SsaaGiMemoryBudget.FallbackReservationBytes + 1),
            fallback, 1920, 1080, 32));
        Assert.Contains("DXGI unavailable", error.Message);
    }

    [Fact]
    public void EstimatorCannotOverflowIntoAnApparentlySmallReservation()
    {
        Assert.Throws<OverflowException>(() => D3D12SsaaGiMemoryBudget.FromResourceSizes(ulong.MaxValue, 0, 32));
        Assert.Throws<OverflowException>(() => D3D12SsaaGiMemoryBudget.FromResourceSizes(1, ulong.MaxValue / 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12SsaaGiMemoryBudget.FromResourceSizes(1, 1, 1));
    }

    [Fact, Trait("Category", "Gpu")]
    public void ActualDeviceMeasuresCommittedAlignmentAndDiagnosticsWithoutAllocatingAFrame()
    {
        using var runtime = new D3D12GpuRuntime();
        var options = new RayGiOptions(LightingMode.SoftwareRayTracing, Denoiser: RayGiDenoiser.Off);
        var raw = D3D12SsaaGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, 32, options, false, false, false);
        var denoise = D3D12SsaaGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, 32,
            options with { Denoiser = RayGiDenoiser.Spatial, Composition = RayGiComposition.ToonPalette }, false, false, false);
        Assert.True(raw.PerSampleBytes >= 6UL * 65536); // 3 textures, indirect, counter, zero upload.
        Assert.True(denoise.PerSampleBytes >= raw.PerSampleBytes + 65536);
        Assert.True(denoise.InFlightBytes > raw.InFlightBytes);
        var additionalPasses = D3D12SsaaGiMemoryBudget.Measure(runtime, 17, 13, 31, 23, 32,
            options, true, true, true);
        Assert.True(additionalPasses.PerFrameBytes > raw.PerFrameBytes);
        Assert.Equal(0, runtime.FrameId);
        Assert.False(runtime.IsRecording);
        Assert.Equal(0, runtime.LiveFrameResources);
    }

    [Theory,InlineData(false),InlineData(true),Trait("Category","Gpu")]
    public void GeneratedOriginalShadowImagesAreReservedOncePerFrameAtDriverAllocationSize(bool straightAlpha)
    {
        using var runtime=new D3D12GpuRuntime();
        var baseline=D3D12RayGiTests.Bounce();
        var shadow=new OriginalSelfShadowOptions(Matrix4x4.Identity,4096,4096,true,false,
            OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual);
        OriginalFrameScene Frame(OriginalSelfShadowOptions? selected)=>new(baseline.Surface,
            options:baseline.Options! with {SelfShadow=selected},materials:baseline.Materials);
        var plain=Frame(null);var generated=Frame(shadow);var notGenerated=Frame(shadow with {Generate=false});
        ulong mapBytes=ImageAllocation(runtime,Format.R32_Float,ResourceFlags.AllowRenderTarget,4096,4096)
            +ImageAllocation(runtime,Format.D24_UNorm_S8_UInt,ResourceFlags.AllowDepthStencil,4096,4096);
        foreach(int samples in new[]{2,32})
        {
            var before=Measure(runtime,plain,samples,straightAlpha);
            var after=Measure(runtime,generated,samples,straightAlpha);
            Assert.Equal(before.PerSampleBytes,after.PerSampleBytes);
            Assert.Equal(mapBytes,after.PerFrameBytes-before.PerFrameBytes);
            Assert.Equal(mapBytes*D3D12SsaaGiMemoryBudget.MaximumInFlightFrames,after.InFlightBytes-before.InFlightBytes);
            Assert.Equal(before,Measure(runtime,notGenerated,samples,straightAlpha));
            // The old estimate would pass this exact budget while the actual shadow set cannot.
            var budget=new D3D12SsaaGiMemoryBudget.Budget(before.InFlightBytes*2,0,true);
            D3D12SsaaGiMemoryBudget.RequireFits(before,budget,17,13,samples);
            Assert.Throws<D3D12GiMemoryBudgetExceededException>(()=>D3D12SsaaGiMemoryBudget.RequireFits(after,budget,17,13,samples));
        }
        Assert.Equal(0,runtime.FrameId);Assert.False(runtime.IsRecording);Assert.Equal(0,runtime.LiveFrameResources);
    }

    [Fact,Trait("Category","Gpu")]
    public void NativeShadowAtlasReservesOnlyThePackedContributingFaces()
    {
        using var runtime=new D3D12GpuRuntime();
        var baseline=D3D12RayGiTests.Bounce();
        NativeSceneLight Light(bool shadows)=>new(Guid.NewGuid(),"budget light",SceneLightKind.Point,
            Vector3.Zero,Quaternion.Identity,Vector3.One,CastShadows:shadows);
        OriginalFrameScene Frame(params NativeSceneLight[] lights)=>new(baseline.Surface,
            options:baseline.Options,materials:baseline.Materials,lighting:new(lights));
        var noShadow=Measure(runtime,Frame(Light(false),Light(false),Light(false),Light(false)),2);
        var caster=Light(true);
        var packed=Measure(runtime,Frame(caster,Light(false),Light(false),Light(false)),2);
        int tile=D3D12OriginalStyleRenderer.NativeShadowTileSize;
        ulong Atlas(int columns,int rows)=>ImageAllocation(runtime,Format.R32_Float,ResourceFlags.AllowRenderTarget,tile*columns,tile*rows)
            +ImageAllocation(runtime,Format.D32_Float,ResourceFlags.AllowDepthStencil,tile*columns,tile*rows);
        Assert.Equal(Atlas(3,2),packed.PerFrameBytes-noShadow.PerFrameBytes);
        Assert.Equal(noShadow.PerSampleBytes,packed.PerSampleBytes);
        var filtered=Frame(caster,Light(true) with {Enabled=false},Light(true) with {Intensity=0},Light(true) with {Color=Vector3.Zero});
        var filteredAtlas=Measure(runtime,filtered,2);
        Assert.Equal(packed,filteredAtlas);
        var allPoints=Frame(Enumerable.Range(0,32).Select(_=>Light(true)).ToArray());
        Assert.Equal(Atlas(14,14),Measure(runtime,allPoints,2).PerFrameBytes-noShadow.PerFrameBytes);
        var allSpots=Frame(Enumerable.Range(0,32).Select(_=>Light(true) with {Kind=SceneLightKind.Spot}).ToArray());
        Assert.Equal(Atlas(6,6),Measure(runtime,allSpots,2).PerFrameBytes-noShadow.PerFrameBytes);
        // A disabled caster does not create an atlas for the remaining active nonshadow light.
        Assert.Equal(noShadow,Measure(runtime,Frame(caster with {Enabled=false},Light(false)),2));
        Assert.Equal(0,runtime.FrameId);Assert.False(runtime.IsRecording);Assert.Equal(0,runtime.LiveFrameResources);
    }

    [Fact,Trait("Category","Gpu")]
    public void CompleteFrameAddsItsWhiteFallbackOnceAndSharesOriginalAndNativeShadowReservations()
    {
        using var runtime=new D3D12GpuRuntime();var baseline=D3D12RayGiTests.Bounce();
        var gi=new RayGiOptions(LightingMode.SoftwareRayTracing);
        var withoutFrame=D3D12SsaaGiMemoryBudget.Measure(runtime,17,13,17,13,2,gi,false,false,false);
        var whiteOnly=Measure(runtime,baseline,2);
        ulong white=ImageAllocation(runtime,Format.R32_Float,ResourceFlags.AllowRenderTarget,1,1);
        Assert.Equal(white,whiteOnly.PerFrameBytes-withoutFrame.PerFrameBytes);
        var shadow=new OriginalSelfShadowOptions(Matrix4x4.Identity,1024,512,true,false,
            OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual);
        var light=new NativeSceneLight(Guid.NewGuid(),"shadow caster",SceneLightKind.Directional,
            Vector3.Zero,Quaternion.Identity,Vector3.One);
        var both=new OriginalFrameScene(baseline.Surface,options:baseline.Options! with {SelfShadow=shadow},
            materials:baseline.Materials,lighting:new([light]));
        int tile=D3D12OriginalStyleRenderer.NativeShadowTileSize;
        ulong images=ImageAllocation(runtime,Format.R32_Float,ResourceFlags.AllowRenderTarget,1024,512)
            +ImageAllocation(runtime,Format.D24_UNorm_S8_UInt,ResourceFlags.AllowDepthStencil,1024,512)
            +ImageAllocation(runtime,Format.R32_Float,ResourceFlags.AllowRenderTarget,tile,tile)
            +ImageAllocation(runtime,Format.D32_Float,ResourceFlags.AllowDepthStencil,tile,tile);
        Assert.Equal(images,Measure(runtime,both,2).PerFrameBytes-whiteOnly.PerFrameBytes);
        Assert.Equal(0,runtime.FrameId);Assert.False(runtime.IsRecording);Assert.Equal(0,runtime.LiveFrameResources);
    }

    private static D3D12SsaaGiMemoryBudget.Estimate Measure(D3D12GpuRuntime runtime,OriginalFrameScene frame,int samples,bool straightAlpha=false)
        =>D3D12SsaaGiMemoryBudget.Measure(runtime,17,13,17,13,samples,new(LightingMode.SoftwareRayTracing),
            false,false,false,straightAlpha,frame);

    private static ulong ImageAllocation(D3D12GpuRuntime runtime,Format format,ResourceFlags flags,int width,int height)
        =>runtime.Device.GetResourceAllocationInfo(0,[ResourceDescription.Texture2D(format,(uint)width,(uint)height,
            mipLevels:1,flags:flags)]).SizeInBytes;

    [Fact, Trait("Category", "Gpu")]
    public void OversizedRequestRejectsBeforeBeginAndPreservesNextNormalFrame()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12RayGiTests.Bounce();
        var normal = session.Capture(frame, 47, 35);
        long frameId = session.Runtime.FrameId, submissions = session.Runtime.Submissions;
        var selected = new OriginalFrameScene(frame.Surface, frame.Outline, frame.OutlineOptions,
            frame.Options! with { SpatialSamples = 32 }, frame.Materials, frame.Lighting, frame.EditorOverlay);
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Denoiser: RayGiDenoiser.Spatial,
            Composition: RayGiComposition.ToonPalette);
        // Device measurement only; do not allocate an enormous image to exercise rejection.
        var estimate = D3D12SsaaGiMemoryBudget.Measure(session.Runtime, 4096, 4096, 4096, 4096, 32,
            gi, false, false, false);
        var budget = D3D12SsaaGiMemoryBudget.Query(session.Runtime);
        if (estimate.InFlightBytes <= budget.AvailableBytes)
        {
            // An exceptionally large future device may support this request. The same policy
            // boundary is still tested above; never launch the huge render just for this test.
            D3D12SsaaGiMemoryBudget.RequireFits(estimate, budget, 4096, 4096, 32);
        }
        else
        {
            var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => session.Render(selected, 4096, 4096, rayGiOptions: gi));
            Assert.Contains("in-flight frames", error.Message);
        }
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.Equal(normal.Rgba, session.Capture(frame, 47, 35).Rgba);
    }
}
