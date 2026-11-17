using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

using Mmd.Modern.App;

namespace Mmd.Modern.Demo;

public sealed record ViewportModelPurpose(string Key,string Name);

/// <summary>Native light editing orchestration; persistent state and undo transactions live in Application.</summary>
public sealed partial class FrameViewportViewModel
{
    public SceneLightingEditor LightEditor {get;private set;}=new();
    public event Action? LightsChanged;
    private bool lightEditingEnabled;
    private (NativeSceneLight Light,Vector2 Mouse,SceneCamera Camera,int Width,int Height,LightDragAxis Axis,bool Rotate)? lightDrag;
    public bool LightEditingEnabled {get=>lightEditingEnabled;set{lightEditingEnabled=value;Invalidate();}}
    public NativeSceneLight? SelectedLight=>LightEditor.Lights.FirstOrDefault(l=>l.Id==LightEditor.SelectedId);
    public IReadOnlyList<ViewportModelPurpose> SceneModels=>sceneComposition is {} composition
        ?composition.Objects.Select(item=>new ViewportModelPurpose(item.Key,item.Name)).ToArray():projectPlayback is {} p
        ?p.Plan.Models.Select(m=>new ViewportModelPurpose("model:"+Path.GetFullPath(m.Path).Replace('\\','/').ToUpperInvariant()+"#"+m.Source.Index,m.Model.Name)).ToArray()
        :HasEngineDemo?[new("demo:room","原创室内场景")]:ModelPath is {} path?[new(path,Path.GetFileNameWithoutExtension(path))]:[];
    public void SelectLight(Guid? id){LightEditor.Select(id);lightEditingEnabled=id.HasValue;NotifyLights();}
    public void AddLight(SceneLightKind kind)
    {
        if(camera is null)return;
        try
        {
            float span=camera.Span;
            var light=new NativeSceneLight(Guid.NewGuid(),$"{kind} {LightEditor.Lights.Count+1}",kind,
                camera.Center+new Vector3(0,span*.25f,-span*.1f),Quaternion.CreateFromYawPitchRoll(0,-.75f,0),Vector3.One,
                kind==SceneLightKind.Directional?.3f:span*span*.05f,span*1.5f,
                AreaSize:kind==SceneLightKind.Area?new Vector2(span*.25f):null);
            LightEditor.Add(light);LightEditingEnabled=true;NotifyLights();
        }
        catch(ArgumentException error){Status=$"无法添加光源：{error.Message}";}
    }
    public void UpdateSelectedLight(Func<NativeSceneLight,NativeSceneLight> update)
    {
        if(SelectedLight is not {} selected)return;
        try{LightEditor.Update(update(selected));NotifyLights();}
        catch(ArgumentException error){Status=$"光源参数无效：{error.Message}";NotifyLights();}
    }
    public void DeleteSelectedLight(){if(LightEditor.SelectedId is {} id)LightEditor.Remove(id);NotifyLights();}
    public void UndoLightEdit(){if(!LightEditor.IsEditing)LightEditor.Undo();NotifyLights();}
    public void RedoLightEdit(){if(!LightEditor.IsEditing)LightEditor.Redo();NotifyLights();}
    public void SetScenePurpose(string key,ModelRole role,bool? indoor=null){LightEditor.SetModelRole(key,role,indoor);NotifyLights();}
    public void SaveSceneLighting(string path){LightEditor.SaveSidecar(path);Status="已保存灯光和用途；原 PMM／PMX 未修改。";}
    public bool LoadSceneLighting(string path)
    {
        bool result=LightEditor.TryLoadSidecarFile(path,out var error);if(!result)Status=$"灯光文件未加载，保留当前设置：{error}";
        else{lightEditingEnabled=LightEditor.SelectedId.HasValue;Status="灯光与场景用途已恢复。";NotifyLights();}return result;
    }
    public bool BeginLightDrag(int x,int y,int width,int height,bool rotate)
    {
        if(!LightEditingEnabled||LightEditor.IsEditing)return false;
        var frame=GetFrame(width,height);var selectedCamera=frame.Options!.CameraOverride!;
        var hit=LightViewportInteraction.HitTest(LightEditor.Snapshot,LightEditor.SelectedId,new(x,y),selectedCamera,width,height,GizmoSize);
        if(hit is null)return false;
        SelectLight(hit.LightId);var selected=SelectedLight!;
        LightEditor.BeginEdit();lightDrag=(selected,new(x,y),selectedCamera,width,height,hit.Axis,rotate);return true;
    }
    public void UpdateLightDrag(int x,int y)
    {
        if(lightDrag is not {} drag)return;var delta=new Vector2(x,y)-drag.Mouse;
        var updated=drag.Rotate?drag.Light with{Rotation=LightViewportInteraction.Rotate(drag.Light.Rotation,delta,drag.Camera)}:
            drag.Light with{Position=LightViewportInteraction.Translate(drag.Light.Position,delta,drag.Camera,drag.Width,drag.Height,drag.Axis)};
        LightEditor.Update(updated);NotifyLights();
    }
    public void EndLightDrag(bool commit)
    {
        if(lightDrag is null)return;lightDrag=null;
        if(commit)LightEditor.CommitEdit();else LightEditor.CancelEdit();NotifyLights();
    }
    private float GizmoSize=>MathF.Max(.1f,(roomEnabled?roomCamera:camera)?.Span*.10f??1);
    private ViewportOverlaySnapshot? LightOverlay(SceneCamera selectedCamera,int width,int height)=>!LightEditingEnabled?null:
        LightViewportInteraction.CreateOverlay(LightEditor.Snapshot,LightEditor.SelectedId,selectedCamera,width,height,GizmoSize);
    private void ResetSceneLighting()
    {
        EndLightDrag(false);LightEditor=new();lightEditingEnabled=false;NotifyLights();
    }
    private void ResetSceneState(){ResetAnimationState();engineDemo?.Dispose();engineDemo=null;sceneComposition=null;compositionPrimaryModel=null;ResetSceneLighting();NotifySceneObjects();}
    private void NotifyLights(){cachedFrame=null;Changed(nameof(LightEditingEnabled));Changed(nameof(SelectedLight));LightsChanged?.Invoke();}
}
