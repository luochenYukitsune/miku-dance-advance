using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12FsrRuntimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("amd_fidelityfx_loader_dx12.dll")]
    [InlineData("relative/amd_fidelityfx_loader_dx12.dll")]
    public void RuntimeRequiresAnExplicitAbsolutePath(string path)
        => Assert.Throws<ArgumentException>(() => D3D12FsrRuntimeProbe.ProbeCurrentDevice(path));

    [Fact]
    public void MissingOptionalInstallationDoesNotCreateAGpuDevice()
    {
        var result = D3D12FsrRuntimeProbe.ProbeCurrentDevice(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "amd_fidelityfx_loader_dx12.dll"));
        Assert.Equal(D3D12FsrRuntimeStatus.NotInstalled, result.Status);
        Assert.Null(result.AdapterName);
        Assert.Empty(result.Providers);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void OfficialRuntimeQueriesActualDeviceAndCreatesAContext()
    {
        string path = Environment.GetEnvironmentVariable("MMD_FSR_RUNTIME_PATH")
            ?? throw new InvalidOperationException("Set MMD_FSR_RUNTIME_PATH to the explicit official loader DLL path for this opt-in GPU test.");
        var result = D3D12FsrRuntimeProbe.ProbeCurrentDevice(path);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        string json = JsonSerializer.Serialize(result, options);
        output.WriteLine(json);
        string? report = Environment.GetEnvironmentVariable("MMD_FSR_PROBE_REPORT");
        if (!string.IsNullOrEmpty(report)) File.WriteAllText(report, json);
        Assert.Equal(D3D12FsrRuntimeStatus.ContextCreated, result.Status);
        Assert.Equal(0u, result.QueryReturnCode);
        Assert.NotEmpty(result.Providers);
        Assert.Equal(D3D12FsrRuntimeStatus.ContextCreated, result.DefaultContext!.Status);
        Assert.Equal(0u, result.DefaultContext.DestroyReturnCode);
        Assert.NotNull(result.DefaultContext.ActualProviderName);
        Assert.All(result.Providers, provider => Assert.False(string.IsNullOrEmpty(provider.Name)));
        Assert.DoesNotContain(result.Providers, provider => provider.Status == D3D12FsrRuntimeStatus.Executed);
    }
}
