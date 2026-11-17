using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;

namespace Mmd.Modern.Demo;

public sealed partial class FrameViewportWindow
{
    private void BuildDemoView()
    {
        Title="MMD Modern · 引擎与物理 Demo";Width=1400;Height=900;MinWidth=950;MinHeight=650;
        var layout=new Grid{RowDefinitions=new("Auto,*,Auto"),ColumnDefinitions=new("340,*"),Margin=new(12)};
        var heading=new TextBlock{Text="MMD Modern  /  光照 × 物理实验室",FontSize=24,Margin=new(0,0,0,12)};
        Grid.SetColumnSpan(heading,2);layout.Children.Add(heading);
        var tools=new StackPanel{Spacing=9,Margin=new(0,0,12,0)};
        tools.Children.Add(new TextBlock{Text="左拖旋转镜头 · 中键拖动平移 · 滚轮缩放\n灯光手柄：左拖移动，右拖旋转",TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=12});
        var open=new Button{Name="DemoOpenPmx",Content="打开 PMX 模型，查看光照…",HorizontalAlignment=HorizontalAlignment.Stretch};
        open.Click+=async(_,_)=>await OpenModel();tools.Children.Add(open);
        Button("返回内置光照／物理场景","DemoLoadBuiltin",ViewModel.LoadEngineDemo);
        var modelSummary=new TextBlock{Name="DemoModelSummary",FontSize=12,TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        modelSummary.Bind(TextBlock.TextProperty,new Binding(nameof(ViewModel.ModelSummary)));tools.Children.Add(modelSummary);
        var modelPath=new TextBlock{Name="DemoModelPath",FontSize=10,Opacity=.7,TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        modelPath.Bind(TextBlock.TextProperty,new Binding(nameof(ViewModel.ModelPath)){TargetNullValue="内置原创场景（无外部模型）"});tools.Children.Add(modelPath);
        Button Button(string label,string name,Action action)
        {var button=new Button{Name=name,Content=label,HorizontalAlignment=HorizontalAlignment.Stretch};button.Click+=(_,_)=>{action();Viewport.Restart();};tools.Children.Add(button);return button;}
        Button("启用光追（静止／DXR优先）","DemoRayTracing",ViewModel.ShowDemoRayTracing);
        var play=Button("播放内置场景真实物理","DemoPlayPhysics",ViewModel.PlayDemoPhysics);
        play.Bind(IsEnabledProperty,new Binding(nameof(ViewModel.HasEngineDemo)));
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        var pause=new Button{Content="暂停",Name="DemoPausePhysics"};pause.Click+=(_,_)=>ViewModel.PauseDemoPhysics();row.Children.Add(pause);
        var reset=new Button{Content="重置",Name="DemoResetPhysics"};reset.Click+=(_,_)=>{ViewModel.ResetDemoPhysics();Viewport.Restart();};row.Children.Add(reset);tools.Children.Add(row);
        row.Bind(IsEnabledProperty,new Binding(nameof(ViewModel.HasEngineDemo)));
        var physics=new TextBlock{FontSize=12,TextWrapping=Avalonia.Media.TextWrapping.Wrap};physics.Bind(TextBlock.TextProperty,new Binding(nameof(ViewModel.DemoPhysicsSummary)));tools.Children.Add(physics);
        AddEngineDemoSettings(tools);
        tools.Children.Add(new Expander{Name="DemoNativeLights",Header="光源编辑 · 位置／颜色／强度／阴影",Content=NativeLightControls.Create(this,ViewModel),HorizontalAlignment=HorizontalAlignment.Stretch});
        var screenshot=new Button{Content="保存 PNG 截图…",HorizontalAlignment=HorizontalAlignment.Stretch};screenshot.Click+=async(_,_)=>await SaveCapture();tools.Children.Add(screenshot);
        var gi=new TextBlock{FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap};gi.Bind(TextBlock.TextProperty,new Binding(nameof(ViewModel.GiExecutionDiagnostics)));tools.Children.Add(gi);
        tools.Children.Add(new TextBlock{Text="内置场景使用 Bepu 实验物理；运动时 GI 关闭。\nPMX 当前展示初始姿态，可编辑原生光源。\n局部灯为直接光＋阴影，暂未进入 GI。",FontSize=11,Opacity=.7,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        var scroll=new ScrollViewer{Content=tools,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,1);layout.Children.Add(scroll);
        Grid.SetColumn(Viewport,1);Grid.SetRow(Viewport,1);layout.Children.Add(Viewport);
        var footer=new StackPanel{Spacing=4,Margin=new(0,10,0,0)};
        var diagnostics=new TextBlock{FontSize=11,TextWrapping=Avalonia.Media.TextWrapping.Wrap};diagnostics.Bind(TextBlock.TextProperty,new Binding(nameof(ViewModel.RenderDiagnostics)));footer.Children.Add(diagnostics);
        var status=new TextBlock{FontSize=12,TextWrapping=Avalonia.Media.TextWrapping.Wrap};status.Bind(TextBlock.TextProperty,new Binding(nameof(ViewModel.Status)));footer.Children.Add(status);
        Grid.SetRow(footer,2);Grid.SetColumnSpan(footer,2);layout.Children.Add(footer);Content=layout;
    }
}
