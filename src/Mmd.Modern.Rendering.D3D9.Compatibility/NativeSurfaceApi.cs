using System.Runtime.InteropServices;
using System.Text;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

internal sealed class NativeSurfaceApi : ID3D9SurfaceNativeApi
{
    private readonly NativeSurfaceExports exports;
    private readonly Action<nint> unload;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private nint library;
    private ulong liveHandle;
    private bool preview;
    private D3D9SurfaceScope scope = D3D9SurfaceScope.IsolatedSurface;

    internal NativeSurfaceApi(nint library, NativeSurfaceExports exports, Action<nint> unload)
    {
        this.library = library;
        this.exports = exports;
        this.unload = unload;
    }

    internal static NativeSurfaceApi Load(string absolutePath)
    {
        ValidateAbsolutePath(absolutePath);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            IntPtr.Size != 8 || !BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("The D3D9 surface experiment requires a little-endian Windows x64 process.");
        // No PATH/name probing, working-directory fallback or automatic product backend registration.
        var module = NativeLibrary.Load(absolutePath, typeof(NativeSurfaceApi).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        try { return new(module, NativeSurfaceExports.Load(module), NativeLibrary.Free); }
        catch { NativeLibrary.Free(module); throw; }
    }

    internal static void ValidateAbsolutePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("An explicit fully qualified native DLL path is required.", nameof(absolutePath));
    }

    public NativeAbi QueryAbi()
    {
        CheckOwner();
        var abi = new NativeAbi { Size = (uint)Marshal.SizeOf<NativeAbi>(), Version = 1 };
        Invoke("query ABI", (ref NativeError error) => exports.QueryAbi(ref abi, ref error));
        abi.Validate();
        return abi;
    }

    public ulong Create(D3D9SurfaceSessionOptions options)
    {
        CheckOwner();
        if (liveHandle != 0) throw new InvalidOperationException("This DLL wrapper already owns a surface session.");
        var create = new NativeCreate
        {
            Size = (uint)Marshal.SizeOf<NativeCreate>(), Version = 1,
            Width = (uint)options.Width, Height = (uint)options.Height, AdapterOrdinal = options.AdapterOrdinal
        };
        Invoke("create", (ref NativeError error) => exports.Create(in create, out liveHandle, ref error));
        if (liveHandle == 0) throw new InvalidDataException("Native create succeeded with a zero session handle.");
        return liveHandle;
    }

    public bool SupportsPreview => exports.CreatePreview is not null && exports.Present is not null;
    public bool SupportsObjectFx => exports.ConfigureObjectFx is not null;
    public bool SupportsObjectFxResources => exports.ConfigureObjectFxV2 is not null;
    public bool SupportsSceneFx => exports.ConfigureSceneFx is not null;
    public bool SupportsEmm => exports.ConfigureEmm is not null;

    public void ConfigureObjectFx(ulong handle, D3D9ObjectFxConfiguration configuration)
    {
        CheckHandle(handle);
        ArgumentNullException.ThrowIfNull(configuration);
        var resources = configuration.Resources;
        if (resources is null ? !SupportsObjectFx : !SupportsObjectFxResources)
            throw new NotSupportedException(resources is null ? "Native object FX configuration export is unavailable." :
                "Native packaged object FX configuration export is unavailable.");
        ConfigureFx(handle, configuration, D3D9SurfaceScope.ConfiguredObjectFx);
    }

