using System.Runtime.Loader;

namespace Mmd.Modern.PluginFixture.PrivateDependency;

public static class PrivateMessage
{
    public static string Format(string pluginId) => $"private-dependency:{pluginId}";

    public static AssemblyLoadContext? LoadContext => AssemblyLoadContext.GetLoadContext(typeof(PrivateMessage).Assembly);
}
