using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12FsrConcurrentLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FourIndependentRuntimesCreateRenderAndDestroyNativeContextsAtTheSameBarriers()
    {
        string loader=Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ??throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH and run this filter in a cold process to exercise signed-loader first use.");
        const int workers=4,epochs=2;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(3));
        using var barrier=new Barrier(workers);
        var tasks=Enumerable.Range(0,workers).Select(worker=>Task.Factory.StartNew(()=>
        {
            try { return RunWorker(worker,epochs,loader,barrier,stop.Token); }
            catch { stop.Cancel();throw; }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)).ToArray();
        var results=await Task.WhenAll(tasks);
        Assert.Equal(workers,results.Select(result=>result.Thread).Distinct().Count());
        Assert.Equal(workers,results.Select(result=>result.RuntimeIdentity).Distinct().Count());
        Assert.Equal(workers,results.Select(result=>result.Queue).Distinct().Count());
        Assert.All(results,result=>Assert.Equal(epochs*2,result.Dispatches));
        foreach(var result in results)
            output.WriteLine($"worker={result.Worker}; thread={result.Thread}; runtime={result.RuntimeIdentity}; device=0x{result.Device:X}; queue=0x{result.Queue:X}; contexts={epochs}; dispatches={result.Dispatches}; provider={result.Provider}");
    }

    private static WorkerResult RunWorker(int worker,int epochs,string loader,Barrier barrier,CancellationToken token)
    {
        // Each worker owns its runtime, command queue, frame slots, input pass and SDK context.
        // D3D12CreateDevice may return the adapter's shared device singleton; queue/runtime ownership is independent.
        using var runtime=new D3D12GpuRuntime();using var inputs=new D3D12TemporalInputPass(runtime);using var cache=new D3D12GpuCache(runtime);
        var scene=SceneSnapshot.CreateEmpty();long dispatches=0;string provider="";
        int thread=Environment.CurrentManagedThreadId;nint device=runtime.Device.NativePointer;
        for(int epoch=0;epoch<epochs;epoch++)
        {
            Meet(); // All four native loader/context construction attempts start from this barrier.
            if(worker==0&&epoch==0)
            {
                // Race the capability-query entry point against the other three actual context constructors.
                var probe=D3D12FsrRuntimeProbe.Probe(runtime.Device,loader,runtime.AdapterName,createContexts:false);
                Assert.Equal(D3D12FsrRuntimeStatus.QuerySucceeded,probe.Status);Assert.NotEmpty(probe.Providers);
            }
            using(var pass=new D3D12FsrTemporalPass(runtime,loader,64,48,96,72))
            {
                Assert.NotEqual(0ul,pass.ProviderId);Assert.False(string.IsNullOrWhiteSpace(pass.ProviderName));provider=pass.ProviderName;
                Meet(); // Every context remains live before concurrent dispatch starts.
                byte red=(byte)(31+worker*37),green=(byte)(88+epoch*20),blue=(byte)(177-worker*20);
                byte[] pixels=new byte[64*48*4];
                for(int index=0;index<pixels.Length;index+=4){pixels[index]=red;pixels[index+1]=green;pixels[index+2]=blue;pixels[index+3]=255;}
                for(int frame=0;frame<2;frame++)
                {
                    var context=runtime.Begin();
                    try
                    {
                        var temporal=inputs.Record(context,scene,scene,64,48);
                        var color=new D3D12GpuTarget("worker-constant",cache.Texture(new($"worker-{worker}-{epoch}",64,48,pixels)),default,
                            64,48,Format.R8G8B8A8_UNorm,false){State=ResourceStates.PixelShaderResource,Initialized=true};
                        var rendered=pass.Record(context,color,temporal,Vector2.Zero,1000f/60,false,.1f,100,MathF.PI/4);
                        var copy=Copy(context,rendered);runtime.Wait(runtime.Submit());
                        Assert.Equal(frame==0,pass.LastReset);
                        var mapped=copy.Buffer.Map<byte>(0,copy.Bytes);
                        try
                        {
                            // Read each worker's real GPU result; a cross-context mix-up cannot pass with distinct colours.
                            int center=36*copy.Layout.Footprint.RowPitch+48*4;
                            Assert.InRange(mapped[center],Math.Max(0,red-2),Math.Min(255,red+2));
                            Assert.InRange(mapped[center+1],Math.Max(0,green-2),Math.Min(255,green+2));
                            Assert.InRange(mapped[center+2],Math.Max(0,blue-2),Math.Min(255,blue+2));
                        }
                        finally { copy.Buffer.Unmap(0); }
                    }
                    finally { runtime.Abort(); }
                }
                Assert.Equal(2,pass.RecordedDispatches);dispatches+=pass.RecordedDispatches;
                Meet(); // Destruction attempts overlap, with all per-device GPU work completed.
            }
            Meet(); // Fully unload each epoch before racing the next group of first-use attempts.
        }
        runtime.WaitForIdle();return new(worker,thread,runtime.DeviceIdentity,device,runtime.Queue.NativePointer,dispatches,provider);

        void Meet()
        {
            if(!barrier.SignalAndWait(TimeSpan.FromSeconds(60),token))throw new TimeoutException("FSR concurrent lifetime barrier timed out.");
        }
    }

    private static (ID3D12Resource Buffer,PlacedSubresourceFootPrint Layout,int Bytes) Copy(D3D12RecordingContext context,D3D12GpuTarget target)
    {
        var layout=new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(target.Resource.Description,0,1,0,layout,new int[1],new ulong[1],out ulong bytes);
        var buffer=context.Own(context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(bytes),ResourceStates.CopyDest));
        context.Transition(target,ResourceStates.CopySource);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer,layout[0]),0,0,0,new TextureCopyLocation(target.Resource,0));
        return(buffer,layout[0],checked((int)bytes));
    }
    private sealed record WorkerResult(int Worker,int Thread,string RuntimeIdentity,nint Device,nint Queue,long Dispatches,string Provider);
}
