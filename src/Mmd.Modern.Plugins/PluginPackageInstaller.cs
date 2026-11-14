using System.IO.Compression;
using System.Text.Json;

namespace Mmd.Modern.Plugins;

public sealed record PluginInstallResult(
    bool Succeeded,
    string? PluginId = null,
    string? Directory = null,
    string? Diagnostic = null);

/// <summary>Installs one ZIP package without loading or enabling its plugin assembly.</summary>
public static class PluginPackageInstaller
{
    private const int MaximumEntries = 2048;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const string StagingPrefix = ".mda-plugin-install-";

    public static Task<PluginInstallResult> InstallAsync(
        string archivePath,
        string installationDirectory,
        CancellationToken cancellationToken = default) =>
        InstallAsync(archivePath, installationDirectory, [], cancellationToken);

    public static async Task<PluginInstallResult> InstallAsync(
        string archivePath,
        string installationDirectory,
        IEnumerable<string> reservedPluginIds,
        CancellationToken cancellationToken = default)
    {
        string? staging = null;
        string? stagingParent = null;
        PluginInstallResult? result = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
            ArgumentNullException.ThrowIfNull(reservedPluginIds);
            // Discovery may report synthetic IDs for malformed manifests. Keep
            // those strings harmless while reserving every real discovered ID.
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in reservedPluginIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(id))
                    reserved.Add(id);
            }
            var installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
            stagingParent = Path.GetDirectoryName(installation);
            if (string.IsNullOrEmpty(stagingParent))
                throw new InvalidDataException("The plugin installation directory must have a parent directory.");
            RequireNoReparsePoints(installation);

            await using var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var entries = ReadPackageEntries(archive, cancellationToken);
            Directory.CreateDirectory(stagingParent);
            RequireNoReparsePoints(stagingParent);
            staging = CreateStagingDirectory(stagingParent);
            await ExtractAsync(entries, staging, cancellationToken).ConfigureAwait(false);

            // This validates schema, identifiers and the entry DLL's existence only.
            // Compatibility and enablement remain the plugin manager's responsibility.
            var manifest = await PluginManifest.ReadAsync(staging, cancellationToken).ConfigureAwait(false);
            RequirePortableComponent(manifest.Id);
            if (reserved.Contains(manifest.Id))
                throw ExistingPlugin(manifest.Id);
            var target = PluginPaths.RequireContainedFile(installation, Path.Combine(installation, manifest.Id));
            RequireNoReparsePoints(target);
            if (PathExists(target) || await ContainsInstalledIdAsync(installation, manifest.Id, cancellationToken).ConfigureAwait(false))
                throw ExistingPlugin(manifest.Id);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(installation);
            RequireNoReparsePoints(target);
            if (PathExists(target))
                throw ExistingPlugin(manifest.Id);
            // Staging is a sibling of the installation root, so committing does not
            // expose a partially extracted plugin to discovery on this volume.
            Directory.Move(staging, target);
            staging = null;
            result = new(true, manifest.Id, target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            result = new(false, Diagnostic: exception.Message);
        }
        finally
        {
            if (staging is not null && stagingParent is not null)
            {
                var cleanupDiagnostic = TryCleanupStaging(stagingParent, staging);
                if (cleanupDiagnostic is not null && result is not null)
                    result = result with { Diagnostic = $"{result.Diagnostic} Staging cleanup failed: {cleanupDiagnostic}" };
            }
        }
        return result!;
    }

    private static async Task<bool> ContainsInstalledIdAsync(string installation, string id,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installation))
            return false;
        foreach (var directory in Directory.EnumerateDirectories(installation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RequireNoReparsePoints(directory);
                var manifest = await PluginManifest.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                if (string.Equals(manifest.Id, id, StringComparison.Ordinal))
                    return true;
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                // Invalid folders remain discovery diagnostics; they must not
                // prevent installation of an unrelated well-formed package.
            }
        }
        return false;
    }

    private static List<PackageEntry> ReadPackageEntries(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("A plugin ZIP may contain at most 2048 entries.");
        var entries = new List<PackageEntry>(archive.Entries.Count);
        // Apply Windows-equivalent collision rules on every host so a package has
        // the same contents when transferred between supported file systems.
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var componentPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var isDirectory = name.EndsWith('/');
            if (isDirectory)
                name = name[..^1];
            if (string.IsNullOrEmpty(name) || name.Length > 1024 || name.StartsWith('/') || name.Contains(':'))
                throw new InvalidDataException("ZIP entries must have bounded relative paths without drive names or alternate streams.");
            var components = name.Split('/');
            var prefix = "";
            foreach (var component in components)
            {
                RequirePortableComponent(component);
                prefix = prefix.Length == 0 ? component : prefix + "/" + component;
                if (componentPaths.TryGetValue(prefix, out var previous) && previous != prefix)
                    throw new InvalidDataException($"ZIP paths use conflicting letter case: '{prefix}'.");
                componentPaths.TryAdd(prefix, prefix);
            }
            if (!paths.TryAdd(name, isDirectory))
                throw new InvalidDataException($"Duplicate ZIP entry path '{name}'.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                unixType is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("ZIP entries must not be symbolic links, reparse points or special files.");
            if ((unixType == 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0) && !isDirectory)
                throw new InvalidDataException("A ZIP directory entry must end with a directory separator.");
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
                throw new InvalidDataException("A plugin ZIP entry exceeds the 64 MiB expanded size limit.");
            if (isDirectory && entry.Length != 0)
                throw new InvalidDataException("A ZIP directory entry must be empty.");
            if (entry.Length > MaximumExpandedBytes - expandedBytes)
                throw new InvalidDataException("The plugin ZIP exceeds the 512 MiB total expanded size limit.");
            expandedBytes += entry.Length;
            entries.Add(new(entry, name, isDirectory));
        }
        foreach (var entry in entries)
        {
            var separator = entry.Path.LastIndexOf('/');
            while (separator >= 0)
            {
                var parent = entry.Path[..separator];
                if (paths.TryGetValue(parent, out var isDirectory) && !isDirectory)
                    throw new InvalidDataException($"A ZIP file is also used as a directory: '{parent}'.");
                separator = parent.LastIndexOf('/');
            }
        }

        var manifests = entries.Where(entry => !entry.IsDirectory &&
            (entry.Path == PluginManifest.FileName || entry.Path.EndsWith('/' + PluginManifest.FileName, StringComparison.Ordinal)))
            .ToArray();
        if (manifests.Length != 1)
            throw new InvalidDataException("The plugin ZIP must contain exactly one mda-plugin.json manifest.");
        var packagePrefix = manifests[0].Path[..^PluginManifest.FileName.Length];
        var packageEntries = new List<PackageEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Path.StartsWith(packagePrefix, StringComparison.Ordinal))
            {
                var relative = entry.Path[packagePrefix.Length..];
                if (relative.Length != 0)
                    packageEntries.Add(entry with { Path = relative });
            }
            else if (!entry.IsDirectory || !packagePrefix.StartsWith(entry.Path + '/', StringComparison.Ordinal))
            {
                throw new InvalidDataException("The plugin ZIP contains files or sibling directories outside its package folder.");
            }
        }
        return packageEntries;
    }

    private static async Task ExtractAsync(IReadOnlyList<PackageEntry> entries, string staging,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long expandedBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PluginPaths.RequireContainedFile(staging,
                Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            RequireNoReparsePoints(path);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(path);
                RequireNoReparsePoints(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RequireNoReparsePoints(path);
            await using var input = entry.ArchiveEntry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long entryBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                entryBytes += read;
                expandedBytes += read;
                if (entryBytes > MaximumEntryBytes || entryBytes > entry.ArchiveEntry.Length || expandedBytes > MaximumExpandedBytes)
                    throw new InvalidDataException("The plugin ZIP expanded beyond its declared or permitted size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (entryBytes != entry.ArchiveEntry.Length)
                throw new InvalidDataException("A plugin ZIP entry did not match its declared expanded size.");
        }
    }

    private static void RequirePortableComponent(string component)
    {
        if (string.IsNullOrEmpty(component) || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ') ||
            component.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
            throw new InvalidDataException("ZIP and plugin directory paths must not contain traversal, invalid or ambiguous components.");
        var deviceName = component.Split('.')[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (deviceName.Length == 4 && deviceName[3] is >= '1' and <= '9' &&
                (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("ZIP and plugin directory paths must not use reserved Windows device names.");
    }

    private static string CreateStagingDirectory(string parent)
    {
        var path = PluginPaths.RequireContainedFile(parent, Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N")));
        RequireNoReparsePoints(path);
        if (PathExists(path))
            throw new IOException("A fresh plugin staging directory could not be created.");
        Directory.CreateDirectory(path);
        RequireNoReparsePoints(path);
        return path;
    }

    private static void RequireNoReparsePoints(string path)
    {
        var absolute = Path.GetFullPath(path);
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        CheckReparsePoint(current);
        foreach (var component in absolute[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            CheckReparsePoint(current);
        }
    }

    private static void CheckReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin installation paths must not contain symbolic links or reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static string? TryCleanupStaging(string parent, string staging)
    {
        try
        {
            var verified = PluginPaths.RequireContainedFile(parent, staging);
            if (Path.GetDirectoryName(verified) != parent || !Path.GetFileName(verified).StartsWith(StagingPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The staging cleanup path is outside its owned parent directory.");
            RequireNoReparsePoints(verified);
            if (!Directory.Exists(verified))
                return null;
            var pending = new Stack<string>();
            pending.Push(verified);
            while (pending.TryPop(out var directory))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    PluginPaths.RequireContainedFile(verified, child);
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("A reparse point prevented staging cleanup.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(child);
                }
            }
            Directory.Delete(verified, recursive: true);
            return null;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return exception.Message;
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static InvalidDataException ExistingPlugin(string id) => new(
        $"Plugin '{id}' already exists. ZIP installation cannot replace an installed or enabled plugin; keep the existing directory unchanged.");

    private static bool IsExpectedFailure(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;

    private sealed record PackageEntry(ZipArchiveEntry ArchiveEntry, string Path, bool IsDirectory);
}
