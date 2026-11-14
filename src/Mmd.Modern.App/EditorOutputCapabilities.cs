using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.App;

/// <summary>Offline output needs the existing GI device query, without initializing optional
/// reconstruction SDKs or submitting an application frame. Actual capture budgets are checked
/// by the renderer using the requested output dimensions and scene, without reducing quality.</summary>
public static class EditorOutputCapabilities
{
    public static EditorPreviewCapabilities QueryInstalledGi(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return new() { DeviceUnavailableReason = "设备检查需要 Windows 64 位 D3D12 运行环境。" };
        try
        {
            using var renderer = new D3D12FrameRenderer();
            cancellationToken.ThrowIfCancellationRequested();
            var gi = renderer.QueryRayGiCapabilities();
            cancellationToken.ThrowIfCancellationRequested();
            return new(gi)
            {
                AdapterName = renderer.AdapterName,
                GiUnavailableReason = !gi.CompilerAvailable ? "GI：未找到所需的 DXC 着色器组件。"
                    : !gi.SoftwareAvailable ? "GI：当前设备未通过软件光照检查。"
                    : !gi.HardwareAvailable ? "硬件 GI：当前设备未通过 DXR 检查，可使用软件 GI。" : null
            };
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        { return new() { GiUnavailableReason = "GI：设备查询未完成，当前未开放。" }; }
    }
}
