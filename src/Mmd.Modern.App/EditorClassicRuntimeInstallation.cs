using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mmd.Modern.App;

/// <summary>Reads the optional app-local Classic component without loading a library or creating
/// a device. The component becomes selectable only after its release-bound staging acceptance.</summary>
internal sealed class EditorClassicRuntimeInstallation
{
    internal const string ComponentDirectory = "tools/classic";
    internal const string ManifestName = "classic-runtime.json";
    internal const string HostRevision = "bfb6297aa63a37770efcd3f7a599de5dd39be12b";
    private static readonly IReadOnlyDictionary<string, string?> RequiredFiles = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["Mmd.Modern.D3D9.Compatibility.dll"] = null,
        ["d3dx9_43.dll"] = "36b84dfa0ef9d3ff3549a7ad54d2a8032bd22c879219ee1a959137c4ef8786c4",
        ["d3dcompiler_43.dll"] = "ffedb064adc25328b24dcf145b04045a867a5574c931516d7845babf2a08937f",
        ["licenses/Microsoft.DXSDK.D3DX/LICENSE.txt"] = "d287af0e16fbcee5c96fba66a3030e2cb09e3a782b80553a27fc0e19c76f02fb",
        ["licenses/Microsoft.DXSDK.D3DX/NOTICE.md"] = "4345a681ab4edb11118c2c60394b3cb1aba92d8921020cc1771f50143904d768",
        ["licenses/Microsoft.DXSDK.D3DX/README.md"] = "75ca088678de1da6b1852c866c1c1b957e10635d6b4990312e12963b23156959",
        ["licenses/Microsoft.DXSDK.D3DX/Microsoft.DXSDK.D3DX.nuspec"] = "33382aa602447726502b8c39da4e63158ebd116065463bf6adf26d49172cf464",
        ["licenses/MikuDanceStudio/LICENSE"] = "d808f870fd8fe49e6d1e34f763d9f2590bb619a10f009f934257e2ff7c968dfc",
        ["provenance.json"] = null
    };
    private readonly EditorCompatibilityRuntimeSource? runtime;
    private EditorClassicRuntimeInstallation(EditorCompatibilityRuntimeSource? runtime, string? reason, string? diagnostic)
        => (this.runtime, UnavailableReason, Diagnostic) = (runtime, reason, diagnostic);

    internal bool IsAvailable => runtime is not null;
    internal string? UnavailableReason { get; }
    internal string? Diagnostic { get; }

    internal static EditorClassicRuntimeInstallation Inspect(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new ArgumentException("An absolute application directory is required.", nameof(applicationDirectory));
        string directory = Path.Combine(Path.GetFullPath(applicationDirectory), ComponentDirectory);
        string manifestPath = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifestPath)) return new(null, "Mda.Text.RenderEngineSelection.ComponentNotInstalled", null);
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The Classic component requires Windows x64.");
            using var manifestStream = File.OpenRead(manifestPath);
            if (manifestStream.Length > 65536) throw new InvalidDataException("Classic runtime manifest is too large.");
            using var document = JsonDocument.Parse(manifestStream);
            var manifest = document.RootElement;
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
                manifest.GetProperty("component").GetString() != "MDA.Classic" ||
                manifest.GetProperty("architecture").GetString() != "win-x64" ||
                manifest.GetProperty("abiVersion").GetInt32() != 1 ||
                manifest.GetProperty("hostRevision").GetString() != HostRevision)
                throw new InvalidDataException("Classic runtime component identity is incompatible.");
            using (var application = File.OpenRead(Path.Combine(applicationDirectory, "Mmd.Modern.App.dll")))
                if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(application)),
                    manifest.GetProperty("appSha256").GetString()))
                    throw new InvalidDataException("Classic runtime acceptance belongs to a different application build.");
            var identities = manifest.GetProperty("files").EnumerateArray().ToArray();
            if (identities.Length != RequiredFiles.Count) throw new InvalidDataException("Classic runtime file table is incomplete.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identity in identities)
            {
                string name = identity.GetProperty("path").GetString() ?? "";
                if (!RequiredFiles.TryGetValue(name, out string? pinnedHash) || !seen.Add(name))
                    throw new InvalidDataException("Classic runtime file is unexpected or duplicated: " + name);
                string expectedHash = identity.GetProperty("sha256").GetString() ?? "";
                if (expectedHash.Length != 64 || expectedHash.Any(value => !char.IsAsciiHexDigit(value)) ||
                    (pinnedHash is not null && !StringComparer.OrdinalIgnoreCase.Equals(pinnedHash, expectedHash)))
                    throw new InvalidDataException("Classic runtime file identity is invalid: " + name);
                string path = Path.Combine(directory, name);
                using var input = File.OpenRead(path);
                if (input.Length != identity.GetProperty("bytes").GetInt64() ||
                    !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(input)), expectedHash))
                    throw new InvalidDataException("Classic runtime file is missing or changed: " + name);
            }
            if (!manifest.GetProperty("productionReady").GetBoolean())
                return new(null, "Mda.Text.RenderEngineSelection.CompatibilityNotReady", null);
            return new(new(Path.Combine(directory, "Mmd.Modern.D3D9.Compatibility.dll"),
                Path.Combine(directory, "d3dx9_43.dll"), Path.Combine(directory, "d3dcompiler_43.dll")), null, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or PlatformNotSupportedException)
        {
            return new(null, "Mda.Text.RenderEngineSelection.ComponentIncomplete", error.Message);
        }
    }

    internal IEditorRenderEngine CreateEngine(string cacheDirectory, EditorClassicToonResources? toonResources = null)
    {
        if (runtime is null) throw new NotSupportedException(Diagnostic ?? "The Classic component is unavailable.");
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (!Path.IsPathFullyQualified(cacheDirectory)) throw new ArgumentException("An absolute Classic cache directory is required.", nameof(cacheDirectory));
        runtime.ValidateFiles(); // Recheck after the modal selection, before constructing any editor or device.
        Directory.CreateDirectory(cacheDirectory);
        return new EditorClassicRenderEngine(runtime, cacheDirectory, toonResources);
    }
}
