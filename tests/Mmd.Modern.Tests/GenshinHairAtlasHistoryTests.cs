using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class GenshinHairAtlasHistoryTests
{
    [Fact,Trait("Category","Gpu")]
    public void SubmittedAtlasEditsResetBothHistoriesWhileAbortPreservesCommittedStateAndCacheIsBounded()
    {
        using var session=new D3D12FrameSession();
        string loader=Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ??throw new InvalidOperationException("A real FSR loader is required for this explicit GPU test.");
        var probe=D3D12FsrRuntimeProbe.Probe(session.Runtime.Device,loader,session.AdapterName,createContexts:false);
        Assert.Equal(D3D12FsrRuntimeStatus.QuerySucceeded,probe.Status);Assert.NotEmpty(probe.Providers);
        var temporal=new D3D12TemporalUpscalingOptions {Mode=D3D12TemporalUpscalingMode.Fsr,
            QualityPreset=D3D12FsrQualityPreset.NativeAa,LoaderPath=loader};
        var gi=new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:2,Seed:43,
            Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial);
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        frame=new(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options! with {
            BackgroundColor=Vector4.One,CameraOverride=frame.Surface.Snapshot.Camera with {VerticalFovDegrees=45}},
            frame.Materials,frame.Lighting,frame.EditorOverlay);
        var baseline=GenshinHairAtlasSurfaceTests.AtlasBinding();
        long readbacks=session.Runtime.Readbacks;
        Render(baseline,1,"FirstFrame","FirstFrame");
        Render(GenshinHairAtlasSurfaceTests.AtlasBinding(),2,"None","None");
        var edited=GenshinHairAtlasSurfaceTests.AtlasBinding(dayNight:.75f);
        Render(edited,1,"ExplicitReset","ExplicitRequest");
        Render(edited,2,"None","None");
        long submissions=session.Runtime.Submissions;
        Assert.Throws<ArgumentOutOfRangeException>(()=>session.Render(frame,96,72,gi,temporalUpscaling:temporal,
            surfaceStyles:GenshinHairAtlasSurfaceTests.Map(GenshinHairAtlasSurfaceTests.AtlasBinding(atlasSlot:4,dayNight:.75f))));
        Assert.Equal(submissions,session.Runtime.Submissions);
        Render(edited,3,"None","None");
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(frame,96,72,gi,
            (_,_)=>throw new OperationCanceledException("Abort unsubmitted atlas settings."),temporalUpscaling:temporal,
            surfaceStyles:GenshinHairAtlasSurfaceTests.Map(baseline)));
        Assert.Equal(submissions+1,session.Runtime.Submissions);Assert.False(session.Runtime.IsRecording);
        Render(edited,4,"None","AbortedFrame");Render(edited,5,"None","None");
        var warm=session.Statistics!;
        foreach(float row in new[]{2f,3f,4f})
        {
            Render(GenshinHairAtlasSurfaceTests.AtlasBinding(explicitRowId:row,dayNight:.75f),1,"ExplicitReset","ExplicitRequest");
            Assert.Equal(warm.ShaderCompilations,session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,session.Statistics.PipelineCreations);
            Assert.InRange(session.Statistics.CachedResources,0,warm.CachedResources+2);
        }
        Assert.Equal(readbacks,session.Runtime.Readbacks);

        void Render(StyleMaterialBindingData binding,ulong frames,string giReason,string aaReason)
        {
            using var output=session.Render(frame,96,72,gi,temporalUpscaling:temporal,
                surfaceStyles:GenshinHairAtlasSurfaceTests.Map(binding));
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.Equal(frames,session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(giReason,session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(aaReason,output.Statistics.TemporalUpscaling!.ResetReason);
        }
    }
}
