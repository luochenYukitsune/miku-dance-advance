using System.Reflection;
using System.Runtime.Versioning;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;

[assembly: SupportedOSPlatform("windows")]

/// <summary>Headless probe run against a published/installed win-x64 MDA directory to confirm
/// every bundled rendering style plugin discovers cleanly, passes host compatibility checks,
/// activates with shared contract identity, and registers its expected style provider.</summary>
internal static class EditorBundledPluginsProbe
{
    private static readonly (string PluginId, string StyleId)[] DefaultExpected =
    [
        ("mda.style.genshin", "genshin"),
        ("mda.style.star-rail", "star-rail"),
        ("mda.style.wuthering-waves", "wuthering-waves"),
        ("mda.style.endfield", "endfield")
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: EditorBundledPluginsProbe <appDirectory> [<pluginId>=<styleId> ...]");
            return 2;
        }

        string appDirectory = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string pluginsRoot = Path.Combine(appDirectory, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            Console.Error.WriteLine($"FAIL: Missing plugins directory: {pluginsRoot}");
            return 1;
        }

        var expected = args.Length > 1
            ? args.Skip(1).Select(pair =>
            {
                string[] parts = pair.Split('=', 2);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                    throw new ArgumentException($"Expected '<pluginId>=<styleId>', got '{pair}'.");
                return (PluginId: parts[0], StyleId: parts[1]);
            }).ToArray()
            : DefaultExpected;

        Version contractVersion = typeof(IMdaPlugin).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Plugin contract assembly has no version.");
        if (contractVersion != new Version(1, 0, 0, 0))
        {
            Console.Error.WriteLine($"FAIL: Mmd.Modern.Plugins.Contracts version mutated to {contractVersion} (expected 1.0.0.0).");
            return 1;
        }

        string appDll = Path.Combine(appDirectory, "Mmd.Modern.App.dll");
        Version rawAppVersion = AssemblyName.GetAssemblyName(appDll).Version ?? new Version(0, 1, 0, 0);
        Version hostVersion = new(rawAppVersion.Major, rawAppVersion.Minor, Math.Max(0, rawAppVersion.Build));

        await using var manager = new PluginManager(
            new PluginHostInfo(hostVersion, 1, "zh-CN"),
            [new PluginCapability(PluginCapabilities.RenderingStyles, typeof(IRenderingStyleProvider))]);

        var discovery = await manager.RefreshAsync([pluginsRoot]);
        if (!discovery.Succeeded)
        {
            Console.Error.WriteLine($"FAIL: PluginManager discovery failed: {discovery.Diagnostic}");
            return 1;
        }

        var verifiedSummary = new List<string>(expected.Length);
        foreach (var (pluginId, expectedStyleId) in expected)
        {
            var discovered = manager.Plugins.SingleOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));
            if (discovered is null)
            {
                Console.Error.WriteLine($"FAIL: Bundled plugin '{pluginId}' was not discovered under {pluginsRoot}.");
                return 1;
            }
            if (discovered.State != PluginState.Disabled)
            {
                Console.Error.WriteLine($"FAIL: Bundled plugin '{pluginId}' entered unexpected discovery state {discovered.State}: {discovered.Diagnostic}");
                return 1;
            }

            var enabled = await manager.EnableAsync(pluginId);
            if (!enabled.Succeeded)
            {
                Console.Error.WriteLine($"FAIL: Enabling bundled plugin '{pluginId}' failed: {enabled.Diagnostic}");
                return 1;
            }

            var active = manager.Plugins.Single(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));
            if (active.State != PluginState.Active)
            {
                Console.Error.WriteLine($"FAIL: Bundled plugin '{pluginId}' is {active.State} after EnableAsync: {active.Diagnostic}");
                return 1;
            }

            var providers = manager.Extensions
                .GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles)
                .Where(e => string.Equals(e.PluginId, pluginId, StringComparison.Ordinal))
                .ToArray();
            if (providers.Length == 0)
            {
                Console.Error.WriteLine($"FAIL: Bundled plugin '{pluginId}' registered no IRenderingStyleProvider extensions.");
                return 1;
            }

            var styles = providers.SelectMany(p => p.Value.GetStyles() ?? []).ToArray();
            var matched = styles.SingleOrDefault(s => string.Equals(s.StyleId, expectedStyleId, StringComparison.Ordinal));
            if (matched is null)
            {
                Console.Error.WriteLine($"FAIL: Bundled plugin '{pluginId}' did not expose expected style '{expectedStyleId}'.");
                return 1;
            }
            if (string.IsNullOrWhiteSpace(matched.DisplayName.Resolve("zh-CN"))
                || string.IsNullOrWhiteSpace(matched.DisplayName.Resolve("en")))
            {
                Console.Error.WriteLine($"FAIL: Style '{expectedStyleId}' in '{pluginId}' is missing zh-CN or en localized DisplayName.");
                return 1;
            }

            verifiedSummary.Add($"{pluginId}->{expectedStyleId}");
        }

        Console.WriteLine($"PASS bundled style plugins verified ({verifiedSummary.Count}/{expected.Length} active, contracts={contractVersion}, app={rawAppVersion}): {string.Join(", ", verifiedSummary)}");
        return 0;
    }
}
