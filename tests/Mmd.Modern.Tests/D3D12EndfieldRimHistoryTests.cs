using Mmd.Modern.Core;
using System.Numerics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;
using static Mmd.Modern.Tests.D3D12EndfieldRimBindingTests;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldRimHistoryTests
{
    [Fact,Trait("Category","Gpu")]
    public void SubmittedRimChangesResetBothHistoriesAndAbortedEditsNeverCommitMaterialKeys()
    {
        using var session=new D3D12FrameSession();
        var frame=D3D12EndfieldRimSurfaceTests.Frame(Matrix4x4.Identity,
            new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2.6f,.1f,30,30));
        frame=new(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options! with {BackgroundColor=Vector4.One},
            frame.Materials,frame.Lighting,frame.EditorOverlay);
        string loader=Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ??throw new InvalidOperationException("Real FSR loader required for NativeAA history acceptance.");
        var temporal=new D3D12TemporalUpscalingOptions{Mode=D3D12TemporalUpscalingMode.Fsr,
            QualityPreset=D3D12FsrQualityPreset.NativeAa,LoaderPath=loader};
        var gi=new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:4,Seed:31,
            Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial);
        var map=Map(Binding);long reads=session.Runtime.Readbacks;
        Render(map,1,"FirstFrame","FirstFrame");Render(Map(Binding with{}),2,"None","None");
        var changed=Map(Binding with{DirectionRadians=.6f,RangeStrength=-.4f});
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(frame,96,72,gi,
            (_,_)=>throw new OperationCanceledException("Abort explicit rim before submit."),
            temporalUpscaling:temporal,surfaceStyles:changed));
        Assert.False(session.Runtime.IsRecording);
        Render(map,3,"None","AbortedFrame");Render(changed,1,"ExplicitReset","ExplicitRequest");
        Render(Map(Binding with{DirectionRadians=.6f,RangeStrength=-.4f}),2,"None","None");
        Render(Map(null),1,"ExplicitReset","ExplicitRequest");
        Render(Map(Binding with{EdgeStrength=0}),2,"None","None");
        Assert.Equal(reads,session.Runtime.Readbacks);

        void Render(D3D12SurfaceStyleMap styles,ulong count,string giReason,string aaReason)
        {
            using var result=session.Render(frame,96,72,gi,temporalUpscaling:temporal,surfaceStyles:styles);
            Assert.Equal(count,session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(giReason,session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(aaReason,result.Statistics.TemporalUpscaling!.ResetReason);
        }
    }
}
