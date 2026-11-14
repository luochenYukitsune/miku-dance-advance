namespace Mmd.Modern.Core;

/// <summary>
/// How the scene is lit. Orthogonal to <see cref="RenderMode"/>: the backend decides where the
/// pixels go, the lighting mode decides how light is computed.
/// </summary>
public enum LightingMode
{
    /// <summary>MMD's original raster lighting (toon + sphere + directional). The compatibility baseline.</summary>
    Legacy,

    /// <summary>Hardware ray tracing (DXR / Vulkan ray query). Highest quality, needs RT cores.</summary>
    HardwareRayTracing,

    /// <summary>GPU compute traversal of a triangle BVH for one-bounce diffuse GI.</summary>
    SoftwareRayTracing,
}

/// <summary>What a probed device can do for lighting. All fields are observed, not assumed.</summary>
public sealed record LightingCapabilities(
    RenderApi Api,
    bool HardwareRayTracing,
    bool ShaderModel63,
    bool Compute,
    int SdfResolution,
    long VideoMemoryMiB = 0,
    bool ScreenSpaceTracing = true)
{
    public static LightingCapabilities None { get; } = new(RenderApi.Software, false, false, false, 0);
}

/// <summary>
/// Observed implementation and execution readiness. Hardware capability alone never makes a
/// lighting menu entry executable. Raw raytracing tiers follow D3D12: 10=1.0, 11=1.1, 12=1.2.
/// </summary>
public sealed record LightingExecutionCapabilities(
    bool BackendAvailable = true,
    bool IsWarp = false,
    bool Compute = false,
    int ShaderModel = 0,
    int RawRaytracingTier = 0,
    bool Device5 = false,
    bool CommandList4 = false,
    bool CompilerAvailable = false,
    bool SoftwareImplementationReady = false,
    bool HardwareImplementationReady = false,
    bool TransportSupported = true,
    bool ViewportSupported = true)
{
    public bool SoftwareReady => BackendAvailable && !IsWarp && Compute && ShaderModel >= 60 &&
        CompilerAvailable && SoftwareImplementationReady && TransportSupported && ViewportSupported;

    public bool HardwareReady => BackendAvailable && !IsWarp && Compute && ShaderModel >= 65 &&
        RawRaytracingTier >= 11 && Device5 && CommandList4 && CompilerAvailable &&
        HardwareImplementationReady && TransportSupported && ViewportSupported;
}

public sealed record LightingRequest(
    LightingMode Preferred = LightingMode.Legacy,
    bool AllowFallback = true,
    bool CompatMode = false);

public sealed record LightingDiagnostic(string Code, string Message);

public sealed record LightingSelection(
    bool Success,
    LightingMode Requested,
    LightingMode Effective,
    bool FallbackUsed,
    IReadOnlyList<LightingDiagnostic> Diagnostics)
{
    public bool IsRayTraced => Effective is LightingMode.HardwareRayTracing or LightingMode.SoftwareRayTracing;
}

/// <summary>One row of the effect-management menu's lighting section.</summary>
public sealed record LightingMenuOption(
    LightingMode Mode,
    string DisplayName,
    string Description,
    bool Available,
    string? UnavailableReason);

public static class LightingDiagnosticCodes
{
    public const string NoHardwareRayTracing = "LIGHT001";
    public const string NoCompute = "LIGHT002";
    public const string CompatForcesLegacy = "LIGHT003";
    public const string SdfResolutionTooLow = "LIGHT004";
    public const string FallbackDisabled = "LIGHT005";
    public const string NoShaderModel63 = "LIGHT006";
    public const string ImplementationUnavailable = "LIGHT007";
    public const string NoShaderModel65 = "LIGHT008";
    public const string CompilerUnavailable = "LIGHT009";
    public const string TransportUnsupported = "LIGHT010";
}

