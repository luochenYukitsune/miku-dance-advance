using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.Demo;

internal static class NativeLightControls
{
    internal static Control Create(Window owner,FrameViewportViewModel vm)
    {
        var panel=new StackPanel{Spacing=7};bool updating=false;
        var model=new ComboBox{Name="ScenePurposeModel",DisplayMemberBinding=new Binding("Name"),HorizontalAlignment=HorizontalAlignment.Stretch};
        var roles=new ComboBox{Name="SceneModelRole",ItemsSource=new[]{"未指定","角色","场景","道具"},HorizontalAlignment=HorizontalAlignment.Stretch};
        var indoors=new CheckBox{Content="室内场景（手动指定）",Name="SceneIndoor"};
        panel.Children.Add(new TextBlock{Text="PMX 用途（请明确指定）",FontSize=12});panel.Children.Add(model);panel.Children.Add(roles);panel.Children.Add(indoors);
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};
        foreach(var kind in new[]{SceneLightKind.Point,SceneLightKind.Spot,SceneLightKind.Directional})
        {var button=new Button{Content=kind switch{SceneLightKind.Point=>"＋点光",SceneLightKind.Spot=>"＋聚光",_=>"＋方向"},Padding=new(5)};button.Click+=(_,_)=>vm.AddLight(kind);row.Children.Add(button);}panel.Children.Add(row);
        var lights=new ComboBox{Name="NativeLightSelection",DisplayMemberBinding=new Binding("Name"),HorizontalAlignment=HorizontalAlignment.Stretch};panel.Children.Add(lights);
        var gizmos=new CheckBox{Content="显示／操作光源手柄",Name="NativeLightGizmos"};gizmos.Bind(ToggleButton.IsCheckedProperty,new Binding(nameof(vm.LightEditingEnabled)){Source=vm,Mode=BindingMode.TwoWay});panel.Children.Add(gizmos);
        panel.Children.Add(new TextBlock{Text="左拖菱形：平面移动；拖彩轴：轴向移动；\n右拖光源：旋转；Esc取消这次拖动。",FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        var name=new TextBox{Watermark="光源名称"};panel.Children.Add(name);
        var kindChoice=new ComboBox{ItemsSource=new[]{"方向光","点光","聚光"},HorizontalAlignment=HorizontalAlignment.Stretch};panel.Children.Add(kindChoice);
        var enabled=new CheckBox{Content="启用光源"};var shadows=new CheckBox{Content="投射阴影（不含半透明透光）"};panel.Children.Add(enabled);panel.Children.Add(shadows);
        var inputs=new Dictionary<string,NumericUpDown>();
        void Number(string key,string title,decimal min,decimal max,decimal increment,Action<decimal> apply)
        {
            var grid=new Grid{ColumnDefinitions=new("90,*")};grid.Children.Add(new TextBlock{Text=title,FontSize=12,VerticalAlignment=VerticalAlignment.Center});
            var input=new NumericUpDown{Name="Light"+key,Minimum=min,Maximum=max,Increment=increment,FormatString="0.00",HorizontalAlignment=HorizontalAlignment.Stretch};
            Grid.SetColumn(input,1);grid.Children.Add(input);panel.Children.Add(grid);inputs.Add(key,input);
            input.ValueChanged+=(_,_)=>{if(!updating&&input.Value is {} value)apply(value);};
        }
        Number("X","位置 X",-100000,100000,.1m,v=>vm.UpdateSelectedLight(l=>l with{Position=l.Position with{X=(float)v}}));
        Number("Y","位置 Y",-100000,100000,.1m,v=>vm.UpdateSelectedLight(l=>l with{Position=l.Position with{Y=(float)v}}));
        Number("Z","位置 Z",-100000,100000,.1m,v=>vm.UpdateSelectedLight(l=>l with{Position=l.Position with{Z=(float)v}}));
        void Direction(bool yaw,float value)=>vm.UpdateSelectedLight(l=>
        {var d=l.Direction;float y=MathF.Atan2(-d.X,-d.Z),p=MathF.Asin(Math.Clamp(d.Y,-1,1));return l with{Rotation=Quaternion.CreateFromYawPitchRoll(yaw?value:y,yaw?p:value,0)};});
        Number("Yaw","方向水平°",-180,180,1,v=>Direction(true,(float)v*MathF.PI/180));
        Number("Pitch","方向俯仰°",-89.9m,89.9m,1,v=>Direction(false,(float)v*MathF.PI/180));
        Number("Intensity","强度",0,10000,.25m,v=>vm.UpdateSelectedLight(l=>l with{Intensity=(float)v}));
        Number("Range","影响范围",.001m,100000,.5m,v=>vm.UpdateSelectedLight(l=>l with{Range=(float)v}));
        Number("Red","红",0,10,.05m,v=>vm.UpdateSelectedLight(l=>l with{Color=l.Color with{X=(float)v}}));
        Number("Green","绿",0,10,.05m,v=>vm.UpdateSelectedLight(l=>l with{Color=l.Color with{Y=(float)v}}));
        Number("Blue","蓝",0,10,.05m,v=>vm.UpdateSelectedLight(l=>l with{Color=l.Color with{Z=(float)v}}));
        Number("InnerCone","内锥半角°",0,89.9m,1,v=>vm.UpdateSelectedLight(l=>l with{InnerConeRadians=(float)v*MathF.PI/180}));
        Number("OuterCone","外锥半角°",.01m,90,1,v=>vm.UpdateSelectedLight(l=>l with{OuterConeRadians=(float)v*MathF.PI/180}));
        var history=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};
        foreach(var (label,action) in new[]{("删除",(Action)vm.DeleteSelectedLight),("撤销",vm.UndoLightEdit),("重做",vm.RedoLightEdit)})
        {var button=new Button{Content=label};button.Click+=(_,_)=>action();history.Children.Add(button);}panel.Children.Add(history);
        var save=new Button{Content="保存灯光／用途…",HorizontalAlignment=HorizontalAlignment.Stretch};var load=new Button{Content="打开灯光／用途…",HorizontalAlignment=HorizontalAlignment.Stretch};panel.Children.Add(save);panel.Children.Add(load);
        save.Click+=async(_,_)=>{var file=await owner.StorageProvider.SaveFilePickerAsync(new(){Title="保存原生灯光与用途",SuggestedFileName="scene.lighting.json",FileTypeChoices=[new("灯光文件"){Patterns=["*.json"]}]});if(file?.TryGetLocalPath() is {} path)try{vm.SaveSceneLighting(path);}catch(Exception error){vm.Status=$"保存失败：{error.Message}";}};
        load.Click+=async(_,_)=>{var files=await owner.StorageProvider.OpenFilePickerAsync(new(){Title="打开原生灯光与用途",AllowMultiple=false,FileTypeFilter=[new("灯光文件"){Patterns=["*.json"]}]});if(files.FirstOrDefault()?.TryGetLocalPath() is {} path)vm.LoadSceneLighting(path);};
        void RefreshPurpose()
        {
            if(model.SelectedItem is not ViewportModelPurpose item)return;
            var purpose=vm.LightEditor.GetModelRole(item.Key);roles.SelectedIndex=(int)purpose.Role;indoors.IsEnabled=purpose.Role==ModelRole.Environment;indoors.IsChecked=purpose.EnvironmentIndoor==true;
        }
        void SavePurpose(){if(!updating&&model.SelectedItem is ViewportModelPurpose item&&roles.SelectedIndex>=0)vm.SetScenePurpose(item.Key,(ModelRole)roles.SelectedIndex,roles.SelectedIndex==(int)ModelRole.Environment?indoors.IsChecked:null);}
        model.SelectionChanged+=(_,_)=>{if(updating)return;updating=true;RefreshPurpose();updating=false;};
        roles.SelectionChanged+=(_,_)=>SavePurpose();indoors.IsCheckedChanged+=(_,_)=>SavePurpose();
        lights.SelectionChanged+=(_,_)=>{if(!updating)vm.SelectLight((lights.SelectedItem as NativeSceneLight)?.Id);};
        name.LostFocus+=(_,_)=>{if(!updating&&!string.IsNullOrWhiteSpace(name.Text))vm.UpdateSelectedLight(l=>l with{Name=name.Text});};
        kindChoice.SelectionChanged+=(_,_)=>{if(!updating&&kindChoice.SelectedIndex>=0)vm.UpdateSelectedLight(l=>l with{Kind=(SceneLightKind)kindChoice.SelectedIndex});};
        enabled.IsCheckedChanged+=(_,_)=>{if(!updating)vm.UpdateSelectedLight(l=>l with{Enabled=enabled.IsChecked==true});};
        shadows.IsCheckedChanged+=(_,_)=>{if(!updating)vm.UpdateSelectedLight(l=>l with{CastShadows=shadows.IsChecked==true});};
        void Refresh()
        {
            if(!Dispatcher.UIThread.CheckAccess()){Dispatcher.UIThread.Post(Refresh);return;}updating=true;
            var previousKey=(model.SelectedItem as ViewportModelPurpose)?.Key;var models=vm.SceneModels;model.ItemsSource=models;model.SelectedItem=models.FirstOrDefault(m=>m.Key==previousKey)??models.FirstOrDefault();RefreshPurpose();
            lights.ItemsSource=vm.LightEditor.Lights;lights.SelectedItem=vm.SelectedLight;
            var light=vm.SelectedLight;name.Text=light?.Name??"";kindChoice.SelectedIndex=light is null?-1:(int)light.Kind;
            enabled.IsChecked=light?.Enabled;shadows.IsChecked=light?.CastShadows;
            foreach(var input in inputs.Values)input.IsEnabled=light is not null;
            if(light is not null)
            {
                var d=light.Direction;float[] values=[light.Position.X,light.Position.Y,light.Position.Z,MathF.Atan2(-d.X,-d.Z)*180/MathF.PI,MathF.Asin(Math.Clamp(d.Y,-1,1))*180/MathF.PI,light.Intensity,light.Range,light.Color.X,light.Color.Y,light.Color.Z,light.InnerConeRadians*180/MathF.PI,light.OuterConeRadians*180/MathF.PI];
                int i=0;foreach(var input in inputs.Values)input.Value=Math.Clamp((decimal)values[i++],input.Minimum,input.Maximum);
            }
            updating=false;
        }
        vm.LightsChanged+=Refresh;owner.Closed+=(_,_)=>vm.LightsChanged-=Refresh;Refresh();return panel;
    }
}
