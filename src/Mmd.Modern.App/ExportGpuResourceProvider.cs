using Mmd.Modern.ViewOutput;
using Vortice.DXGI;

namespace Mmd.Modern.App;

/// <summary>Queries process-local residency on the selected renderer's adapter, using its own DXGI interfaces.
/// The sampling thread never accesses a renderer's device or command queue.</summary>
internal sealed class ExportGpuResourceProvider
{
    private ViewRendererDeviceInfo? device;
    public void SetDevice(ViewRendererDeviceInfo value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref device, value);
    }

    public RenderGpuMemorySnapshot? Sample()
    {
        var selected = Volatile.Read(ref device);
        if (selected is null) return null;
        var identity = new RenderGpuMemorySnapshot
        {
            AdapterName = selected.AdapterName, AdapterLuid = selected.AdapterLuid,
            SampledAtUtc = DateTimeOffset.UtcNow
        };
        if (!OperatingSystem.IsWindows() || selected.IsSoftware)
            return identity with { UnavailableReason = "Local GPU memory is unavailable for this renderer." };
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter3>(Vortice.Luid.FromInt64(selected.AdapterLuid));
            var memory = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            if (memory.Budget == 0)
                return identity with { UnavailableReason = "The selected adapter did not report a local-memory budget." };
            return identity with
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                ProcessLocalVideoMemoryUsageBytes = memory.CurrentUsage,
                LocalVideoMemoryBudgetBytes = memory.Budget
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return identity with { UnavailableReason = "DXGI local-memory query: " + error.Message };
        }
    }
}
