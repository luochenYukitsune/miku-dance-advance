using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

internal readonly record struct D3D12FxcShaderRequest(
    string Source,
    string EntryPoint,
    string SourceName,
    string Profile,
    ShaderFlags Flags = ShaderFlags.None);

/// <summary>Thread-safe in-memory and disk cache for SM5 FXC shader compilation with in-flight deduplication.
/// Failures are never cached and surface only to callers that actually request the failing shader.</summary>
internal static class D3D12FxcShaderCache
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly byte[] Magic = "MMDFXC1\0"u8.ToArray();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, byte[]> Compiled = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<byte[]>> InFlight = new(StringComparer.Ordinal);
    private static int invocationCount;

    internal static int InvocationCount => Volatile.Read(ref invocationCount);

    internal static byte[] Compile(
        string source,
        string entryPoint,
        string sourceName,
        string profile,
        ShaderFlags flags = ShaderFlags.None,
        string? cacheDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        string? resolvedDir = ResolveDirectory(cacheDirectory);
        string key = ComputeKey(source, entryPoint, sourceName, profile, flags);
        string memoryKey = $"{resolvedDir ?? ""}\n{key}";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<byte[]>? ownerTcs = null;
            Task<byte[]>? joinTask = null;
            lock (Gate)
            {
                if (Compiled.TryGetValue(memoryKey, out var cached))
                    return cached.ToArray();
                if (InFlight.TryGetValue(memoryKey, out var pending))
                    joinTask = pending;
                else
                {
                    ownerTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    InFlight.Add(memoryKey, ownerTcs.Task);
                }
            }

            if (joinTask is not null)
            {
                byte[] joined;
                try
                {
                    joined = joinTask.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                return joined.ToArray();
            }

            try
            {
                byte[]? bytes = resolvedDir is null ? null : TryLoadFromDisk(resolvedDir, key);
                if (bytes is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref invocationCount);
                    bytes = Compiler.Compile(source, entryPoint, sourceName, profile, flags).ToArray();
                    if (resolvedDir is not null)
                        TrySaveToDisk(resolvedDir, key, bytes);
                }

                lock (Gate)
                {
                    Compiled[memoryKey] = bytes;
                    InFlight.Remove(memoryKey);
                }
                ownerTcs!.TrySetResult(bytes);
                return bytes.ToArray();
            }
            catch (OperationCanceledException canceled)
            {
                lock (Gate) InFlight.Remove(memoryKey);
                ownerTcs!.TrySetCanceled(canceled.CancellationToken.CanBeCanceled ? canceled.CancellationToken : CancellationToken.None);
                throw;
            }
            catch (Exception error)
            {
                lock (Gate) InFlight.Remove(memoryKey);
                ownerTcs!.TrySetException(error);
                _ = ownerTcs.Task.Exception;
                throw;
            }
        }
    }

    internal static Task PrewarmAsync(
        IEnumerable<D3D12FxcShaderRequest> requests,
        int maxParallelism = 0,
        string? cacheDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelism);
        var list = requests.ToArray();
        if (list.Length == 0 || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        int concurrency = Math.Min(list.Length, maxParallelism == 0 ? DxcShaderCompiler.DefaultPrewarmParallelism : maxParallelism);
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
                        if (!cancellationToken.IsCancellationRequested)
                            Compile(req.Source, req.EntryPoint, req.SourceName, req.Profile, req.Flags, cacheDirectory, cancellationToken);
                    }
                    catch (Exception) { }
                    finally { semaphore.Release(); }
                }, CancellationToken.None);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    private static string? ResolveDirectory(string? explicitDirectory)
    {
        if (explicitDirectory is not null) return Path.GetFullPath(explicitDirectory);
        try
        {
            string? env = Environment.GetEnvironmentVariable("MMD_DXC_CACHE");
            string? workspace = env is null ? D3D12RuntimePaths.FindWorkspaceRoot() : null;
            string? localData = env is null && workspace is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null;
            return D3D12RuntimePaths.ResolveFxcCacheDirectory(null, env, workspace, localData);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static string ComputeKey(string source, string entryPoint, string sourceName, string profile, ShaderFlags flags)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Feed(string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
            hash.AppendData(len);
            hash.AppendData(bytes);
        }
        Feed("MmdFxcV1");
        Feed(source);
        Feed(entryPoint);
        Feed(sourceName);
        Feed(profile);
        Span<byte> flagBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(flagBytes, unchecked((int)flags));
        hash.AppendData(flagBytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static byte[]? TryLoadFromDisk(string directory, string key)
    {
        string path = Path.Combine(directory, key + ".bin");
        if (!File.Exists(path)) return null;
        try
        {
            byte[] data;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length < Magic.Length + 4 + 4 + 32 || stream.Length > 64L * 1024 * 1024)
                    return DeleteAndNull(path);
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }
            if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                return DeleteAndNull(path);
            int payloadEnd = data.Length - 32;
            if (!SHA256.HashData(data.AsSpan(0, payloadEnd)).AsSpan().SequenceEqual(data.AsSpan(payloadEnd, 32)))
                return DeleteAndNull(path);

            int offset = Magic.Length;
            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            if (keyLen <= 0 || offset + keyLen + 4 > payloadEnd)
                return DeleteAndNull(path);
            string storedKey = Utf8.GetString(data.AsSpan(offset, keyLen));
            offset += keyLen;
            if (!string.Equals(storedKey, key, StringComparison.Ordinal))
                return DeleteAndNull(path);

            int codeLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            if (codeLen < 4 || offset + codeLen != payloadEnd)
                return DeleteAndNull(path);
            byte[] code = data.AsSpan(offset, codeLen).ToArray();
            if (!code.AsSpan(0, 4).SequenceEqual("DXBC"u8))
                return DeleteAndNull(path);
            return code;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DeleteAndNull(path);
        }

        static byte[]? DeleteAndNull(string filePath)
        {
            try { File.Delete(filePath); } catch { }
            return null;
        }
    }

    private static void TrySaveToDisk(string directory, string key, byte[] bytecode)
    {
        string tempPath = Path.Combine(directory, $".{key}-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            byte[] keyBytes = Utf8.GetBytes(key);
            int payloadLength = Magic.Length + 4 + keyBytes.Length + 4 + bytecode.Length;
            byte[] buffer = new byte[payloadLength + 32];
            Magic.CopyTo(buffer, 0);
            int offset = Magic.Length;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), keyBytes.Length);
            offset += 4;
            keyBytes.CopyTo(buffer, offset);
            offset += keyBytes.Length;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), bytecode.Length);
            offset += 4;
            bytecode.CopyTo(buffer, offset);
            SHA256.HashData(buffer.AsSpan(0, payloadLength)).CopyTo(buffer.AsSpan(payloadLength, 32));

            File.WriteAllBytes(tempPath, buffer);
            File.Move(tempPath, Path.Combine(directory, key + ".bin"), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
