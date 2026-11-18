using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D3D12Api = Vortice.Direct3D12.D3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Hardware adapter choice shared by every D3D12 device creation path, so preview and export agree.
/// The high-performance order from IDXGIFactory6 comes first (hybrid laptops may list the iGPU first in
/// EnumAdapters1); the EnumAdapters1 order follows as fallback. Software adapters are skipped, each LUID is
/// tried once, and WARP stays the last resort.</summary>
internal static class D3D12AdapterSelection
{
    internal readonly record struct Candidate(long Luid, string Name, bool Software);

    internal static (T Adapter, TDevice Device)? Select<T, TDevice>(IEnumerable<T>? highPerformance, IEnumerable<T> enumerated,
        Func<T, Candidate> describe, Func<T, TDevice?> create) where TDevice : class
    {
        var tried = new HashSet<long>();
        foreach (var source in highPerformance is null ? [enumerated] : new[] { highPerformance, enumerated })
            foreach (var adapter in source)
            {
                var candidate = describe(adapter);
                if (candidate.Software || !tried.Add(candidate.Luid)) continue;
                if (create(adapter) is { } device) return (adapter, device);
            }
        return null;
    }

    internal static (ID3D12Device Device, string Name, bool Warp) CreateDevice(FeatureLevel minimum)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        var owned = new List<IDXGIAdapter1>();
        try
        {
            List<IDXGIAdapter1>? highPerformance = null;
            using (var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>())
                if (factory6 is not null)
                {
                    highPerformance = [];
                    for (int i = 0; factory6.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
                    { owned.Add(adapter); highPerformance.Add(adapter); }
                }
            var enumerated = new List<IDXGIAdapter1>();
            for (int i = 0; factory.EnumAdapters1(i, out var adapter).Success && adapter is not null; i++)
            { owned.Add(adapter); enumerated.Add(adapter); }
            var selected = Select(highPerformance, enumerated, adapter =>
            {
                var description = adapter.Description1;
                return new Candidate((long)description.Luid, description.Description, (description.Flags & AdapterFlags.Software) != 0);
            }, adapter => D3D12Api.D3D12CreateDevice(adapter, minimum, out ID3D12Device? device).Success ? device : null);
            if (selected is { } hardware) return (hardware.Device, hardware.Adapter.Description1.Description, false);
        }
        finally { foreach (var adapter in owned) adapter.Dispose(); }
        using var warp = factory.EnumWarpAdapter<IDXGIAdapter1>();
        D3D12Api.D3D12CreateDevice(warp, minimum, out ID3D12Device? fallback).CheckError();
        return (fallback ?? throw new InvalidOperationException("D3D12 WARP device unavailable."), warp.Description1.Description, true);
    }
}
