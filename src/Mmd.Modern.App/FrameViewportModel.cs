using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>Prepared without touching the current viewport. Consumed only on a successful UI-thread commit.</summary>
public sealed class PreparedViewportModel : IDisposable
{
    internal bool Available=true;
    internal PmxModel Model {get;}
    internal OriginalStyleScene Surface {get;}
    internal OriginalOutlineData Outline {get;}
    internal OriginalFrameMaterialFlags[] Flags {get;}
    internal FrameViewportCamera Camera {get;}
    internal string Path {get;}
    internal IReadOnlyList<FrameViewportSource> Sources {get;}
    internal IReadOnlyList<string> Diagnostics {get;}
    internal PreparedViewportModel(PmxModel model,OriginalStyleScene surface,OriginalOutlineData outline,OriginalFrameMaterialFlags[] flags,
        FrameViewportCamera camera,string path,IReadOnlyList<FrameViewportSource> sources,IReadOnlyList<string> diagnostics)
    {Model=model;Surface=surface;Outline=outline;Flags=flags;Camera=camera;Path=path;Sources=sources;Diagnostics=diagnostics;}
    public void Dispose()=>Available=false;
}

public sealed partial class FrameViewportViewModel
{
    public PreparedViewportModel PrepareModel(string path)
    {
        string full=Path.GetFullPath(path),directory=Path.GetDirectoryName(full)!;
        if(!Path.GetExtension(full).Equals(".pmx",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("实时完整帧视口需要 PMX 模型。");
        PmxModel model;using(var stream=File.OpenRead(full))model=PmxReader.Read(stream);
        var captured=new Dictionary<string,FrameViewportSource>(StringComparer.OrdinalIgnoreCase);
        void Capture(string file)
        {if(captured.ContainsKey(file))return;var info=new FileInfo(file);captured[file]=new(info.FullName,info.Length,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant());}
        Capture(full);var diagnostics=new List<string>();
        SceneTexture? Load(string root,string relative)
        {
            try{return ReadTexture(root,relative,Capture);}
            catch(Exception error)when(error is IOException or InvalidDataException or ArgumentException)
            {diagnostics.Add($"{relative}: {error.Message}");return null;}
        }
        var adapted=PmxOriginalStyleAdapter.FromRestPose(model,p=>Load(directory,p),
            options.ToonDirectory is null?null:i=>Load(options.ToonDirectory,$"toon{i+1:00}.bmp"));
        var outlineData=PmxOriginalOutlineAdapter.FromRestPose(model);
        var materialFlags=PmxOriginalFrameAdapter.FromMaterials(model).ToArray();
        Vector3 min=new(float.MaxValue),max=new(float.MinValue);
        foreach(uint index in adapted.Scene.Snapshot.Indices)
        {var position=adapted.Scene.Snapshot.Vertices[(int)index].Position;min=Vector3.Min(min,position);max=Vector3.Max(max,position);}
        return new(model,adapted.Scene,outlineData,materialFlags,new(min,max),full,captured.Values.ToArray(),adapted.Diagnostics.Concat(diagnostics).ToArray());
    }
    public void CommitModel(PreparedViewportModel candidate,Mmd.Modern.Application.ModelRole role=Mmd.Modern.Application.ModelRole.Character)
    {
        ArgumentNullException.ThrowIfNull(candidate);if(!candidate.Available)throw new ObjectDisposedException(nameof(candidate));
        sceneReady=false;ResetSceneState();candidate.Available=false;
        surface=candidate.Surface;outline=candidate.Outline;flags=candidate.Flags;camera=candidate.Camera;animationSourceModel=candidate.Model;
        sources.Clear();foreach(var source in candidate.Sources)sources.Add(source.Path,source);
        ModelPath=candidate.Path;Diagnostics=candidate.Diagnostics;
        demoRoom=null;roomCamera=null;fittedShadow=fittedRoomShadow=null;ShadowProjectionFitCount=0;
        if(IsEngineDemo)roomEnabled=false;
        rayGi=rayGi with{Mode=LightingMode.Legacy};ResetCamera();
        var model=candidate.Model;
        ModelSummary=$"{model.Name}\n{model.Vertices.Count:N0} 顶点 / {model.Faces.Count:N0} 三角面 / {model.Materials.Count} 材质\nPMX 初始姿态；可调整灯光、画风与镜头";
        sceneReady=true;
        InitializeSceneComposition(role);
        Status="模型已载入。左键旋转，中键／空白右键平移，滚轮缩放；灯光与画风实时更新。";
        NotifyMotionState();NotifyLights();NotifySceneControls();
    }
    private void NotifySceneControls()
    {
        Changed(nameof(HasScene));Changed(nameof(HasEngineDemo));Changed(nameof(ModelPath));Changed(nameof(DemoPhysicsSummary));
        Changed(nameof(DemoPhysicsPlaying));Changed(nameof(ScenePresetIndex));Changed(nameof(GiMode));Changed(nameof(GiEnabled));
        Changed(nameof(OutlineEnabled));Changed(nameof(SelfShadowEnabled));Changed(nameof(ShadowFitEnabled));Changed(nameof(GroundShadowEnabled));
        Changed(nameof(AntialiasingIndex));Changed(nameof(GiStrength));Changed(nameof(GiQualityIndex));Changed(nameof(GiCompositionIndex));
        Changed(nameof(GiDenoiseEnabled));Changed(nameof(GiDenoiseAvailable));Changed(nameof(Yaw));Changed(nameof(Pitch));Changed(nameof(Zoom));Changed(nameof(PanX));Changed(nameof(PanY));
    }
}
