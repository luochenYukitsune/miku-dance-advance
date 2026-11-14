using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

public enum D3D12FsrRuntimeStatus
{
    NotInstalled, NotSupported, LoadFailed, QueryFailed, QuerySucceeded, ContextCreationFailed, ContextCreated, Executed
}

/// <summary>Provider IDs and names come unchanged from AMD's runtime; enumeration alone does not prove support.</summary>
public sealed record D3D12FsrProviderProbe(ulong Id, string Name, D3D12FsrRuntimeStatus Status,
    uint? CreateReturnCode = null, uint? ProviderQueryReturnCode = null, ulong? ActualProviderId = null,
    string? ActualProviderName = null, uint? DestroyReturnCode = null);

public sealed record D3D12FsrRuntimeProbeResult(D3D12FsrRuntimeStatus Status, string LoaderPath,
    string? AdapterName, uint? QueryReturnCode, IReadOnlyList<D3D12FsrProviderProbe> Providers,
    D3D12FsrProviderProbe? DefaultContext, string Detail);

/// <summary>
/// Optional AMD FSR SDK 2.3.0 / upscaler API 4.1.1 diagnostics. This never dispatches GPU work.
/// Libraries are loaded only when explicitly called with a fully qualified loader DLL path.
/// </summary>
public static class D3D12FsrRuntimeProbe
{
    private const string LoaderName = "amd_fidelityfx_loader_dx12.dll";
    private const string UpscalerName = "amd_fidelityfx_upscaler_dx12.dll";
    private const ulong UpscaleType = 0x10000;
    private const uint ApiVersion = (4u << 22) | (1u << 12) | 1u;

    public static D3D12FsrRuntimeProbeResult ProbeCurrentDevice(string loaderPath, bool createContexts = true)
    {
        string path = ValidatePath(loaderPath);
        var missing = CheckInstallation(path);
        if (missing is not null) return missing;
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            return Result(D3D12FsrRuntimeStatus.NotSupported, path, "The SDK runtime requires Windows x64.");
        using var runtime = new D3D12GpuRuntime();
        return Probe(runtime.Device, path, runtime.AdapterName, createContexts);
    }

