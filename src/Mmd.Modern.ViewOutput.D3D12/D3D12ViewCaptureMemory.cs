using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

internal static class D3D12ViewCaptureMemory
{
    internal static D3D12CaptureMemoryOptions Map(ViewCaptureMemoryMode mode) => mode switch
    {
        ViewCaptureMemoryMode.Auto => D3D12CaptureMemoryOptions.Auto,
        ViewCaptureMemoryMode.PreferSpeed => D3D12CaptureMemoryOptions.Default,
        ViewCaptureMemoryMode.LowMemory => D3D12CaptureMemoryOptions.LowMemory,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
