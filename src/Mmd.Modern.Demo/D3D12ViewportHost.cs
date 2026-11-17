using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

using Mmd.Modern.App;

namespace Mmd.Modern.Demo;

public sealed class D3D12ViewportHost : NativeControlHost, IDisposable
{
    private D3D12SwapchainPresenter? presenter;
    private IntPtr childWindow;
    private IntPtr previousWindowProcedure;
    private WindowProcedure? inputProcedure;
    private bool lightDragging,cameraDragging;
    private uint pointerButton;
    private int pointerX,pointerY;
    private double pointerYaw,pointerPitch;
    private bool suspended, rendering, stopped;
    private volatile bool disposed;
    private readonly ViewportFrameDispatch dispatch;
    private readonly System.Threading.Timer frameTimer;
    private LightingMode? diagnosticMode;
    public FrameViewportViewModel? ViewModel { get; set; }
    public event Action<D3D12PresentationStatistics, double>? FramePresented;
    public event Action<Exception>? Failed;
    public D3D12PresentationStatistics? Statistics => presenter?.Statistics;
    public string? AdapterName => presenter?.AdapterName;
    public bool IsWarp => presenter?.IsWarp ?? false;
    public bool DebugRequested => presenter?.DebugRequested ?? false;
    public bool DebugLayerEnabled => presenter?.DebugLayerEnabled ?? false;
    public bool DebugInfoQueueAvailable => presenter?.DebugInfoQueueAvailable ?? false;
    public bool HasChildWindow => childWindow != IntPtr.Zero;
    public D3D12RayGiCapabilities? RayGiCapabilities { get; private set; }
    public D3D12RayGiStatistics? RayGiStatistics => presenter?.RayGiStatistics;
    public int EditorOverlayDrawCalls=>presenter?.EditorOverlayDrawCalls??0;
    public D3D12NativeLightStatistics? NativeLightStatistics=>presenter?.NativeLightStatistics;

