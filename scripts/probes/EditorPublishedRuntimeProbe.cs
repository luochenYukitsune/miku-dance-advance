using System.Diagnostics;
using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;

[assembly: SupportedOSPlatform("windows")]

/// <summary>Runs against published DLLs. Installation, device initialization and actual frame execution
/// are separate evidence. No SDK environment override or product source project is used.</summary>
internal static class EditorPublishedRuntimeProbe
{
    private const int Width = 640, Height = 480;
    private static readonly string[] CrtNames = ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
    private static readonly string[] Variables = ["MMD_FSR_LOADER_PATH", "MMD_XESS_RUNTIME_PATH", "MMD_DLSS_BRIDGE_PATH", "MMD_DLSS_SDK_DIRECTORY"];
    private static readonly List<object> Cases = [];
    private static readonly List<string> Checks = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true, Converters = { new JsonStringEnumConverter() } };
    private static string output = "", payload = "";

    public static async Task<int> Main(string[] args)
    {
        output = Path.GetFullPath(args[0]); payload = Path.GetFullPath(args[1]);
        var required = args.Skip(2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(output);
        EditorOptionalRuntimePaths? paths = null;
        EditorPreviewCapabilities? capabilities = null;
        string? failure = null;
        try
        {
            Require(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(payload.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "AppContext uses the isolated published payload");
            foreach (string variable in Variables)
                Require(Environment.GetEnvironmentVariable(variable) is null, $"Child process has no {variable} override");
            Require(Path.GetFullPath(typeof(EditorPreviewCapabilities).Assembly.Location)
                .Equals(Path.Combine(payload, "Mmd.Modern.App.dll"), StringComparison.OrdinalIgnoreCase), "Editor capabilities execute the published App assembly");
            Require(EditorOptionalRuntimeProbe.Main([Path.Combine(output, "path-contracts")]) == 0, "Optional runtime installation path contracts pass");
            paths = EditorOptionalRuntimePaths.Resolve(payload, _ => null);
            Require(paths.Failures.Count == 0, "App-local resolution has no invalid overrides");
            capabilities = EditorPreviewCapabilities.QueryInstalled(Width, Height, paths);
            Write("published-runtime-capabilities.json", new { paths, capabilities });
            Require(capabilities.DeviceUnavailableReason is null && capabilities.AdapterName is not null, "Published runtime creates a D3D12 device");
            foreach (string provider in required)
                Require(provider.ToLowerInvariant() switch { "fsr" => capabilities.FsrAvailable, "xess" => capabilities.XeSsAvailable,
                    "dlss" => capabilities.DlssAvailable, _ => throw new ArgumentException($"Unknown required provider {provider}") }, $"Required {provider} passes actual device initialization");

            var scene = Fixture();
            await Capture("Off", ViewTemporalUpscalingOptions.Off, paths, scene);
            if (capabilities.FsrAvailable)
            {
                await Capture("Fsr-Quality", new() { Mode = ViewTemporalUpscalingMode.Fsr, QualityPreset = ViewTemporalQualityPreset.Quality }, paths, scene);
                await Capture("Fsr-NativeAa", new() { Mode = ViewTemporalUpscalingMode.Fsr, QualityPreset = ViewTemporalQualityPreset.NativeAa }, paths, scene);
            }
            else Cases.Add(new { name = "Fsr", skipped = true, reason = capabilities.FsrUnavailableReason });
            var xess = capabilities.XeSsQueries?.Where(q => q.Initialized && q.Failure is null).ToArray() ?? [];
            if (xess.Length > 0)
            {
                var quality = xess.FirstOrDefault(q => q.Quality == D3D12XeSsQuality.Quality) ?? xess[0];
                await Capture("XeSs-" + quality.Quality, new() { Mode = ViewTemporalUpscalingMode.XeSs, XeSsQuality = XeSsQuality(quality.Quality) }, paths, scene);
                if (quality.Quality != D3D12XeSsQuality.AntiAliasing && xess.Any(q => q.Quality == D3D12XeSsQuality.AntiAliasing))
                    await Capture("XeSs-NativeAa", new() { Mode = ViewTemporalUpscalingMode.XeSs, XeSsQuality = ViewXeSsQuality.NativeAa }, paths, scene);
            }
            else Cases.Add(new { name = "XeSs", skipped = true, reason = capabilities.XeSsUnavailableReason });
            var dlss = capabilities.DlssQueries?.Where(q => q.Available && q.Initialized && q.Failure is null).ToArray() ?? [];
            if (dlss.Length > 0)
            {
                var quality = dlss.FirstOrDefault(q => q.Quality == D3D12DlssQuality.Quality) ?? dlss[0];
                await Capture("Dlss-" + quality.Quality, new() { Mode = ViewTemporalUpscalingMode.Dlss, DlssQuality = Enum.Parse<ViewDlssQuality>(quality.Quality.ToString()) }, paths, scene);
                if (quality.Quality != D3D12DlssQuality.NativeAa && dlss.Any(q => q.Quality == D3D12DlssQuality.NativeAa))
                    await Capture("Dlss-NativeAa", new() { Mode = ViewTemporalUpscalingMode.Dlss, DlssQuality = ViewDlssQuality.NativeAa }, paths, scene);
            }
            else Cases.Add(new { name = "Dlss", skipped = true, reason = capabilities.DlssUnavailableReason });
        }
        catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
        finally
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.GetName().Name?.StartsWith("Mmd.Modern.", StringComparison.Ordinal) == true)
                .Select(a => new { name = a.GetName().Name, identity = a.FullName, file = Identity(a.Location), published = UnderPayload(a.Location) }).ToArray();
            if (assemblies.Any(a => !a.published)) failure ??= "A product assembly loaded outside the isolated published payload.";
            Write("published-runtime-report.json", new { schemaVersion = 1, passed = failure is null, failure, checks = Checks,
                scope = "Published app-local capability queries and capture frames; normal EXE, HWND presentation and install lifecycle require separate acceptance.",
                requiredProviders = required, appBaseDirectory = AppContext.BaseDirectory, workingDirectory = Environment.CurrentDirectory,
                runtime = Environment.Version.ToString(), processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                paths, capabilities, assemblies, runtimeFiles = RuntimeFiles(), cases = Cases });
        }
        Console.WriteLine($"{(failure is null ? "PASS" : "FAIL")} published optional runtime verification: {output}");
        return failure is null ? 0 : 1;
    }

    private static async Task Capture(string name, ViewTemporalUpscalingOptions temporal, EditorOptionalRuntimePaths paths, OriginalFrameScene scene)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1, TemporalUpscaling: temporal);
            await using var renderer = new D3D12ViewRenderer(new(Width, Height, quality), paths.FsrLoaderPath, paths.XessLibraryPath, paths.DlssBridgePath, paths.DlssSdkDirectory);
            Require(!renderer.IsWarp, $"{name} executes on hardware");
            var plan = new OfflineFramePlan(Width, Height, new(30), 0, 1);
            var frames = new List<object>();
            for (int i = 0; i < 2; i++)
            {
                using var frame = await renderer.CaptureAsync(new(plan.GetSample(i), Width, Height, scene, null, null, null));
                byte[] rgba = frame.Pixels.ToArray();
                Require(frame.Width == Width && frame.Height == Height && rgba.Length == Width * Height * 4, $"{name} frame {i} reads complete RGBA");
                int colored = 0;
                for (int p = 0; p < rgba.Length; p += 4)
                    if (Math.Abs(rgba[p] - rgba[0]) + Math.Abs(rgba[p + 1] - rgba[1]) + Math.Abs(rgba[p + 2] - rgba[2]) > 32) colored++;
                Require(colored > 1000, $"{name} frame {i} contains rendered geometry");
                var statistics = renderer.Statistics ?? throw new InvalidOperationException("Capture returned no frame statistics.");
                Require(statistics.ReadbackCount == 1 && statistics.SceneDrawCalls > 0, $"{name} frame {i} records scene draw and explicit readback");
                if (temporal.Mode != ViewTemporalUpscalingMode.Off)
                {
                    var executed = statistics.TemporalUpscaling ?? throw new InvalidOperationException("Requested reconstruction did not execute.");
                    Require((int)executed.Mode == (int)temporal.Mode && executed.SubmittedFrames >= i + 1, $"{name} frame {i} executes selected provider");
                    Require(executed.ResetHistory == (i == 0), $"{name} frame {i} has expected temporal history");
                    bool nativeAa = temporal.Mode switch { ViewTemporalUpscalingMode.Fsr => temporal.QualityPreset == ViewTemporalQualityPreset.NativeAa,
                        ViewTemporalUpscalingMode.XeSs => temporal.XeSsQuality == ViewXeSsQuality.NativeAa,
                        _ => temporal.DlssQuality == ViewDlssQuality.NativeAa };
                    if (nativeAa) Require(executed.RenderWidth == Width && executed.RenderHeight == Height, $"{name} uses native input dimensions");
                }
                string file = Path.Combine(output, name + "-" + i + ".rgba");
                File.WriteAllBytes(file, rgba);
                frames.Add(new { index = i, rgba = Identity(file), coloredPixels = colored, statistics });
            }
            var modules = Modules();
            string? entry = temporal.Mode switch { ViewTemporalUpscalingMode.Fsr => paths.FsrLoaderPath,
                ViewTemporalUpscalingMode.XeSs => paths.XessLibraryPath, ViewTemporalUpscalingMode.Dlss => paths.DlssBridgePath, _ => null };
            if (entry is not null) Require(modules.Any(m => m.path.Equals(entry, StringComparison.OrdinalIgnoreCase)), $"{name} loads the selected app-local entry DLL");
            if (entry is not null)
                foreach (string crt in CrtNames)
                    Require(modules.Any(m => Path.GetFileName(m.path).Equals(crt, StringComparison.OrdinalIgnoreCase) && UnderPayload(m.path)),
                        $"{name} resolves {crt} from the published payload");
            Cases.Add(new { name, passed = true, renderer.AdapterName, renderer.IsWarp, temporal, frames, loadedNativeModules = modules, milliseconds = clock.ElapsedMilliseconds });
        }
        catch (Exception error)
        {
            Cases.Add(new { name, passed = false, temporal, failure = error.ToString(), loadedNativeModules = Modules(), milliseconds = clock.ElapsedMilliseconds });
            throw;
        }
    }

    // Same small box topology as the existing D3D12OriginalOutlineTests fixture. No user asset is modified.
    private static OriginalFrameScene Fixture()
    {
        Vector3[] positions = [new(-1,-1,0),new(1,-1,0),new(1,1,0),new(-1,1,0),new(-1,-1,.12f),new(1,-1,.12f),new(1,1,.12f),new(-1,1,.12f)];
        var vertices = positions.Select((v,i) => new SceneVertex(v, new(v.X,v.Y,i < 4 ? -.2f : .2f), Vector2.Zero)).ToArray();
        uint[] indices = [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
        var mesh = new SceneSnapshot(vertices, indices, [new("runtime-fixture",0,36,new(.7f,.8f,.9f,1),Vector3.Zero,Vector3.Zero,1)],
            camera: new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,4,.1f,20,45));
        var surface = new OriginalStyleScene(mesh, [new(new(.25f,.4f,.65f), DoubleSided: true)], new(Vector3.One,Vector3.Zero,Matrix4x4.Identity));
        return new(surface, options: new(OriginalAlphaBlendMode.Normal,true,false,EnableFrustumCulling:false));
    }
    private static ViewXeSsQuality XeSsQuality(D3D12XeSsQuality value) => value == D3D12XeSsQuality.AntiAliasing
        ? ViewXeSsQuality.NativeAa : Enum.Parse<ViewXeSsQuality>(value.ToString());
    private static bool UnderPayload(string path) => Path.GetFullPath(path).StartsWith(payload.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static object Identity(string path) => new { path = Path.GetFullPath(path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) };
    private static object[] RuntimeFiles()
    {
        string root = Path.Combine(payload, "tools", "upscaling");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order().Select(Identity).ToArray() : [];
    }
    private sealed record NativeModule(string path, long bytes, string sha256);
    private static NativeModule[] Modules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Where(m => m.ModuleName is not null &&
            (m.ModuleName.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase) || m.ModuleName.StartsWith("libxess", StringComparison.OrdinalIgnoreCase)
            || m.ModuleName.Equals("mmd_dlss.dll", StringComparison.OrdinalIgnoreCase) || m.ModuleName.StartsWith("nvngx", StringComparison.OrdinalIgnoreCase)
            || CrtNames.Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase)))
            .Select(m => new NativeModule(m.FileName, new FileInfo(m.FileName).Length, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(m.FileName))))).ToArray();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Checks.Add(message); }
    private static void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, Json));
}
