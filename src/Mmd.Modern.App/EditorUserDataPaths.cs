using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Mmd.Modern.App;

/// <summary>Editor-owned personal files. Construction is read-only; only ordinary production
/// startup initializes the installer-selected layout. Explicit hosts keep their own paths.</summary>
internal sealed class EditorUserDataPaths
{
    internal const string RegistryPath = @"Software\MDA\MikuDanceAdvance";
    internal const string MarkerFileName = ".mda-user-data.ini";
    internal const string InitialMarker = "[MDA]\r\nAppId=MDA.MikuDanceAdvance\r\nLayoutVersion=1\r\n";
    private static readonly string LegacyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDA");
    private static EditorUserDataPaths current = new(LegacyDirectory);
    private readonly string legacyDirectory;
    private readonly string? previousRoot;
    internal static EditorUserDataPaths Current => Volatile.Read(ref current);
    internal string RootDirectory { get; }
    internal bool IsConfigured { get; }
    internal string ConfigDirectory => IsConfigured ? Path.Combine(RootDirectory, "Config") : RootDirectory;
    internal string PluginDirectory => Path.Combine(RootDirectory, "Plugins");
    internal string RecoveryDirectory => Path.Combine(RootDirectory, "Recovery");
    internal string ProjectCacheDirectory => IsConfigured ? Path.Combine(RootDirectory, "Cache", "Projects")
        : Path.Combine(RootDirectory, "ProjectCache");
    internal string ExportLogDirectory => Path.Combine(RootDirectory, "Logs", "Export");
    internal string UpdateDirectory => Path.Combine(ConfigDirectory, "Updates");
    internal string MarkerPath => Path.Combine(RootDirectory, MarkerFileName);

    internal EditorUserDataPaths(string legacyDirectory, string? configuredRoot = null, string? previousRoot = null)
    {
        this.legacyDirectory = NormalizeDirectory(legacyDirectory);
        IsConfigured = configuredRoot is not null;
        RootDirectory = configuredRoot is null ? this.legacyDirectory : NormalizeDirectory(configuredRoot);
        this.previousRoot = previousRoot is null ? null : NormalizeDirectory(previousRoot);
    }

