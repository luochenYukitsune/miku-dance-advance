using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>
/// Lighting-mode policy: three user-visible choices (original / hardware RT / software RT) that
/// must degrade predictably and explain themselves in the menu.
/// </summary>
public sealed class LightingPolicyTests
{
    private static LightingCapabilities Hardware() => new(
        RenderApi.Direct3D12,
        HardwareRayTracing: true,
        ShaderModel63: true,
        Compute: true,
        SdfResolution: 128,
        VideoMemoryMiB: 8192);

    private static LightingCapabilities ComputeOnly() => new(
        RenderApi.Direct3D11,
        HardwareRayTracing: false,
        ShaderModel63: false,
        Compute: true,
        SdfResolution: 128);

    private static LightingCapabilities Weak() => new(
        RenderApi.Direct3D11,
        HardwareRayTracing: false,
        ShaderModel63: false,
        Compute: false,
        SdfResolution: 0);

    private static LightingExecutionCapabilities Execution() => new(
        BackendAvailable: true,
        IsWarp: false,
        Compute: true,
        ShaderModel: 65,
        RawRaytracingTier: 12,
        Device5: true,
        CommandList4: true,
        CompilerAvailable: true,
        SoftwareImplementationReady: true,
        HardwareImplementationReady: true,
        TransportSupported: true,
        ViewportSupported: true);

    [Fact]
    public void Original_lighting_is_always_available()
    {
        var selection = LightingPlanner.Select(new LightingRequest(LightingMode.Legacy), LightingCapabilities.None);

        Assert.True(selection.Success);
        Assert.Equal(LightingMode.Legacy, selection.Effective);
        Assert.False(selection.IsRayTraced);
        Assert.False(selection.FallbackUsed);
        Assert.Empty(selection.Diagnostics);
    }

