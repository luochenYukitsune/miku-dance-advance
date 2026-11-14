using System.Collections;
using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12HardwareRayGiLegacyTraversalTests
{
    [Fact]
    public void AcceptedClosestHitPreservesLegacyPixelsAndRawIndirect()
    {
        var frame=Fixture(32,false,false);
        var original=Capture(frame,LegacyTrace,"layers-legacy");
        var candidate=Capture(frame,D3D12HardwareRayGi.TraceSource,"layers-current");
        Assert.Equal(original.Pixels,candidate.Pixels);
        Assert.Equal(original.Indirect,candidate.Indirect);
        Assert.True(candidate.Visits<=original.Visits,$"Closest candidate visits: legacy={original.Visits}, current={candidate.Visits}");
    }

    [Fact]
    public void CutoutBeforeTheNearestAcceptedSurfacePreservesLegacyPixels()
    {
        var frame=Fixture(3,false,true);
        var original=Capture(frame,LegacyTrace,"cutout-legacy");
        var candidate=Capture(frame,D3D12HardwareRayGi.TraceSource,"cutout-current");
        Assert.Equal(original.Pixels,candidate.Pixels);
        Assert.Equal(original.Indirect,candidate.Indirect);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(9, false)]
    [InlineData(32, true)]
    [InlineData(129, true)]
    public void AuditCoincidentDifferentMaterialsAgainstLegacyPixels(int layers, bool variedBounds)
    {
        var frame=Fixture(layers,true,false,variedBounds);
        var original=Capture(frame,LegacyTrace,"coincident-legacy");
        var candidate=Capture(frame,D3D12HardwareRayGi.TraceSource,"coincident-current");
        Assert.Equal(original.Pixels,candidate.Pixels);
        Assert.Equal(original.Indirect,candidate.Indirect);
    }

    [Fact]
    public void RawIndirectOracleSeesEmissionDifferencesHiddenBySaturatedFinalPixels()
    {
        var red=Capture(Fixture(1,false,false),LegacyTrace,"sensitivity-red");
        var green=Capture(Fixture(1,false,false,swapEmission:true),LegacyTrace,"sensitivity-green");
        Assert.Equal(red.Pixels,green.Pixels);
        Assert.False(red.Indirect.AsSpan().SequenceEqual(green.Indirect));
    }

    private static (byte[] Pixels,byte[] Indirect,uint Visits) Capture(OriginalFrameScene frame,string trace,string label)
    {
        string? previous=Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        string? timing=Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING");
        string directory=Path.Combine(Environment.GetEnvironmentVariable("MMD_TIMING_HOOK_ARTIFACTS")??Path.GetTempPath(),label+"-"+Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS",directory);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING",null);
            using var session=new D3D12FrameSession();
            var options=new RayGiOptions(LightingMode.HardwareRayTracing,SamplesPerPixel:2,Seed:17,MaxRayDistance:1000,RayEpsilon:.001f);
            session.Capture(frame,33,25,rayGiOptions:options);
            const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
            var renderer=typeof(D3D12FrameSession).GetField("renderer",flags)!.GetValue(session)!;
            var gi=renderer.GetType().GetField("rayGiRenderer",flags)!.GetValue(renderer)!;
            var pipelines=(IDictionary)gi.GetType().GetField("computePipelines",flags)!.GetValue(gi)!;
            var pipeline=Assert.Single(pipelines.Values.Cast<object>());
            int any=trace.IndexOf("bool TraceAny(",StringComparison.Ordinal);
            Assert.True(any>0);
            string instrumented=trace[..any].Replace("while(query.Proceed()) {","while(query.Proceed()) { InterlockedAdd(GiDiagnostics[3],1);",StringComparison.Ordinal)
                .Replace("while(tieQuery.Proceed()) {","while(tieQuery.Proceed()) { InterlockedAdd(GiDiagnostics[3],1);",StringComparison.Ordinal)+trace[any..];
            string source=D3D12RayGiShaders.Source(true).Replace(D3D12HardwareRayGi.TraceSource,instrumented,StringComparison.Ordinal);
            var compiled=new DxcShaderCompiler().CompileCompute(source,"CSMain","cs_6_5",new Dictionary<string,string?>{{"GI_HARDWARE","1"},{"GI_DIAGNOSTICS","1"}});
            var root=(ID3D12RootSignature)pipeline.GetType().GetProperty("Root")!.GetValue(pipeline)!;
            var old=(ID3D12PipelineState)pipeline.GetType().GetProperty("State")!.GetValue(pipeline)!;
            var state=session.Runtime.Device.CreateComputePipelineState(new ComputePipelineStateDescription{RootSignature=root,ComputeShader=compiled.Dxil});
            pipeline.GetType().GetProperty("State")!.SetValue(pipeline,state);
            old.Dispose(); // The warm explicit capture already completed its fence.
            var pixels=session.Capture(frame,33,25,rayGiOptions:options);
            string counters=Path.Combine(directory,session.Statistics!.DeviceIdentity,"frame-"+session.Statistics.FrameId,"counters-0.bin");
            uint visits=BitConverter.ToUInt32(File.ReadAllBytes(counters),12);
            byte[] indirect=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(counters)!,"indirect-0.bin"));
            File.WriteAllBytes(Path.Combine(directory,"pixels.rgba"),pixels.Rgba);
            File.WriteAllText(Path.Combine(directory,"comparison-input.json"),System.Text.Json.JsonSerializer.Serialize(new {label,visits,compiled.SourceHash,compiled.OutputHash}));
            return (pixels.Rgba,indirect,visits);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS",previous);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING",timing);
        }
    }

    private static OriginalFrameScene Fixture(int layers,bool coincident,bool cutout,bool variedBounds=false,bool swapEmission=false)
    {
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var materials=new List<SceneMaterial>();
        var bindings=new List<OriginalStyleMaterialBinding>();
        void Quad(float z,float half,Vector3 normal,Vector3 emission,int texture=-1)
        {
            uint v=(uint)vertices.Count;int start=indices.Count;
            vertices.AddRange([new(new(-half,-half,z),normal,new(.25f,.5f)),new(new(half,-half,z),normal,new(.25f,.5f)),
                new(new(half,half,z),normal,new(.25f,.5f)),new(new(-half,half,z),normal,new(.25f,.5f))]);
            indices.AddRange([v,v+2,v+1,v,v+3,v+2]);
            materials.Add(new("layer-"+materials.Count,start,6,new(.9f,.9f,.9f,1),Vector3.Zero,Vector3.Zero,1,DiffuseTexture:texture){Emission=emission});
            bindings.Add(new(Vector3.One,DoubleSided:true));
        }
        Quad(0,2,-Vector3.UnitZ,Vector3.Zero);
        for(int i=0;i<layers;i++)Quad(coincident?-8:-8-i*2,variedBounds?100+i*4:100,Vector3.UnitZ,
            i%2==(swapEmission?1:0)?new(1,0,0):new(0,1,0),cutout&&i==0?0:-1);
        SceneTexture[] textures=cutout?[new("fully-cutout",1,1,[255,255,255,0])]:[];
        var snapshot=new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,textures,
            camera:new(new(0,0,-6),Vector3.Zero,Vector3.UnitY,4.5f,.1f,100),lightDirection:Vector3.Normalize(new(.5f,1,.5f)));
        return new(new OriginalStyleScene(snapshot,bindings,OriginalStyleProfile.SelectedReference with {TextureSampling=OriginalTextureSampling.PointClamp}));
    }

    // Accepted uncommitted traversal from d784808, independent of the product candidate.
    private const string LegacyTrace="""
        bool TraceClosest(GiRay ray, out GiHit hit) {
            RayDesc desc;
            desc.Origin=ray.Origin; desc.Direction=ray.Direction; desc.TMin=ray.TMin; desc.TMax=ray.TMax;
            RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> query;
            query.TraceRayInline(GiHardwareScene,RAY_FLAG_NONE,0xff,desc);
            bool found=false; float closest=ray.TMax;
            while(query.Proceed()) {
                if(query.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE) continue;
                uint triangleIndex=query.CandidatePrimitiveIndex();
                float2 barycentrics=query.CandidateTriangleBarycentrics();
                float distance=query.CandidateTriangleRayT();
                if(distance<ray.TMin||distance>closest) continue;
                if(!AcceptCandidate(triangleIndex,barycentrics,ray.Direction)) continue;
                found=true; closest=distance;
                GiTriangle tri=GiTriangles[triangleIndex];
                uint front=dot(cross(tri.P1.xyz-tri.P0.xyz,tri.P2.xyz-tri.P0.xyz),ray.Direction)<0?1:0;
                hit.TriangleIndex=triangleIndex; hit.T=distance; hit.Barycentrics=barycentrics; hit.FrontFace=front;
            }
            return found;
        }
        bool TraceAny(GiRay ray) {
            RayDesc desc;
            desc.Origin=ray.Origin; desc.Direction=ray.Direction; desc.TMin=ray.TMin; desc.TMax=ray.TMax;
            RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> query;
            query.TraceRayInline(GiHardwareScene,RAY_FLAG_NONE,0xff,desc);
            while(query.Proceed()) {
                if(query.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE) continue;
                if(AcceptCandidate(query.CandidatePrimitiveIndex(),query.CandidateTriangleBarycentrics(),ray.Direction))
                    return true;
            }
            return false;
        }
        """;
}
