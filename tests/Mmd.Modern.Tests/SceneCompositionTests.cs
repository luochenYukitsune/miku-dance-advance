using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SceneCompositionTests
{
    [Fact] public void MultipleObjectsRetainSourceOrderAndIndependentTransforms()
    {
        var source=EngineShowcaseScene.Create().Frame;var editor=new SceneCompositionEditor();
        var first=editor.Add("character","character.pmx",ModelRole.Character,source);
        Assert.Same(source.Surface,editor.Frame.Surface);
        Assert.Same(source.Outline,editor.Frame.Outline);
        Assert.Same(source.Options,editor.Frame.Options);
        Assert.All(editor.Frame.Materials,flags=>Assert.Equal(RayGiToonResponse.PreservePaletteBands,flags.GiToonResponseOverride));
        Assert.All(source.Materials,flags=>Assert.Null(flags.GiToonResponseOverride));
        Assert.Same(editor.Frame,editor.Frame);
        var second=editor.Add("environment","room.pmx",ModelRole.Environment,source);
        editor.SetTransform(second.Id,new(10,2,-3),Quaternion.CreateFromAxisAngle(Vector3.UnitY,.5f),2);
        var combined=editor.Frame;
        Assert.Equal(source.Surface.Snapshot.Vertices.Length*2,combined.Surface.Snapshot.Vertices.Length);
        Assert.Equal(source.Surface.Snapshot.Vertices[0],combined.Surface.Snapshot.Vertices[0]);
        var expected=Vector3.Transform(source.Surface.Snapshot.Vertices[0].Position,Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationY(.5f)*Matrix4x4.CreateTranslation(10,2,-3));
        Assert.InRange(Vector3.Distance(expected,combined.Surface.Snapshot.Vertices[source.Surface.Snapshot.Vertices.Length].Position),0,1e-5f);
        Assert.Equal(source.Surface.Snapshot.Materials[0].IndexCount,combined.Surface.Snapshot.Materials[0].IndexCount);
        Assert.Equal(source.Surface.Snapshot.Indices.Length,combined.Surface.Snapshot.Materials[source.Surface.Materials.Count].StartIndex);
        Assert.Equal(source.Surface.Snapshot.Vertices[0].Position,source.Outline!.Vertices[0].Position);
        Assert.Same(combined,editor.Frame);Assert.Equal(second.Id,editor.SelectedId);Assert.NotEqual(first.Id,second.Id);
    }
    [Fact] public void SelectionUndoAndRejectedTransformDoNotCorruptOtherObjects()
    {
        var source=EngineShowcaseScene.Create().Frame;var editor=new SceneCompositionEditor();
        var first=editor.Add("one","same.pmx",ModelRole.Character,source);var second=editor.Add("two","same.pmx",ModelRole.Prop,source);
        editor.SetTransform(first.Id,Vector3.UnitX,Quaternion.Identity,1);var moved=editor.Frame;
        Assert.Throws<ArgumentOutOfRangeException>(()=>editor.SetTransform(first.Id,Vector3.Zero,Quaternion.Identity,0));
        Assert.Same(moved,editor.Frame);editor.Undo();Assert.Equal(Vector3.Zero,editor.Objects[0].Position);
        editor.Redo();Assert.Equal(Vector3.UnitX,editor.Objects[0].Position);Assert.Equal(Vector3.Zero,editor.Objects[1].Position);
        editor.Remove(second.Id);Assert.Single(editor.Objects);editor.Undo();Assert.Equal(2,editor.Objects.Count);
    }

    [Fact] public void MainViewportAppendsPmxPreservesLightsAndMovesOnlySelectedObject()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"fixtures","rich-w1-uv4.pmx");
        using var vm=new FrameViewportViewModel(new(Enabled:true));vm.LoadModel(path);
        vm.AddLight(SceneLightKind.Point);var light=vm.LightEditor.Lights[0];var original=vm.GetFrame(800,600);
        using(var prepared=vm.PrepareModel(path))vm.AddPreparedSceneModel(prepared,ModelRole.Environment);
        Assert.Equal(2,vm.SceneObjects.Count);Assert.Equal(2,vm.SceneModels.Count);Assert.Equal(light,vm.LightEditor.Lights[0]);
        var firstCount=original.Surface.Snapshot.Vertices.Length;
        vm.TransformSelectedSceneObject(new(3,2,1),Quaternion.Identity,1);var moved=vm.GetFrame(800,600);
        Assert.Equal(original.Surface.Snapshot.Vertices[0].Position,moved.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(original.Surface.Snapshot.Vertices[0].Position+new Vector3(3,2,1),moved.Surface.Snapshot.Vertices[firstCount].Position);
        Assert.Equal(moved.Surface.Snapshot.Vertices[firstCount].Position,moved.Outline!.Vertices[firstCount].Position);
        vm.UndoSceneObjectEdit();Assert.Equal(original.Surface.Snapshot.Vertices[0].Position,vm.GetFrame(800,600).Surface.Snapshot.Vertices[firstCount].Position);
        vm.RedoSceneObjectEdit();Assert.Equal(new Vector3(3,2,1),vm.SelectedSceneObject!.Position);
    }

    [Fact] public void UndoingAppendRestoresStandaloneMotionWithoutAllowingInvalidMixedAnimation()
    {
        string path=Path.Combine(Path.GetTempPath(),"scene-motion-"+Guid.NewGuid()+".pmx");
        using(var writer=new BinaryWriter(File.Create(path),System.Text.Encoding.UTF8))
        {
            void Text(string text){var bytes=System.Text.Encoding.UTF8.GetBytes(text);writer.Write(bytes.Length);writer.Write(bytes);}
            void V(Vector3 value){writer.Write(value.X);writer.Write(value.Y);writer.Write(value.Z);}
            writer.Write("PMX "u8);writer.Write(2f);writer.Write((byte)8);writer.Write(new byte[]{1,0,1,1,1,1,1,1});
            Text("scene test");Text("");Text("");Text("");writer.Write(3);
            foreach(var p in new[]{Vector3.Zero,Vector3.UnitX,Vector3.UnitY}){V(p);V(-Vector3.UnitZ);writer.Write(0f);writer.Write(0f);writer.Write((byte)0);writer.Write((sbyte)0);writer.Write(1f);}
            writer.Write(3);writer.Write(new byte[]{0,1,2});writer.Write(0);writer.Write(1);Text("mat");Text("");
            writer.Write(1f);writer.Write(1f);writer.Write(1f);writer.Write(1f);V(Vector3.Zero);writer.Write(1f);V(new(.4f));writer.Write((byte)0x1e);
            V(Vector3.Zero);writer.Write(1f);writer.Write(1f);writer.Write((sbyte)-1);writer.Write((sbyte)-1);writer.Write((byte)0);writer.Write((byte)0);writer.Write((sbyte)-1);Text("");writer.Write(3);
            writer.Write(1);Text("root");Text("");V(Vector3.Zero);writer.Write((sbyte)-1);writer.Write(0);writer.Write((ushort)0);V(Vector3.UnitY);
            for(int i=0;i<4;i++)writer.Write(0);
        }
        using var vm=new FrameViewportViewModel(new(Enabled:true));vm.LoadModel(path);vm.UndoSceneObjectEdit();Assert.Single(vm.SceneObjects);
        using(var candidate=vm.PrepareModel(path))vm.AddPreparedSceneModel(candidate,ModelRole.Environment);
        using var input=File.OpenRead(path);var model=PmxReader.Read(input);
        var motion=new VmdMotion{BoneKeyframes=[new(){BoneName=model.Bones[0].Name,Frame=0}]};
        Assert.Throws<InvalidOperationException>(()=>vm.LoadMotion(motion));
        vm.UndoSceneObjectEdit();vm.MotionPhysicsEnabled=false;vm.LoadMotion(motion);
        Assert.True(vm.HasMotion);Assert.False(vm.HasEditableScene);
    }
}