    public D3D12ViewportHost()
    {
        // A scene frame can exceed the timer period. Run after input/layout rather than
        // continuously replenishing foreground Render jobs that starve native messages.
        dispatch = new(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background), RenderTick);
        frameTimer = new(_ =>
        {
            try { dispatch.Pulse(); }
            catch (InvalidOperationException) when (disposed) { }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The complete-frame viewport requires Windows D3D12.");
        // STATIC otherwise returns HTTRANSPARENT for WM_NCHITTEST, so real mouse
        // input bypasses this subclass even though direct SendMessage calls work.
        const int notifyMouseInput = 0x00000100; // SS_NOTIFY
        childWindow = CreateWindowExW(0, "STATIC", "MMD D3D12 child viewport", 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000 | notifyMouseInput,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (childWindow == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        inputProcedure=OnWindowMessage;
        previousWindowProcedure=SetWindowLongPtrW(childWindow,-4,Marshal.GetFunctionPointerForDelegate(inputProcedure));
        if(previousWindowProcedure==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        StartLoop();
        return new PlatformHandle(childWindow, "HWND");
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // As in EditorViewportHost: never destroy a handle that is no longer ours. Windows destroys the child with
        // its parent and may reuse the value for an unrelated window.
        if (control.Handle != childWindow) return;
        DetachInput();
        StopLoop();
        presenter?.Dispose(); presenter = null; RayGiCapabilities = null;
        if (control.Handle != IntPtr.Zero) DestroyWindow(control.Handle);
        childWindow = IntPtr.Zero;
    }

    public void Suspend(bool value)
    {
        if (suspended == value) return;
        if (value)
        {
            suspended = true; StopLoop();
            presenter?.WaitForIdle();
        }
        else { suspended = false; StartLoop(); }
    }
    public void WaitForIdle() => presenter?.WaitForIdle();
    public void Restart() { stopped = false; StartLoop(); }
    private void StartLoop()
    {
        if (disposed || suspended || stopped || childWindow == IntPtr.Zero) return;
        dispatch.Start(); frameTimer.Change(0, 8);
    }
    private void StopLoop()
    {
        dispatch.Stop();
        if (!disposed) frameTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }
    private D3D12ViewportSize CurrentSize()
    {
        if (childWindow == IntPtr.Zero || !GetClientRect(childWindow, out var rectangle)) return new(0, 0);
        // Same 1..4096 swapchain limit as the editor viewport; an oversized window must not stop the frame loop.
        return new(Math.Clamp(rectangle.Right - rectangle.Left, 0, MaximumRenderDimension),
            Math.Clamp(rectangle.Bottom - rectangle.Top, 0, MaximumRenderDimension));
    }
    // The D3D12 swapchain accepts 1..4096 per axis; same cap as the editor viewport, kept here because the demo
    // host lives in its own assembly.
    private const int MaximumRenderDimension = 4096;
    private void RenderTick()
    {
        if (disposed || suspended || rendering || childWindow == IntPtr.Zero || ViewModel?.HasScene != true) return;
        var size = CurrentSize();
        if (size.IsSuspended) return;
        rendering = true;
        var watch = Stopwatch.StartNew();
        try
        {
            if (presenter is null)
            {
                presenter = new(childWindow, size.Width, size.Height);
                RayGiCapabilities = presenter.QueryRayGiCapabilities();
                var capabilities = RayGiCapabilities;
                ViewModel.UpdateLightingCapabilities(new(RenderApi.Direct3D12, capabilities.RawRaytracingTier >= 11,
                    capabilities.ShaderModel >= 63, capabilities.Compute, SdfResolution: 0),
                    new(IsWarp: capabilities.IsWarp, Compute: capabilities.Compute, ShaderModel: capabilities.ShaderModel,
                        RawRaytracingTier: capabilities.RawRaytracingTier, Device5: capabilities.Device5, CommandList4: capabilities.CommandList4,
                        CompilerAvailable: capabilities.CompilerAvailable, SoftwareImplementationReady: capabilities.SoftwareImplementationReady,
                        HardwareImplementationReady: capabilities.HardwareImplementationReady));
            }
            ViewModel.TickAnimation();
            if (presenter.Present(ViewModel.GetFrame(size.Width, size.Height), size.Width, size.Height))
            {
                var gi = presenter.RayGiStatistics;
                if (ViewModel.GiEnabled && (gi is null || gi.Effective != ViewModel.GiMode || gi.DispatchCount == 0))
                    throw new InvalidOperationException("请求的 GI 未产生实际 backend dispatch；帧未被标记为 GI 成功。");
                if (presenter.Statistics.PresentCount == 1 || presenter.Statistics.PresentCount % 15 == 0 || diagnosticMode != ViewModel.GiMode)
                {
                    diagnosticMode = ViewModel.GiMode;
                    string gpuTime = gi?.GpuMilliseconds is { } gpuMs ? $"GPU {gpuMs:F2} ms" : "GPU 时间未测量";
                    ViewModel.GiExecutionDiagnostics = ViewModel.GiEnabled
                    ? $"实际 GI：{presenter.RayGiBackendIdentity ?? gi!.Effective.ToString()}\n{gi!.SamplesPerPixel} SPP · 强度 {gi.Strength:F1} · dispatch {gi.DispatchCount} · GBuffer draw {gi.GBufferDrawCalls}\nGI CPU 录制 {gi.RecordCpuMilliseconds:F1} ms · {gpuTime}"
                        : "实际 GI：Off · 本帧无 GI dispatch · GPU 时间未测量";
                }
                FramePresented?.Invoke(presenter.Statistics, watch.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception error)
        {
            stopped = true; StopLoop();
            ViewModel!.GiExecutionDiagnostics = $"GI/帧执行失败：{error.Message}";
            Failed?.Invoke(error);
        }
        finally { rendering = false; }
    }
    public RenderedFrame Capture()
    {
        if (presenter is null || ViewModel?.HasScene != true) throw new InvalidOperationException("Wait for the first presented frame before capturing.");
        var statistics = presenter.Statistics;
        return presenter.Capture(ViewModel.GetFrame(statistics.Width, statistics.Height));
    }
    public void Dispose()
    {
        if (disposed) return;
        DetachInput();
        disposed = true; dispatch.Stop(); frameTimer.Dispose();
        presenter?.Dispose(); presenter = null;
    }
    private IntPtr OnWindowMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam)
    {
        try
        {
            int x=unchecked((short)(lParam.ToInt64()&0xffff)),y=unchecked((short)((lParam.ToInt64()>>16)&0xffff));
            if(ViewModel is {} vm&&vm.HasScene)
            {
                if(message is 0x201 or 0x204 or 0x207)
                {
                    if(lightDragging||cameraDragging)return IntPtr.Zero;
                    var size=CurrentSize();if(size.IsSuspended)return IntPtr.Zero;
                    SetFocus(window);lightDragging=message!=0x207&&vm.BeginLightDrag(x,y,size.Width,size.Height,message==0x204);
                    cameraDragging=!lightDragging;pointerButton=message;pointerX=x;pointerY=y;pointerYaw=vm.Yaw;pointerPitch=vm.Pitch;SetCapture(window);return IntPtr.Zero;
                }
                if(message==0x200&&(lightDragging||cameraDragging))
                {
                    if(lightDragging)vm.UpdateLightDrag(x,y);
                    else if(pointerButton==0x201){vm.Yaw=pointerYaw+(x-pointerX)*.30;vm.Pitch=pointerPitch+(y-pointerY)*.30;}
                    else
                    {
                        var size=CurrentSize();
                        if(!size.IsSuspended)vm.PanCameraPixels(x-pointerX,y-pointerY,size.Width,size.Height);
                        pointerX=x;pointerY=y;
                    }
                    return IntPtr.Zero;
                }
                if(message is 0x202 or 0x205 or 0x208)
                {if(message!=pointerButton+1)return IntPtr.Zero;if(lightDragging)vm.EndLightDrag(true);lightDragging=cameraDragging=false;pointerButton=0;ReleaseCapture();return IntPtr.Zero;}
                if(message==0x215||(message==0x100&&wParam.ToInt64()==27))
                {if(lightDragging)vm.EndLightDrag(false);lightDragging=cameraDragging=false;if(message==0x100)ReleaseCapture();}
                if(message==0x20a)
                {int delta=unchecked((short)((wParam.ToInt64()>>16)&0xffff));vm.Zoom*=Math.Pow(.90,delta/120.0);return IntPtr.Zero;}
            }
        }
        catch(Exception error){lightDragging=cameraDragging=false;ViewModel?.EndLightDrag(false);if(ViewModel is {} vm)vm.Status=$"视口操作未完成：{error.Message}";}
        return CallWindowProcW(previousWindowProcedure,window,message,wParam,lParam);
    }
    private void DetachInput()
    {
        if(lightDragging)ViewModel?.EndLightDrag(false);lightDragging=cameraDragging=false;
        if(childWindow!=IntPtr.Zero&&previousWindowProcedure!=IntPtr.Zero)SetWindowLongPtrW(childWindow,-4,previousWindowProcedure);
        previousWindowProcedure=IntPtr.Zero;inputProcedure=null;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate IntPtr WindowProcedure(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWindowLongPtrW(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll")]private static extern IntPtr CallWindowProcW(IntPtr previous,IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")]private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern IntPtr SetFocus(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd, out NativeRectangle rectangle);
}
