using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vortice;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

internal readonly record struct D3D12AdapterIdentity(
    long AdapterLuid,
    uint VendorId,
    uint DeviceId,
    uint SubsystemId,
    uint Revision,
    long DriverVersion,
    bool IsSoftware);

/// <summary>Disk-backed <see cref="ID3D12PipelineLibrary"/> PSO cache for one D3D12 device.
/// All library operations are serialized under an internal lock per D3D12 threading rules.
/// Any load, header, integrity or driver mismatch silently discards the stale file and falls
/// back to standard PSO creation without affecting rendering correctness.</summary>
internal sealed class D3D12PipelineLibraryCache : IDisposable
{
    private const uint FileFormatVersion = 1;
    private const int MaxFileBytes = 256 * 1024 * 1024;
    private const int MaxKeyCount = 65536;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly byte[] FileMagic = "MDAPSO1\0"u8.ToArray();

    private readonly ID3D12Device device;
    private readonly object gate = new();
    private readonly ConditionalWeakTable<ID3D12RootSignature, string> rootSignatureHashes = new();
    private readonly HashSet<string> knownKeys = new(StringComparer.Ordinal);
    private ID3D12PipelineLibrary? library;
    private GCHandle pinnedBlobHandle;
    private bool hasPinnedBlob;
    private bool dirty;
    private bool tainted;
    private bool disposed;
    private int libraryHits;
    private int libraryStores;
    private Task? pendingFlush;
    private readonly object flushGate = new();

    internal D3D12PipelineLibraryCache(ID3D12Device device, string? explicitCacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
        AdapterIdentity = ResolveAdapterIdentity(device);
        CacheDirectory = ResolveCacheDirectory(explicitCacheDirectory);
        CacheFilePath = CacheDirectory is null ? null : Path.Combine(CacheDirectory, BuildFileName(AdapterIdentity));
        InitializeLibrary();
    }

    internal D3D12AdapterIdentity AdapterIdentity { get; }
    internal string? CacheDirectory { get; }
    internal string? CacheFilePath { get; }
    internal bool IsLibraryAvailable { get { lock (gate) return library is not null && !tainted; } }
    internal bool LoadedFromDisk { get; private set; }
    internal int LibraryHits => Volatile.Read(ref libraryHits);
    internal int LibraryStores => Volatile.Read(ref libraryStores);

    internal void RegisterRootSignature(ID3D12RootSignature rootSignature, RootSignatureDescription description, RootSignatureVersion version)
    {
        ArgumentNullException.ThrowIfNull(rootSignature);
        string hash = ComputeRootSignatureHash(description, version);
        rootSignatureHashes.AddOrUpdate(rootSignature, hash);
    }

    internal ID3D12PipelineState CreateGraphicsPipelineState(GraphicsPipelineStateDescription description)
    {
        string key = ComputeGraphicsKey(description, TryGetRootSignatureHash(description.RootSignature));
        lock (gate)
        {
            if (library is not null && !disposed && !tainted && knownKeys.Contains(key))
            {
                try
                {
                    var loaded = library.LoadGraphicsPipeline(key, description);
                    if (loaded is not null)
                    {
                        Interlocked.Increment(ref libraryHits);
                        return loaded;
                    }
                }
                catch
                {
                    MarkTaintedLocked();
                }
            }
        }

        var created = device.CreateGraphicsPipelineState(description);
        StorePipelineState(key, created);
        return created;
    }

    internal ID3D12PipelineState CreateComputePipelineState(ComputePipelineStateDescription description)
    {
        string key = ComputeComputeKey(description, TryGetRootSignatureHash(description.RootSignature));
        lock (gate)
        {
            if (library is not null && !disposed && !tainted && knownKeys.Contains(key))
            {
                try
                {
                    var loaded = library.LoadComputePipeline(key, description);
                    if (loaded is not null)
                    {
                        Interlocked.Increment(ref libraryHits);
                        return loaded;
                    }
                }
                catch
                {
                    MarkTaintedLocked();
                }
            }
        }

        var created = device.CreateComputePipelineState(description);
        StorePipelineState(key, created);
        return created;
    }

