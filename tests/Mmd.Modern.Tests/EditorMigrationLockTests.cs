using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorMigrationLockTests
{
    [Fact]
    public void OnlyLockContentionIsRetried()
    {
        Assert.False(EditorUserDataPaths.IsLockContention(new FileNotFoundException()));
        Assert.False(EditorUserDataPaths.IsLockContention(new DirectoryNotFoundException()));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(EditorUserDataPaths.IsLockContention(new IOException("sharing", unchecked((int)0x80070020))));
            Assert.True(EditorUserDataPaths.IsLockContention(new IOException("lock", unchecked((int)0x80070021))));
            Assert.False(EditorUserDataPaths.IsLockContention(new IOException("disk full", unchecked((int)0x80070070))));
        }
    }
}