    public void ConfigureSceneFx(ulong handle, D3D9SceneFxConfiguration configuration)
    {
        CheckHandle(handle);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!SupportsSceneFx) throw new NotSupportedException("Native scene FX configuration export is unavailable.");
        ConfigureFx(handle, configuration.CopiedConfiguration, D3D9SurfaceScope.ConfiguredSceneFx);
    }

    public void ConfigureEmm(ulong handle, D3D9EmmSessionConfiguration selected)
    {
        CheckHandle(handle);
        ArgumentNullException.ThrowIfNull(selected);
        if (!SupportsEmm) throw new NotSupportedException("Native EMM configuration export is unavailable.");
        if (scope != D3D9SurfaceScope.IsolatedSurface) throw new InvalidOperationException("The session's FX configuration is immutable.");
        var configuration = selected.Configuration;
        using var pinned = new PinnedEmmTables();
        var bindings = configuration.Objects.Where(row => row.RecordKey is not null).Select(row => new NativeEmmBinding
        {
            Size = (uint)Marshal.SizeOf<NativeEmmBinding>(), Version = 1,
            Key = pinned.Text(row.RecordKey!), ObjectId = selected.NativeObjectIds[row.ObjectId], Kind = row.IsModel ? 1U : 2U
        }).ToArray();
        var roots = configuration.Objects.Select(row => new NativeEmmRootSeed
        {
            Size = (uint)Marshal.SizeOf<NativeEmmRootSeed>(), Version = 1,
            ObjectId = selected.NativeObjectIds[row.ObjectId], Kind = row.IsModel ? 1U : 2U,
            EffectPath = pinned.Text(row.RootSeed.Kind == D3D9EmmRuntimeEffectKind.File ? row.RootSeed.MaterializedAbsolutePath : null)
        }).ToArray();
        var sections = new NativeEmmSection[configuration.Sections.Count];
        var entryCount = 0;
        for (var index = 0; index < sections.Length; index++)
        {
            var section = configuration.Sections[index];
            var entries = new List<NativeEmmEntry>();
            foreach (var entry in section.Entries)
            {
                if (entry.Effect is not null || entry.Shown is not null)
                    entries.Add(Entry(entry.RecordKey, -1, entry.Effect, entry.Shown));
                foreach (var subset in entry.SubsetEffects.Keys.Concat(entry.SubsetShown.Keys).Distinct().Order())
                {
                    entry.SubsetEffects.TryGetValue(subset, out var effect);
                    bool? shown = entry.SubsetShown.TryGetValue(subset, out var value) ? value : null;
                    entries.Add(Entry(entry.RecordKey, subset, effect, shown));
                }
            }
            entryCount = checked(entryCount + entries.Count);
            if (entryCount > 65536)
                throw new ArgumentException("EMM transport is limited to 65536 combined whole-object/subset rows.", nameof(selected));
            sections[index] = new NativeEmmSection
            {
                Size = (uint)Marshal.SizeOf<NativeEmmSection>(), Version = 1,
                Name = pinned.Text(section.Name), BaseName = pinned.Text(section.BaseName),
                OwnerKey = pinned.Text(section.Owner?.RecordKey), ParentTarget = pinned.Text(section.Owner?.ParentTargetLabel),
                DefaultPath = pinned.Text(EffectPath(section.DefaultEffect)), OwnerSubset = section.Owner?.Subset ?? -1,
                Flags = section.DefaultEffect is null ? 0U : 1U,
                Entries = pinned.Array(entries.ToArray()), EntryCount = (ulong)entries.Count
            };
        }
        var resources = configuration.Resources.Bindings.Select(row => new NativeEffectResourceBinding
        {
            Size = (uint)Marshal.SizeOf<NativeEffectResourceBinding>(), Version = 1,
            OriginUtf8 = pinned.Utf8(row.OriginUtf8).Data, OriginLength = (ulong)row.OriginUtf8.Length,
            MaterializedUtf8 = pinned.Utf8(row.MaterializedUtf8).Data, MaterializedLength = (ulong)row.MaterializedUtf8.Length
        }).ToArray();
        var native = new NativeEmmConfiguration
        {
            Size = (uint)Marshal.SizeOf<NativeEmmConfiguration>(), Version = 1,
            Sections = pinned.Array(sections), SectionCount = (ulong)sections.Length,
            Bindings = pinned.Array(bindings), BindingCount = (ulong)bindings.Length,
            RootSeeds = pinned.Array(roots), RootSeedCount = (ulong)roots.Length,
            Resources = pinned.Array(resources), ResourceCount = (ulong)resources.Length
        };
        Invoke("configure EMM", (ref NativeError error) => exports.ConfigureEmm!(handle, in native, ref error));
        scope = D3D9SurfaceScope.ConfiguredEmm;

        NativeEmmEntry Entry(string key, int subset, D3D9EmmRuntimeEffectValue? effect, bool? shown) => new()
        {
            Size = (uint)Marshal.SizeOf<NativeEmmEntry>(), Version = 1, Key = pinned.Text(key), Subset = subset,
            Flags = (effect is null ? 0U : 1U) | (shown.HasValue ? 2U : 0U) | (shown == true ? 4U : 0U),
            Path = pinned.Text(EffectPath(effect))
        };
    }

    private static string? EffectPath(D3D9EmmRuntimeEffectValue? effect)
    {
        if (effect is null) return null;
        return effect.Kind switch
        {
            D3D9EmmRuntimeEffectKind.File => effect.MaterializedAbsolutePath,
            D3D9EmmRuntimeEffectKind.None => "none",
            D3D9EmmRuntimeEffectKind.Hide => "hide",
            D3D9EmmRuntimeEffectKind.MainDefault => "main_default",
            _ => throw new InvalidDataException("Unknown EMM effect value kind.")
        };
    }

    private sealed class PinnedEmmTables : IDisposable
    {
        private readonly List<GCHandle> handles = new();
        private long copiedBytes;
        internal NativeUtf8Span Text(string? value)
        {
            if (string.IsNullOrEmpty(value)) return default;
            return Utf8(D3D9EffectResourcePaths.Utf8.GetBytes(value));
        }
        internal NativeUtf8Span Utf8(byte[] bytes)
        {
            copiedBytes += bytes.Length;
            if (copiedBytes > 16L * 1024 * 1024)
                throw new ArgumentException("EMM transported strings and resource paths exceed the 16 MiB copied-string budget.");
            return new() { Data = Array(bytes), Length = (ulong)bytes.Length };
        }
        internal nint Array<T>(T[] values) where T : struct
        {
            if (values.Length == 0) return 0;
            var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                var address = handle.AddrOfPinnedObject();
                handles.Add(handle);
                return address;
            }
            catch { handle.Free(); throw; }
        }
        public void Dispose()
        {
            foreach (var handle in handles) handle.Free();
            handles.Clear();
        }
    }

    private unsafe void ConfigureFx(ulong handle, D3D9ObjectFxConfiguration configuration, D3D9SurfaceScope requestedScope)
    {
        if (scope != D3D9SurfaceScope.IsolatedSurface) throw new InvalidOperationException("The session's FX configuration is immutable.");
        var resources = configuration.Resources;
        var operation = requestedScope == D3D9SurfaceScope.ConfiguredSceneFx ? "configure scene FX" : "configure object FX";
        var rows = new NativeObjectFxAssignment[configuration.Assignments.Count];
        var resourceRows = new NativeEffectResourceBinding[resources?.Bindings.Count ?? 0];
        var paths = new GCHandle[rows.Length + resourceRows.Length * 2];
        try
        {
            for (var i = 0; i < rows.Length; i++)
            {
                var row = configuration.Assignments[i];
                rows[i] = new NativeObjectFxAssignment
                {
                    Size = (uint)Marshal.SizeOf<NativeObjectFxAssignment>(), Version = 1,
                    ObjectId = row.ObjectId, Subset = row.Subset, Assignment = (uint)row.Assignment
                };
                if (row.Utf8Path is { } bytes)
                {
                    paths[i] = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    rows[i].Utf8Path = paths[i].AddrOfPinnedObject();
                    rows[i].PathLength = (ulong)bytes.Length;
                }
            }
            for (var i = 0; i < resourceRows.Length; i++)
            {
                var row = resources!.Bindings[i];
                var originIndex = rows.Length + i * 2;
                paths[originIndex] = GCHandle.Alloc(row.OriginUtf8, GCHandleType.Pinned);
                paths[originIndex + 1] = GCHandle.Alloc(row.MaterializedUtf8, GCHandleType.Pinned);
                resourceRows[i] = new NativeEffectResourceBinding
                {
                    Size = (uint)Marshal.SizeOf<NativeEffectResourceBinding>(), Version = 1,
                    OriginUtf8 = paths[originIndex].AddrOfPinnedObject(), OriginLength = (ulong)row.OriginUtf8.Length,
                    MaterializedUtf8 = paths[originIndex + 1].AddrOfPinnedObject(), MaterializedLength = (ulong)row.MaterializedUtf8.Length
                };
            }
            fixed (NativeObjectFxAssignment* assignments = rows)
            fixed (NativeEffectResourceBinding* mappings = resourceRows)
            {
                if (resources is null)
                {
                    var native = new NativeObjectFxConfiguration
                    {
                        Size = (uint)Marshal.SizeOf<NativeObjectFxConfiguration>(), Version = 1,
                        Assignments = (nint)assignments, AssignmentCount = (ulong)rows.Length
                    };
                    Invoke(operation, (ref NativeError error) => exports.ConfigureObjectFx!(handle, in native, ref error));
                }
                else
                {
                    var native = new NativeObjectFxConfigurationV2
                    {
                        Size = (uint)Marshal.SizeOf<NativeObjectFxConfigurationV2>(), Version = 2,
                        Assignments = (nint)assignments, AssignmentCount = (ulong)rows.Length,
                        Resources = (nint)mappings, ResourceCount = (ulong)resourceRows.Length
                    };
                    var configure = requestedScope == D3D9SurfaceScope.ConfiguredSceneFx ? exports.ConfigureSceneFx! : exports.ConfigureObjectFxV2!;
                    Invoke(operation, (ref NativeError error) => configure(handle, in native, ref error));
                }
            }
            scope = requestedScope;
        }
        finally
        {
            foreach (var path in paths) if (path.IsAllocated) path.Free();
        }
    }

    public ulong CreatePreview(nint targetHwnd, D3D9SurfaceSessionOptions options)
    {
        CheckOwner();
        if (liveHandle != 0) throw new InvalidOperationException("This DLL wrapper already owns a surface session.");
        if (targetHwnd == 0) throw new ArgumentException("Preview requires a borrowed target HWND.", nameof(targetHwnd));
        if (!SupportsPreview) throw new NotSupportedException("Native surface preview requires both optional CreatePreview and Present exports.");
        var create = new NativeCreatePreview
        {
            Size = (uint)Marshal.SizeOf<NativeCreatePreview>(), Version = 1,
            Width = (uint)options.Width, Height = (uint)options.Height, AdapterOrdinal = options.AdapterOrdinal,
            TargetHwnd = unchecked((ulong)(nuint)targetHwnd)
        };
        Invoke("create preview", (ref NativeError error) => exports.CreatePreview!(in create, out liveHandle, ref error));
        if (liveHandle == 0) throw new InvalidDataException("Native preview create succeeded with a zero session handle.");
        preview = true;
        return liveHandle;
    }

    public unsafe D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
    {
        CheckHandle(handle);
        if (preview) throw new InvalidOperationException("A preview session cannot capture CPU RGBA.");
        ValidateScope(snapshot);
        if (snapshot.PreviewOverlayPrimitiveCount != 0)
            throw new NotSupportedException("Editor overlays are preview-only and cannot enter a capture session.");
        var rgba = new byte[checked(snapshot.Width * snapshot.Height * 4)];
        var result = new NativeCaptureResult { Size = (uint)Marshal.SizeOf<NativeCaptureResult>(), Version = 1 };
        fixed (byte* frame = snapshot.FrameBytes, visual = snapshot.VisualBytes, output = rgba)
        {
            var capture = new NativeCapture
            {
                Size = (uint)Marshal.SizeOf<NativeCapture>(), Version = 1,
                FrameBytes = (nint)frame, FrameLength = (ulong)snapshot.FrameBytes.Length,
                VisualBytes = (nint)visual, VisualLength = (ulong)snapshot.VisualBytes.Length,
                RequiredFeatures = snapshot.RequiredFeatures, Flags = (uint)snapshot.Scope, RenderOrder = (uint)order,
                Width = (uint)snapshot.Width, Height = (uint)snapshot.Height,
                Rgba = (nint)output, RgbaCapacity = (ulong)rgba.Length, RgbaStride = (ulong)snapshot.Width * 4
            };
            Invoke("capture", (ref NativeError error) => exports.Capture(handle, in capture, ref result, ref error));
        }
        var expectedFlags = ResultScopeFlag;
        if (result.Size < Marshal.SizeOf<NativeCaptureResult>() || result.Version != 1 || result.Reserved != 0 ||
            result.Width != snapshot.Width || result.Height != snapshot.Height || result.SourceFrame != snapshot.SourceFrame ||
            result.RgbaBytesRequired != (ulong)rgba.Length || (result.Flags & expectedFlags) != expectedFlags ||
            (result.Flags & ~(expectedFlags | 2U)) != 0)
            throw new InvalidDataException("Native capture returned incompatible result metadata or output extent.");
        return new(snapshot.Width, snapshot.Height, rgba, result);
    }

    public unsafe D3D9SurfacePresentation Present(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
    {
        CheckHandle(handle);
        if (!preview) throw new InvalidOperationException("A capture session cannot present to a borrowed window.");
        ValidateScope(snapshot);
        var result = new NativePresentResult { Size = (uint)Marshal.SizeOf<NativePresentResult>(), Version = 1 };
        fixed (byte* frame = snapshot.FrameBytes, visual = snapshot.VisualBytes)
        fixed (NativeEditorOverlayPrimitive* primitives = snapshot.PreviewOverlayPrimitives)
        {
            var present = new NativePresent
            {
                Size = (uint)Marshal.SizeOf<NativePresent>(), Version = 1,
                FrameBytes = (nint)frame, FrameLength = (ulong)snapshot.FrameBytes.Length,
                VisualBytes = (nint)visual, VisualLength = (ulong)snapshot.VisualBytes.Length,
                RequiredFeatures = snapshot.RequiredFeatures | 128UL, Flags = (uint)snapshot.Scope, RenderOrder = (uint)order,
                Width = (uint)snapshot.Width, Height = (uint)snapshot.Height
            };
            if (snapshot.PreviewOverlayPrimitiveCount == 0)
                Invoke("present", (ref NativeError error) => exports.Present!(handle, in present, ref result, ref error));
            else
            {
                if (exports.PresentWithOverlay is null)
                    throw new NotSupportedException("This native Classic component does not support editor overlays. Install the matching component version.");
                present.Size = (uint)Marshal.SizeOf<NativePresentWithOverlay>();
                var extended = new NativePresentWithOverlay
                {
                    Present = present, Primitives = (nint)primitives,
                    PrimitiveCount = (ulong)snapshot.PreviewOverlayPrimitiveCount
                };
                Invoke("present", (ref NativeError error) => exports.PresentWithOverlay(handle, in extended, ref result, ref error));
            }
        }
        var expectedFlags = ResultScopeFlag | 4U;
        if (result.Size < Marshal.SizeOf<NativePresentResult>() || result.Version != 1 || result.Reserved != 0 ||
            result.Width != snapshot.Width || result.Height != snapshot.Height || result.SourceFrame != snapshot.SourceFrame ||
            result.ReadbackBytes != 0 || (result.Flags & expectedFlags) != expectedFlags ||
            (result.Flags & ~(expectedFlags | 2U)) != 0)
            throw new InvalidDataException("Native present returned incompatible result metadata or output extent.");
        return new(result);
    }

    public void Resize(ulong handle, int width, int height)
    {
        CheckHandle(handle);
        var resize = new NativeResize { Size = (uint)Marshal.SizeOf<NativeResize>(), Version = 1, Width = (uint)width, Height = (uint)height };
        Invoke("resize", (ref NativeError error) => exports.Resize(handle, in resize, ref error));
    }

    public void Destroy(ulong handle)
    {
        CheckHandle(handle);
        Invoke("destroy", (ref NativeError error) => exports.Destroy(handle, ref error));
        liveHandle = 0; // A failed Destroy retains both ownership and its DLL reference.
        scope = D3D9SurfaceScope.IsolatedSurface;
        preview = false;
    }

    public void Dispose()
    {
        if (library == 0) return;
        CheckOwner();
        // Also handles a contract-breaking Create that reports failure with a nonzero handle.
        if (liveHandle != 0) Destroy(liveHandle);
        unload(library);
        library = 0;
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Native D3D9 surface calls must stay on their dedicated owner Thread.");
        ObjectDisposedException.ThrowIf(library == 0, this);
    }

    private void CheckHandle(ulong handle)
    {
        CheckOwner();
        if (handle == 0 || handle != liveHandle) throw new InvalidOperationException("Invalid D3D9 surface session ownership.");
    }

    private void ValidateScope(D3D9SurfaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Scope != scope) throw new InvalidOperationException("Snapshot scope does not match the session's immutable FX configuration.");
    }

    private uint ResultScopeFlag => scope switch
    {
        D3D9SurfaceScope.ConfiguredObjectFx => 8U,
        D3D9SurfaceScope.ConfiguredSceneFx => 16U,
        D3D9SurfaceScope.ConfiguredEmm => 32U,
        _ => 1U
    };

    private delegate int NativeOperation(ref NativeError error);

    private static unsafe void Invoke(string operation, NativeOperation call)
    {
        // Do not retry an operation just to fetch a longer error: it could mutate the live device twice.
        var bytes = new byte[4096];
        fixed (byte* utf8 = bytes)
        {
            var error = new NativeError
            {
                Size = (uint)Marshal.SizeOf<NativeError>(), Version = 1, Utf8 = (nint)utf8, Capacity = (ulong)bytes.Length
            };
            var status = call(ref error);
            if (status != 0)
            {
                var end = Array.IndexOf(bytes, (byte)0);
                var diagnostic = Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
                if (string.IsNullOrEmpty(diagnostic)) diagnostic = "Native call supplied no UTF-8 diagnostic.";
                if (error.Status != status) diagnostic += $" [error.status={error.Status}, return status={status}]";
                throw new D3D9SurfaceNativeException(operation, status, diagnostic, error.RequiredBytes,
                    error.RequiredBytes > (ulong)bytes.Length || end < 0);
            }
            if (error.Size < Marshal.SizeOf<NativeError>() || error.Version != 1 || error.Reserved != 0 || error.Status != 0)
                throw new InvalidDataException($"Native {operation} returned an incompatible error structure.");
        }
    }
}
