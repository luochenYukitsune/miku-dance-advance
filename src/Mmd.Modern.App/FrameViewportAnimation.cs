using System.Diagnostics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics.Runtime;

namespace Mmd.Modern.App;

/// <summary>Playback orchestration only; numerical evaluation stays in the Animation module.</summary>
public sealed partial class FrameViewportViewModel
{
    private PmxModel? animationSourceModel;
    private CharacterAnimationController? characterAnimation;
    private OriginalStyleScene? animationRestSurface;
    private OriginalOutlineData? animationRestOutline;
    private readonly FramePlaybackClock playbackClock=new();
    private long animationTimestamp=Stopwatch.GetTimestamp();
    private uint? visibleMotionFrame;
    private bool motionIkEnabled=true;
    private bool motionPhysicsPreferred=true;
    private bool motionMorphsEnabled=true;
    public bool MotionMorphsAvailable=>projectPlayback?.HasMorphTracks??characterAnimation?.MatchedMorphTrackCount>0;
    public bool MotionMorphsEnabled
    {
        get=>motionMorphsEnabled;
        set
        {
            if(value==motionMorphsEnabled)return;
            bool old=motionMorphsEnabled;motionMorphsEnabled=value;
            try{ApplyMotionFrame(resetPhysics:true);animationTimestamp=Stopwatch.GetTimestamp();}
            catch(Exception error){motionMorphsEnabled=old;playbackClock.Pause();Status=$"表情／物理重置失败，保留最后有效姿态：{error.Message}";}
            Changed();Changed(nameof(IsMotionPlaying));Changed(nameof(MotionPhysicsSummary));
        }
    }
    public bool MotionIkEnabled
    {
        get=>motionIkEnabled;
        set
        {
            if(value==motionIkEnabled)return;
            bool old=motionIkEnabled;motionIkEnabled=value;
            try{ApplyMotionFrame(resetPhysics:true);animationTimestamp=Stopwatch.GetTimestamp();}
            catch(Exception error){motionIkEnabled=old;playbackClock.Pause();Status=$"IK／物理重置失败，保留最后有效姿态：{error.Message}";}
            Changed();Changed(nameof(IsMotionPlaying));Changed(nameof(MotionPhysicsSummary));
        }
    }
    public bool HasMotion=>projectPlayback is not null||characterAnimation is not null;
    public bool IsMotionPlaying=>playbackClock.IsPlaying;
    public bool AnimationSceneSelectionAvailable=>!HasMotion;
    public double MotionDuration=>playbackClock.DurationFrames;
    public double MotionFrame {get=>visibleMotionFrame??0;set{if(value!=MotionFrame)SeekMotion(value);}}
    public string MotionSummary {get;private set;}="未载入动作";
    public IReadOnlyList<string> MotionDiagnostics=>projectPlayback?.Diagnostics??characterAnimation?.Diagnostics??Array.Empty<string>();
    public string MotionCompatibilityDetails=>string.Join(Environment.NewLine,MotionDiagnostics);
    public bool MotionPhysicsAvailable=>projectPlayback?.SupportsPhysics??characterAnimation?.SupportsPhysics==true;
    public bool MotionPhysicsEnabled
    {
        get=>projectPlayback?.PhysicsEnabled??characterAnimation?.PhysicsEnabled==true;
        set
        {
            if(projectPlayback is not null)
            {
                try{projectPlayback.SetPhysics(value,motionIkEnabled,motionMorphsEnabled);var frame=projectPlayback.Frame;surface=frame.Surface;outline=frame.Outline;flags=frame.Materials.ToArray();cachedFrame=null;fittedShadow=null;motionPhysicsPreferred=value;}
                catch(Exception error){playbackClock.Pause();Status=$"工程物理切换失败：{error.Message}";}
                NotifyMotionState();return;
            }
            if(characterAnimation is null){motionPhysicsPreferred=value;return;}
            if(value==MotionPhysicsEnabled)return;
            try
            {
                if(value)characterAnimation.EnablePhysics(ProjectPhysicsRuntime.CreateRigid(ProjectPhysicsBackend.Bepu),(uint)Math.Floor(playbackClock.Frame),motionIkEnabled,motionMorphsEnabled);
                else characterAnimation.DisablePhysics();
                motionPhysicsPreferred=value;
                animationTimestamp=Stopwatch.GetTimestamp();visibleMotionFrame=null;ApplyMotionFrame();
                Status=value?"Modern 实验物理已开启；跳帧在目标姿态重置，Bepu与原版Bullet仍有差异。":"角色物理已关闭。";
            }
            catch(Exception error){playbackClock.Pause();Status=$"物理切换失败，保留最后有效姿态：{error.Message}";}
            NotifyMotionState();
        }
    }
    public int MotionPhysicsLastStepCount=>projectPlayback?.PhysicsLastStepCount??characterAnimation?.PhysicsLastStepCount??0;
    public double MotionPhysicsDiscardedSeconds=>projectPlayback?.PhysicsDiscardedSeconds??characterAnimation?.PhysicsDiscardedSeconds??0;
    public string MotionPhysicsSummary=>!MotionPhysicsEnabled?(HasMotion?"物理 Off":"载入动作后默认开启角色物理"):
        characterAnimation?.PhysicsFaulted==true?"物理出错，已暂停；重置可恢复。":
        $"Bepu · 实验 · 120Hz / 每帧最多10步\n跳帧重置 · 累计丢弃积压 {MotionPhysicsDiscardedSeconds:0.000}s";

