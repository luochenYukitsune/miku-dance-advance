using System.Reflection;
using System.Runtime.InteropServices;

namespace Mmd.Modern.App;

internal sealed record EditorAboutInfo(string Version, string? Build, string Runtime)
{
    internal static EditorAboutInfo Current
    {
        get
        {
            Assembly product = typeof(EditorWindow).Assembly;
            return FromMetadata(product.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                product.GetName().Version);
        }
    }

    internal static EditorAboutInfo FromMetadata(string? informationalVersion, Version? assemblyVersion)
    {
        string[] identity = informationalVersion?.Trim().Split('+', 2) ?? [];
        string version = identity.Length > 0 && !string.IsNullOrWhiteSpace(identity[0])
            ? identity[0] : assemblyVersion?.ToString() ?? "—";
        string? build = identity.Length == 2 && !string.IsNullOrWhiteSpace(identity[1]) ? identity[1].Trim() : null;
        if (build?.Length > 12) build = build[..12];
        return new(version, build, RuntimeInformation.FrameworkDescription + " · " + RuntimeInformation.ProcessArchitecture);
    }
}
