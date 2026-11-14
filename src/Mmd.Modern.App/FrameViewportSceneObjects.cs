using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>Main editor orchestration for static PMX objects; animation evaluation remains in Application.</summary>
public sealed partial class FrameViewportViewModel
{
    private SceneCompositionEditor? sceneComposition;
    private PmxModel? compositionPrimaryModel;
    public event Action? SceneObjectsChanged;
    public IReadOnlyList<EditableSceneObject> SceneObjects=>sceneComposition?.Objects??(IReadOnlyList<EditableSceneObject>)Array.Empty<EditableSceneObject>();
    public EditableSceneObject? SelectedSceneObject=>sceneComposition?.Selected;
    public bool HasEditableScene=>sceneComposition is not null&&!HasMotion&&!HasProject;
    private void InitializeSceneComposition(ModelRole role=ModelRole.Character)
    {
        if(IsEngineDemo||surface is null||outline is null||camera is null||ModelPath is null)return;
        sceneComposition=new();
        compositionPrimaryModel=animationSourceModel;
        var input=new OriginalFrameScene(surface,outline,new(new(camera.Center,1),Matrix4x4.Identity,camera.CreateCamera(1,1).Eye,30,1),materials:flags);
        var item=sceneComposition.Add(Path.GetFileNameWithoutExtension(ModelPath),ModelPath,role,input);
        sceneComposition.ClearHistory();
        LightEditor.SetModelRole(item.Key,role);NotifySceneObjects();
    }
    public void AddPreparedSceneModel(PreparedViewportModel candidate,ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(candidate);if(!candidate.Available)throw new ObjectDisposedException(nameof(candidate));
        if(HasMotion||HasProject||HasEngineDemo)throw new InvalidOperationException("当前追加入口用于独立PMX场景；请先停止／卸载动作，或打开独立PMX。");
        if(sceneComposition is null)
        {
            CommitModel(candidate,role);
            return;
        }
        var input=new OriginalFrameScene(candidate.Surface,candidate.Outline,
            new(new(candidate.Camera.Center,1),Matrix4x4.Identity,candidate.Camera.CreateCamera(1,1).Eye,30,1),materials:candidate.Flags);
        var item=sceneComposition.Add(Path.GetFileNameWithoutExtension(candidate.Path),candidate.Path,role,input);candidate.Available=false;
        foreach(var source in candidate.Sources)sources[source.Path]=source;
        LightEditor.SetModelRole(item.Key,role);
        Diagnostics=Diagnostics.Concat(candidate.Diagnostics).Distinct().ToArray();
        ApplySceneComposition(refit:true);
        Status=$"已追加{(role==ModelRole.Environment?"场景":"角色")}，保留已有模型和灯光；共 {SceneObjects.Count} 个物体。";
    }
    public void SelectSceneObject(Guid? id){sceneComposition?.Select(id);NotifySceneObjects();}
    public void TransformSelectedSceneObject(Vector3 position,Quaternion rotation,float scale)
    {
        if(!HasEditableScene||SelectedSceneObject is not {} item)return;
        sceneComposition!.SetTransform(item.Id,position,rotation,scale);ApplySceneComposition(refit:false);
        Status="物体变换已更新；共享网格、描边和阴影同步。可撤销，原PMX未修改。";
    }
    public void RemoveSelectedSceneObject()
    {
        if(!HasEditableScene||SelectedSceneObject is not {} item)return;
        sceneComposition!.Remove(item.Id);ApplySceneComposition(refit:false);
    }
    public void UndoSceneObjectEdit(){if(!HasEditableScene)return;sceneComposition!.Undo();ApplySceneComposition(refit:false);}
    public void RedoSceneObjectEdit(){if(!HasEditableScene)return;sceneComposition!.Redo();ApplySceneComposition(refit:false);}
    public void FocusSelectedSceneObject()
    {
        if(SelectedSceneObject is not {} item)return;
        var source=item.Source.Surface.Snapshot;Vector3 min=new(float.MaxValue),max=new(float.MinValue);
        foreach(uint index in source.Indices){var p=Vector3.Transform(source.Vertices[(int)index].Position,source.World*item.World);min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
        camera=new(min,max);ResetCamera();Status="镜头已聚焦所选物体；场景与灯光保持原位。";
    }
    private void ApplySceneComposition(bool refit)
    {
        var frame=sceneComposition!.Frame;
        surface=frame.Surface;outline=frame.Outline;flags=frame.Materials.ToArray();cachedFrame=null;fittedShadow=fittedRoomShadow=null;
        demoRoom=null;roomCamera=null;roomEnabled=false;shadowFitEnabled=true;
        if(GiEnabled)TrySelectGiMode(LightingMode.Legacy);
        if(refit)
        {
            Vector3 min=new(float.MaxValue),max=new(float.MinValue);
            foreach(uint index in surface.Snapshot.Indices){var p=surface.Snapshot.Vertices[(int)index].Position;min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
            camera=new(min,max);ResetCamera();
        }
        animationSourceModel=SceneObjects.Count==1&&SceneObjects[0].SourcePath==ModelPath&&SceneObjects[0].World==Matrix4x4.Identity
            ?compositionPrimaryModel:null;
        ModelSummary=$"PMX 场景 · {SceneObjects.Count} 个物体\n{surface.Snapshot.Vertices.Length:N0} 顶点 / {surface.Snapshot.Indices.Length/3:N0} 三角面\n列表选中，面板调整位置／旋转／缩放";
        NotifySceneObjects();NotifyLights();NotifyMotionState();Changed(nameof(ShadowFitEnabled));
    }
    private void NotifySceneObjects(){Changed(nameof(SceneObjects));Changed(nameof(SelectedSceneObject));Changed(nameof(HasEditableScene));SceneObjectsChanged?.Invoke();}
}