/// <summary>
/// Chooses a lighting mode from what the device reports.
/// </summary>
/// <remarks>
/// Two rules are deliberately strict:
/// <list type="bullet">
/// <item>Compatibility rendering never upgrades lighting. A D3D9 pixel baseline that silently
/// becomes ray traced is not a baseline.</item>
/// <item>Every downgrade is reported as a diagnostic with a reason, so the menu can explain
/// itself instead of quietly looking worse than the user asked for.</item>
/// </list>
/// </remarks>
public static class LightingPlanner
{
    /// <summary>
    /// Strict product selection. Unlike the historical capability-only overload, this checks the
    /// actual compiler, transport, viewport and implemented backend required to execute a frame.
    /// </summary>
    public static LightingSelection Select(
        LightingRequest request,
        LightingCapabilities capabilities,
        LightingExecutionCapabilities execution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(execution);

        var diagnostics = new List<LightingDiagnostic>();
        if (request.CompatMode && request.Preferred != LightingMode.Legacy)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.CompatForcesLegacy,
                "Compatibility rendering keeps MMD's original lighting; ray tracing is disabled."));
            return new(true, request.Preferred, LightingMode.Legacy, true, diagnostics);
        }

        switch (request.Preferred)
        {
            case LightingMode.Legacy:
                return new(true, LightingMode.Legacy, LightingMode.Legacy, false, diagnostics);

            case LightingMode.HardwareRayTracing:
                if (CanTraceHardware(capabilities, execution, diagnostics))
                    return new(true, LightingMode.HardwareRayTracing, LightingMode.HardwareRayTracing, false, diagnostics);

                if (!request.AllowFallback)
                {
                    diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                        "Hardware ray tracing is unavailable and fallback is disabled."));
                    return new(false, request.Preferred, request.Preferred, false, diagnostics);
                }

                if (CanTraceSoftware(capabilities, execution, diagnostics))
                {
                    diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                        "Falling back to software GPU BVH ray tracing."));
                    return new(true, request.Preferred, LightingMode.SoftwareRayTracing, true, diagnostics);
                }

                diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                    "Falling back to MMD's original lighting."));
                return new(true, request.Preferred, LightingMode.Legacy, true, diagnostics);

            case LightingMode.SoftwareRayTracing:
                if (CanTraceSoftware(capabilities, execution, diagnostics))
                    return new(true, LightingMode.SoftwareRayTracing, LightingMode.SoftwareRayTracing, false, diagnostics);

                if (!request.AllowFallback)
                {
                    diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                        "Software ray tracing is unavailable and fallback is disabled."));
                    return new(false, request.Preferred, request.Preferred, false, diagnostics);
                }

                diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                    "Falling back to MMD's original lighting."));
                return new(true, request.Preferred, LightingMode.Legacy, true, diagnostics);

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Preferred, "Unknown lighting mode.");
        }
    }

    public static LightingSelection Select(LightingRequest request, LightingCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);

        var diagnostics = new List<LightingDiagnostic>();

        if (request.CompatMode && request.Preferred != LightingMode.Legacy)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.CompatForcesLegacy,
                "Compatibility rendering keeps MMD's original lighting; ray tracing is disabled."));
            return new(true, request.Preferred, LightingMode.Legacy, true, diagnostics);
        }

        switch (request.Preferred)
        {
            case LightingMode.Legacy:
                return new(true, LightingMode.Legacy, LightingMode.Legacy, false, diagnostics);

            case LightingMode.HardwareRayTracing:
                if (CanTraceHardware(capabilities, diagnostics))
                {
                    return new(true, request.Preferred, LightingMode.HardwareRayTracing, false, diagnostics);
                }

                if (!request.AllowFallback)
                {
                    diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                        "Hardware ray tracing is unavailable and fallback is disabled."));
                    return new(false, request.Preferred, request.Preferred, false, diagnostics);
                }

                if (CanTraceSoftware(capabilities, diagnostics))
                {
                    diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                        "Falling back to software ray tracing."));
                    return new(true, request.Preferred, LightingMode.SoftwareRayTracing, true, diagnostics);
                }

                diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                    "Falling back to MMD's original lighting."));
                return new(true, request.Preferred, LightingMode.Legacy, true, diagnostics);

            case LightingMode.SoftwareRayTracing:
                if (CanTraceSoftware(capabilities, diagnostics))
                {
                    return new(true, request.Preferred, LightingMode.SoftwareRayTracing, false, diagnostics);
                }

                if (!request.AllowFallback)
                {
                    diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                        "Software ray tracing is unavailable and fallback is disabled."));
                    return new(false, request.Preferred, request.Preferred, false, diagnostics);
                }

                diagnostics.Add(new(LightingDiagnosticCodes.FallbackDisabled,
                    "Falling back to MMD's original lighting."));
                return new(true, request.Preferred, LightingMode.Legacy, true, diagnostics);

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Preferred, "Unknown lighting mode.");
        }
    }

    /// <summary>Menu rows with availability and the reason a mode is greyed out.</summary>
    public static IReadOnlyList<LightingMenuOption> Menu(LightingCapabilities capabilities, bool compatMode)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var options = new List<LightingMenuOption>(3);

        options.Add(new LightingMenuOption(
            LightingMode.Legacy,
            "原版光照",
            "MMD 的 toon / 球面贴图 / 方向光。兼容基线，像素可验证。",
            true,
            null));

        var hardware = Select(new LightingRequest(LightingMode.HardwareRayTracing, AllowFallback: false, compatMode), capabilities);
        options.Add(new LightingMenuOption(
            LightingMode.HardwareRayTracing,
            "硬件光追",
            "DXR / Vulkan ray query：光线追踪阴影、AO、反射。需要 RT 核心。",
            !compatMode && hardware.Success,
            compatMode ? "兼容模式固定使用原版光照。" : hardware.Success ? null : Explain(hardware, compatMode)));

        var software = Select(new LightingRequest(LightingMode.SoftwareRayTracing, AllowFallback: false, compatMode), capabilities);
        options.Add(new LightingMenuOption(
            LightingMode.SoftwareRayTracing,
            "软光追",
            "屏幕空间追踪 + SDF 回退的 Lumen 式全局光照，任意支持 compute 的显卡。",
            !compatMode && software.Success,
            compatMode ? "兼容模式固定使用原版光照。" : software.Success ? null : Explain(software, compatMode)));

        return options;
    }

    /// <summary>Strict product menu backed by implemented execution readiness.</summary>
    public static IReadOnlyList<LightingMenuOption> Menu(
        LightingCapabilities capabilities,
        LightingExecutionCapabilities execution,
        bool compatMode)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(execution);

        var options = new List<LightingMenuOption>(3)
        {
            new(LightingMode.Legacy,
                "原版光照",
                "MMD 的 toon / 球面贴图 / 方向光。兼容基线与未增强默认。",
                true,
                null),
        };

        var hardware = Select(new LightingRequest(
            LightingMode.HardwareRayTracing, AllowFallback: false, compatMode), capabilities, execution);
        options.Add(new LightingMenuOption(
            LightingMode.HardwareRayTracing,
            "硬件光追 GI",
            "DXR RayQuery 一弹射漫反射 GI：真实二次表面与颜色反弹，默认关闭。",
            !compatMode && hardware.Success,
            compatMode ? "兼容模式固定使用原版光照。" : hardware.Success ? null : Explain(hardware, compatMode)));

        var software = Select(new LightingRequest(
            LightingMode.SoftwareRayTracing, AllowFallback: false, compatMode), capabilities, execution);
        options.Add(new LightingMenuOption(
            LightingMode.SoftwareRayTracing,
            "软光追 GI",
            "计算着色器遍历真实三角形 BVH：一弹射颜色反弹，任意支持 SM6 的独立显卡。",
            !compatMode && software.Success,
            compatMode ? "兼容模式固定使用原版光照。" : software.Success ? null : Explain(software, compatMode)));

        return options;
    }

    private static string Explain(LightingSelection selection, bool compatMode)
    {
        if (compatMode)
        {
            return "兼容模式固定使用原版光照。";
        }

        return selection.Diagnostics.FirstOrDefault(d =>
            d.Code != LightingDiagnosticCodes.FallbackDisabled)?.Message
            ?? "当前设备不支持该模式。";
    }

    private static bool CanTraceHardware(LightingCapabilities capabilities, List<LightingDiagnostic> diagnostics)
    {
        if (!capabilities.HardwareRayTracing)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.NoHardwareRayTracing,
                "Device reports no hardware ray tracing (DXR tier or VK_KHR_ray_query missing)."));
            return false;
        }

        if (!capabilities.ShaderModel63)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.NoShaderModel63,
                "Hardware ray tracing needs shader model 6.3+."));
            return false;
        }

        return true;
    }

    private static bool CanTraceHardware(
        LightingCapabilities capabilities,
        LightingExecutionCapabilities execution,
        List<LightingDiagnostic> diagnostics)
    {
        bool available = CheckCommonExecution(execution, diagnostics);
        if (!capabilities.Compute || !execution.Compute)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.NoCompute,
                "Hardware inline RayQuery requires an available compute queue."));
            available = false;
        }
        if (!capabilities.HardwareRayTracing || execution.RawRaytracingTier < 11)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.NoHardwareRayTracing,
                "Hardware inline RayQuery requires DXR tier 1.1 or newer (raw tier >= 11)."));
            available = false;
        }
        if (execution.ShaderModel < 65)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.NoShaderModel65,
                "Hardware inline RayQuery requires shader model 6.5."));
            available = false;
        }
        if (!execution.Device5 || !execution.CommandList4)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.ImplementationUnavailable,
                "D3D12 Device5 and GraphicsCommandList4 are required for inline RayQuery."));
            available = false;
        }
        if (!execution.HardwareImplementationReady)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.ImplementationUnavailable,
                "The hardware ray-tracing recorder is not implemented or ready for this scene."));
            available = false;
        }
        return available;
    }

    private static bool CanTraceSoftware(LightingCapabilities capabilities, List<LightingDiagnostic> diagnostics)
    {
        if (!capabilities.Compute)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.NoCompute,
                "Software ray tracing needs a compute queue."));
            return false;
        }

        if (capabilities.SdfResolution < 32)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.SdfResolutionTooLow,
                $"Software ray tracing needs an SDF volume of at least 32^3 (device reports {capabilities.SdfResolution})."));
            return false;
        }

        return true;
    }

    private static bool CanTraceSoftware(
        LightingCapabilities capabilities,
        LightingExecutionCapabilities execution,
        List<LightingDiagnostic> diagnostics)
    {
        bool available = CheckCommonExecution(execution, diagnostics);
        if (!capabilities.Compute || !execution.Compute)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.NoCompute,
                "Software GPU BVH ray tracing needs an available compute queue."));
            available = false;
        }
        if (execution.ShaderModel < 60)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.ImplementationUnavailable,
                "Software GPU BVH ray tracing requires shader model 6.0."));
            available = false;
        }
        if (!execution.SoftwareImplementationReady)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.ImplementationUnavailable,
                "The software GPU BVH recorder is not implemented or ready for this scene."));
            available = false;
        }
        return available;
    }

    private static bool CheckCommonExecution(
        LightingExecutionCapabilities execution,
        List<LightingDiagnostic> diagnostics)
    {
        bool available = true;
        if (!execution.BackendAvailable || execution.IsWarp)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.ImplementationUnavailable,
                "Ray-traced GI requires a hardware D3D12 adapter; WARP and unavailable backends are refused."));
            available = false;
        }
        if (!execution.CompilerAvailable)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.CompilerUnavailable,
                "DXC is unavailable, so SM6 GI shaders cannot be compiled."));
            available = false;
        }
        if (!execution.TransportSupported)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.TransportUnsupported,
                "The current scene transport (for example the transparency diagnostic) cannot be ray traced."));
            available = false;
        }
        if (!execution.ViewportSupported)
        {
            diagnostics.Add(new(LightingDiagnosticCodes.TransportUnsupported,
                "The current viewport cannot host this ray-traced GI mode."));
            available = false;
        }
        return available;
    }
}
