namespace Mmd.Modern.TestSupport;

/// <summary>
/// Scratch root for tests that need real files. Defaults to the system temp directory so tests run on any
/// machine; set MDA_TEST_ROOT to put fixtures on a specific (for example Windows-semantics) volume.
/// </summary>
internal static class TestWorkspace
{
    internal static string Root(string area)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        string? configured = Environment.GetEnvironmentVariable("MDA_TEST_ROOT");
        string root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "MDA", "Tests") : configured;
        return Path.GetFullPath(Path.Combine(root, area));
    }
}
