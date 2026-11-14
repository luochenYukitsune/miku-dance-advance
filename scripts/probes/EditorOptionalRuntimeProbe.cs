using System.Text.Json;
using Mmd.Modern.App;

/// <summary>Installation-path contracts only: fixture DLLs are never loaded and no environment is changed.</summary>
internal static class EditorOptionalRuntimeProbe
{
    private static readonly List<string> Checks = [];

    public static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(output);
        try
        {
            Run(Path.Combine(output, "runtime-fixtures"));
            File.WriteAllText(Path.Combine(output, "optional-runtime-report.json"), JsonSerializer.Serialize(
                new { passed = true, checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {Checks.Count} editor optional runtime path checks: {output}");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "optional-runtime-report.json"), JsonSerializer.Serialize(
                new { passed = false, checks = Checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Run(string root)
    {
        string app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Read(string name) => environment.GetValueOrDefault(name);
        var missing = EditorOptionalRuntimePaths.Resolve(app, Read);
        Require(missing.FsrLoaderPath is null && missing.XessLibraryPath is null
            && missing.DlssBridgePath is null && missing.DlssSdkDirectory is null, "Absent optional SDKs remain unavailable");
        Require(missing.Failures.Count == 0, "An installation without optional SDKs has no invalid overrides");

        // Root files deliberately do not count: only the documented provider subdirectory is searched.
        Put(app, "amd_fidelityfx_loader_dx12.dll"); Put(app, "libxess.dll");
        Put(app, "mmd_dlss.dll"); Put(app, "nvngx_dlss.dll");
        var rootOnly = EditorOptionalRuntimePaths.Resolve(app, Read);
        Require(rootOnly.FsrLoaderPath is null && rootOnly.XessLibraryPath is null
            && rootOnly.DlssBridgePath is null && rootOnly.DlssSdkDirectory is null, "No executable-root or recursive SDK fallback");

        string localFsr = Put(Path.Combine(app, "tools", "upscaling", "fsr"), "amd_fidelityfx_loader_dx12.dll");
        string localXess = Put(Path.Combine(app, "tools", "upscaling", "xess"), "libxess.dll");
        string localDlss = Path.Combine(app, "tools", "upscaling", "dlss");
        string localBridge = Put(localDlss, "mmd_dlss.dll"); Put(localDlss, "nvngx_dlss.dll");
        var local = EditorOptionalRuntimePaths.Resolve(app, Read);
        Require(local.FsrLoaderPath == localFsr && local.XessLibraryPath == localXess
            && local.DlssBridgePath == localBridge && local.DlssSdkDirectory == localDlss, "Documented app-local SDK layout resolves absolute paths");

        string external = Path.Combine(root, "external");
        environment["MMD_FSR_LOADER_PATH"] = Put(external, "amd_fidelityfx_loader_dx12.dll");
        environment["MMD_XESS_RUNTIME_PATH"] = Put(external, "libxess.dll");
        environment["MMD_DLSS_BRIDGE_PATH"] = Put(external, "mmd_dlss.dll");
        Put(external, "nvngx_dlss.dll"); environment["MMD_DLSS_SDK_DIRECTORY"] = external;
        var overridden = EditorOptionalRuntimePaths.Resolve(app, Read);
        Require(overridden.FsrLoaderPath == environment["MMD_FSR_LOADER_PATH"]
            && overridden.XessLibraryPath == environment["MMD_XESS_RUNTIME_PATH"]
            && overridden.DlssBridgePath == environment["MMD_DLSS_BRIDGE_PATH"]
            && overridden.DlssSdkDirectory == external, "Explicit installed components take priority over app-local SDKs");

        foreach (string variable in environment.Keys.ToArray())
        {
            string? original = environment[variable];
            foreach (string invalid in new[] { "", "relative/component.dll", Path.Combine(root, "missing", variable) })
            {
                environment[variable] = invalid;
                var rejected = EditorOptionalRuntimePaths.Resolve(app, Read);
                Require(Selected(rejected, variable) is null && rejected.Failures.ContainsKey(variable),
                    $"Explicit invalid {variable} is reported without app-local fallback: {invalid.Length}");
            }
            environment[variable] = original;
        }

        foreach (string variable in new[] { "MMD_FSR_LOADER_PATH", "MMD_XESS_RUNTIME_PATH", "MMD_DLSS_BRIDGE_PATH" })
        {
            string? original = environment[variable];
            environment[variable] = Put(external, "renamed.dll");
            var rejected = EditorOptionalRuntimePaths.Resolve(app, Read);
            Require(Selected(rejected, variable) is null && rejected.Failures.ContainsKey(variable), $"Exact SDK filename required by {variable}");
            environment[variable] = original;
        }
        string incomplete = Path.Combine(root, "incomplete-dlss"); Directory.CreateDirectory(incomplete);
        environment["MMD_DLSS_SDK_DIRECTORY"] = incomplete;
        var incompleteSdk = EditorOptionalRuntimePaths.Resolve(app, Read);
        Require(incompleteSdk.DlssSdkDirectory is null && incompleteSdk.Failures.ContainsKey("MMD_DLSS_SDK_DIRECTORY"),
            "An explicit DLSS directory requires its official component and cannot borrow the local SDK");
        Require(local.FsrLoaderPath == localFsr && local.Failures.Count == 0, "Resolved path snapshots remain stable after later queries");
        bool rejectedRelativeBase = false;
        try { _ = EditorOptionalRuntimePaths.Resolve("relative/app", Read); }
        catch (ArgumentException) { rejectedRelativeBase = true; }
        Require(rejectedRelativeBase, "Application base directory must be absolute");
    }

    private static string? Selected(EditorOptionalRuntimePaths paths, string variable) => variable switch
    {
        "MMD_FSR_LOADER_PATH" => paths.FsrLoaderPath, "MMD_XESS_RUNTIME_PATH" => paths.XessLibraryPath,
        "MMD_DLSS_BRIDGE_PATH" => paths.DlssBridgePath, "MMD_DLSS_SDK_DIRECTORY" => paths.DlssSdkDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(variable))
    };
    private static string Put(string directory, string filename)
    {
        Directory.CreateDirectory(directory);
        string path = Path.GetFullPath(Path.Combine(directory, filename));
        File.WriteAllBytes(path, [0]); // Presence evidence only. These bytes must never reach a native probe.
        return path;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Checks.Add(message); }
}
