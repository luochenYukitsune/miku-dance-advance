using System.Reflection;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorAboutInfoTests
{
    [Fact]
    public void InformationalVersionKeepsPrereleaseAndSeparatesBuildIdentity()
    {
        var info = EditorAboutInfo.FromMetadata("0.2.0-preview.3+140ed76abcde87654321", new Version(1, 0, 0, 0));

        Assert.Equal("0.2.0-preview.3", info.Version);
        Assert.Equal("140ed76abcde", info.Build);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public void MissingInformationalVersionUsesActualAssemblyVersion(string? informationalVersion)
    {
        var info = EditorAboutInfo.FromMetadata(informationalVersion, new Version(2, 4, 6, 8));

        Assert.Equal("2.4.6.8", info.Version);
        Assert.Null(info.Build);
    }

    [Fact]
    public void RuntimeIdentityComesFromTheProductAssembly()
    {
        Assembly product = typeof(EditorWindow).Assembly;
        var expected = EditorAboutInfo.FromMetadata(
            product.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            product.GetName().Version);

        var actual = EditorAboutInfo.Current;

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Build, actual.Build);
        Assert.Contains(Environment.Version.ToString(), actual.Runtime);
    }
}
