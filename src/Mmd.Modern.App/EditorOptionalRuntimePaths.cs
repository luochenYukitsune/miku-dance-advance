using System.Collections.ObjectModel;

namespace Mmd.Modern.App;

/// <summary>Installed optional components shared by editor probes, preview and export.
/// Resolving a path neither loads an SDK nor proves support on the current device.</summary>
public sealed record EditorOptionalRuntimePaths
{
    public string? FsrLoaderPath { get; private init; }
    public string? XessLibraryPath { get; private init; }
    public string? DlssBridgePath { get; private init; }
    public string? DlssSdkDirectory { get; private init; }
    /// <summary>Native installation diagnostics, keyed by the explicit environment override.
    /// Invalid overrides are retained here and never replaced with app-local components.</summary>
    public IReadOnlyDictionary<string, string> Failures { get; private init; }
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>Environment overrides take priority over tools/upscaling/{fsr,xess,dlss}
    /// beneath the executable directory. No PATH, working directory or workspace search is performed.</summary>
    public static EditorOptionalRuntimePaths Resolve(string? baseDirectory = null, Func<string, string?>? readEnvironment = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        if (!Path.IsPathFullyQualified(baseDirectory))
            throw new ArgumentException("The application component directory must be absolute.", nameof(baseDirectory));
        string componentRoot = Path.Combine(Path.GetFullPath(baseDirectory), "tools", "upscaling");
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        return new()
        {
            FsrLoaderPath = FilePath("MMD_FSR_LOADER_PATH", "fsr", "amd_fidelityfx_loader_dx12.dll"),
            XessLibraryPath = FilePath("MMD_XESS_RUNTIME_PATH", "xess", "libxess.dll"),
            DlssBridgePath = FilePath("MMD_DLSS_BRIDGE_PATH", "dlss", "mmd_dlss.dll"),
            DlssSdkDirectory = FilePath("MMD_DLSS_SDK_DIRECTORY", "dlss", "nvngx_dlss.dll", directory: true),
            Failures = new ReadOnlyDictionary<string, string>(failures)
        };

        string? FilePath(string variable, string provider, string expectedFile, bool directory = false)
        {
            string? configured = readEnvironment(variable);
            string candidate = configured ?? (directory ? Path.Combine(componentRoot, provider)
                : Path.Combine(componentRoot, provider, expectedFile));
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
                    return Invalid("requires an absolute path.");
                if (!directory && !string.Equals(Path.GetFileName(candidate), expectedFile, StringComparison.OrdinalIgnoreCase))
                    return Invalid($"requires the exact component filename {expectedFile}.");
                string absolute = Path.GetFullPath(candidate);
                if (directory ? !Directory.Exists(absolute) || !File.Exists(Path.Combine(absolute, expectedFile)) : !File.Exists(absolute))
                    return Invalid($"does not contain the installed component {expectedFile}.");
                return absolute;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            { return Invalid($"could not be resolved ({error.GetType().Name})."); }

            string? Invalid(string reason)
            {
                if (configured is not null) failures[variable] = $"{variable} {reason}";
                return null;
            }
        }
    }
}