    internal void Flush()
    {
        byte[]? fileBytes = null;
        string? targetPath;
        lock (gate)
        {
            if (disposed || tainted || !dirty || library is null || CacheFilePath is null) return;
            targetPath = CacheFilePath;
            try
            {
                int serializedSize = checked((int)library.SerializedSize);
                if (serializedSize <= 0 || serializedSize > MaxFileBytes) return;
                byte[] blob = new byte[serializedSize];
                unsafe
                {
                    fixed (byte* ptr = blob)
                        library.Serialize((nint)ptr, serializedSize);
                }
                fileBytes = BuildFileBytes(AdapterIdentity, knownKeys, blob);
                dirty = false;
            }
            catch
            {
                return;
            }
        }

        WriteAtomic(targetPath, fileBytes);
    }

    internal void FlushIfDirtyAsync()
    {
        lock (gate)
        {
            if (disposed || tainted || !dirty || library is null || CacheFilePath is null) return;
        }
        lock (flushGate)
        {
            if (pendingFlush is { IsCompleted: false }) return;
            pendingFlush = Task.Run(Flush);
        }
    }

    public void Dispose()
    {
        Task? inFlightFlush;
        lock (flushGate)
        {
            inFlightFlush = pendingFlush;
            pendingFlush = null;
        }
        if (inFlightFlush is not null)
        {
            try { inFlightFlush.GetAwaiter().GetResult(); } catch { }
        }

        byte[]? pendingWrite = null;
        string? targetPath = null;
        lock (gate)
        {
            if (disposed) return;
            if (!tainted && dirty && library is not null && CacheFilePath is not null)
            {
                targetPath = CacheFilePath;
                try
                {
                    int serializedSize = checked((int)library.SerializedSize);
                    if (serializedSize > 0 && serializedSize <= MaxFileBytes)
                    {
                        byte[] blob = new byte[serializedSize];
                        unsafe
                        {
                            fixed (byte* ptr = blob)
                                library.Serialize((nint)ptr, serializedSize);
                        }
                        pendingWrite = BuildFileBytes(AdapterIdentity, knownKeys, blob);
                    }
                }
                catch { pendingWrite = null; }
                dirty = false;
            }
            disposed = true;
            library?.Dispose();
            library = null;
            FreePinnedBlobLocked();
        }

        if (pendingWrite is not null && targetPath is not null)
            WriteAtomic(targetPath, pendingWrite);
    }

    private void InitializeLibrary()
    {
        using var device1 = device.QueryInterfaceOrNull<ID3D12Device1>();
        if (device1 is null) return;

        if (CacheFilePath is not null && File.Exists(CacheFilePath))
        {
            try
            {
                byte[]? fileBytes = TryReadBytes(CacheFilePath);
                if (fileBytes is not null
                    && TryParseFileBytes(fileBytes, AdapterIdentity, out string[] keys, out byte[] blob))
                {
                    var pin = GCHandle.Alloc(blob, GCHandleType.Pinned);
                    try
                    {
                        if (device1.CreatePipelineLibrary(blob, out var loaded).Success && loaded is not null)
                        {
                            library = loaded;
                            pinnedBlobHandle = pin;
                            hasPinnedBlob = true;
                            foreach (string k in keys) knownKeys.Add(k);
                            LoadedFromDisk = true;
                            return;
                        }
                    }
                    catch
                    {
                        pin.Free();
                        throw;
                    }
                    pin.Free();
                }
            }
            catch { }

            TryDeleteFile(CacheFilePath);
        }

        try
        {
            if (device1.CreatePipelineLibrary(Array.Empty<byte>(), out var empty).Success && empty is not null)
                library = empty;
        }
        catch
        {
            library = null;
        }
    }