    internal static void InitializeProduction()
    {
        try
        {
            string? root = null, previous = null;
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
                root = ReadRegistryDirectory(key, "UserDataRoot");
                if (root is not null) previous = ReadRegistryDirectory(key, "PreviousUserDataRoot");
            }
            var paths = new EditorUserDataPaths(LegacyDirectory, root, previous);
            paths.Initialize();
            // Publish only a fully usable layout. A failure never switches to empty defaults.
            Volatile.Write(ref current, paths);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            throw new IOException(EditorLocalization.Text("Mda.Text.UserData.Unavailable"), error);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadRegistryDirectory(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        return value is string text && !string.IsNullOrWhiteSpace(text) ? text
            : throw new InvalidDataException(EditorLocalization.Text("Mda.Text.UserData.InvalidConfiguration"));
    }

    // A second instance started at the same moment (double-click, file association) used to fail startup on the
    // first sharing violation. Wait briefly for the other instance to finish its migration instead.
    private static FileStream AcquireMigrationLock(string lockPath)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (!(DateTime.UtcNow > deadline) && File.Exists(lockPath))
            {
                Thread.Sleep(100);
            }
        }
    }

    internal void Initialize()
    {
        if (!IsConfigured) return;
        try
        {
            EnsureNoReparse(RootDirectory);
            _ = ReadMarker(RootDirectory);
            string lockPath = Path.Combine(RootDirectory, ".mda-user-data.lock");
            EnsureNoReparse(lockPath);
            using var migrationLock = AcquireMigrationLock(lockPath);
            bool initialized = ReadMarker(RootDirectory);
            foreach (string path in new[] { RootDirectory, ConfigDirectory, PluginDirectory, RecoveryDirectory,
                ProjectCacheDirectory, UpdateDirectory, ExportLogDirectory }) EnsureWritableDirectory(path);
            if (initialized) return;

            string sourceRoot = previousRoot ?? legacyDirectory;
            bool sourceIsLegacy = SamePath(sourceRoot, legacyDirectory);
            if (sourceIsLegacy && SourceFileExists(Path.Combine(sourceRoot, MarkerFileName)))
                // A default root that has completed migration owns its current settings in
                // Config. Its retained flat files are only the original legacy snapshot.
                sourceIsLegacy = !ReadMarker(sourceRoot);
            else if (!sourceIsLegacy && !SamePath(sourceRoot, RootDirectory)) _ = ReadMarker(sourceRoot);
            string sourceConfig = sourceIsLegacy ? sourceRoot : Path.Combine(sourceRoot, "Config");
            EnsureNoReparse(sourceConfig);
            string sourceRecovery = Path.Combine(sourceRoot, "Recovery");
            string preferenceSource = Path.Combine(sourceConfig, "editor-preferences.json");
            var copies = new List<MigrationFile>();
            if (SourceFileExists(preferenceSource))
            {
                EnsureNoReparse(preferenceSource);
                var preferences = EditorPreferences.LoadForMigration(preferenceSource, sourceRecovery);
                var migrated = SamePath(preferences.RecoveryDirectory, sourceRecovery)
                    ? preferences with { RecoveryDirectory = RecoveryDirectory } : preferences;
                // Include the previous default even when the user had selected another recovery
                // location. Large recovery snapshots stay in place and remain discoverable.
                migrated = migrated.WithRecoveryDirectoryHistory(preferences with { RecoveryDirectory = sourceRecovery });
                string target = Path.Combine(ConfigDirectory, "editor-preferences.json");
                if (!SamePath(preferenceSource, target)) copies.Add(new(preferenceSource, target, migrated.SerializeForStorage()));
            }
            else if (!SamePath(sourceRecovery, RecoveryDirectory))
            {
                var preferences = new EditorPreferences { RecoveryDirectory = RecoveryDirectory }
                    .WithRecoveryDirectoryHistory(new EditorPreferences { RecoveryDirectory = sourceRecovery });
                copies.Add(new(null, Path.Combine(ConfigDirectory, "editor-preferences.json"), preferences.SerializeForStorage()));
            }
            foreach (string name in new[] { "editor-preview.json", "editor-output.json" })
            {
                string source = Path.Combine(sourceConfig, name);
                if (SourceFileExists(source)) copies.Add(new(source, Path.Combine(ConfigDirectory, name)));
            }
            string sourceUpdateSkip = Path.Combine(sourceConfig, "Updates", "skipped-version.txt");
            if (SourceFileExists(sourceUpdateSkip)) copies.Add(new(sourceUpdateSkip, Path.Combine(UpdateDirectory, "skipped-version.txt")));
            string sourcePlugins = Path.Combine(sourceRoot, "Plugins");
            EnsureNoReparse(sourcePlugins);
            if (!SamePath(sourcePlugins, PluginDirectory) && (IsWithin(sourcePlugins, PluginDirectory) || IsWithin(PluginDirectory, sourcePlugins)))
                throw new IOException(EditorLocalization.Text("Mda.Text.UserData.InvalidConfiguration"));
            if (!SamePath(sourcePlugins, PluginDirectory) && Directory.Exists(sourcePlugins))
                CollectPluginFiles(sourcePlugins, PluginDirectory, copies);
            // Validate the whole source tree before publishing any plugin bytes; importing never
            // loads assemblies or follows junctions/symbolic links outside the old plugin root.
            foreach (var copy in copies) ValidateCopy(copy);
            foreach (var copy in copies) PublishCopy(copy);
            WriteMarkerInitialized();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Text.Json.JsonException or System.Security.SecurityException)
        {
            throw new IOException(EditorLocalization.Format("Mda.Text.UserData.InitializationFailed", RootDirectory), error);
        }
    }

    private static void CollectPluginFiles(string source, string target, List<MigrationFile> copies)
    {
        EnsureNoReparse(source);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            EnsureNoReparse(entry);
            string destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CollectPluginFiles(entry, destination, copies);
            else copies.Add(new(entry, destination));
        }
    }

    private static bool SourceFileExists(string path)
    {
        // File.Exists hides access errors; migration must surface them instead of treating an
        // unreadable old configuration as a first run with empty settings.
        EnsureNoReparse(path);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException(EditorLocalization.Format("Mda.Text.UserData.Conflict", path));
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void ValidateCopy(MigrationFile copy)
    {
        if (copy.Source is not null) EnsureNoReparse(copy.Source);
        EnsureNoReparse(copy.Target);
        if (Directory.Exists(copy.Target) || File.Exists(copy.Target) && !Matches(copy))
            throw new IOException(EditorLocalization.Format("Mda.Text.UserData.Conflict", copy.Target));
    }

    private static bool Matches(MigrationFile copy)
    {
        using var target = File.OpenRead(copy.Target);
        byte[] hash = SHA256.HashData(target);
        if (copy.Bytes is not null) return hash.AsSpan().SequenceEqual(SHA256.HashData(copy.Bytes));
        using var source = File.OpenRead(copy.Source!);
        return hash.AsSpan().SequenceEqual(SHA256.HashData(source));
    }

    private static void PublishCopy(MigrationFile copy)
    {
        ValidateCopy(copy);
        if (File.Exists(copy.Target)) return; // Identical files permit retry after an interrupted copy.
        string parent = Path.GetDirectoryName(copy.Target)!;
        EnsureNoReparse(parent);
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".mda-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (copy.Bytes is not null) output.Write(copy.Bytes);
                else { using var source = File.OpenRead(copy.Source!); source.CopyTo(output); }
                output.Flush(flushToDisk: true);
            }
            EnsureNoReparse(copy.Target);
            File.Move(temporary, copy.Target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ReadMarker(string root)
    {
        string path = Path.Combine(root, MarkerFileName);
        EnsureNoReparse(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 16384) throw InvalidMarker(root);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool section = false;
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) { section = line.Equals("[MDA]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!section) continue;
            string[] pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) throw InvalidMarker(root);
        }
        if (fields.GetValueOrDefault("AppId") != "MDA.MikuDanceAdvance" || fields.GetValueOrDefault("LayoutVersion") != "1"
            || fields.TryGetValue("Initialized", out string? initialized) && initialized is not ("0" or "1")) throw InvalidMarker(root);
        return fields.GetValueOrDefault("Initialized") == "1";
    }

    private void WriteMarkerInitialized()
    {
        string temporary = Path.Combine(RootDirectory, ".mda-marker-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(Encoding.UTF8.GetBytes(InitialMarker + "Initialized=1\r\n")); output.Flush(flushToDisk: true); }
            EnsureNoReparse(MarkerPath);
            File.Move(temporary, MarkerPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureWritableDirectory(string path)
    {
        EnsureNoReparse(path);
        Directory.CreateDirectory(path);
        string probe = Path.Combine(path, ".mda-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var output = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        output.WriteByte(0);
        output.Flush(flushToDisk: true);
    }

    private static void EnsureNoReparse(string path)
    {
        for (string? currentPath = Path.GetFullPath(path); currentPath is not null; currentPath = Path.GetDirectoryName(currentPath))
        {
            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(EditorLocalization.Format("Mda.Text.UserData.LinkedPath", currentPath));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException(EditorLocalization.Text("Mda.Text.UserData.InvalidConfiguration"));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetDirectoryName(full) is null || OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new ArgumentException(EditorLocalization.Text("Mda.Text.UserData.InvalidConfiguration"));
        return full;
    }

    private static bool SamePath(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));
    private static bool IsWithin(string parent, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static IOException InvalidMarker(string root) => new(EditorLocalization.Format("Mda.Text.UserData.InvalidMarker", root));
    private sealed record MigrationFile(string? Source, string Target, byte[]? Bytes = null);
}
