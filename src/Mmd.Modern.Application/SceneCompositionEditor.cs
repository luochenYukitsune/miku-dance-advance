using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed record EditableSceneObject(Guid Id,string Name,string SourcePath,ModelRole Role,OriginalFrameScene Source,
    Vector3 Position,Quaternion Rotation,float Scale)
{
    /// <summary>True for a source-derived name, false for an authored alias; null retains unknown legacy provenance.</summary>
    public bool? NameFollowsSource { get; init; }
    public Matrix4x4 World=>Matrix4x4.CreateScale(Scale)*Matrix4x4.CreateFromQuaternion(Rotation)*Matrix4x4.CreateTranslation(Position);
    public string Key=>"scene-object:"+Id.ToString("D");
    public override string ToString()=>$"{Name} · {Role switch{ModelRole.Character=>"角色",ModelRole.Environment=>"场景",_=>"物体"}}";
}

/// <summary>Editable object placements over immutable format-neutral frames; no file I/O, GUI or device dependencies.</summary>
public sealed partial class SceneCompositionEditor
{
    private record State(EditableSceneObject[] Objects,Guid? Selected);
    private readonly List<EditableSceneObject> objects=[];
    private readonly Stack<State> undo=new(),redo=new();
    private OriginalFrameScene? cached;
    public ReadOnlyCollection<EditableSceneObject> Objects=>objects.AsReadOnly();
    public Guid? SelectedId {get;private set;}
    public EditableSceneObject? Selected=>objects.FirstOrDefault(o=>o.Id==SelectedId);
    public bool CanUndo=>undo.Count>0;
    public bool CanRedo=>redo.Count>0;
    internal static SceneCompositionEditor FromObjects(IEnumerable<EditableSceneObject> items,Guid? selected)
    {
        var candidate=items.ToArray();
        if(candidate.Length==0||candidate.Any(item=>item is null||item.Id==Guid.Empty)
            ||candidate.Select(item=>item.Id).Distinct().Count()!=candidate.Length)
            throw new ArgumentException("A scene requires non-empty, unique object identities.");
        var result=new SceneCompositionEditor();
        foreach(var item in candidate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Name);ArgumentException.ThrowIfNullOrWhiteSpace(item.SourcePath);
            if(item.Source?.Outline is null||!Enum.IsDefined(item.Role))throw new ArgumentException("An object requires a source frame with outline geometry and a valid role.");
            ValidateTransform(item.Position,item.Rotation,item.Scale);
        }
        result.objects.AddRange(candidate);result.Select(selected);return result;
    }
    public EditableSceneObject Add(string name,string sourcePath,ModelRole role,OriginalFrameScene frame,bool? nameFollowsSource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);ArgumentNullException.ThrowIfNull(frame);
        if(frame.Outline is null||!Enum.IsDefined(role))throw new ArgumentException("An editable object needs valid role and matching outline geometry.");
        var item=new EditableSceneObject(Guid.NewGuid(),name,sourcePath,role,frame,Vector3.Zero,Quaternion.Identity,1)
            { NameFollowsSource = nameFollowsSource };
        Save();objects.Add(item);SelectedId=item.Id;cached=null;return item;
    }
    public void Select(Guid? id)
    {if(id.HasValue&&!objects.Any(o=>o.Id==id))throw new ArgumentException("Unknown scene object.");SelectedId=id;}
    public void SetTransform(Guid id,Vector3 position,Quaternion rotation,float scale)
    {
        ValidateTransform(position,rotation,scale);
        int index=Index(id);var updated=objects[index] with{Position=position,Rotation=rotation,Scale=scale};
        if(updated==objects[index])return;Save();objects[index]=updated;cached=null;
    }
    internal static void ValidateTransform(Vector3 position,Quaternion rotation,float scale)
    {
        if(!float.IsFinite(position.X)||!float.IsFinite(position.Y)||!float.IsFinite(position.Z)||!float.IsFinite(scale)||scale<=0
            ||!float.IsFinite(rotation.LengthSquared())||MathF.Abs(rotation.LengthSquared()-1)>1e-4f)throw new ArgumentOutOfRangeException(nameof(position));
    }
    public void Remove(Guid id)
    {
        int index=Index(id);if(objects.Count==1)throw new InvalidOperationException("The scene must keep at least one object.");
        Save();objects.RemoveAt(index);if(SelectedId==id)SelectedId=objects[Math.Min(index,objects.Count-1)].Id;cached=null;
    }
    public void Undo(){if(undo.Count==0)return;redo.Push(Snapshot());Restore(undo.Pop());}
    public void Redo(){if(redo.Count==0)return;undo.Push(Snapshot());Restore(redo.Pop());}
    public void ClearHistory(){undo.Clear();redo.Clear();}
    private int Index(Guid id){int index=objects.FindIndex(o=>o.Id==id);return index>=0?index:throw new ArgumentException("Unknown scene object.");}
    private State Snapshot()=>new(objects.ToArray(),SelectedId);
    private void Save(){undo.Push(Snapshot());redo.Clear();}
    private void Restore(State state){objects.Clear();objects.AddRange(state.Objects);SelectedId=state.Selected;cached=null;}
    public OriginalFrameScene Frame
    {
        get
        {
            if(cached is not null)return cached;if(objects.Count==0)throw new InvalidOperationException("Load an object before composing the scene.");
            if(objects.Count==1&&objects[0].World==Matrix4x4.Identity)
                return cached=SceneModelShadingPolicy.Apply(objects[0].Source,objects[0].Role);
            var placed=objects.Select(item=>
            {
                var input=SceneModelShadingPolicy.Apply(item.Source,item.Role);var snapshot=input.Surface.Snapshot;
                var transformed=new SceneSnapshot(snapshot.Vertices,snapshot.Indices,snapshot.Materials,snapshot.Textures,
                    world:snapshot.World*item.World,camera:snapshot.Camera,lightDirection:snapshot.LightDirection,
                    additionalUvs:snapshot.AdditionalUvs,rawRadialSeeds:snapshot.RawRadialSeeds);
                return new OriginalFrameScene(new(transformed,input.Surface.Materials,input.Surface.Profile),input.Outline,input.OutlineOptions,materials:input.Materials);
            }).ToArray();
            var first=objects[0].Source.Surface;
            var combined=ProjectSceneAssembler.Combine(placed,first.Profile.LightAmbient,first.Snapshot.LightDirection);
            return cached=new(new(combined.Surface.Snapshot,combined.Surface.Materials,first.Profile),combined.Outline,combined.OutlineOptions,materials:combined.Materials);
        }
    }
}
