using System.Diagnostics;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics.Runtime;

namespace Mmd.Modern.App;

public sealed partial class FrameViewportViewModel
{
    private EngineShowcaseController? engineDemo;
    private long demoTimestamp;
    private bool demoLightingRequested;
    public bool IsEngineDemo=>options.EngineDemo;
    public bool HasEngineDemo=>engineDemo is not null;
    public bool DemoPhysicsPlaying=>engineDemo?.IsPlaying==true;
    public long DemoPhysicsStepCount=>engineDemo?.TotalStepCount??0;
    public string DemoPhysicsSummary=>engineDemo is {} demo
        ?$"Bepu 真实刚体 · 4个动态物体 · {demo.ElapsedSeconds:0.00}s\n120Hz · 已执行 {demo.TotalStepCount:N0} 步 · {(demo.IsPlaying?"运行中":"暂停")}"
        :"尚未加载演示";
    public void LoadEngineDemo()
    {
        ResetSceneState();sources.Clear();ModelPath=null;
        engineDemo=new(_=>ProjectPhysicsRuntime.CreateRigid(ProjectPhysicsBackend.Bepu,new(0,-9.81f,0),groundY:null));
        var frame=engineDemo.Frame;Vector3 min=new(float.MaxValue),max=new(float.MinValue);
        foreach(var vertex in frame.Surface.Snapshot.Vertices){min=Vector3.Min(min,vertex.Position);max=Vector3.Max(max,vertex.Position);}
        camera=new(min,max);roomEnabled=false;demoRoom=null;roomCamera=null;
        shadowFitEnabled=true;groundShadowEnabled=false;outlineEnabled=true;selfShadowEnabled=true;
        antialiasing=FrameAntialiasing.Smaa1X;roomEmissionStrength=0;
        rayGi=rayGi with{Mode=LightingMode.Legacy,Strength=.65f,SamplesPerPixel=4,Composition=RayGiComposition.ToonPalette,Denoiser=RayGiDenoiser.Spatial};
        yaw=-24;pitch=13;zoom=.86;panX=panY=0;Diagnostics=[];
        ApplyDemoFrame(frame);sceneReady=true;
        LightEditor.Add(new(Guid.Parse("aa000001-0000-0000-0000-000000000001"),"暖色点光",SceneLightKind.Point,new(-3.5f,4.2f,.5f),Quaternion.Identity,new(1,.32f,.10f),12,14));
        LightEditor.Add(new(Guid.Parse("aa000001-0000-0000-0000-000000000002"),"冷色聚光",SceneLightKind.Spot,new(4.8f,5.6f,-1),
            Quaternion.CreateFromYawPitchRoll(.45f,-.78f,0),new(.14f,.48f,1),24,16,.25f,.70f));
        LightEditor.SetModelRole("demo:room",ModelRole.Environment,true);LightEditor.Select(null);lightEditingEnabled=false;
        ModelSummary="原创引擎实验室\n15部件 · 4个动态刚体 · 真实碰撞／弹簧\n自带资源，无需用户模型";
        Status="左键拖动转镜头，滚轮缩放；可切换光追展示与物理演示。";
        demoTimestamp=Stopwatch.GetTimestamp();demoLightingRequested=true;ApplyDemoLightingSelection();
        NotifyMotionState();NotifyLights();NotifySceneControls();
    }
    public void ShowDemoRayTracing()
    {
        if(engineDemo is null)
        {
            if(HardwareGiAvailable)TrySelectGiMode(LightingMode.HardwareRayTracing);
            else if(SoftwareGiAvailable)TrySelectGiMode(LightingMode.SoftwareRayTracing);
            else Status=GiAvailabilityDetails;
            return;
        }
        ApplyDemoFrame(engineDemo.Reset());demoLightingRequested=true;ApplyDemoLightingSelection();
        Status="光追展示：物体恢复静止，使用当前可用光追后端；局部灯直接光独立计算。";
        NotifyDemoState();
    }
    public void PlayDemoPhysics()
    {
        if(engineDemo is null)return;demoLightingRequested=false;_ =TrySelectGiMode(LightingMode.Legacy);
        engineDemo.IsPlaying=true;demoTimestamp=Stopwatch.GetTimestamp();
        Status="真实物理运行中：碰撞与弹簧由Bepu求解；动态GI暂关闭，灯光阴影继续更新。";NotifyDemoState();
    }
    public void PauseDemoPhysics(){if(engineDemo is null)return;engineDemo.IsPlaying=false;NotifyDemoState();}
    public void ResetDemoPhysics()
    {
        if(engineDemo is null)return;ApplyDemoFrame(engineDemo.Reset());demoTimestamp=Stopwatch.GetTimestamp();NotifyDemoState();
    }
    public void AdvanceDemoPhysics(double seconds)
    {
        if(engineDemo is null)return;
        var previous=engineDemo.Frame;var frame=engineDemo.Advance(seconds);
        if(!ReferenceEquals(previous,frame)){ApplyDemoFrame(frame);Changed(nameof(DemoPhysicsSummary));}
    }
    private void TickEngineDemo()
    {
        if(!DemoPhysicsPlaying)return;
        long now=Stopwatch.GetTimestamp();double seconds=Stopwatch.GetElapsedTime(demoTimestamp,now).TotalSeconds;demoTimestamp=now;
        try{AdvanceDemoPhysics(seconds);}catch(Exception error){PauseDemoPhysics();Status=$"物理演示暂停：{error.Message}";}
    }
    private void ApplyDemoFrame(OriginalFrameScene frame)
    {
        surface=new(frame.Surface.Snapshot,frame.Surface.Materials,frame.Surface.Profile with{LightAmbient=new(.40f),LightSpecular=new(.3f)});
        outline=frame.Outline;flags=frame.Materials.ToArray();cachedFrame=null;fittedShadow=null;
    }
    private void ApplyDemoLightingSelection()
    {
        if(!HasEngineDemo||!demoLightingRequested||!lightingObserved)return;
        demoLightingRequested=false;
        if(HardwareGiAvailable)TrySelectGiMode(LightingMode.HardwareRayTracing);
        else if(SoftwareGiAvailable)TrySelectGiMode(LightingMode.SoftwareRayTracing);
        else Status="当前设备光追不可用；继续展示D3D12灯光、阴影和真实物理。";
    }
    private void NotifyDemoState(){cachedFrame=null;Changed(nameof(DemoPhysicsPlaying));Changed(nameof(DemoPhysicsSummary));}
}
