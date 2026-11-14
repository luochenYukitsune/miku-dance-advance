using Vortice.Direct3D12.Debug;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Freezes debug configuration before this assembly creates its first device.
/// Enabling the D3D12 debug layer after device creation can remove existing devices.
/// A later environment-variable change therefore never enables or disables the layer.</summary>
internal static class D3D12DebugLayerBootstrap
{
    private static readonly D3D12DebugLayerPolicy Policy = new();

    internal static bool BeforeDeviceCreation(bool requested) => Policy.BeforeDeviceCreation(requested, () =>
    {
        if (!Vortice.Direct3D12.D3D12.D3D12GetDebugInterface<ID3D12Debug>(out var debug).Success || debug is null)
            return false;
        using (debug) debug.EnableDebugLayer();
        return true;
    });
}

/// <summary>One process-wide decision shared by both renderer device creation paths.
/// Separate policy objects allow deterministic tests without touching global native state.</summary>
internal sealed class D3D12DebugLayerPolicy
{
    private readonly object gate = new();
    private bool initialized, enabled;

    internal bool BeforeDeviceCreation(bool requested, Func<bool> enable)
    {
        lock (gate)
        {
            if (initialized) return enabled;
            // Freeze even on failure: retrying later cannot establish that no device exists.
            initialized = true;
            if (requested) enabled = enable();
            return enabled;
        }
    }
}