    /// <summary>The caller keeps the real device alive for the entire synchronous probe.</summary>
    public static D3D12FsrRuntimeProbeResult Probe(ID3D12Device device, string loaderPath,
        string? adapterName = null, bool createContexts = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        string path = ValidatePath(loaderPath);
        var missing = CheckInstallation(path);
        if (missing is not null) return missing;
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            return Result(D3D12FsrRuntimeStatus.NotSupported, path, "The SDK runtime requires Windows x64.");
        lock (D3D12FsrNativeLifetime.Gate)
        {
            nint upscaler = 0, loader = 0;
            try
            {
                // Both optional modules are explicit absolute paths. Preloading the effect DLL also
                // prevents the vendor loader's name lookup from resolving a different installation.
                var flags = DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32;
                upscaler = NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(path)!, UpscalerName), typeof(D3D12FsrRuntimeProbe).Assembly, flags);
                loader = NativeLibrary.Load(path, typeof(D3D12FsrRuntimeProbe).Assembly, flags);
                var query = Export<QueryDelegate>(loader, "ffxQuery");
                var create = Export<CreateDelegate>(loader, "ffxCreateContext");
                var destroy = Export<DestroyDelegate>(loader, "ffxDestroyContext");
                using var memory = new NativeScope();
                nint countPointer = memory.Allocate(8);
                Marshal.WriteInt64(countPointer, 0);
                var desc = new GetVersions { Header = new(4), CreateDescType = UpscaleType,
                    Device = device.NativePointer, OutputCount = countPointer };
                nint descPointer = memory.Structure(desc);
                uint code = query(0, descPointer);
                if (code != 0) return Result(D3D12FsrRuntimeStatus.QueryFailed, path, "ffxQuery(GetVersions) count query failed.", adapterName, code);
                ulong count = unchecked((ulong)Marshal.ReadInt64(countPointer));
                if (count == 0) return Result(D3D12FsrRuntimeStatus.NotSupported, path, "The runtime returned no upscale providers for this device.", adapterName, code);
                if (count > 64) return Result(D3D12FsrRuntimeStatus.QueryFailed, path, "Provider count exceeds the diagnostic bound of 64.", adapterName, code);
                nint ids = memory.Allocate(checked((int)count * 8)), names = memory.Allocate(checked((int)count * 8));
                desc.VersionIds = ids; desc.VersionNames = names;
                Marshal.StructureToPtr(desc, descPointer, false);
                code = query(0, descPointer);
                ulong returned = unchecked((ulong)Marshal.ReadInt64(countPointer));
                if (code != 0 || returned > count)
                    return Result(D3D12FsrRuntimeStatus.QueryFailed, path, "ffxQuery(GetVersions) array query failed or exceeded capacity.", adapterName, code);
                var providers = new List<D3D12FsrProviderProbe>();
                for (int i = 0; i < (int)returned; i++)
                {
                    ulong id = unchecked((ulong)Marshal.ReadInt64(ids, i * 8));
                    string name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(names, i * 8)) ?? "";
                    providers.Add(createContexts ? CreateProbe(device.NativePointer, id, name, query, create, destroy)
                        : new(id, name, D3D12FsrRuntimeStatus.QuerySucceeded));
                }
                var defaultContext = createContexts ? CreateProbe(device.NativePointer, null, "Runtime default", query, create, destroy) : null;
                var status = defaultContext?.Status == D3D12FsrRuntimeStatus.ContextCreated
                    ? D3D12FsrRuntimeStatus.ContextCreated : D3D12FsrRuntimeStatus.QuerySucceeded;
                return new(status, path, adapterName, code, providers.AsReadOnly(), defaultContext,
                    "Actual device query; context probes use 64x64 render / 96x96 output. No ffxDispatch call or temporal image output has occurred.");
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { return Result(D3D12FsrRuntimeStatus.LoadFailed, path, e.Message, adapterName); }
            finally
            {
                if (loader != 0) NativeLibrary.Free(loader);
                if (upscaler != 0) NativeLibrary.Free(upscaler);
            }
        }
    }

    private static D3D12FsrProviderProbe CreateProbe(nint device, ulong? id, string name,
        QueryDelegate query, CreateDelegate create, DestroyDelegate destroy)
    {
        using var memory = new NativeScope();
        nint version = memory.Structure(new UpscaleVersion { Header = new(0x1000b), Version = ApiVersion });
        nint backend = memory.Structure(new Backend { Header = new(2, version), Device = device });
        nint next = id.HasValue ? memory.Structure(new OverrideVersion { Header = new(5, backend), VersionId = id.Value }) : backend;
        nint description = memory.Structure(new Upscale { Header = new(UpscaleType, next), Flags = 1u << 5,
            RenderWidth = 64, RenderHeight = 64, OutputWidth = 96, OutputHeight = 96 });
        nint context = 0;
        uint created = create(ref context, description, 0);
        uint? queried = null, destroyed = null;
        ulong? actualId = null; string? actualName = null;
        try
        {
            if (created == 0 && context != 0)
            {
                nint providerDescription = memory.Structure(new ProviderVersion { Header = new(6) });
                nint contextPointer = memory.Allocate(IntPtr.Size);
                Marshal.WriteIntPtr(contextPointer, context);
                queried = query(contextPointer, providerDescription);
                if (queried == 0)
                {
                    var provider = Marshal.PtrToStructure<ProviderVersion>(providerDescription);
                    actualId = provider.VersionId;
                    actualName = Marshal.PtrToStringUTF8(provider.VersionName);
                }
            }
        }
        finally
        {
            if (context != 0) destroyed = destroy(ref context, 0);
        }
        return new(id ?? 0, name, created == 0 && destroyed == 0 ? D3D12FsrRuntimeStatus.ContextCreated
            : D3D12FsrRuntimeStatus.ContextCreationFailed, created, queried, actualId, actualName, destroyed);
    }

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An explicit fully qualified runtime path is required.", nameof(path));
        if (!string.Equals(Path.GetFileName(path), LoaderName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Specify the {LoaderName} entry point.", nameof(path));
        return Path.GetFullPath(path);
    }
    private static D3D12FsrRuntimeProbeResult? CheckInstallation(string path)
        => File.Exists(path) && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, UpscalerName)) ? null
            : Result(D3D12FsrRuntimeStatus.NotInstalled, path, "The optional signed loader and upscaler DLL pair is not installed at the specified path.");
    private static D3D12FsrRuntimeProbeResult Result(D3D12FsrRuntimeStatus status, string path, string detail,
        string? adapterName = null, uint? queryCode = null) => new(status, path, adapterName, queryCode, [], null, detail);
    private static T Export<T>(nint library, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint QueryDelegate(nint context, nint description);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CreateDelegate(ref nint context, nint description, nint callbacks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint DestroyDelegate(ref nint context, nint callbacks);
    [StructLayout(LayoutKind.Sequential)] private struct Header(ulong type, nint next = 0) { public ulong Type = type; public nint Next = next; }
    [StructLayout(LayoutKind.Sequential)] private struct GetVersions { public Header Header; public ulong CreateDescType; public nint Device, OutputCount, VersionIds, VersionNames; }
    [StructLayout(LayoutKind.Sequential)] private struct Backend { public Header Header; public nint Device; }
    [StructLayout(LayoutKind.Sequential)] private struct OverrideVersion { public Header Header; public ulong VersionId; }
    [StructLayout(LayoutKind.Sequential)] private struct UpscaleVersion { public Header Header; public uint Version; }
    [StructLayout(LayoutKind.Sequential)] private struct Upscale { public Header Header; public uint Flags, RenderWidth, RenderHeight, OutputWidth, OutputHeight; public nint Message; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderVersion { public Header Header; public ulong VersionId; public nint VersionName; }
    private sealed class NativeScope : IDisposable
    {
        private readonly List<nint> allocations = [];
        public nint Allocate(int bytes) { nint result = Marshal.AllocHGlobal(bytes); allocations.Add(result); return result; }
        public nint Structure<T>(T value) where T : struct { nint result = Allocate(Marshal.SizeOf<T>()); Marshal.StructureToPtr(value, result, false); return result; }
        public void Dispose() { foreach (nint pointer in allocations) Marshal.FreeHGlobal(pointer); }
    }
}
