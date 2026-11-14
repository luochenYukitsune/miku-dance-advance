using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public sealed partial class FrameViewportWindow
{
    private void AddEngineDemoSettings(StackPanel tools)
    {
        var vm=ViewModel;
        StackPanel Group(string name,string title,bool expanded=false)
        {
            var content=new StackPanel{Spacing=7};
            tools.Children.Add(new Expander{Name=name,Header=title,Content=content,IsExpanded=expanded,HorizontalAlignment=HorizontalAlignment.Stretch});
            return content;
        }
        Slider Slider(StackPanel content,string name,string label,string property,double initial,double min,double max,double step)
        {
            var slider=AddSlider(content,label,vm,property,initial,min,max);slider.Name=name;
            slider.SmallChange=step;slider.LargeChange=step*10;
            slider.PropertyChanged+=(_,args)=>{if(args.Property==RangeBase.ValueProperty)Viewport.Restart();};
            var value=new NumericUpDown{Name=name+"Value",Minimum=(decimal)min,Maximum=(decimal)max,Increment=(decimal)step,FormatString="0.00",HorizontalAlignment=HorizontalAlignment.Stretch};
            value.Bind(NumericUpDown.ValueProperty,new Binding(property){Source=vm,Mode=BindingMode.TwoWay});
            value.ValueChanged+=(_,_)=>Viewport.Restart();content.Children.Add(value);
            return slider;
        }
        ComboBox Choice(StackPanel content,string name,string label,string property,string[] values)
        {
            content.Children.Add(new TextBlock{Text=label,FontSize=12});
            var choice=new ComboBox{Name=name,ItemsSource=values,HorizontalAlignment=HorizontalAlignment.Stretch};
            choice.Bind(SelectingItemsControl.SelectedIndexProperty,new Binding(property){Source=vm,Mode=BindingMode.TwoWay});
            choice.SelectionChanged+=(_,_)=>Viewport.Restart();content.Children.Add(choice);return choice;
        }
        void Toggle(StackPanel content,string name,string label,string property,string? available=null)
        {
            var checkbox=new CheckBox{Name=name,Content=label};
            checkbox.Bind(ToggleButton.IsCheckedProperty,new Binding(property){Source=vm,Mode=BindingMode.TwoWay});
            if(available is not null)checkbox.Bind(IsEnabledProperty,new Binding(available){Source=vm});
            checkbox.IsCheckedChanged+=(_,_)=>Viewport.Restart();content.Children.Add(checkbox);
        }

        var camera=Group("DemoCameraSettings","镜头 · 旋转／平移／缩放");
        Slider(camera,"DemoCameraYaw","水平角度",nameof(vm.Yaw),vm.Yaw,-180,180,1);
        Slider(camera,"DemoCameraPitch","俯仰角度",nameof(vm.Pitch),vm.Pitch,-85,85,1);
        Slider(camera,"DemoCameraZoom","缩放",nameof(vm.Zoom),vm.Zoom,.15,8,.05);
        Slider(camera,"DemoCameraPanX","水平平移",nameof(vm.PanX),vm.PanX,-2,2,.02);
        Slider(camera,"DemoCameraPanY","垂直平移",nameof(vm.PanY),vm.PanY,-2,2,.02);
        var reset=new Button{Name="DemoResetCamera",Content="重置镜头",HorizontalAlignment=HorizontalAlignment.Stretch};
        reset.Click+=(_,_)=>{vm.ResetCamera();Viewport.Restart();};camera.Children.Add(reset);

        var surface=Group("DemoSurfaceSettings","材质与画风 · 环境光／高光／描边",true);
        Toggle(surface,"DemoOutline","描边",nameof(vm.OutlineEnabled));
        Slider(surface,"DemoOutlineWidth","描边宽度倍率",nameof(vm.OutlineWidthScale),vm.OutlineWidthScale,0,3,.05);
        Slider(surface,"DemoOpacity","整体透明度",nameof(vm.MaterialOpacity),vm.MaterialOpacity,0,1,.05);
        surface.Children.Add(new TextBlock{Text="整体透明度保留纹理镂空；低于 1 时关闭 GI。逐材质编辑后续接入。",FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        Toggle(surface,"DemoSelfShadow","自阴影",nameof(vm.SelfShadowEnabled));
        Toggle(surface,"DemoShadowFit","自阴影覆盖整个场景",nameof(vm.ShadowFitEnabled));
        Toggle(surface,"DemoGroundShadow","地面投影",nameof(vm.GroundShadowEnabled));
        Slider(surface,"DemoAmbient","环境光强度",nameof(vm.AmbientStrength),vm.AmbientStrength,0,2,.05);
        Slider(surface,"DemoSpecular","高光强度",nameof(vm.SpecularStrength),vm.SpecularStrength,0,2,.05);
        var resetAppearance=new Button{Name="DemoResetAppearance",Content="还原材质与画风参数",HorizontalAlignment=HorizontalAlignment.Stretch};
        resetAppearance.Click+=(_,_)=>{vm.ResetAppearanceSettings();Viewport.Restart();};surface.Children.Add(resetAppearance);
        surface.Children.Add(new TextBlock{Text="1 为模型原有基础照明；局部彩色灯在下方光源编辑中调整。",FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap});

        var indirect=Group("DemoGiSettings","间接光 GI · 光追与画质");
        var software=new ComboBoxItem{Content="GPU 软件 BVH",IsEnabled=vm.SoftwareGiAvailable};
        var hardware=new ComboBoxItem{Content="DXR 硬件 GI",IsEnabled=vm.HardwareGiAvailable};
        static int ModeIndex(LightingMode mode)=>mode switch{LightingMode.SoftwareRayTracing=>1,LightingMode.HardwareRayTracing=>2,_=>0};
        var mode=new ComboBox{Name="DemoGiMode",ItemsSource=new[]{new ComboBoxItem{Content="原版光照（GI Off）"},software,hardware},SelectedIndex=ModeIndex(vm.GiMode),HorizontalAlignment=HorizontalAlignment.Stretch};
        bool updating=false;
        mode.SelectionChanged+=(_,_)=>
        {
            if(updating)return;
            var requested=mode.SelectedIndex switch{1=>LightingMode.SoftwareRayTracing,2=>LightingMode.HardwareRayTracing,_=>LightingMode.Legacy};
            if(!vm.TrySelectGiMode(requested)){updating=true;mode.SelectedIndex=ModeIndex(vm.GiMode);updating=false;}
            Viewport.Restart();
        };
        System.ComponentModel.PropertyChangedEventHandler updateMode=(_,args)=>
        {
            if(args.PropertyName==nameof(vm.GiMode)){updating=true;mode.SelectedIndex=ModeIndex(vm.GiMode);updating=false;Viewport.Restart();}
            if(args.PropertyName is nameof(vm.SoftwareGiAvailable) or nameof(vm.HardwareGiAvailable))
            {software.IsEnabled=vm.SoftwareGiAvailable;hardware.IsEnabled=vm.HardwareGiAvailable;}
        };
        vm.PropertyChanged+=updateMode;Closed+=(_,_)=>vm.PropertyChanged-=updateMode;
        indirect.Children.Add(mode);
        Slider(indirect,"DemoGiStrength","间接光强度",nameof(vm.GiStrength),vm.GiStrength,0,12,.1);
        Choice(indirect,"DemoGiQuality","每像素采样数",nameof(vm.GiQualityIndex),["低 · 4 SPP（交互）","中 · 16 SPP","高 · 64 SPP（较慢）"]);
        Choice(indirect,"DemoGiComposition","合成方式",nameof(vm.GiCompositionIndex),["旧版叠加","三渲二受限反弹"]);
        Toggle(indirect,"DemoGiDenoise","补光降噪（保边空间）",nameof(vm.GiDenoiseEnabled),nameof(vm.GiDenoiseAvailable));
        Toggle(indirect,"DemoGiNearOcclusion","近场遮蔽（实验，可能改变色块）",nameof(vm.NearOcclusionEnabled));
        var availability=new TextBlock{FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        availability.Bind(TextBlock.TextProperty,new Binding(nameof(vm.GiAvailabilityDetails)));indirect.Children.Add(availability);

        var antialiasing=Group("DemoAntialiasingSettings","抗锯齿");
        Choice(antialiasing,"DemoAntialiasing","当前帧抗锯齿",nameof(vm.AntialiasingIndex),["Off","SMAA1x"]);
        antialiasing.Children.Add(new TextBlock{Text="SMAA 与 GI 独立，光追关闭时也能使用。",FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
    }
}
