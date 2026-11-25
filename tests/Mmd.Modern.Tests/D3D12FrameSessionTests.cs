using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using System.Numerics;
using System.Reflection;
using Vortice.Direct3D12;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class D3D12FrameSessionContractTests
{
    [Fact] public void PersistentSessionExposesOpaqueGpuOutputAndCapture()
    {
        var assembly=typeof(D3D12FrameSession).Assembly;
        var session=assembly.GetType("Mmd.Modern.Rendering.D3D12.D3D12FrameSession");
        Assert.NotNull(session);
        Assert.Contains(session!.GetMethods(),m=>m.Name=="Capture");
        var output=assembly.GetType("Mmd.Modern.Rendering.D3D12.D3D12FrameOutput");
        Assert.NotNull(output);
        Assert.DoesNotContain(output!.GetProperties(),p=>p.PropertyType.Name.StartsWith("ID3D12",StringComparison.Ordinal));
    }
}

[Trait("Category","Gpu")]
public sealed class D3D12FrameSessionTests
{
    [Theory][InlineData(257,191)][InlineData(961,181)]
    public void CompleteFrameCaptureMatchesSurfaceEntryAndOnlyFinalReadback(int width,int height)
    {
        var scene=D3D12OriginalFrameTests.Shadow(true);
        using var expected=new D3D12OriginalStyleRenderer();
        var baseline=expected.Render(scene,width,height);
        using var session=new D3D12FrameSession();
        var actual=session.Capture(scene,width,height);
        Assert.Equal(baseline.Rgba,actual.Rgba);
        Assert.Equal(1,session.Statistics!.SubmissionCount);
        Assert.Equal(1,session.Statistics.ReadbackCount);
        Assert.Single(session.LastRecordings.Select(r=>r.DeviceIdentity).Distinct());
        Assert.Single(session.LastRecordings.Select(r=>r.CommandListIdentity).Distinct());
        using var fresh=new D3D12FrameSession();
        Assert.Equal(actual.Rgba,fresh.Capture(scene,width,height).Rgba);
    }
    [Fact]
    public void AbortedPresentKeepsCompletedFrameDiagnosticsUntilTheNextSuccessfulRender()
    {
        var scene = D3D12OriginalFrameTests.Shadow(true);
        using var session = new D3D12FrameSession();
        using var first = session.Render(scene, 64, 48);
        var firstTargets = session.LastTargets;
        var firstRecordings = session.LastRecordings;
        Assert.Contains(firstTargets, target => target.Name == "$output" && target.Size == (64, 48));
        Assert.NotEmpty(firstRecordings);
        Assert.All(firstRecordings, recording => Assert.Equal(first.FrameId, recording.FrameId));

        var failure = new InvalidOperationException("Presentation recording failed.");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => session.RenderForPresent(scene, 96, 72, null,
            (_, target) =>
            {
                Assert.Equal((96, 72), (target.Width, target.Height));
                throw failure;
            })));
        Assert.Same(firstTargets, session.LastTargets);
        Assert.Same(firstRecordings, session.LastRecordings);
        Assert.Same(first.Statistics, session.Statistics);

        using var recovered = session.Render(scene, 96, 72);
        Assert.Contains(session.LastTargets, target => target.Name == "$output" && target.Size == (96, 72));
        Assert.True(recovered.FrameId > first.FrameId);
        Assert.NotEmpty(session.LastRecordings);
        Assert.All(session.LastRecordings, recording => Assert.Equal(recovered.FrameId, recording.FrameId));
    }

    [Fact]
    public void RemovalFenceAllowsTeardownAndANewSessionRendersTheSameLighting()
    {
        var scene = D3D12NativeLightTests.Fixture();
        var light = D3D12NativeLightTests.Light(SceneLightKind.Point) with { CastShadows = false };
        var lit = D3D12NativeLightTests.WithLight(scene, [light]);
        using var session = new D3D12FrameSession();
        var expected = session.Capture(lit, 129, 97);
        Assert.NotEqual(session.Capture(scene, 129, 97).Rgba, expected.Rgba);
        var runtime = session.Runtime;
        var frame = runtime.Begin();
        var owned = frame.Own(runtime.Device.CreateFence(0, FenceFlags.None));
        runtime.Wait(runtime.Submit());

        // All GPU work has completed. Exercise the DXGI removal sentinel on this
        // session's private fence without removing the device used by other tests.
        var field = typeof(D3D12GpuRuntime).GetField("fence", BindingFlags.Instance | BindingFlags.NonPublic);
        var fence = Assert.IsAssignableFrom<ID3D12Fence>(field!.GetValue(runtime));
        fence.Signal(ulong.MaxValue);
        Assert.Equal(ulong.MaxValue, runtime.CompletedFence);
        Assert.Throws<InvalidOperationException>(session.WaitForIdle);
        session.Dispose();
        Assert.Equal(IntPtr.Zero, owned.NativePointer);
        Assert.Equal(IntPtr.Zero, fence.NativePointer);
        Assert.Equal(IntPtr.Zero, runtime.Device.NativePointer);

        using var fresh = new D3D12FrameSession();
        Assert.Equal(expected.Rgba, fresh.Capture(lit, 129, 97).Rgba);
    }

    [Fact] public void PersistentGpuFramesReuseImmutableResourcesWithoutReadback()
    {
        var scene=D3D12OriginalFrameTests.Shadow(true);using var session=new D3D12FrameSession();
        using var first=session.Render(scene,257,191);
        var uploads=first.Statistics.BufferUploads;var textures=first.Statistics.TextureUploads;
        var compile=first.Statistics.ShaderCompilations;var pso=first.Statistics.PipelineCreations;
        for(int i=0;i<12;i++)
        {
            using var next=session.Render(scene,257,191);
            Assert.Equal(0,next.Statistics.ReadbackCount);Assert.Equal(1,next.Statistics.SubmissionCount);
            Assert.Equal(uploads,next.Statistics.BufferUploads);Assert.Equal(textures,next.Statistics.TextureUploads);
            Assert.Equal(compile,next.Statistics.ShaderCompilations);Assert.Equal(pso,next.Statistics.PipelineCreations);
        }
        session.WaitForIdle();
    }
    [Fact] public void ThreeHundredRetainedFramesAndCameraUpdatesKeepStaticCachesBounded()
    {
        var box=D3D12OriginalOutlineTests.Box();
        var frame=new OriginalFrameScene(box.Surface,box.Outline,box.OutlineOptions,D3D12OriginalFrameTests.Options());
        using var session=new D3D12FrameSession();
        for(int i=0;i<6;i++){using var warm=session.Render(frame,257,191);}
        var before=session.Statistics!;var timer=System.Diagnostics.Stopwatch.StartNew();
        for(int i=0;i<300;i++)
        {
            using var output=session.Render(frame,257,191);
            Assert.Equal(0,output.Statistics.ReadbackCount);Assert.InRange(output.Statistics.PendingFrames,0,3);
        }
        timer.Stop();var after=session.Statistics!;
        Assert.Equal(before.BufferUploads,after.BufferUploads);Assert.Equal(before.TextureUploads,after.TextureUploads);
        Assert.Equal(before.ShaderCompilations,after.ShaderCompilations);Assert.Equal(before.PipelineCreations,after.PipelineCreations);
        Assert.Equal(before.CachedResources,after.CachedResources);Assert.Equal(before.LiveFrameResources,after.LiveFrameResources);
        for(int i=0;i<30;i++)
        {
            var eye=new Vector3(MathF.Sin(i*.1f),0,-5);var camera=box.Surface.Snapshot.Camera with{Eye=eye};
            var moved=new OriginalFrameScene(box.Surface,box.Outline,box.OutlineOptions! with{CameraPosition=eye},frame.Options! with{CameraOverride=camera});
            using var output=session.Render(moved,257,191);
            Assert.Equal(before.CachedResources,output.Statistics.CachedResources);Assert.Equal(before.TextureUploads,output.Statistics.TextureUploads);
        }
        var capture=session.Capture(frame,257,191);using var fresh=new D3D12FrameSession();Assert.Equal(capture.Rgba,fresh.Capture(frame,257,191).Rgba);
        session.WaitForIdle();
        Save("persistent-300",new{frames=300,elapsedMilliseconds=timer.Elapsed.TotalMilliseconds,before,after,last=session.Statistics,session.DebugRequested,session.DebugLayerEnabled,session.DebugInfoQueueAvailable});
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Save(string name,object value)
    {
        string? directory=Environment.GetEnvironmentVariable("MMD_GPU_RUNTIME_ARTIFACTS");if(directory is null)return;
        Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,name+".json"),System.Text.Json.JsonSerializer.Serialize(value,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }

}