    public void ResetMotionPhysics()
    {
        if(!MotionPhysicsEnabled)return;
        try{ApplyMotionFrame(resetPhysics:true);animationTimestamp=Stopwatch.GetTimestamp();Status="已在当前动作姿态重置物理历史。";}
        catch(Exception error){playbackClock.Pause();Status=$"物理重置失败，保留最后有效姿态：{error.Message}";}
        NotifyMotionState();
    }

    public void LoadMotion(string path)
    {
        using var stream=File.OpenRead(path);var motion=VmdReader.Read(stream);LoadMotion(motion,path);
    }
    public void LoadMotion(VmdMotion motion,string? sourcePath=null)
    {
        if(projectPlayback is not null)throw new InvalidOperationException("工程正使用内嵌动作；请先打开单个PMX再载入外部VMD。");
        if(sceneComposition is not null&&(SceneObjects.Count>1||SceneObjects[0].World!=System.Numerics.Matrix4x4.Identity))
            throw new InvalidOperationException("当前为多物体／已变换的静态场景；逐物体动作下一步接入。可撤销追加和变换，或重新打开单个PMX后载入VMD。");
        if(animationSourceModel is null||surface is null||outline is null)throw new InvalidOperationException("请先载入PMX模型。");
        var restSurface=animationRestSurface??surface;var restOutline=animationRestOutline??outline;
        var controller=new CharacterAnimationController(animationSourceModel,motion,restSurface,restOutline);
        try
        {
            if(motionPhysicsPreferred&&controller.SupportsPhysics)controller.EnablePhysics(ProjectPhysicsRuntime.CreateRigid(ProjectPhysicsBackend.Bepu),0,motionIkEnabled,motionMorphsEnabled);
            else _=controller.Sample(0,motionIkEnabled,motionMorphsEnabled);
            if(sourcePath is not null)Remember(Path.GetFullPath(sourcePath));
        }
        catch{controller.Dispose();throw;}
        // Prepare and validate before replacing the active motion. Dynamic GI is a later stage.
        RoomEnabled=false;TrySelectGiMode(LightingMode.Legacy);
        animationRestSurface=restSurface;animationRestOutline=restOutline;characterAnimation?.Dispose();characterAnimation=controller;
        playbackClock.SetDuration(controller.DurationFrames);visibleMotionFrame=null;
        ApplyMotionFrame();
        MotionSummary=$"角色动作 · {controller.DurationFrames} 帧 / 30fps\n骨骼／IK／实验物理 · 表情 {controller.MatchedMorphTrackCount} 轨道\n支持顶点／骨／UV／材质表情；其他类型见兼容提示";
        Status=$"动作已载入，{MotionDiagnostics.Count} 项兼容提示。可播放、暂停或跳帧。";
        NotifyMotionState();
    }
    public void PlayMotion()
    {
        if(!HasMotion)return;playbackClock.Play();animationTimestamp=Stopwatch.GetTimestamp();ApplyMotionFrame();Changed(nameof(IsMotionPlaying));
    }
    public void PauseMotion()
    {
        playbackClock.Pause();animationTimestamp=Stopwatch.GetTimestamp();Changed(nameof(IsMotionPlaying));
    }
    public void SeekMotion(double frame)
    {
        if(!double.IsFinite(frame))throw new ArgumentOutOfRangeException(nameof(frame));
        if(!HasMotion)return;
        playbackClock.Seek(Math.Round(frame));animationTimestamp=Stopwatch.GetTimestamp();
        try{ApplyMotionFrame(resetPhysics:true);}
        catch(Exception error){playbackClock.Pause();Status=$"跳帧／物理重置失败，保留最后有效姿态：{error.Message}";NotifyMotionState();}
    }
    public void TickAnimation()
    {
        TickEngineDemo();
        if(!HasMotion||!playbackClock.IsPlaying)return;
        long now=Stopwatch.GetTimestamp();var elapsed=Stopwatch.GetElapsedTime(animationTimestamp,now);animationTimestamp=now;
        AdvanceMotion(elapsed);
    }
    /// <summary>Advances the application timeline by an explicit elapsed interval; rendering remains independent.</summary>
    public void AdvanceMotion(TimeSpan elapsed)
    {
        if(elapsed<TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(elapsed));
        if(!HasMotion||!playbackClock.IsPlaying)return;
        animationTimestamp=Stopwatch.GetTimestamp();
        bool wasPlaying=playbackClock.IsPlaying;double previous=playbackClock.Frame;playbackClock.Advance(elapsed);
        try{ApplyMotionFrame((playbackClock.Frame-previous)/30);}
        catch(Exception error){playbackClock.Pause();Status=$"动作／物理求值失败，已暂停并保留最后有效姿态：{error.Message}";Changed(nameof(MotionPhysicsSummary));}
        if(wasPlaying!=playbackClock.IsPlaying)Changed(nameof(IsMotionPlaying));
    }
    public void UnloadMotion()
    {
        if(animationRestSurface is not null)surface=animationRestSurface;
        if(animationRestOutline is not null)outline=animationRestOutline;
        if(animationSourceModel is not null&&surface is not null&&outline is not null)flags=PmxOriginalFrameAdapter.FromEvaluatedMaterials(animationSourceModel,surface,outline).ToArray();
        ResetAnimationState();cachedFrame=null;fittedShadow=fittedRoomShadow=null;NotifyMotionState();
    }
    private void ResetAnimationState()
    {
        projectPlayback?.Dispose();projectPlayback=null;projectCameraEnabled=false;
        characterAnimation?.Dispose();characterAnimation=null;animationRestSurface=null;animationRestOutline=null;visibleMotionFrame=null;
        playbackClock.SetDuration(0);MotionSummary="未载入动作";
    }
    private void ApplyMotionFrame(double elapsedSeconds=0,bool resetPhysics=false)
    {
        if(projectPlayback is not null){ApplyProjectFrame(elapsedSeconds,resetPhysics);return;}
        if(characterAnimation is null)return;
        uint frame=(uint)Math.Floor(playbackClock.Frame);
        if(characterAnimation.PhysicsEnabled&&resetPhysics)(surface,outline)=characterAnimation.ResetPhysics(frame,motionIkEnabled,motionMorphsEnabled);
        else if(elapsedSeconds>0&&(characterAnimation.PhysicsEnabled||(motionMorphsEnabled&&MotionMorphsAvailable)))
            (surface,outline)=characterAnimation.Advance(playbackClock.Frame,elapsedSeconds,motionIkEnabled,motionMorphsEnabled);
        else{if(!resetPhysics&&visibleMotionFrame==frame)return;(surface,outline)=characterAnimation.Sample(frame,motionIkEnabled,motionMorphsEnabled);}
        visibleMotionFrame=frame;
        flags=PmxOriginalFrameAdapter.FromEvaluatedMaterials(animationSourceModel!,surface!,outline!).ToArray();
        cachedFrame=null;demoRoom=null;fittedShadow=fittedRoomShadow=null;
        Changed(nameof(MotionFrame));if(MotionPhysicsEnabled)Changed(nameof(MotionPhysicsSummary));
    }
    private void NotifyMotionState()
    {
        NotifySceneObjects();
        Changed(nameof(HasProject));Changed(nameof(ProjectCameraEnabled));Changed(nameof(ProjectSummary));
        Changed(nameof(HasMotion));Changed(nameof(IsMotionPlaying));Changed(nameof(MotionFrame));Changed(nameof(MotionDuration));
        Changed(nameof(MotionSummary));Changed(nameof(MotionDiagnostics));Changed(nameof(MotionCompatibilityDetails));Changed(nameof(AnimationSceneSelectionAvailable));NotifyGiMenu();
        Changed(nameof(MotionPhysicsEnabled));Changed(nameof(MotionPhysicsAvailable));Changed(nameof(MotionPhysicsSummary));
        Changed(nameof(MotionMorphsEnabled));Changed(nameof(MotionMorphsAvailable));
    }
}
