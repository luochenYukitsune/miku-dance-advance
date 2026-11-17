using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mmd.Modern.Application;

namespace Mmd.Modern.Demo;

/// <summary>Object placement UI; source frames and undo live in Application.</summary>
internal static class SceneObjectControls
{
    public static Control Create(FrameViewportWindow owner,FrameViewportViewModel vm)
    {
        var panel=new StackPanel{Spacing=6};bool refreshing=false;
        async Task Add(ModelRole role)
        {
            var files=await owner.StorageProvider.OpenFilePickerAsync(new(){Title=role==ModelRole.Environment?"追加场景 PMX":"追加角色 PMX",AllowMultiple=false,
                FileTypeFilter=[new("PMX 模型"){Patterns=["*.pmx"]}]});
            if(files.FirstOrDefault()?.TryGetLocalPath() is not {} path)return;
            owner.Viewport.Suspend(true);panel.IsEnabled=false;
            try
            {
                vm.Status="正在准备追加模型，保留当前场景…";
                using var candidate=await Task.Run(()=>vm.PrepareModel(path));vm.AddPreparedSceneModel(candidate,role);owner.Viewport.Restart();
            }
            catch(Exception error){vm.Status=$"追加失败，当前场景保留：{error.Message}";}
            finally{panel.IsEnabled=true;owner.Viewport.Suspend(false);}
        }
        var addCharacter=new Button{Name="SceneAddCharacter",Content="追加角色 PMX…",HorizontalAlignment=HorizontalAlignment.Stretch};
        addCharacter.Click+=async(_,_)=>await Add(ModelRole.Character);panel.Children.Add(addCharacter);
        var addRoom=new Button{Name="SceneAddEnvironment",Content="追加场景 PMX…",HorizontalAlignment=HorizontalAlignment.Stretch};
        addRoom.Click+=async(_,_)=>await Add(ModelRole.Environment);panel.Children.Add(addRoom);
        var objects=new ComboBox{Name="SceneObjectSelection",HorizontalAlignment=HorizontalAlignment.Stretch};panel.Children.Add(objects);
        objects.SelectionChanged+=(_,_)=>{if(!refreshing)vm.SelectSceneObject((objects.SelectedItem as EditableSceneObject)?.Id);};
        var fields=new StackPanel{Spacing=5};panel.Children.Add(fields);
        NumericUpDown Number(string name,string title,decimal min,decimal max,decimal step)
        {
            fields.Children.Add(new TextBlock{Text=title,FontSize=12});
            var value=new NumericUpDown{Name=name,Minimum=min,Maximum=max,Increment=step,FormatString="0.00",HorizontalAlignment=HorizontalAlignment.Stretch};
            fields.Children.Add(value);return value;
        }
        var x=Number("SceneObjectX","位置 X",-100000,100000,.1m);var y=Number("SceneObjectY","位置 Y",-100000,100000,.1m);var z=Number("SceneObjectZ","位置 Z",-100000,100000,.1m);
        var yaw=Number("SceneObjectYaw","水平旋转（度）",-360,360,1);var scale=Number("SceneObjectScale","等比缩放",.001m,1000,.05m);
        void Apply()
        {
            if(refreshing||vm.SelectedSceneObject is null)return;
            try
            {
                vm.TransformSelectedSceneObject(new((float)(x.Value??0),(float)(y.Value??0),(float)(z.Value??0)),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY,(float)(yaw.Value??0)*MathF.PI/180),(float)(scale.Value??1));owner.Viewport.Restart();
            }
            catch(ArgumentException error){vm.Status=$"变换未应用：{error.Message}";Refresh();}
        }
        foreach(var value in new[]{x,y,z,yaw,scale})value.ValueChanged+=(_,_)=>Apply();
        void Button(string name,string title,Action command)
        {
            var button=new Button{Name=name,Content=title,HorizontalAlignment=HorizontalAlignment.Stretch};
            button.Click+=(_,_)=>{try{command();owner.Viewport.Restart();}catch(InvalidOperationException error){vm.Status=error.Message;}};fields.Children.Add(button);
        }
        Button("SceneFocusObject","镜头聚焦所选物体",vm.FocusSelectedSceneObject);
        Button("SceneResetObject","还原所选物体变换",()=>vm.TransformSelectedSceneObject(Vector3.Zero,Quaternion.Identity,1));
        Button("SceneUndoObject","撤销物体编辑",vm.UndoSceneObjectEdit);Button("SceneRedoObject","重做物体编辑",vm.RedoSceneObjectEdit);
        Button("SceneRemoveObject","移除所选物体",vm.RemoveSelectedSceneObject);
        panel.Children.Add(new TextBlock{Text="追加保留其他物体与灯光；打开 PMX 则替换场景。\n当前列表选中、面板变换；多物体动作及视口物体手柄后续接入。",FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        void Refresh()
        {
            if(!Dispatcher.UIThread.CheckAccess()){Dispatcher.UIThread.Post(Refresh);return;}
            refreshing=true;
            try
            {
                objects.ItemsSource=vm.SceneObjects;objects.SelectedItem=vm.SelectedSceneObject;fields.IsEnabled=vm.HasEditableScene;
                if(vm.SelectedSceneObject is {} item)
                {
                    x.Value=(decimal)item.Position.X;y.Value=(decimal)item.Position.Y;z.Value=(decimal)item.Position.Z;scale.Value=(decimal)item.Scale;
                    var rotation=Matrix4x4.CreateFromQuaternion(item.Rotation);yaw.Value=(decimal)(MathF.Atan2(rotation.M31,rotation.M33)*180/MathF.PI);
                }
            }
            finally{refreshing=false;}
        }
        vm.SceneObjectsChanged+=Refresh;owner.Closed+=(_,_)=>vm.SceneObjectsChanged-=Refresh;Refresh();return panel;
    }
}
