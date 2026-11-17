using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics.Runtime;

namespace Mmd.Modern.Demo;

/// <summary>Prepared off-thread; ownership transfers only at a successful UI-thread commit.</summary>
public sealed class PreparedViewportProject : IDisposable
{
    internal PmmProjectPlayback? Playback;
    internal IReadOnlyList<FrameViewportSource> Sources {get;}
    internal IReadOnlyList<string> Diagnostics {get;}
    internal PreparedViewportProject(PmmProjectPlayback playback,IReadOnlyList<FrameViewportSource> sources,IReadOnlyList<string> diagnostics)
    {Playback=playback;Sources=sources;Diagnostics=diagnostics;}
    public void Dispose(){Playback?.Dispose();Playback=null;}
}

public sealed partial class FrameViewportViewModel
{
    private PmmProjectPlayback? projectPlayback;
    private bool projectCameraEnabled;
    public bool HasProject=>projectPlayback is not null;
    public bool ProjectCameraEnabled
    {
        get=>projectCameraEnabled&&HasProject;
        set{if(projectCameraEnabled==value)return;projectCameraEnabled=value;Invalidate();}
    }
    public PmmDocument? ProjectDocument=>projectPlayback?.Plan.Document;
    public string ProjectSummary=>projectPlayback is null?"未载入 PMM 工程":$"PMMv{projectPlayback.Plan.Document.Version} · {projectPlayback.Plan.Models.Count} 模型 · {projectPlayback.Plan.OutputWidth}×{projectPlayback.Plan.OutputHeight}\n保存帧 {projectPlayback.Plan.CurrentFrame} · 输出 {projectPlayback.Plan.Document.Settings.PreferredFps:0.##}fps";
    public PreparedViewportProject PrepareProject(string path,PmmImportOptions? importOptions=null,CancellationToken cancellationToken=default)
    {
        string full=Path.GetFullPath(path);
        var plan=PmmProjectReader.Prepare(full,importOptions??new(){MmdDirectory=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"../MMD932"))},cancellationToken:cancellationToken);
        if(!plan.CanCommit)throw new InvalidDataException("PMM 引用尚未解决："+string.Join("；",plan.UnresolvedAssets.Select(a=>$"模型#{a.ModelIndex} {a.Resolution.Status}: {a.Error??string.Join(" | ",a.Resolution.References)}")));
        var captured=new Dictionary<string,FrameViewportSource>(StringComparer.OrdinalIgnoreCase);
        void Capture(string file)
        {if(captured.ContainsKey(file))return;var info=new FileInfo(file);captured[file]=new(info.FullName,info.Length,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant());}
        Capture(full);var diagnostics=new List<string>();var textures=new Dictionary<string,SceneTexture?>(StringComparer.OrdinalIgnoreCase);
        SceneTexture? Texture(string root,string relative)
        {
            string key=Path.GetFullPath(root)+"|"+relative;if(textures.TryGetValue(key,out var known))return known;
            try{return textures[key]=ReadTexture(root,relative,Capture);}
            catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException){diagnostics.Add($"纹理 {relative}: {e.Message}");return textures[key]=null;}
        }
        var scenes=new Dictionary<int,OriginalFrameScene>();
        foreach(var prepared in plan.Models)
        {
            Capture(prepared.Path);string root=Path.GetDirectoryName(prepared.Path)!;
            var adapted=PmxOriginalStyleAdapter.FromRestPose(prepared.Model,p=>Texture(root,p),options.ToonDirectory is null?null:i=>Texture(options.ToonDirectory,$"toon{i+1:00}.bmp"));
            var edge=PmxOriginalOutlineAdapter.FromRestPose(prepared.Model);
            edge=new(edge.Vertices,edge.Materials.Select(m=>m with{Width=m.Width*(plan.Document.Version==1?1:prepared.Source.EdgeWidth)}));
            var modelFlags=PmxOriginalFrameAdapter.FromMaterials(prepared.Model).Select(f=>f with{CastSelfShadow=f.CastSelfShadow&&(plan.Document.Version==1||prepared.Source.SelfShadowEnabled),ReceiveSelfShadow=f.ReceiveSelfShadow&&(plan.Document.Version==1||prepared.Source.SelfShadowEnabled)}).ToArray();
            scenes.Add(prepared.Source.Index,new(adapted.Scene,edge,new(Vector4.One,Matrix4x4.Identity,new(0,0,-10),30,1),materials:modelFlags));
            diagnostics.AddRange(adapted.Diagnostics.Select(d=>$"模型#{prepared.Source.Index}: {d}"));
        }
        var playback=new PmmProjectPlayback(plan,scenes,(gravity,ground)=>ProjectPhysicsRuntime.CreateRigid(ProjectPhysicsBackend.Bepu,gravity,ground));
        return new(playback,captured.Values.ToArray(),diagnostics);
    }
    public void LoadProject(string path,PmmImportOptions? importOptions=null)
    {using var candidate=PrepareProject(path,importOptions);CommitProject(candidate);}
    public void CommitProject(PreparedViewportProject candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);var playback=candidate.Playback??throw new ObjectDisposedException(nameof(candidate));
        var frame=playback.Frame;Vector3 min=new(float.MaxValue),max=new(float.MinValue);
        foreach(var vertex in frame.Surface.Snapshot.Vertices){min=Vector3.Min(min,vertex.Position);max=Vector3.Max(max,vertex.Position);}
        var projectCamera=new FrameViewportCamera(min,max);_ =playback.Camera.ViewProjection(playback.Plan.OutputWidth,playback.Plan.OutputHeight);
        // Everything that can fail during preparation is complete before the active project is released.
        sceneReady=false;ResetSceneState();projectPlayback=playback;candidate.Playback=null;
        surface=frame.Surface;outline=frame.Outline;flags=frame.Materials.ToArray();camera=projectCamera;animationSourceModel=null;
        sources.Clear();foreach(var source in candidate.Sources)sources.Add(source.Path,source);
        Diagnostics=candidate.Diagnostics.Concat(playback.Diagnostics).Distinct(StringComparer.Ordinal).ToArray();ModelPath=playback.Plan.ProjectPath;
        roomEnabled=false;demoRoom=null;roomCamera=null;cachedFrame=null;fittedShadow=fittedRoomShadow=null;shadowFitEnabled=true;
        rayGi=rayGi with{Mode=LightingMode.Legacy};
        // PMM 0001 omits the self-shadow control. Use the viewport default without changing raw metadata.
        selfShadowEnabled=playback.Plan.Document.Version==1||playback.Plan.Document.SelfShadow.Enabled;groundShadowEnabled=playback.Plan.Document.Settings.GroundShadowShown;
        playbackClock.SetDuration(playback.DurationFrames);playbackClock.Seek(playback.Plan.CurrentFrame);visibleMotionFrame=(uint)playback.Plan.CurrentFrame;
        animationTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();projectCameraEnabled=true;
        ModelSummary=Path.GetFileName(playback.Plan.ProjectPath)+"\n"+ProjectSummary;
        MotionSummary=$"工程内嵌动作 · {playback.DurationFrames} 帧 / 30fps\n多模型／镜头／灯光／外亲；兼容差异见提示";
        Status="PMM 工程已载入，恢复保存姿态和镜头；可播放或跳帧。";sceneReady=true;
        NotifyMotionState();NotifyLights();Changed(nameof(HasScene));Changed(nameof(HasProject));Changed(nameof(ProjectCameraEnabled));Changed(nameof(ProjectSummary));
        Changed(nameof(SelfShadowEnabled));Changed(nameof(GroundShadowEnabled));Changed(nameof(ShadowFitEnabled));Changed(nameof(ScenePresetIndex));
    }
    private void ApplyProjectFrame(double elapsedSeconds,bool resetPhysics)
    {
        var project=projectPlayback!;
        if(resetPhysics)project.Seek(playbackClock.Frame,motionIkEnabled,motionMorphsEnabled);
        else if(elapsedSeconds>0)project.Advance(playbackClock.Frame,elapsedSeconds,motionIkEnabled,motionMorphsEnabled);
        else if(visibleMotionFrame==(uint)playbackClock.Frame)return;
        else project.Seek(playbackClock.Frame,motionIkEnabled,motionMorphsEnabled);
        var frame=project.Frame;surface=frame.Surface;outline=frame.Outline;flags=frame.Materials.ToArray();
        visibleMotionFrame=(uint)playbackClock.Frame;cachedFrame=null;fittedShadow=null;
        Changed(nameof(MotionFrame));Changed(nameof(MotionPhysicsSummary));
    }
}
