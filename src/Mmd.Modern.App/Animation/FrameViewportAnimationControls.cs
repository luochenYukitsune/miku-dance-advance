using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Mmd.Modern.App.Animation;

/// <summary>The viewer's motion panel; contains no parsing or animation mathematics.</summary>
internal static class FrameViewportAnimationControls
{
    internal static Control Create(FrameViewportWindow window,FrameViewportViewModel vm)
    {
        var panel=new StackPanel {Spacing=8};
        var open=new Button {Name="OpenMotion",Content="打开 VMD 骨骼动作…",HorizontalAlignment=HorizontalAlignment.Stretch};
        open.Bind(Control.IsEnabledProperty,new Binding(nameof(vm.HasScene)) {Source=vm});
        open.Click+=async (_,_)=>
        {
            var files=await window.StorageProvider.OpenFilePickerAsync(new() {Title="选择 VMD 动作",AllowMultiple=false,
                FileTypeFilter=[new("VMD 动作") {Patterns=["*.vmd"]}]});
            if(files.FirstOrDefault()?.TryGetLocalPath() is not {} path)return;
            try { vm.LoadMotion(path);window.Viewport.Restart(); }
            catch(Exception error) {vm.Status=$"动作载入失败：{error.Message}";}
        };
        panel.Children.Add(open);
        var summary=new TextBlock {FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        summary.Bind(TextBlock.TextProperty,new Binding(nameof(vm.MotionSummary)){Source=vm});panel.Children.Add(summary);
        var transport=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        foreach(var (name,label,action) in new (string,string,Action)[] {
            ("MotionPlay","播放",vm.PlayMotion),("MotionPause","暂停",vm.PauseMotion),("MotionUnload","卸载",vm.UnloadMotion)})
        {
            var button=new Button {Name=name,Content=label};
            button.Bind(Control.IsEnabledProperty,new Binding(nameof(vm.HasMotion)){Source=vm});
            button.Click+=(_,_)=>action();transport.Children.Add(button);
        }
        panel.Children.Add(transport);
        var ik=new CheckBox {Name="MotionIkEnabled",Content="IK（遵循 VMD 开关）"};
        ik.Bind(ToggleButton.IsCheckedProperty,new Binding(nameof(vm.MotionIkEnabled)){Source=vm,Mode=BindingMode.TwoWay});
        ik.Bind(Control.IsEnabledProperty,new Binding(nameof(vm.HasMotion)){Source=vm});panel.Children.Add(ik);
        var morphs=new CheckBox {Name="MotionMorphsEnabled",Content="VMD 表情（顶点／骨表情）"};
        morphs.Bind(ToggleButton.IsCheckedProperty,new Binding(nameof(vm.MotionMorphsEnabled)){Source=vm,Mode=BindingMode.TwoWay});
        morphs.Bind(Control.IsEnabledProperty,new Binding(nameof(vm.MotionMorphsAvailable)){Source=vm});panel.Children.Add(morphs);
        var physics=new CheckBox {Name="MotionPhysicsEnabled",Content="角色物理（Modern 实验／默认开）"};
        physics.Bind(ToggleButton.IsCheckedProperty,new Binding(nameof(vm.MotionPhysicsEnabled)){Source=vm,Mode=BindingMode.TwoWay});
        physics.Bind(Control.IsEnabledProperty,new Binding(nameof(vm.MotionPhysicsAvailable)){Source=vm});panel.Children.Add(physics);
        var resetPhysics=new Button {Name="MotionPhysicsReset",Content="在当前帧重置物理"};
        resetPhysics.Bind(Control.IsEnabledProperty,new Binding(nameof(vm.MotionPhysicsEnabled)){Source=vm});
        resetPhysics.Click+=(_,_)=>vm.ResetMotionPhysics();panel.Children.Add(resetPhysics);
        var physicsSummary=new TextBlock {FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        physicsSummary.Bind(TextBlock.TextProperty,new Binding(nameof(vm.MotionPhysicsSummary)){Source=vm});panel.Children.Add(physicsSummary);
        var frame=new Slider {Name="MotionFrameSlider",Minimum=0,TickFrequency=1,IsSnapToTickEnabled=true};
        frame.Bind(RangeBase.MaximumProperty,new Binding(nameof(vm.MotionDuration)){Source=vm});
        frame.Bind(RangeBase.ValueProperty,new Binding(nameof(vm.MotionFrame)){Source=vm,Mode=BindingMode.TwoWay});
        frame.Bind(Control.IsEnabledProperty,new Binding(nameof(vm.HasMotion)){Source=vm});panel.Children.Add(frame);
        var counter=new TextBlock {FontSize=12};
        counter.Bind(TextBlock.TextProperty,new Binding(nameof(vm.MotionFrame)){Source=vm,StringFormat="第 {0:0} 帧 · 30 fps"});panel.Children.Add(counter);
        var compatibility=new TextBlock {FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        compatibility.Bind(TextBlock.TextProperty,new Binding(nameof(vm.MotionCompatibilityDetails)){Source=vm});
        panel.Children.Add(new Expander {Header="动作兼容提示",Content=compatibility});
        return panel;
    }
}
