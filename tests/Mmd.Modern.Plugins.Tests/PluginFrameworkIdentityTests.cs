using System.Reflection;
using Xunit;

namespace Mmd.Modern.Plugins.Tests;

public sealed class PluginFrameworkIdentityTests
{
    private const string FrameworkToken = "b03f5f7f11d50a3a";

    [Fact]
    public void Self_contained_inventory_does_not_share_application_or_third_party_assemblies()
    {
        var names = new[] { "System.Runtime", "Avalonia.Base", "Vortice.Direct3D12", "Mmd.Modern.App" };
        var available = names.Select(name => Identity(name, FrameworkToken)).ToArray();
        var inventory = PluginFrameworkIdentity.CreateInventory(available);

        Assert.True(PluginFrameworkIdentity.IsFrameworkAssembly(available[0], inventory));
        foreach (var candidate in available.Skip(1))
            Assert.False(PluginFrameworkIdentity.IsFrameworkAssembly(candidate, inventory));
    }

    [Fact]
    public void Framework_names_without_matching_strong_name_identity_are_private_dependencies()
    {
        var unsigned = new AssemblyName("System.PrivateDependency, Version=10.0.0.0, Culture=neutral");
        var actual = Identity("System.Text.Json", "cc7b13ffcd2ddd51");
        var inventory = PluginFrameworkIdentity.CreateInventory([unsigned, actual]);

        Assert.False(PluginFrameworkIdentity.IsFrameworkAssembly(unsigned, inventory));
        Assert.False(PluginFrameworkIdentity.IsFrameworkAssembly(new AssemblyName("System.Text.Json"), inventory));
        Assert.False(PluginFrameworkIdentity.IsFrameworkAssembly(Identity("System.Text.Json", FrameworkToken), inventory));
        Assert.True(PluginFrameworkIdentity.IsFrameworkAssembly(actual, inventory));
    }

    [Fact]
    public void Newer_private_framework_package_is_not_replaced_with_older_host_assembly()
    {
        var available = Identity("System.Text.Json", "cc7b13ffcd2ddd51");
        var inventory = PluginFrameworkIdentity.CreateInventory([available]);
        var newer = Identity("System.Text.Json", "cc7b13ffcd2ddd51");
        newer.Version = new Version(11, 0, 0, 0);

        Assert.False(PluginFrameworkIdentity.IsFrameworkAssembly(newer, inventory));
        Assert.True(PluginFrameworkIdentity.IsFrameworkAssembly(available, inventory));
    }

    [Fact]
    public void Current_core_framework_and_legacy_facade_identities_remain_available()
    {
        var runtimeIdentities = new[]
        {
            typeof(object).Assembly.GetName(),
            typeof(System.Text.Json.JsonSerializer).Assembly.GetName(),
            typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly.GetName(),
            typeof(Microsoft.VisualBasic.Constants).Assembly.GetName(),
            typeof(Microsoft.Win32.RegistryKey).Assembly.GetName()
        };
        Assert.All(runtimeIdentities, identity => Assert.True(PluginFrameworkIdentity.IsFrameworkAssembly(identity)));
        var facades = new[]
        {
            Identity("mscorlib", "b77a5c561934e089"), Identity("netstandard", "cc7b13ffcd2ddd51"),
            Identity("Microsoft.VisualBasic", FrameworkToken), Identity("Microsoft.Win32.Primitives", FrameworkToken),
            Identity("WindowsBase", "31bf3856ad364e35")
        };
        var inventory = PluginFrameworkIdentity.CreateInventory(facades);
        Assert.All(facades, identity => Assert.True(PluginFrameworkIdentity.IsFrameworkAssembly(identity, inventory)));
    }

    private static AssemblyName Identity(string name, string token) =>
        new($"{name}, Version=10.0.0.0, Culture=neutral, PublicKeyToken={token}");
}