    private void StorePipelineState(string key, ID3D12PipelineState state)
    {
        lock (gate)
        {
            if (library is null || disposed || tainted) return;
            if (!knownKeys.Add(key)) return;
            try
            {
                library.StorePipeline(key, state);
                dirty = true;
                Interlocked.Increment(ref libraryStores);
            }
            catch
            {
                knownKeys.Remove(key);
            }
        }
    }

    private void MarkTaintedLocked()
    {
        tainted = true;
        dirty = false;
        knownKeys.Clear();
        library?.Dispose();
        library = null;
        FreePinnedBlobLocked();
        if (CacheFilePath is not null) TryDeleteFile(CacheFilePath);
    }

    private void FreePinnedBlobLocked()
    {
        if (!hasPinnedBlob) return;
        pinnedBlobHandle.Free();
        hasPinnedBlob = false;
    }

    private string? TryGetRootSignatureHash(ID3D12RootSignature? root)
        => root is not null && rootSignatureHashes.TryGetValue(root, out string? hash) ? hash : null;

    internal static D3D12AdapterIdentity ResolveAdapterIdentity(ID3D12Device device)
    {
        long luid = device.AdapterLuid;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            if (factory.EnumAdapterByLuid(Unsafe.As<long, Luid>(ref luid), out IDXGIAdapter1? adapter).Success && adapter is not null)
            {
                using (adapter)
                {
                    var desc = adapter.Description1;
                    long driverVersion = 0;
                    _ = adapter.CheckInterfaceSupport<IDXGIDevice>(out driverVersion);
                    return new(luid, unchecked((uint)desc.VendorId), unchecked((uint)desc.DeviceId),
                        unchecked((uint)desc.SubsystemId), unchecked((uint)desc.Revision), driverVersion,
                        (desc.Flags & AdapterFlags.Software) != 0);
                }
            }
        }
        catch { }
        return new(luid, 0, 0, 0, 0, 0, false);
    }

    internal static string BuildFileName(D3D12AdapterIdentity identity)
        => $"pso-{identity.VendorId:x4}-{identity.DeviceId:x4}-{identity.SubsystemId:x8}-{identity.Revision:x4}{(identity.IsSoftware ? "-warp" : "")}.bin";

    internal static byte[] BuildFileBytes(D3D12AdapterIdentity identity, IReadOnlyCollection<string> keys, ReadOnlySpan<byte> libraryBlob)
    {
        string[] ordered = keys.Order(StringComparer.Ordinal).ToArray();
        int keysBytes = 0;
        byte[][] keyUtf8 = new byte[ordered.Length][];
        for (int i = 0; i < ordered.Length; i++)
        {
            keyUtf8[i] = Utf8.GetBytes(ordered[i]);
            keysBytes = checked(keysBytes + 4 + keyUtf8[i].Length);
        }

        int headerSize = FileMagic.Length + 4 + 8 + 4 + 4 + 4 + 4 + 8 + 4;
        int payloadLength = checked(headerSize + keysBytes + 4 + libraryBlob.Length);
        byte[] buffer = new byte[checked(payloadLength + 32)];
        var span = buffer.AsSpan();
        int offset = 0;
        FileMagic.CopyTo(span);
        offset += FileMagic.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), FileFormatVersion); offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, 8), identity.AdapterLuid); offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), identity.VendorId); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), identity.DeviceId); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), identity.SubsystemId); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), identity.Revision); offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, 8), identity.DriverVersion); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), ordered.Length); offset += 4;
        for (int i = 0; i < keyUtf8.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), keyUtf8[i].Length); offset += 4;
            keyUtf8[i].CopyTo(span.Slice(offset)); offset += keyUtf8[i].Length;
        }
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), libraryBlob.Length); offset += 4;
        libraryBlob.CopyTo(span.Slice(offset)); offset += libraryBlob.Length;
        SHA256.HashData(span[..payloadLength]).CopyTo(span.Slice(payloadLength, 32));
        return buffer;
    }

    internal static bool TryParseFileBytes(ReadOnlySpan<byte> fileBytes, D3D12AdapterIdentity expectedIdentity,
        out string[] keys, out byte[] libraryBlob)
    {
        keys = [];
        libraryBlob = [];
        const int minHeader = 8 + 4 + 8 + 4 + 4 + 4 + 4 + 8 + 4 + 4;
        if (fileBytes.Length < minHeader + 32 || fileBytes.Length > MaxFileBytes) return false;
        if (!fileBytes[..FileMagic.Length].SequenceEqual(FileMagic)) return false;

        int payloadEnd = fileBytes.Length - 32;
        if (!SHA256.HashData(fileBytes[..payloadEnd]).AsSpan().SequenceEqual(fileBytes.Slice(payloadEnd, 32)))
            return false;

        int offset = FileMagic.Length;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
        long luid = BinaryPrimitives.ReadInt64LittleEndian(fileBytes.Slice(offset, 8)); offset += 8;
        uint vendorId = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
        uint deviceId = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
        uint subsystemId = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
        uint revision = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
        long driverVersion = BinaryPrimitives.ReadInt64LittleEndian(fileBytes.Slice(offset, 8)); offset += 8;
        if (version != FileFormatVersion
            || luid != expectedIdentity.AdapterLuid
            || vendorId != expectedIdentity.VendorId
            || deviceId != expectedIdentity.DeviceId
            || subsystemId != expectedIdentity.SubsystemId
            || revision != expectedIdentity.Revision
            || driverVersion != expectedIdentity.DriverVersion)
            return false;

        int keyCount = BinaryPrimitives.ReadInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
        if (keyCount < 0 || keyCount > MaxKeyCount) return false;
        var parsedKeys = new string[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            if (offset + 4 > payloadEnd) return false;
            int len = BinaryPrimitives.ReadInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
            if (len <= 0 || len > 1024 || offset + len > payloadEnd) return false;
            parsedKeys[i] = Utf8.GetString(fileBytes.Slice(offset, len));
            offset += len;
        }
        if (offset + 4 > payloadEnd) return false;
        int blobLength = BinaryPrimitives.ReadInt32LittleEndian(fileBytes.Slice(offset, 4)); offset += 4;
        if (blobLength <= 0 || offset + blobLength != payloadEnd) return false;

        keys = parsedKeys;
        libraryBlob = fileBytes.Slice(offset, blobLength).ToArray();
        return true;
    }

    internal static string ComputeRootSignatureHash(RootSignatureDescription description, RootSignatureVersion version)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new HashWriter(hash);
        writer.WriteString("MdaRootSigV1");
        writer.WriteInt((int)version);
        writer.WriteInt((int)description.Flags);
        var parameters = description.Parameters;
        writer.WriteInt(parameters?.Length ?? 0);
        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                writer.WriteInt((int)p.ParameterType);
                writer.WriteInt((int)p.ShaderVisibility);
                if (p.ParameterType == RootParameterType.DescriptorTable)
                {
                    var ranges = p.DescriptorTable.Ranges;
                    writer.WriteInt(ranges?.Length ?? 0);
                    if (ranges is not null)
                    {
                        foreach (var r in ranges)
                        {
                            writer.WriteInt((int)r.RangeType);
                            writer.WriteInt(r.NumDescriptors);
                            writer.WriteInt(r.BaseShaderRegister);
                            writer.WriteInt(r.RegisterSpace);
                            writer.WriteInt(r.OffsetInDescriptorsFromTableStart);
                        }
                    }
                }
                else if (p.ParameterType == RootParameterType.Constant32Bits)
                {
                    writer.WriteInt(p.Constants.ShaderRegister);
                    writer.WriteInt(p.Constants.RegisterSpace);
                    writer.WriteInt(p.Constants.Num32BitValues);
                }
                else
                {
                    writer.WriteInt(p.Descriptor.ShaderRegister);
                    writer.WriteInt(p.Descriptor.RegisterSpace);
                }
            }
        }
        var samplers = description.StaticSamplers;
        writer.WriteInt(samplers?.Length ?? 0);
        if (samplers is not null)
        {
            foreach (var s in samplers)
            {
                writer.WriteInt((int)s.Filter);
                writer.WriteInt((int)s.AddressU);
                writer.WriteInt((int)s.AddressV);
                writer.WriteInt((int)s.AddressW);
                writer.WriteFloat(s.MipLODBias);
                writer.WriteInt(s.MaxAnisotropy);
                writer.WriteInt((int)s.ComparisonFunction);
                writer.WriteInt((int)s.BorderColor);
                writer.WriteFloat(s.MinLOD);
                writer.WriteFloat(s.MaxLOD);
                writer.WriteInt(s.ShaderRegister);
                writer.WriteInt(s.RegisterSpace);
                writer.WriteInt((int)s.ShaderVisibility);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string ComputeGraphicsKey(GraphicsPipelineStateDescription desc, string? rootSignatureHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new HashWriter(hash);
        writer.WriteString("MdaGfxPsoV1");
        writer.WriteString(rootSignatureHash ?? "");
        writer.WriteShader(desc.VertexShader.Span);
        writer.WriteShader(desc.PixelShader.Span);
        writer.WriteShader(desc.DomainShader.Span);
        writer.WriteShader(desc.HullShader.Span);
        writer.WriteShader(desc.GeometryShader.Span);

        var elements = desc.InputLayout?.Elements;
        writer.WriteInt(elements?.Length ?? 0);
        if (elements is not null)
        {
            foreach (var e in elements)
            {
                writer.WriteString(e.SemanticName ?? "");
                writer.WriteInt(e.SemanticIndex);
                writer.WriteInt((int)e.Format);
                writer.WriteInt(e.Slot);
                writer.WriteInt(e.AlignedByteOffset);
                writer.WriteInt((int)e.Classification);
                writer.WriteInt(e.InstanceDataStepRate);
            }
        }

        var so = desc.StreamOutput;
        if (so is not null)
        {
            writer.WriteUInt(so.RasterizedStream);
            writer.WriteInt(so.Elements?.Length ?? 0);
            if (so.Elements is not null)
            {
                foreach (var se in so.Elements)
                {
                    writer.WriteInt(se.Stream);
                    writer.WriteString(se.SemanticName ?? "");
                    writer.WriteInt(se.SemanticIndex);
                    writer.WriteByte(se.StartComponent);
                    writer.WriteByte(se.ComponentCount);
                    writer.WriteByte(se.OutputSlot);
                }
            }
            writer.WriteInt(so.Strides?.Length ?? 0);
            if (so.Strides is not null)
                foreach (uint stride in so.Strides) writer.WriteUInt(stride);
        }
        else
        {
            writer.WriteUInt(0);
            writer.WriteInt(0);
            writer.WriteInt(0);
        }

        var blend = desc.BlendState;
        writer.WriteBool(blend.AlphaToCoverageEnable);
        writer.WriteBool(blend.IndependentBlendEnable);
        var rts = blend.RenderTarget;
        writer.WriteInt(rts?.Length ?? 0);
        if (rts is not null)
        {
            foreach (var rt in rts)
            {
                writer.WriteBool(rt.BlendEnable);
                writer.WriteBool(rt.LogicOpEnable);
                writer.WriteInt((int)rt.SrcBlend);
                writer.WriteInt((int)rt.DestBlend);
                writer.WriteInt((int)rt.BlendOp);
                writer.WriteInt((int)rt.SrcBlendAlpha);
                writer.WriteInt((int)rt.DestBlendAlpha);
                writer.WriteInt((int)rt.BlendOpAlpha);
                writer.WriteInt((int)rt.LogicOp);
                writer.WriteByte((byte)rt.RenderTargetWriteMask);
            }
        }

        writer.WriteUInt(desc.SampleMask);

        var raster = desc.RasterizerState;
        writer.WriteInt((int)raster.FillMode);
        writer.WriteInt((int)raster.CullMode);
        writer.WriteBool(raster.FrontCounterClockwise);
        writer.WriteInt(raster.DepthBias);
        writer.WriteFloat(raster.DepthBiasClamp);
        writer.WriteFloat(raster.SlopeScaledDepthBias);
        writer.WriteBool(raster.DepthClipEnable);
        writer.WriteBool(raster.MultisampleEnable);
        writer.WriteBool(raster.AntialiasedLineEnable);
        writer.WriteInt(raster.ForcedSampleCount);
        writer.WriteInt((int)raster.ConservativeRaster);

        var depth = desc.DepthStencilState;
        writer.WriteBool(depth.DepthEnable);
        writer.WriteInt((int)depth.DepthWriteMask);
        writer.WriteInt((int)depth.DepthFunc);
        writer.WriteBool(depth.StencilEnable);
        writer.WriteByte(depth.StencilReadMask);
        writer.WriteByte(depth.StencilWriteMask);
        WriteDepthStencilOp(ref writer, depth.FrontFace);
        WriteDepthStencilOp(ref writer, depth.BackFace);

        writer.WriteInt((int)desc.IndexBufferStripCutValue);
        writer.WriteInt((int)desc.PrimitiveTopologyType);
        var rtFormats = desc.RenderTargetFormats;
        writer.WriteInt(rtFormats?.Length ?? 0);
        if (rtFormats is not null)
            foreach (var f in rtFormats) writer.WriteInt((int)f);
        writer.WriteInt((int)desc.DepthStencilFormat);
        writer.WriteInt(desc.SampleDescription.Count);
        writer.WriteInt(desc.SampleDescription.Quality);
        writer.WriteInt(desc.NodeMask);
        writer.WriteInt((int)desc.Flags);

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string ComputeComputeKey(ComputePipelineStateDescription desc, string? rootSignatureHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new HashWriter(hash);
        writer.WriteString("MdaCmpPsoV1");
        writer.WriteString(rootSignatureHash ?? "");
        writer.WriteShader(desc.ComputeShader.Span);
        writer.WriteUInt(desc.NodeMask);
        writer.WriteInt((int)desc.Flags);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void WriteDepthStencilOp(ref HashWriter writer, DepthStencilOperationDescription op)
    {
        writer.WriteInt((int)op.StencilFailOp);
        writer.WriteInt((int)op.StencilDepthFailOp);
        writer.WriteInt((int)op.StencilPassOp);
        writer.WriteInt((int)op.StencilFunc);
    }

    private static string? ResolveCacheDirectory(string? explicitCacheDirectory)
    {
        try
        {
            string? env = explicitCacheDirectory is null ? Environment.GetEnvironmentVariable("MMD_DXC_CACHE") : null;
            string? workspace = explicitCacheDirectory is null && env is null ? D3D12RuntimePaths.FindWorkspaceRoot() : null;
            string? localData = explicitCacheDirectory is null && env is null && workspace is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null;
            string dir = D3D12RuntimePaths.ResolvePsoCacheDirectory(explicitCacheDirectory, env, workspace, localData);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? TryReadBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > MaxFileBytes) return null;
            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return;
        string tempPath = Path.Combine(directory, $".pso-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private readonly ref struct HashWriter(IncrementalHash hash)
    {
        public void WriteByte(byte value) => hash.AppendData([value]);
        public void WriteBool(bool value) => WriteByte((byte)(value ? 1 : 0));
        public void WriteInt(int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            hash.AppendData(buf);
        }
        public void WriteUInt(uint value) => WriteInt(unchecked((int)value));
        public void WriteFloat(float value) => WriteInt(BitConverter.SingleToInt32Bits(value));
        public void WriteString(string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            WriteInt(bytes.Length);
            hash.AppendData(bytes);
        }
        public void WriteShader(ReadOnlySpan<byte> bytecode)
        {
            WriteInt(bytecode.Length);
            if (!bytecode.IsEmpty) hash.AppendData(SHA256.HashData(bytecode));
        }
    }
}
