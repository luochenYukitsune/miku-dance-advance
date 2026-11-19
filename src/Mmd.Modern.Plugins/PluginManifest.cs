using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mmd.Modern.Plugins;

internal sealed record PluginDependency(string Id, Version MinimumVersion, Version? MaximumVersion);

internal sealed record PluginManifest(
    string Id,
    string Name,
    Version Version,
    string EntryAssembly,
    string EntryType,
    int ApiVersion,
    Version MinimumHostVersion,
    IReadOnlyList<PluginDependency> Dependencies,
    IReadOnlySet<string> Capabilities,
    byte[] Digest,
    string? Description = null,
    string? Author = null,
    IReadOnlyList<string>? ProvidedStyles = null,
    IReadOnlyDictionary<string, string>? LocalizedNames = null,
    IReadOnlyDictionary<string, string>? LocalizedDescriptions = null)
{
    internal const string FileName = "mda-plugin.json";
    private const int MaximumBytes = 65536;

    internal static async Task<PluginManifest> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        var path = PluginPaths.RequireContainedFile(directory, Path.Combine(directory, FileName));
        var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        RequireProperties(root, ["schemaVersion", "id", "name", "version", "entryAssembly", "entryType",
            "apiVersion", "minimumHostVersion", "dependencies", "capabilities",
            "description", "author", "localizedNames", "localizedDescriptions", "providesStyles"]);
        if (RequireInt(root, "schemaVersion") != 1)
            throw new InvalidDataException("Unsupported plugin manifest schemaVersion; expected 1.");
        var id = RequireIdentifier(RequireString(root, "id"), "plugin id");
        var name = RequireString(root, "name", 200);
        var version = RequireVersion(root, "version");
        var entryAssembly = RequireString(root, "entryAssembly", 512);
        if (Path.IsPathRooted(entryAssembly) || entryAssembly.Contains(':') ||
            !string.Equals(Path.GetExtension(entryAssembly), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("entryAssembly must be a relative DLL path inside the plugin directory.");
        var entryPath = PluginPaths.RequireContainedFile(directory, Path.Combine(directory, entryAssembly));
        if (!File.Exists(entryPath))
            throw new InvalidDataException("The entryAssembly file does not exist.");
        var entryType = RequireString(root, "entryType", 512);
        var apiVersion = RequireInt(root, "apiVersion");
        if (apiVersion < 1)
            throw new InvalidDataException("apiVersion must be positive.");
        var minimumHostVersion = RequireVersion(root, "minimumHostVersion");
        var dependencies = new List<PluginDependency>();
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("dependencies", out var dependencyArray))
        {
            RequireArray(dependencyArray, "dependencies");
            foreach (var dependency in dependencyArray.EnumerateArray())
            {
                RequireProperties(dependency, ["id", "minimumVersion", "maximumVersion"]);
                var dependencyId = RequireIdentifier(RequireString(dependency, "id"), "dependency id");
                if (!dependencyIds.Add(dependencyId))
                    throw new InvalidDataException($"Duplicate dependency '{dependencyId}'.");
                var minimum = RequireVersion(dependency, "minimumVersion");
                var maximum = dependency.TryGetProperty("maximumVersion", out _)
                    ? RequireVersion(dependency, "maximumVersion") : null;
                if (maximum is not null && maximum < minimum)
                    throw new InvalidDataException($"Dependency '{dependencyId}' has an inverted version range.");
                dependencies.Add(new PluginDependency(dependencyId, minimum, maximum));
            }
        }
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("capabilities", out var capabilityArray))
        {
            RequireArray(capabilityArray, "capabilities");
            foreach (var capability in capabilityArray.EnumerateArray())
            {
                if (capability.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Capability identifiers must be strings.");
                var capabilityId = RequireIdentifier(capability.GetString()!, "capability id");
                if (!capabilities.Add(capabilityId))
                    throw new InvalidDataException($"Duplicate capability '{capabilityId}'.");
            }
        }
        var description = ReadOptionalString(root, "description", maximumLength: 2048, allowNewlines: true);
        var author = ReadOptionalString(root, "author", maximumLength: 200, allowNewlines: false);
        var localizedNames = ReadOptionalLocalizedMap(root, "localizedNames", maximumLength: 200,
            requireNonEmptyValue: true, allowNewlines: false);
        var localizedDescriptions = ReadOptionalLocalizedMap(root, "localizedDescriptions", maximumLength: 2048,
            requireNonEmptyValue: false, allowNewlines: true);
        var providedStyles = ReadOptionalStyleSlugs(root, "providesStyles");
        return new PluginManifest(id, name, version, entryPath, entryType, apiVersion,
            minimumHostVersion, dependencies.AsReadOnly(), capabilities, SHA256.HashData(bytes),
            description, author, providedStyles, localizedNames, localizedDescriptions);
    }

    internal async Task VerifyUnchangedAsync(string directory, CancellationToken cancellationToken)
    {
        var path = PluginPaths.RequireContainedFile(directory, Path.Combine(directory, FileName));
        var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Digest, SHA256.HashData(bytes)))
            throw new InvalidDataException("The plugin manifest changed after discovery. Refresh the plugin list before enabling it.");
        PluginPaths.RequireContainedFile(directory, EntryAssembly);
    }

    internal static string RequireIdentifier(string value, string description)
    {
        if (value.Length is < 1 or > 128 || value[0] is < 'a' or > 'z' ||
            value.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
            throw new InvalidDataException($"Invalid {description}; use a lowercase letter followed by lowercase letters, digits, '.', '_' or '-'.");
        return value;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        if (length > MaximumBytes)
            throw new InvalidDataException("The plugin manifest exceeds the 64 KiB limit.");
        return buffer[..length];
    }

    private static void RequireProperties(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A manifest or dependency must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown manifest property '{property.Name}'.");
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"Duplicate manifest property '{property.Name}'.");
        }
    }

    private static string RequireString(JsonElement element, string property, int maximumLength = 128)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximumLength ||
            value.GetString()!.Any(char.IsControl))
            throw new InvalidDataException($"Manifest property '{property}' must be a nonempty string of at most {maximumLength} characters.");
        return value.GetString()!;
    }

    private static int RequireInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
            throw new InvalidDataException($"Manifest property '{property}' must be an integer.");
        return number;
    }

    private static Version RequireVersion(JsonElement element, string property)
    {
        var value = RequireString(element, property, 64);
        if (value.Any(c => c is not (>= '0' and <= '9') and not '.') || !Version.TryParse(value, out var version))
            throw new InvalidDataException($"Manifest property '{property}' must be a numeric .NET version with two to four components.");
        return version;
    }

    private static void RequireArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 64)
            throw new InvalidDataException($"Manifest property '{property}' must be an array of at most 64 entries.");
    }

    private static string? ReadOptionalString(JsonElement element, string property, int maximumLength, bool allowNewlines)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Manifest property '{property}' must be a string.");
        var text = value.GetString()!;
        if (text.Length > maximumLength || HasDisallowedControlChars(text, allowNewlines))
            throw new InvalidDataException($"Manifest property '{property}' exceeds {maximumLength} characters or contains invalid control characters.");
        return text;
    }

    private static IReadOnlyDictionary<string, string>? ReadOptionalLocalizedMap(
        JsonElement element,
        string property,
        int maximumLength,
        bool requireNonEmptyValue,
        bool allowNewlines)
    {
        if (!element.TryGetProperty(property, out var mapElement))
            return null;
        if (mapElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Manifest property '{property}' must be a JSON object mapping language tags to strings.");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in mapElement.EnumerateObject())
        {
            if (map.Count >= 64)
                throw new InvalidDataException($"Manifest property '{property}' may contain at most 64 entries.");
            if (!IsValidLanguageKey(entry.Name))
                throw new InvalidDataException($"Invalid language key '{entry.Name}' in manifest property '{property}'.");
            if (entry.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Values in manifest property '{property}' must be strings.");
            var text = entry.Value.GetString()!;
            if (requireNonEmptyValue && string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException($"Values in manifest property '{property}' must be non-empty strings.");
            if (text.Length > maximumLength || HasDisallowedControlChars(text, allowNewlines))
                throw new InvalidDataException($"Value for '{entry.Name}' in manifest property '{property}' exceeds {maximumLength} characters or contains invalid control characters.");
            if (!map.TryAdd(entry.Name, text))
                throw new InvalidDataException($"Duplicate language key '{entry.Name}' in manifest property '{property}'.");
        }
        return new ReadOnlyDictionary<string, string>(map);
    }

    private static IReadOnlyList<string>? ReadOptionalStyleSlugs(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var arrayElement))
            return null;
        RequireArray(arrayElement, property);
        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Entries in manifest property '{property}' must be strings.");
            var slug = RequireStyleSlug(item.GetString()!);
            if (!seen.Add(slug))
                throw new InvalidDataException($"Duplicate style slug '{slug}' in manifest property '{property}'.");
            slugs.Add(slug);
        }
        return slugs.AsReadOnly();
    }

    private static string RequireStyleSlug(string value)
    {
        if (value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new InvalidDataException("Style slugs in 'providesStyles' must be non-empty ASCII strings of letters, digits, '.', '_' or '-'.");
        return value;
    }

    private static bool IsValidLanguageKey(string key) =>
        key.Length is >= 1 and <= 32 &&
        char.IsAsciiLetter(key[0]) &&
        key.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool HasDisallowedControlChars(string value, bool allowNewlines) =>
        value.Any(c => char.IsControl(c) && (!allowNewlines || c is not ('\r' or '\n' or '\t')));
}
