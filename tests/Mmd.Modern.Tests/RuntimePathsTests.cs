using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RuntimePathsTests
{
    [Fact]
    public void DxcExplicitCachePrecedesEnvironmentWorkspaceAndLocalData()
    {
        string explicitCache = Path.Combine(Root, "explicit", "..", "cache");

        string result = D3D12RuntimePaths.ResolveDxcCacheDirectory(explicitCache,
            Path.Combine(Root, "environment"), Path.Combine(Root, "workspace"), null);

        Assert.Equal(Path.GetFullPath(explicitCache), result);
    }

    [Fact]
    public void DxcEnvironmentCachePrecedesWorkspaceAndLocalData()
    {
        string environmentCache = Path.Combine(Root, "environment", "..", "cache");

        string result = D3D12RuntimePaths.ResolveDxcCacheDirectory(null,
            environmentCache, Path.Combine(Root, "workspace"), null);

        Assert.Equal(Path.GetFullPath(environmentCache), result);
    }

    [Fact]
    public void DxcWorkspaceCacheRetainsItsExistingLocation()
    {
        string workspace = Path.Combine(Root, "workspace");

        string result = D3D12RuntimePaths.ResolveDxcCacheDirectory(null, null,
            workspace, Path.Combine(Root, "local-data"));

        Assert.Equal(Path.Combine(workspace, ".dev-cache", "modern-dotnet", "dxc-compute"), result);
    }

    [Fact]
    public void DxcOutsideWorkspaceUsesLocalApplicationData()
    {
        string localData = Path.Combine(Root, "local-data");

        string result = D3D12RuntimePaths.ResolveDxcCacheDirectory(null, null, null, localData);

        Assert.Equal(Path.Combine(localData, "MDA", "Dxc"), result);
    }

    [Fact]
    public void DlssEnvironmentLogsPrecedeLocalData()
    {
        string environmentLogs = Path.Combine(Root, "environment", "..", "logs");

        string result = D3D12RuntimePaths.ResolveDlssLogDirectory(environmentLogs, null);

        Assert.Equal(Path.GetFullPath(environmentLogs), result);
    }

    [Fact]
    public void DlssDefaultLogsUseLocalApplicationData()
    {
        string localData = Path.Combine(Root, "local-data");

        string result = D3D12RuntimePaths.ResolveDlssLogDirectory(null, localData);

        Assert.Equal(Path.Combine(localData, "MDA", "Logs", "Dlss"), result);
    }

    [Fact]
    public void RelativeExplicitOverridesRetainFullPathNormalization()
    {
        const string relative = "mda-explicit-runtime-path";
        string expected = Path.GetFullPath(relative);

        Assert.Equal(expected, D3D12RuntimePaths.ResolveDxcCacheDirectory(relative, null, null, null));
        Assert.Equal(expected, D3D12RuntimePaths.ResolveDxcCacheDirectory(null, relative, null, null));
        Assert.Equal(expected, D3D12RuntimePaths.ResolveDlssLogDirectory(relative, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative-local-data")]
    public void DxcUnavailableLocalDataRequiresAnExplicitOverride(string? localData)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            D3D12RuntimePaths.ResolveDxcCacheDirectory(null, null, null, localData));

        Assert.Contains("LocalApplicationData", error.Message, StringComparison.Ordinal);
        Assert.Contains("MMD_DXC_CACHE", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative-local-data")]
    public void DlssUnavailableLocalDataRequiresAnExplicitOverride(string? localData)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            D3D12RuntimePaths.ResolveDlssLogDirectory(null, localData));

        Assert.Contains("LocalApplicationData", error.Message, StringComparison.Ordinal);
        Assert.Contains("MMD_DLSS_LOG_DIRECTORY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidDxcExplicitCacheDoesNotFallThroughToEnvironment()
    {
        Assert.Throws<ArgumentException>(() => D3D12RuntimePaths.ResolveDxcCacheDirectory(
            "", Path.Combine(Root, "environment"), Path.Combine(Root, "workspace"), Root));
    }

    [Fact]
    public void InvalidDxcEnvironmentCacheDoesNotFallThroughToWorkspace()
    {
        Assert.Throws<ArgumentException>(() => D3D12RuntimePaths.ResolveDxcCacheDirectory(
            null, "", Path.Combine(Root, "workspace"), Root));
    }

    [Fact]
    public void InvalidDlssEnvironmentLogsDoNotFallThroughToLocalData()
    {
        Assert.Throws<ArgumentException>(() => D3D12RuntimePaths.ResolveDlssLogDirectory("", Root));
    }

    private static string Root => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mda-runtime-paths-tests"));
}
