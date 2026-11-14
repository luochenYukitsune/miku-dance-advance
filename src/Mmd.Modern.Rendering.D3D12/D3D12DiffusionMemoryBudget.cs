using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

internal static class D3D12DiffusionMemoryBudget
{
    internal static ulong Measure(D3D12GpuRuntime runtime, int width, int height, bool active, bool alphaCoverage = false)
    {
        if (!active) return 0;
        ulong Allocation(ResourceDescription description)
        {
            ulong bytes = runtime.Device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
            if (bytes == 0 || bytes == ulong.MaxValue)
                throw new NotSupportedException("Device cannot estimate Diffusion resources.");
            return bytes;
        }
        ulong image = Allocation(ResourceDescription.Texture2D(alphaCoverage ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm,
            (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget));
        // UploadConstants may cross a retained 1MiB page boundary in every recording slot.
        ulong constants = Allocation(ResourceDescription.Buffer(1024 * 1024));
        return checked((2 * image + constants) * D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
    }
}
