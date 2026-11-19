using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>
/// Optional SM6 compute compiler. Construct at renderer initialization and reuse the instance:
/// successful identical requests are cached in memory, so frame submission never needs a process.
/// Compilation proves DXIL generation only; device support and GPU execution need separate checks.
/// </summary>
public sealed class DxcShaderCompiler
{
    private static readonly string[] CompileFlags = ["-HV", "2021", "-Ges", "-Gis", "-O3", "-Qstrip_debug"];
    private static readonly UTF8Encoding SourceEncoding = new(false);
    private readonly object gate = new();
    private readonly Dictionary<string, DxcCompilationResult> compiled = new(StringComparer.Ordinal);
    private int compileInvocationCount;

    public DxcShaderCompiler(string? compilerPath = null, string? cacheDirectory = null)
    {
        CompilerPath = ResolveCompilerPath(compilerPath);
        string? environmentCache = cacheDirectory is null ? Environment.GetEnvironmentVariable("MMD_DXC_CACHE") : null;
        string? workspace = cacheDirectory is null && environmentCache is null ? FindWorkspaceRoot() : null;
        string? localData = cacheDirectory is null && environmentCache is null && workspace is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null;
        CacheDirectory = D3D12RuntimePaths.ResolveDxcCacheDirectory(cacheDirectory, environmentCache, workspace, localData);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(Path.Combine(CacheDirectory, "tmp"));

        var version = Run(["--version"], CacheDirectory);
        if (version.ExitCode != 0)
            throw new DxcShaderCompilationException("DXC version query failed.", version.Diagnostics, version.ExitCode);
        CompilerVersion = version.Diagnostics.Trim();
        if (string.IsNullOrWhiteSpace(CompilerVersion))
            throw new DxcShaderCompilationException("DXC returned an empty compiler identity.", version.Diagnostics, version.ExitCode);

        var compilerDirectory = Path.GetDirectoryName(CompilerPath)!;
        CompilerFiles = Array.AsReadOnly(new[] { Path.GetFileName(CompilerPath), "dxcompiler.dll", "dxil.dll" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(name => new DxcCompilerFile(name, File.Exists(Path.Combine(compilerDirectory, name))
                ? Hash(File.ReadAllBytes(Path.Combine(compilerDirectory, name))) : "missing"))
            .ToArray());
        CompilerHash = Hash(SourceEncoding.GetBytes(JsonSerializer.Serialize(new
        {
            Version = CompilerVersion,
            Files = CompilerFiles,
        })));
    }

    public string CompilerPath { get; }
    public string CacheDirectory { get; }
    public string CompilerVersion { get; }
    public string CompilerHash { get; }
    public IReadOnlyList<DxcCompilerFile> CompilerFiles { get; }
    public int CompileInvocationCount => Volatile.Read(ref compileInvocationCount);

    /// <summary>
    /// Compiles self-contained compute HLSL. Include directives are deliberately rejected because
    /// external files would otherwise escape the source/cache identity. Macro order is canonical.
    /// Failed requests are never cached. Each returned DXIL array belongs to its caller.
    /// </summary>
    public DxcCompilationResult CompileCompute(
        string source,
        string entryPoint = "CSMain",
        string profile = "cs_6_5",
        IReadOnlyDictionary<string, string?>? defines = null)
        => CompileProgram(source, entryPoint, profile, defines, "cs");

    public DxcCompilationResult CompilePixel(
        string source,
        string entryPoint = "PSMain",
        string profile = "ps_6_5",
        IReadOnlyDictionary<string, string?>? defines = null)
        => CompileProgram(source, entryPoint, profile, defines, "ps");

    public DxcCompilationResult CompileVertex(
        string source,
        string entryPoint = "VSMain",
        string profile = "vs_6_0",
        IReadOnlyDictionary<string, string?>? defines = null)
        => CompileProgram(source, entryPoint, profile, defines, "vs");

    private DxcCompilationResult CompileProgram(
        string source,
        string entryPoint,
        string profile,
        IReadOnlyDictionary<string, string?>? defines,
        string shaderKind)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        if (!Regex.IsMatch(entryPoint, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("The compute entry point must be an HLSL identifier.", nameof(entryPoint));
        if (!Regex.IsMatch(profile, $"^{shaderKind}_6_[0-9]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"DXC requires a {shaderKind}_6_x profile.", nameof(profile));
        if (ContainsIncludeDirective(source))
            throw new ArgumentException("Compute source must be self-contained: inline includes so all dependencies participate in the cache key.", nameof(source));

        var macros = defines?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray() ?? [];
        foreach (var macro in macros)
            if (!Regex.IsMatch(macro.Key, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Macro names must be HLSL identifiers.", nameof(defines));

        byte[] sourceBytes = SourceEncoding.GetBytes(source);
        string sourceHash = Hash(sourceBytes);
        string key = Hash(SourceEncoding.GetBytes(JsonSerializer.Serialize(new
        {
            Format = "MmdDxcComputeV1",
            SourceHash = sourceHash,
            CompilerHash,
            EntryPoint = entryPoint,
            Profile = profile,
            Flags = CompileFlags,
            Defines = macros,
        })));

        lock (gate)
        {
            if (compiled.TryGetValue(key, out var cached))
                return cached with { Dxil = cached.Dxil.ToArray(), CacheHit = true };

            // Persistent cache: the key covers the source, entry point, profile, flags, macros and the exact compiler
            // (version + binary hashes). A published DXIL with the recorded output hash is reused across runs instead
            // of recompiling every start/export and leaving a new directory behind each time.
            string published = Path.Combine(CacheDirectory, key + ".dxil");
            string publishedHash = Path.Combine(CacheDirectory, key + ".sha256");
            try
            {
                if (File.Exists(published) && File.Exists(publishedHash))
                {
                    byte[] cachedDxil = File.ReadAllBytes(published);
                    string expected = File.ReadAllText(publishedHash).Trim();
                    if (cachedDxil.Length >= 4 && cachedDxil.AsSpan(0, 4).SequenceEqual("DXBC"u8)
                        && string.Equals(Hash(cachedDxil), expected, StringComparison.Ordinal))
                    {
                        var hit = new DxcCompilationResult(cachedDxil, sourceHash, CompilerHash, CompilerVersion,
                            expected, key, "", entryPoint, profile, true);
                        compiled.Add(key, hit with { CacheHit = false });
                        return hit with { Dxil = cachedDxil.ToArray() };
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

            // Separate instances/processes may initialize the same shader concurrently.
            // A private attempt directory avoids sharing writable source/output files.
            string workDirectory = Path.Combine(CacheDirectory, key, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            File.WriteAllBytes(Path.Combine(workDirectory, "source.hlsl"), sourceBytes);
            var arguments = new List<string>(CompileFlags) { "-T", profile, "-E", entryPoint };
            foreach (var macro in macros)
            {
                arguments.Add("-D");
                arguments.Add(macro.Value is null ? macro.Key : $"{macro.Key}={macro.Value}");
            }
            // Relative file names prevent the workspace path from affecting the DXIL identity.
            arguments.AddRange(["-Fo", "shader.dxil", "source.hlsl"]);
            Interlocked.Increment(ref compileInvocationCount);
            var process = Run(arguments, workDirectory);
            string outputPath = Path.Combine(workDirectory, "shader.dxil");
            if (process.ExitCode != 0 || !File.Exists(outputPath))
                throw new DxcShaderCompilationException(
                    $"DXC could not compile {entryPoint} ({profile}).", process.Diagnostics, process.ExitCode);

            byte[] dxil = File.ReadAllBytes(outputPath);
            if (dxil.Length < 4 || !dxil.AsSpan(0, 4).SequenceEqual("DXBC"u8))
                throw new DxcShaderCompilationException("DXC returned an invalid DXIL container.", process.Diagnostics, process.ExitCode);

            var result = new DxcCompilationResult(dxil, sourceHash, CompilerHash, CompilerVersion,
                Hash(dxil), key, process.Diagnostics, entryPoint, profile, false);
            File.WriteAllText(Path.Combine(workDirectory, "compilation.json"), JsonSerializer.Serialize(new
            {
                result.SourceHash,
                result.CompilerHash,
                result.CompilerVersion,
                result.OutputHash,
                result.CacheKey,
                result.EntryPoint,
                result.Profile,
                CompilerPath,
                CompilerFiles,
                Arguments = arguments,
                Defines = macros,
                result.Diagnostics,
            }, new JsonSerializerOptions { WriteIndented = true }), SourceEncoding);
            compiled.Add(key, result);
            // Publish the output atomically (hash first, then DXIL via rename), then drop the private attempt
            // directory. A concurrent process publishing the same key writes identical bytes.
            try
            {
                string temporary = published + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(publishedHash, result.OutputHash);
                File.WriteAllBytes(temporary, dxil);
                File.Move(temporary, published, overwrite: true);
                File.Copy(Path.Combine(workDirectory, "compilation.json"), Path.Combine(CacheDirectory, key + ".json"), overwrite: true);
                Directory.Delete(workDirectory, recursive: true);
                string keyDirectory = Path.Combine(CacheDirectory, key);
                if (!Directory.EnumerateFileSystemEntries(keyDirectory).Any()) Directory.Delete(keyDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return result with { Dxil = result.Dxil.ToArray() };
        }
    }

    public static string ResolveCompilerPath(string? compilerPath = null)
    {
        string? explicitPath = compilerPath ?? Environment.GetEnvironmentVariable("MMD_DXC_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string resolved = Path.GetFullPath(explicitPath);
            if (!File.Exists(resolved))
                throw new FileNotFoundException("DXC was explicitly configured but its executable is missing. Run scripts/setup-dxc.ps1 or set MMD_DXC_PATH.", resolved);
            return resolved;
        }

        string bundled=Path.Combine(AppContext.BaseDirectory,"tools","dxc","dxc.exe");
        if(File.Exists(bundled))return Path.GetFullPath(bundled);
        // The fallbacks below run an executable found outside the installation. An installed app (one that is
        // not running from a source checkout) must not run a dxc found on PATH, and no build may search from the
        // process's current directory, which an associated document can control. MMD_DXC_PATH stays explicit.
        bool developmentBuild = RunsFromSourceTree();
        string? workspace = FindWorkspaceRoot();
        if (workspace is not null)
        {
            foreach (string candidate in new[]
            {
                Path.Combine(workspace, ".dev-cache", "modern-dotnet", "dxc", "bin", "x64", "dxc.exe"),
                Path.Combine(workspace, ".dev-cache", "ray-shader-toolchain-20261018", "tools", "dxc", "v1.8.2505.1", "bin", "x64", "dxc.exe"),
            })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        if (!developmentBuild)
            throw new FileNotFoundException("The bundled DXC is missing from this installation. Reinstall MDA or set MMD_DXC_PATH.", bundled);
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "dxc.exe" : "dxc");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("DXC is unavailable. Run scripts/setup-dxc.ps1 or set MMD_DXC_PATH; optional SM6 features cannot initialize.");
    }

    private (int ExitCode, string Diagnostics) Run(IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(CompilerPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = Path.Combine(CacheDirectory, "tmp");
        start.Environment["TMP"] = Path.Combine(CacheDirectory, "tmp");
        try
        {
            using var process = Process.Start(start)
                ?? throw new DxcShaderCompilationException("DXC process did not start.", "Process.Start returned null.", null);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                string timedOutOutput = string.Join(Environment.NewLine, Task.WhenAll(stdout, stderr).GetAwaiter().GetResult());
                throw new DxcShaderCompilationException("DXC exceeded the 30-second compilation timeout.", timedOutOutput, null);
            }
            string diagnostics = string.Join(Environment.NewLine, Task.WhenAll(stdout, stderr).GetAwaiter().GetResult()).Trim();
            return (process.ExitCode, diagnostics);
        }
        catch (Win32Exception exception)
        {
            throw new DxcShaderCompilationException("DXC process could not start. Verify the compiler and adjacent native DLLs.", exception.Message, null, exception);
        }
    }

    // An installed copy is a published self-contained app: the installer and the CI publish both place the bundled
    // tools/dxc next to it. Development and test builds come from MSBuild's output directory, which the repository
    // redirects to <repo>/../.dev-cache/modern-dotnet/bin/<project>/<config>/<tfm>/ (Directory.Build.props), so no
    // checkout is among its ancestors. Detect both layouts rather than inferring "installed" from the apphost.
    private static bool RunsFromSourceTree()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mmd.Modern.slnx"))) return true;
            // <cache>/bin/<project>/...: a sibling obj/ tree and a checkout beside the cache's parent.
            if (string.Equals(directory.Name, "bin", StringComparison.OrdinalIgnoreCase) && directory.Parent is { } cache
                && Directory.Exists(Path.Combine(cache.FullName, "obj"))) return true;
        }
        return false;
    }

    private static string? FindWorkspaceRoot()
    {
        // Only the application's own location is trusted, never the process's current directory.
        foreach (string start in new[] { AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "MMD932_modern", "AGENTS.md")))
                    return directory.FullName;
        }
        return null;
    }

    private static bool ContainsIncludeDirective(string source)
    {
        // Preprocessing splices lines before interpreting comments. Strip comments while
        // retaining strings/newlines so a comment prefix cannot hide an external include.
        string spliced = Regex.Replace(source, @"\\\r?\n", "", RegexOptions.CultureInvariant);
        var visible = new StringBuilder(spliced.Length);
        bool blockComment = false, lineComment = false;
        char quote = '\0';
        for (int index = 0; index < spliced.Length; index++)
        {
            char current = spliced[index];
            char next = index + 1 < spliced.Length ? spliced[index + 1] : '\0';
            if (lineComment)
            {
                visible.Append(current is '\r' or '\n' ? current : ' ');
                if (current is '\r' or '\n') lineComment = false;
            }
            else if (blockComment)
            {
                if (current == '*' && next == '/')
                {
                    visible.Append("  ");
                    index++;
                    blockComment = false;
                }
                else visible.Append(current is '\r' or '\n' ? current : ' ');
            }
            else if (quote != '\0')
            {
                visible.Append(current);
                if (current == '\\' && next != '\0') visible.Append(spliced[++index]);
                else if (current == quote) quote = '\0';
            }
            else if (current == '/' && next is '/' or '*')
            {
                visible.Append("  ");
                index++;
                lineComment = next == '/';
                blockComment = next == '*';
            }
            else
            {
                visible.Append(current);
                if (current is '\"' or '\'') quote = current;
            }
        }
        return Regex.IsMatch(visible.ToString(), @"(?m)^\s*#\s*include\b", RegexOptions.CultureInvariant);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed record DxcCompilerFile(string Name, string Sha256);

public sealed record DxcCompilationResult(
    byte[] Dxil,
    string SourceHash,
    string CompilerHash,
    string CompilerVersion,
    string OutputHash,
    string CacheKey,
    string Diagnostics,
    string EntryPoint,
    string Profile,
    bool CacheHit);

public sealed class DxcShaderCompilationException : InvalidOperationException
{
    public DxcShaderCompilationException(string message, string diagnostics, int? exitCode, Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(diagnostics) ? message : $"{message}{Environment.NewLine}{diagnostics}", innerException)
    {
        Diagnostics = diagnostics;
        ExitCode = exitCode;
    }

    public string Diagnostics { get; }
    public int? ExitCode { get; }
}
