using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.D3DCompiler;
using Xunit;

namespace Mmd.Modern.Tests;

// Verified scope and execution evidence: docs/stages/2026-10-24-native-diffusion.md.
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12DiffusionGpuTests
{
    [Theory]
    [InlineData("VSMain", "vs_5_1")]
    [InlineData("PSHorizontal", "ps_5_1")]
    [InlineData("PSComposite", "ps_5_1")]
    public void NativeStagesCompile(string entry,string target)
    {
        var code=Compiler.Compile(D3D12DiffusionPass.Source,entry,"diffusion-test.hlsl",target,
            ShaderFlags.EnableStrictness|ShaderFlags.OptimizationLevel3).ToArray();
        Assert.Equal(new byte[]{68,88,66,67},code[..4]);
    }

    [Theory, Trait("Category","Gpu")]
    [InlineData(1,1,.7f,.002f)]
    [InlineData(1,17,1f,.01f)]
    [InlineData(23,1,.7f,.01f)]
    [InlineData(65,49,.7f,.01f)]
    [InlineData(129,97,1f,.002f)]
    [InlineData(65,49,.7f,0f)]
    public void BothUnormStoresMatchIndependentBilinearClampOracle(int width,int height,float strength,float extent)
    {
        using var session=new D3D12FrameSession();
        var frame=Frame();
        var baseline=session.Capture(frame,width,height);
        var actual=session.Capture(frame,width,height,diffusion:new(true,strength,extent));
        var expected=DiffusionReferenceImage.Apply(baseline.Rgba,width,height,strength,extent);
        for(int i=0;i<actual.Rgba.Length;i++)
        {
            if(i%4==3)Assert.Equal(255,actual.Rgba[i]);
            // Hardware bilinear sampler has finite subtexel precision and both real
            // UNorm8 stores round. Investigate measured residuals before changing this.
            else Assert.InRange(Math.Abs(actual.Rgba[i]-expected[i]),0,2);
        }
        Assert.Equal(baseline.DrawCalls+2,actual.DrawCalls);
        Assert.Equal(2,session.LastRecordings.Count(r=>r.Channel.StartsWith("diffusion:")));
        Assert.Equal(baseline.Rgba,session.Capture(frame,width,height).Rgba);
    }

    [Fact, Trait("Category","Gpu")]
    public void NullDisabledAndZeroStrengthAreExactBypassWithNoNewPassOrPipeline()
    {
        using var session=new D3D12FrameSession();
        var frame=Frame();var baseline=session.Capture(frame,65,49);
        // The first frame includes immutable geometry/texture staging uploads.
        // Compare bypass modes to the established warm path, not cold staging.
        Assert.Equal(baseline.Rgba,session.Capture(frame,65,49).Rgba);
        var warm=session.Statistics!;
        foreach(var options in new D3D12DiffusionOptions[]{D3D12DiffusionOptions.Off,new(false,1,.01f),new(true,0,.01f)})
        {
            Assert.Equal(baseline.Rgba,session.Capture(frame,65,49,diffusion:options).Rgba);
            Assert.Equal(warm.ShaderCompilations,session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,session.Statistics!.PipelineCreations);
            Assert.DoesNotContain(session.LastRecordings,r=>r.Channel.StartsWith("diffusion:"));
            Assert.Equal(warm.FrameOwnedAllocations,session.Statistics!.FrameOwnedAllocations);
        }
        Assert.Equal(0UL,D3D12DiffusionMemoryBudget.Measure(session.Runtime,65,49,false));
        Assert.True(D3D12DiffusionMemoryBudget.Measure(session.Runtime,65,49,true)>0);
    }

    [Fact, Trait("Category","Gpu")]
    public void SlidersStayWarmAndGpuOnlyOutputDoesNotReadBackOrAddSubmissions()
    {
        using var session=new D3D12FrameSession();var frame=Frame();
        session.Capture(frame,65,49,diffusion:new(true));var warm=session.Statistics!;
        long reads=session.Runtime.Readbacks;
        for(int i=0;i<7;i++)
        {
            using var output=session.Render(frame,65,49,diffusion:new(true,.1f+i*.1f,i*.001f));
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.Equal(1,output.Statistics.SubmissionCount);
            Assert.Equal(warm.ShaderCompilations,output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,output.Statistics.PipelineCreations);
            Assert.Equal(warm.CachedResources,output.Statistics.CachedResources);
            Assert.InRange(output.Statistics.PendingFrames,0,3);
        }
        Assert.Equal(reads,session.Runtime.Readbacks);
        session.Capture(frame,79,53,diffusion:new(true));
        Assert.Equal(warm.PipelineCreations,session.Statistics!.PipelineCreations);
    }

    [Fact, Trait("Category","Gpu")]
    public void BadAlphaOrDisabledInvalidScalarFailsBeforeRecordingAndGoodFrameStillWorks()
    {
        using var session=new D3D12FrameSession();var frame=Frame();
        var baseline=session.Capture(frame,65,49);long submissions=session.Runtime.Submissions;
        var transparent=new OriginalFrameScene(frame.Surface,options:frame.Options! with{BackgroundColor=new(0,0,0,0)});
        Assert.Throws<NotSupportedException>(()=>session.Capture(transparent,65,49,diffusion:new(true)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>session.Capture(frame,65,49,diffusion:new(false,float.NaN)));
        Assert.Equal(submissions,session.Runtime.Submissions);
        Assert.Equal(baseline.Rgba,session.Capture(frame,65,49).Rgba);
    }

    [Fact, Trait("Category","Gpu")]
    public void SmaaAndSsaaResolveBeforeOneDisplaySizedFilter()
    {
        using var session=new D3D12FrameSession();var source=Frame();
        var frame=new OriginalFrameScene(source.Surface,options:source.Options! with
            {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=2});
        var baseline=session.Capture(frame,65,49);
        var actual=session.Capture(frame,65,49,diffusion:new(true,.7f,.01f));
        var expected=DiffusionReferenceImage.Apply(baseline.Rgba,65,49,.7,.01);
        for(int i=0;i<actual.Rgba.Length;i++)Assert.InRange(Math.Abs(actual.Rgba[i]-expected[i]),0,2);
        Assert.Equal(2,session.LastRecordings.Count(r=>r.Channel.StartsWith("diffusion:")));
        Assert.All(session.LastTargets.Where(t=>t.Name.StartsWith("$diffusion-")),t=>
            {Assert.Equal(65,t.Width);Assert.Equal(49,t.Height);});
    }

    private static OriginalFrameScene Frame()
    {
        var pixels=new byte[8*8*4];
        for(int y=0;y<8;y++)for(int x=0;x<8;x++)
        {int p=(y*8+x)*4;pixels[p]=(byte)(x*35);pixels[p+1]=(byte)(y*35);pixels[p+2]=(byte)((x+y)%2==0?240:20);pixels[p+3]=255;}
        SceneVertex[] vertices=[new(new(-1.5f,-1.5f,0),-Vector3.UnitZ,new(0,1)),
            new(new(1.5f,-1.5f,0),-Vector3.UnitZ,new(1,1)),new(new(1.5f,1.5f,0),-Vector3.UnitZ,new(1,0)),
            new(new(-1.5f,1.5f,0),-Vector3.UnitZ,new(0,0))];
        var scene=new SceneSnapshot(vertices,[0,2,1,0,3,2],
            [new("explicit filter witness",0,6,Vector4.One,Vector3.Zero,Vector3.Zero,1,0)],
            [new("authored test pattern",8,8,pixels)],camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,30),
            lightDirection:-Vector3.UnitZ);
        return new(new(scene,[new(Vector3.One)],new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false));
    }
}
