using System.Buffers.Binary;
using System.Collections.Concurrent;
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
    private static readonly byte[] DiskCacheMagic = "MDDXCC1\0"u8.ToArray();
    private static readonly ConcurrentDictionary<string, DxcShaderCompiler> SharedCompilers = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CompilerIdentity> Identities = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Dictionary<string, DxcCompilationResult> compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<DxcCompilationResult>> inFlight = new(StringComparer.Ordinal);
    private int compileInvocationCount;
    private int diskCacheHitCount;

    public static int DefaultPrewarmParallelism => Math.Max(1, Environment.ProcessorCount / 2);

    public DxcShaderCompiler(string? compilerPath = null, string? cacheDirectory = null)
    {
        CompilerPath = ResolveCompilerPath(compilerPath);
        CacheDirectory = ResolveCacheDirectory(cacheDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(Path.Combine(CacheDirectory, "tmp"));

        var identity = GetOrCreateIdentity(CompilerPath, CacheDirectory);
        CompilerVersion = identity.Version;
        CompilerFiles = identity.Files;
        CompilerHash = identity.Hash;
    }

    public string CompilerPath { get; }
    public string CacheDirectory { get; }
    public string CompilerVersion { get; }
    public string CompilerHash { get; }
    public IReadOnlyList<DxcCompilerFile> CompilerFiles { get; }
    public int CompileInvocationCount => Volatile.Read(ref compileInvocationCount);
    public int DiskCacheHitCount => Volatile.Read(ref diskCacheHitCount);

    internal static DxcShaderCompiler GetOrCreateShared(string? compilerPath = null, string? cacheDirectory = null)
    {
        string resolvedCompiler = ResolveCompilerPath(compilerPath);
        string resolvedCache = ResolveCacheDirectory(cacheDirectory);
        string key = $"{resolvedCompiler}\n{resolvedCache}";
        return SharedCompilers.GetOrAdd(key, _ => new DxcShaderCompiler(resolvedCompiler, resolvedCache));
    }

    private static string ResolveCacheDirectory(string? cacheDirectory)
    {
        string? environmentCache = cacheDirectory is null ? Environment.GetEnvironmentVariable("MMD_DXC_CACHE") : null;
        string? workspace = cacheDirectory is null && environmentCache is null ? D3D12RuntimePaths.FindWorkspaceRoot() : null;
        string? localData = cacheDirectory is null && environmentCache is null && workspace is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null;
        return D3D12RuntimePaths.ResolveDxcCacheDirectory(cacheDirectory, environmentCache, workspace, localData);
    }

    private static CompilerIdentity GetOrCreateIdentity(string compilerPath, string cacheDirectory)
    {
        string compilerDirectory = Path.GetDirectoryName(compilerPath)!;
        string[] names = new[] { Path.GetFileName(compilerPath), "dxcompiler.dll", "dxil.dll" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string stamp = string.Join("|", names.Select(name =>
        {
            string full = Path.Combine(compilerDirectory, name);
            var info = new FileInfo(full);
            return info.Exists ? $"{name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : $"{name}:missing";
        }));
        string cacheKey = $"{compilerPath}\n{stamp}";
        if (Identities.TryGetValue(cacheKey, out var cached)) return cached;

        var version = RunProcess(compilerPath, cacheDirectory, ["--version"], cacheDirectory, CancellationToken.None);
        if (version.ExitCode != 0)
            throw new DxcShaderCompilationException("DXC version query failed.", version.Diagnostics, version.ExitCode);
        string compilerVersion = version.Diagnostics.Trim();
        if (string.IsNullOrWhiteSpace(compilerVersion))
            throw new DxcShaderCompilationException("DXC returned an empty compiler identity.", version.Diagnostics, version.ExitCode);

        var files = Array.AsReadOnly(names
            .Select(name => new DxcCompilerFile(name, File.Exists(Path.Combine(compilerDirectory, name))
                ? Hash(File.ReadAllBytes(Path.Combine(compilerDirectory, name))) : "missing"))
            .ToArray());
        string hash = Hash(SourceEncoding.GetBytes(JsonSerializer.Serialize(new
        {
            Version = compilerVersion,
            Files = files,
        })));
        return Identities.GetOrAdd(cacheKey, _ => new CompilerIdentity(compilerVersion, files, hash));
    }

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

    public Task PrewarmAsync(
        IEnumerable<DxcShaderWarmupRequest> requests,
        int maxParallelism = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelism);
        var list = requests.ToArray();
        if (list.Length == 0 || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        int concurrency = Math.Min(list.Length, maxParallelism == 0 ? DefaultPrewarmParallelism : maxParallelism);
        return Task.Run(async () =>
        {
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var tasks = new Task[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                var req = list[i];
                tasks[i] = Task.Run(async () =>
                {
                    try { await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    try
                    {
                        if (!cancellationToken.IsCancellationRequested && TryProfileKind(req.Profile, out string kind))
                            CompileProgram(req.Source, req.EntryPoint, req.Profile, req.Defines, kind, cancellationToken);
                    }
                    catch (Exception) { }
                    finally { semaphore.Release(); }
                }, CancellationToken.None);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    internal void TryPrewarmSingle(DxcShaderWarmupRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!cancellationToken.IsCancellationRequested && TryProfileKind(request.Profile, out string kind))
                CompileProgram(request.Source, request.EntryPoint, request.Profile, request.Defines, kind, cancellationToken);
        }
        catch (Exception) { }
    }

    private static bool TryProfileKind(string profile, out string kind)
    {
        if (!string.IsNullOrWhiteSpace(profile))
        {
            if (profile.StartsWith("cs_", StringComparison.Ordinal)) { kind = "cs"; return true; }
            if (profile.StartsWith("ps_", StringComparison.Ordinal)) { kind = "ps"; return true; }
            if (profile.StartsWith("vs_", StringComparison.Ordinal)) { kind = "vs"; return true; }
        }
        kind = "";
        return false;
    }

    private DxcCompilationResult CompileProgram(
        string source,
        string entryPoint,
        string profile,
        IReadOnlyDictionary<string, string?>? defines,
        string shaderKind,
        CancellationToken cancellationToken = default)
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<DxcCompilationResult>? ownerTcs = null;
            Task<DxcCompilationResult>? joinTask = null;
            lock (gate)
            {
                if (compiled.TryGetValue(key, out var cached))
                    return cached with { Dxil = cached.Dxil.ToArray(), CacheHit = true };
                if (inFlight.TryGetValue(key, out var existing))
                    joinTask = existing;
                else
                {
                    ownerTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    inFlight.Add(key, ownerTcs.Task);
                }
            }

            if (joinTask is not null)
            {
                DxcCompilationResult joined;
                try
                {
                    joined = joinTask.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Another caller's prewarm/request was cancelled; retry cleanly for this caller.
                    continue;
                }
                return joined with { Dxil = joined.Dxil.ToArray(), CacheHit = true };
            }

            try
            {
                var result = TryLoadFromDisk(key, sourceHash, entryPoint, profile)
                    ?? ExecuteAndCacheToDisk(key, sourceBytes, sourceHash, entryPoint, profile, macros, cancellationToken);
                lock (gate)
                {
                    compiled[key] = result;
                    inFlight.Remove(key);
                }
                ownerTcs!.TrySetResult(result);
                return result with { Dxil = result.Dxil.ToArray() };
            }
            catch (OperationCanceledException canceled)
            {
                lock (gate) inFlight.Remove(key);
                ownerTcs!.TrySetCanceled(canceled.CancellationToken.CanBeCanceled ? canceled.CancellationToken : CancellationToken.None);
                throw;
            }
            catch (Exception error)
            {
                lock (gate) inFlight.Remove(key);
                ownerTcs!.TrySetException(error);
                _ = ownerTcs.Task.Exception;
                throw;
            }
        }
    }

    private DxcCompilationResult? TryLoadFromDisk(string key, string sourceHash, string entryPoint, string profile)
    {
        string cachePath = Path.Combine(CacheDirectory, key, "cache.bin");
        if (!File.Exists(cachePath)) return null;
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length < DiskCacheMagic.Length + 4 + 4 + 32 || stream.Length > 64L * 1024 * 1024)
                    return DeleteAndNull(cachePath);
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            if (!bytes.AsSpan(0, DiskCacheMagic.Length).SequenceEqual(DiskCacheMagic))
                return DeleteAndNull(cachePath);
            int payloadEnd = bytes.Length - 32;
            byte[] actualDigest = SHA256.HashData(bytes.AsSpan(0, payloadEnd));
            if (!actualDigest.AsSpan().SequenceEqual(bytes.AsSpan(payloadEnd, 32)))
                return DeleteAndNull(cachePath);

            int offset = DiskCacheMagic.Length;
            int metaLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (metaLen <= 0 || offset + metaLen + 4 > payloadEnd)
                return DeleteAndNull(cachePath);
            var meta = JsonSerializer.Deserialize<DiskCacheMetadata>(bytes.AsSpan(offset, metaLen));
            offset += metaLen;
            int dxilLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (meta is null || dxilLen < 4 || offset + dxilLen != payloadEnd)
                return DeleteAndNull(cachePath);
            byte[] dxil = bytes.AsSpan(offset, dxilLen).ToArray();
            if (!dxil.AsSpan(0, 4).SequenceEqual("DXBC"u8))
                return DeleteAndNull(cachePath);
            if (!string.Equals(meta.CacheKey, key, StringComparison.Ordinal)
                || !string.Equals(meta.CompilerHash, CompilerHash, StringComparison.Ordinal)
                || !string.Equals(meta.SourceHash, sourceHash, StringComparison.Ordinal)
                || !string.Equals(meta.EntryPoint, entryPoint, StringComparison.Ordinal)
                || !string.Equals(meta.Profile, profile, StringComparison.Ordinal)
                || !string.Equals(Hash(dxil), meta.OutputHash, StringComparison.Ordinal))
                return DeleteAndNull(cachePath);

            Interlocked.Increment(ref diskCacheHitCount);
            return new DxcCompilationResult(dxil, sourceHash, CompilerHash, CompilerVersion,
                meta.OutputHash, key, meta.Diagnostics ?? "", entryPoint, profile, CacheHit: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return DeleteAndNull(cachePath);
        }

        static DxcCompilationResult? DeleteAndNull(string path)
        {
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    private DxcCompilationResult ExecuteAndCacheToDisk(
        string key,
        byte[] sourceBytes,
        string sourceHash,
        string entryPoint,
        string profile,
        KeyValuePair<string, string?>[] macros,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string keyDirectory = Path.Combine(CacheDirectory, key);
        string workDirectory = Path.Combine(CacheDirectory, "tmp", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(workDirectory);
        try
        {
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
            var process = RunProcess(CompilerPath, CacheDirectory, arguments, workDirectory, cancellationToken);
            string outputPath = Path.Combine(workDirectory, "shader.dxil");
            if (process.ExitCode != 0 || !File.Exists(outputPath))
                throw new DxcShaderCompilationException(
                    $"DXC could not compile {entryPoint} ({profile}).", process.Diagnostics, process.ExitCode);

            byte[] dxil = File.ReadAllBytes(outputPath);
            if (dxil.Length < 4 || !dxil.AsSpan(0, 4).SequenceEqual("DXBC"u8))
                throw new DxcShaderCompilationException("DXC returned an invalid DXIL container.", process.Diagnostics, process.ExitCode);

            string outputHash = Hash(dxil);
            var result = new DxcCompilationResult(dxil, sourceHash, CompilerHash, CompilerVersion,
                outputHash, key, process.Diagnostics, entryPoint, profile, false);
            TryWriteDiskCache(keyDirectory, workDirectory, result);
            return result;
        }
        finally
        {
            try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true); } catch { }
        }
    }

    private static void TryWriteDiskCache(string keyDirectory, string workDirectory, DxcCompilationResult result)
    {
        try
        {
            byte[] metaBytes = SourceEncoding.GetBytes(JsonSerializer.Serialize(new DiskCacheMetadata(
                result.CacheKey, result.SourceHash, result.CompilerHash, result.CompilerVersion,
                result.OutputHash, result.EntryPoint, result.Profile, result.Diagnostics)));
            int payloadLength = DiskCacheMagic.Length + 4 + metaBytes.Length + 4 + result.Dxil.Length;
            byte[] buffer = new byte[payloadLength + 32];
            DiskCacheMagic.CopyTo(buffer, 0);
            int offset = DiskCacheMagic.Length;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), metaBytes.Length);
            offset += 4;
            metaBytes.CopyTo(buffer, offset);
            offset += metaBytes.Length;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), result.Dxil.Length);
            offset += 4;
            result.Dxil.CopyTo(buffer, offset);
            SHA256.HashData(buffer.AsSpan(0, payloadLength)).CopyTo(buffer.AsSpan(payloadLength, 32));

            Directory.CreateDirectory(keyDirectory);
            string tempPath = Path.Combine(workDirectory, "cache.tmp");
            File.WriteAllBytes(tempPath, buffer);
            File.Move(tempPath, Path.Combine(keyDirectory, "cache.bin"), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed record CompilerIdentity(string Version, IReadOnlyList<DxcCompilerFile> Files, string Hash);
    private sealed record DiskCacheMetadata(string CacheKey, string SourceHash, string CompilerHash, string CompilerVersion,
        string OutputHash, string EntryPoint, string Profile, string? Diagnostics);

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
        string? workspace = D3D12RuntimePaths.FindWorkspaceRoot();
        if (workspace is not null)
        {
            foreach (string candidate in new[]
            {
                Path.Combine(workspace, ".dev-cache", "modern-dotnet", "dxc", "bin", "x64", "dxc.exe"),
                Path.Combine(workspace, ".dev-cache", "ray-shader-toolchain-20261018", "tools", "dxc", "v1.8.2505.1", "bin", "x64", "dxc.exe"),
            })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "dxc.exe" : "dxc");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("DXC is unavailable. Run scripts/setup-dxc.ps1 or set MMD_DXC_PATH; optional SM6 features cannot initialize.");
    }

    private static (int ExitCode, string Diagnostics) RunProcess(string compilerPath, string cacheDirectory,
        IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(compilerPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = Path.Combine(cacheDirectory, "tmp");
        start.Environment["TMP"] = Path.Combine(cacheDirectory, "tmp");
        try
        {
            using var process = Process.Start(start)
                ?? throw new DxcShaderCompilationException("DXC process did not start.", "Process.Start returned null.", null);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            if (cancellationToken.CanBeCanceled)
            {
                using var timeout = new CancellationTokenSource(30_000);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { process.WaitForExit(); } catch { }
                    try { Task.WhenAll(stdout, stderr).GetAwaiter().GetResult(); } catch { }
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    process.WaitForExit();
                    string timedOutOutput = string.Join(Environment.NewLine, Task.WhenAll(stdout, stderr).GetAwaiter().GetResult());
                    throw new DxcShaderCompilationException("DXC exceeded the 30-second compilation timeout.", timedOutOutput, null);
                }
            }
            else if (!process.WaitForExit(30_000))
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

public sealed record DxcShaderWarmupRequest(
    string Source,
    string EntryPoint = "CSMain",
    string Profile = "cs_6_5",
    IReadOnlyDictionary<string, string?>? Defines = null);

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
