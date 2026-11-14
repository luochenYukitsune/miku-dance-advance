namespace Mmd.Modern.Plugins;

internal static class PluginPaths
{
    internal static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string RequireContainedFile(string directory, string file)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var path = Path.GetFullPath(file);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, Comparison))
            throw new InvalidDataException("A plugin file path escapes its plugin directory.");
        // Reparse points are rejected on every existing component beneath the plugin root.
        // This is a loader containment rule, not a security boundary against plugin code.
        RequireNoReparsePoint(root);
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            RequireNoReparsePoint(current);
        }
        return path;
    }

    private static void RequireNoReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin paths must not contain symbolic links or reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
