using System.Reflection;
using Avalonia.Headless;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorStartupConfigurationTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    [Fact]
    public async Task ControlledConfigurationDefaultsToNoPersistenceAndConstructionDoesNotCreateDirectories() => await session.Dispatch(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-startup-configuration-" + Guid.NewGuid().ToString("N"));
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "recovery") };
        var configuration = new EditorStartupConfiguration(preferences, Path.Combine(directory, "cache"));
        Assert.False(configuration.PersistPreferences);
        configuration.Validate();
        Assert.False(Directory.Exists(directory));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InvalidControlledConfigurationIsRejectedBeforeStartupOrDirectoryCreation() => await session.Dispatch(() =>
    {
        string cache = Path.Combine(Path.GetTempPath(), "mda-startup-configuration-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentNullException>(() => new EditorStartupConfiguration(null!, cache).Validate());
        Assert.ThrowsAny<ArgumentException>(() => new EditorStartupConfiguration(new(), "relative-cache").Validate());
        Assert.ThrowsAny<ArgumentException>(() => new EditorStartupConfiguration(new() { RecoveryDirectory = "relative-recovery" }, cache).Validate());
        Assert.False(Directory.Exists(cache));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public void OrdinaryAppStillHasItsOriginalParameterlessConstructor()
        => Assert.NotNull(typeof(global::Mmd.Modern.App.App).GetConstructor(BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null));
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
