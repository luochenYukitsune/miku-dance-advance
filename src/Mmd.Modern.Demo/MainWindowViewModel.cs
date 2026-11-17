using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.Veldrid;

namespace Mmd.Modern.Demo;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string fixturePath;
    private readonly Func<IRenderBackend> createRenderer;
    private bool busy, disposed, raiseArm;
    private double yaw;
    private Bitmap? preview;
    private string status = "准备加载原创 PMD 模型。";
    private string modelSummary = "等待加载模型。";
    public event PropertyChangedEventHandler? PropertyChanged;
    public AsyncCommand RenderCommand { get; }
    public Bitmap? Preview { get => preview; private set { preview?.Dispose(); preview = value; Changed(); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string ModelSummary { get => modelSummary; private set { modelSummary = value; Changed(); } }
    public bool RaiseArm { get => raiseArm; set { raiseArm = value; Changed(); } }
    public double Yaw { get => yaw; set { yaw = value; Changed(); } }

    public MainWindowViewModel(string? fixturePath = null, Func<IRenderBackend>? createRenderer = null)
    {
        this.fixturePath = fixturePath ?? Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        this.createRenderer = createRenderer ?? (() => new VeldridRenderBackend());
        RenderCommand = new AsyncCommand(RenderAsync, () => !busy && !disposed);
    }

    public async Task RenderAsync()
    {
        if (busy || disposed) return;
        busy = true; RenderCommand.Refresh(); Status = "正在读取模型并渲染…";
        bool arm = RaiseArm; float angle = (float)Yaw;
        try
        {
            var result = await Task.Run(() =>
            {
                using var source = File.OpenRead(fixturePath);
                var model = PmdReader.Read(source);
                var rotations = Enumerable.Repeat(Quaternion.Identity, model.Bones.Length).ToArray();
                if (arm) rotations[2] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.55f);
                using var renderer = createRenderer();
                var frame = renderer.Render(PmdSceneAdapter.FromSkinned(model, Skinning.Apply(model, Skinning.CreateSkinMatrices(model, rotations)), angle).Snapshot, 960, 720);
                return (Frame: frame, model.Name, VertexCount: model.Vertices.Length, TriangleCount: model.Indices.Length / 3, BoneCount: model.Bones.Length);
            });
            if (disposed) return;
            using var image = new MemoryStream(FrameImage.ToBmp(result.Frame));
            Preview = new Bitmap(image);
            Status = $"已显示 {result.Name}。";
            ModelSummary = $"{result.VertexCount:N0} 顶点 / {result.TriangleCount:N0} 三角面\n{result.Frame.DrawCalls} 材质分组 / {result.BoneCount} 骨骼";
        }
        catch (Exception error) { if (!disposed) Status = $"渲染失败：{error.Message}"; }
        finally { busy = false; RenderCommand.Refresh(); }
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public void Dispose() { if (disposed) return; disposed = true; preview?.Dispose(); preview = null; RenderCommand.Refresh(); }
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