    [Fact]
    public void Hardware_ray_tracing_is_selected_on_capable_device()
    {
        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing),
            Hardware());

        Assert.True(selection.Success);
        Assert.Equal(LightingMode.HardwareRayTracing, selection.Effective);
        Assert.True(selection.IsRayTraced);
        Assert.False(selection.FallbackUsed);
        Assert.Empty(selection.Diagnostics);
    }

    [Fact]
    public void Missing_rt_cores_fall_back_to_software_tracing_with_a_reason()
    {
        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing),
            ComputeOnly());

        Assert.True(selection.Success);
        Assert.Equal(LightingMode.SoftwareRayTracing, selection.Effective);
        Assert.True(selection.FallbackUsed);
        Assert.Contains(selection.Diagnostics, d => d.Code == LightingDiagnosticCodes.NoHardwareRayTracing);
    }

    [Fact]
    public void Missing_compute_falls_back_to_original_lighting()
    {
        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing),
            Weak());

        Assert.True(selection.Success);
        Assert.Equal(LightingMode.Legacy, selection.Effective);
        Assert.True(selection.FallbackUsed);
        Assert.False(selection.IsRayTraced);
    }

    [Fact]
    public void Fallback_disabled_turns_unavailability_into_failure()
    {
        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing, AllowFallback: false),
            ComputeOnly());

        Assert.False(selection.Success);
        Assert.Equal(LightingMode.HardwareRayTracing, selection.Requested);
    }

    [Fact]
    public void Compatibility_mode_never_upgrades_lighting()
    {
        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing, CompatMode: true),
            Hardware());

        Assert.True(selection.Success);
        Assert.Equal(LightingMode.Legacy, selection.Effective);
        Assert.True(selection.FallbackUsed);
        Assert.Contains(selection.Diagnostics, d => d.Code == LightingDiagnosticCodes.CompatForcesLegacy);
    }

    [Fact]
    public void Software_tracing_requires_an_sdf_volume()
    {
        var tinySdf = ComputeOnly() with { SdfResolution = 8 };

        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.SoftwareRayTracing),
            tinySdf);

        Assert.Equal(LightingMode.Legacy, selection.Effective);
        Assert.Contains(selection.Diagnostics, d => d.Code == LightingDiagnosticCodes.SdfResolutionTooLow);
    }

    [Fact]
    public void Menu_shows_three_modes_with_reasons_for_greyed_out_ones()
    {
        var options = LightingPlanner.Menu(ComputeOnly(), compatMode: false);

        Assert.Equal(3, options.Count);
        Assert.Equal(
            [LightingMode.Legacy, LightingMode.HardwareRayTracing, LightingMode.SoftwareRayTracing],
            options.Select(o => o.Mode));

        Assert.True(options[0].Available);
        Assert.False(options[1].Available);
        Assert.NotNull(options[1].UnavailableReason);
        Assert.Contains("ray tracing", options[1].UnavailableReason!, StringComparison.OrdinalIgnoreCase);
        Assert.True(options[2].Available);
        Assert.Null(options[2].UnavailableReason);
    }

    [Fact]
    public void Menu_in_compat_mode_offers_only_original_lighting()
    {
        var options = LightingPlanner.Menu(Hardware(), compatMode: true);

        Assert.True(options[0].Available);
        Assert.False(options[1].Available);
        Assert.False(options[2].Available);
        Assert.All(options.Skip(1), o => Assert.Contains("兼容模式", o.UnavailableReason!, StringComparison.Ordinal));
    }

    [Fact]
    public void Hardware_mode_needs_shader_model_63()
    {
        var oldShaderModel = Hardware() with { ShaderModel63 = false };

        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing),
            oldShaderModel);

        Assert.Equal(LightingMode.SoftwareRayTracing, selection.Effective);
        Assert.Contains(selection.Diagnostics, d => d.Code == LightingDiagnosticCodes.NoShaderModel63);
    }

    [Fact]
    public void Strict_policy_rejects_capable_device_without_implemented_backend()
    {
        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing, AllowFallback: false),
            Hardware(), Execution() with { HardwareImplementationReady = false });

        Assert.False(selection.Success);
        Assert.Equal(LightingMode.HardwareRayTracing, selection.Effective);
        Assert.Contains(selection.Diagnostics, d => d.Code == LightingDiagnosticCodes.ImplementationUnavailable);
    }

    [Fact]
    public void Strict_software_bvh_does_not_require_an_sdf()
    {
        var noSdf = Hardware() with { HardwareRayTracing = false, ShaderModel63 = false, SdfResolution = 0 };

        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.SoftwareRayTracing, AllowFallback: false),
            noSdf, Execution());

        Assert.True(selection.Success);
        Assert.Equal(LightingMode.SoftwareRayTracing, selection.Effective);
        Assert.DoesNotContain(selection.Diagnostics, d => d.Code == LightingDiagnosticCodes.SdfResolutionTooLow);
    }

    [Fact]
    public void Strict_hardware_accepts_raw_tier_twelve_and_shader_model_65()
    {
        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing, AllowFallback: false),
            Hardware(), Execution());

        Assert.True(selection.Success);
        Assert.Equal(LightingMode.HardwareRayTracing, selection.Effective);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Strict_hardware_requires_both_observed_and_execution_compute(bool observed, bool execution)
    {
        var result = LightingPlanner.Select(new(LightingMode.HardwareRayTracing, AllowFallback: false),
            Hardware() with { Compute = observed }, Execution() with { Compute = execution });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == LightingDiagnosticCodes.NoCompute);
    }

    [Theory]
    [InlineData(10, 65, true, true)]
    [InlineData(11, 63, true, true)]
    [InlineData(12, 65, false, true)]
    [InlineData(12, 65, true, false)]
    public void Strict_inline_ray_query_needs_tier_11_sm65_device5_and_list4(int tier, int shaderModel, bool device5, bool list4)
    {
        var unavailable = Execution() with
        {
            RawRaytracingTier = tier,
            ShaderModel = shaderModel,
            Device5 = device5,
            CommandList4 = list4,
        };

        var selection = LightingPlanner.Select(
            new LightingRequest(LightingMode.HardwareRayTracing, AllowFallback: false),
            Hardware(), unavailable);

        Assert.False(selection.Success);
        Assert.Equal(LightingMode.HardwareRayTracing, selection.Effective);
    }

    [Fact]
    public void Strict_policy_reports_compiler_and_unsupported_transport_separately()
    {
        var noCompiler = LightingPlanner.Select(
            new LightingRequest(LightingMode.SoftwareRayTracing, AllowFallback: false),
            Hardware(), Execution() with { CompilerAvailable = false });
        Assert.False(noCompiler.Success);
        Assert.Contains(noCompiler.Diagnostics, d => d.Code == LightingDiagnosticCodes.CompilerUnavailable);

        var unsupported = LightingPlanner.Select(
            new LightingRequest(LightingMode.SoftwareRayTracing, AllowFallback: false),
            Hardware(), Execution() with { TransportSupported = false });
        Assert.False(unsupported.Success);
        Assert.Contains(unsupported.Diagnostics, d => d.Code == LightingDiagnosticCodes.TransportUnsupported);
    }

    [Fact]
    public void Strict_menu_describes_real_one_bounce_gpu_bvh_and_locks_compat()
    {
        var options = LightingPlanner.Menu(Hardware(), Execution(), compatMode: false);
        Assert.True(options[1].Available);
        Assert.True(options[2].Available);
        Assert.Contains("GI", options[1].Description);
        Assert.Contains("BVH", options[2].Description);

        var compat = LightingPlanner.Menu(Hardware(), Execution(), compatMode: true);
        Assert.All(compat.Skip(1), option => Assert.False(option.Available));
    }
}
