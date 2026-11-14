namespace Mmd.Modern.Core;

/// <summary>Rendering mode selected by the user or by policy.</summary>
public enum RenderMode
{
    Modern,
    CompatD3D9,
    CompatOpenGL,
    Software,
}

/// <summary>Concrete graphics API. Kept backend-neutral so Core stays free of GPU types.</summary>
public enum RenderApi
{
    Vulkan,
    Direct3D12,
    Direct3D11,
    OpenGL,
    Direct3D9,
    Software,
}

public sealed record RenderBackendRequest(
    RenderMode Mode = RenderMode.Modern,
    bool PreferRayTracing = false,
    bool AllowFallback = true,
    RenderApi? ForcedApi = null);

/// <summary>Capabilities reported by a probed backend. Device strings are diagnostic only.</summary>
public sealed record RenderCapabilities(
    RenderApi Api,
    string Device,
    string Driver,
    bool RayTracing,
    bool Compute,
    bool GeometryShader,
    bool Multisample,
    int MaxTextureSize,
    string ShaderModel);

public sealed record RenderBackendPlan(
    RenderMode Mode,
    RenderApi Api,
    bool RayTracing,
    IReadOnlyList<RenderApi> AttemptOrder,
    bool FallbackUsed,
    string Reason);

public sealed record RenderBackendSelection(
    bool Success,
    RenderBackendPlan? Plan,
    string? FailureReason);

/// <summary>
/// Deterministic backend ordering. Compatibility modes never silently fall back to a
/// different API: a D3D9 pixel baseline that quietly becomes D3D11 is not a baseline.
/// </summary>
public static class RenderBackendPlanner
{
    public static IReadOnlyList<RenderApi> Order(RenderMode mode, bool windows) => mode switch
    {
        RenderMode.Modern => windows
            ? [RenderApi.Vulkan, RenderApi.Direct3D12, RenderApi.Direct3D11]
            : [RenderApi.Vulkan, RenderApi.OpenGL],
        RenderMode.CompatD3D9 => [RenderApi.Direct3D9],
        RenderMode.CompatOpenGL => [RenderApi.OpenGL],
        RenderMode.Software => [RenderApi.Software],
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown render mode."),
    };

    public static RenderBackendSelection Select(
        RenderBackendRequest request,
        IReadOnlyList<RenderCapabilities> available,
        bool windows)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(available);

        var order = Order(request.Mode, windows).ToList();

        if (request.ForcedApi is { } forced)
        {
            if (!order.Contains(forced))
            {
                return new(false, null,
                    $"API {forced} is not allowed in mode {request.Mode}.");
            }
            order = [forced, .. order.Where(api => api != forced)];
        }

        // Ray tracing is an enhancement of the modern path only. When requested, prefer
        // RT-capable backends while preserving the mode's relative order.
        var rayTracingRequested = request.PreferRayTracing && request.Mode == RenderMode.Modern;
        var effectiveOrder = request.ForcedApi is not null || !rayTracingRequested
            ? order
            : [.. order.Where(api => Capability(available, api)?.RayTracing == true),
               .. order.Where(api => Capability(available, api)?.RayTracing != true)];

        var ranked = effectiveOrder
            .Select(api => (Api: api, Capability: Capability(available, api)))
            .Where(entry => entry.Capability is not null)
            .ToList();

        if (ranked.Count == 0)
        {
            return new(false, null,
                $"No available backend for mode {request.Mode}. Probed: {string.Join(", ", available.Select(a => a.Api))}.");
        }

        var selected = ranked[0];
        if (selected.Api != order[0] && !request.AllowFallback)
        {
            return new(false, null,
                $"Preferred backend {order[0]} is unavailable and fallback is disabled.");
        }

        var fallbackUsed = selected.Api != order[0];
        var rayTracing = rayTracingRequested && selected.Capability!.RayTracing;
        var reason = rayTracing
            ? "Selected a ray-tracing capable modern backend."
            : rayTracingRequested
                ? "Ray tracing was requested but the selected backend reports no support; raster baseline used."
                : "Selected by mode order and capability probe.";

        if (fallbackUsed)
        {
            reason += $" Fallback from {order[0]} to {selected.Api}.";
        }

        return new(true,
            new RenderBackendPlan(
                request.Mode,
                selected.Api,
                rayTracing,
                order,
                fallbackUsed,
                reason),
            null);
    }

    private static RenderCapabilities? Capability(
        IReadOnlyList<RenderCapabilities> available,
        RenderApi api)
    {
        foreach (var candidate in available)
        {
            if (candidate.Api == api) return candidate;
        }
        return null;
    }
}
