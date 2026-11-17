using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// CPU-only compiler adapter for explicit frozen sources. The two Microsoft modules must be the already
/// verified x64 runtime; no device, MME session, original-source fallback or product backend is created.
/// </summary>
public sealed partial class D3D9NativeEffectDependencyInspector : ID3D9EffectDependencyInspector, IDisposable
{
    private const string D3dxSha = "36b84dfa0ef9d3ff3549a7ad54d2a8032bd22c879219ee1a959137c4ef8786c4";
    private const string CompilerSha = "ffedb064adc25328b24dcf145b04045a867a5574c931516d7845babf2a08937f";
    private readonly object gate = new();
    private nint library, d3dxModule, compilerModule;
    private readonly string d3dxPath, compilerPath;
    private readonly InspectCall inspect;
    private readonly InfoCall getInfo;
    private readonly ProfileCall getProfile;
    private readonly ParameterCall getParameter;
    private readonly IncludeCall getInclude;
    private readonly ReferenceCall getReference;
    private readonly DiagnosticCall getDiagnostic;
    private readonly DestroyCall destroy;
    private readonly AnnotationCall getAnnotation;

    public D3D9NativeEffectDependencyInspector(string nativeAbsolutePath, string d3dxAbsolutePath, string compilerAbsolutePath)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Effect compiler inspection requires little-endian Windows x64.");
        NativeSurfaceApi.ValidateAbsolutePath(nativeAbsolutePath);
        NativeSurfaceApi.ValidateAbsolutePath(d3dxAbsolutePath);
        NativeSurfaceApi.ValidateAbsolutePath(compilerAbsolutePath);
        d3dxPath = Path.GetFullPath(d3dxAbsolutePath); compilerPath = Path.GetFullPath(compilerAbsolutePath);
        // Hold the exact files against write/delete between hashing and module loading.
        using var d3dxFile = new FileStream(d3dxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var compilerFile = new FileStream(compilerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(d3dxFile)) != D3dxSha ||
            Convert.ToHexStringLower(SHA256.HashData(compilerFile)) != CompilerSha)
            throw new InvalidDataException("The explicit Microsoft compiler runtime differs from the verified x64 inputs.");
        try
        {
            compilerModule = LoadModule(compilerPath); d3dxModule = LoadModule(d3dxPath);
            VerifySelectedModule("D3DCompiler_43.dll", compilerPath); VerifySelectedModule("D3DX9_43.dll", d3dxPath);
            library = LoadModule(nativeAbsolutePath);
            inspect = Export<InspectCall>("MdaD3D9InspectDependenciesV1"); getInfo = Export<InfoCall>("MdaD3D9DependencyInfoV1");
            getProfile = Export<ProfileCall>("MdaD3D9DependencyProfileV1"); getParameter = Export<ParameterCall>("MdaD3D9DependencyParameterV1");
            getInclude = Export<IncludeCall>("MdaD3D9DependencyIncludeV1"); getReference = Export<ReferenceCall>("MdaD3D9DependencyReferenceV1");
            getDiagnostic = Export<DiagnosticCall>("MdaD3D9DependencyDiagnosticV1"); destroy = Export<DestroyCall>("MdaD3D9DestroyDependencyInspectionV1");
            getAnnotation = Export<AnnotationCall>("MdaD3D9DependencyAnnotationV1");
            if (NativeLibrary.TryGetExport(library, "MdaD3D9DiscoverDependencyRootV1", out nint discovery))
                discoverRoot = Marshal.GetDelegateForFunctionPointer<RootDiscoverCall>(discovery);
        }
        catch { Unload(); throw; }
    }

    public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
        Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topFx); ArgumentNullException.ThrowIfNull(captureInclude);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(library == 0, this); cancellationToken.ThrowIfCancellationRequested();
            int codePage = checked((int)GetACP());
            Encoding acp = StrictEncoding(codePage);
            byte[] origin = acp.GetBytes(topFx.OriginAbsolutePath);
            if (acp.GetString(origin) != topFx.OriginAbsolutePath || origin.Length >= 260 || origin.Contains((byte)0))
                throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported,
                    "compiler-origin", "The top effect origin must round-trip through the native ACP within MAX_PATH.", topFx.OriginAbsolutePath);
            var resources = new Dictionary<ulong, string> { [1] = topFx.OriginAbsolutePath };
            var resourceIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { [topFx.OriginAbsolutePath] = 1 };
            ExceptionDispatchInfo? callbackFailure = null;
            using var pins = new Pins();
            ResolveCall resolve = (nint _, in IncludeRequest request, ref IncludeResponse response) =>
            {
                try
                {
                    callbackFailure?.Throw(); cancellationToken.ThrowIfCancellationRequested();
                    if (request.Size < Marshal.SizeOf<IncludeRequest>() || request.Version != 1 || request.RootResourceId != 1 ||
                        request.Kind > 1 || request.Depth is < 1 or > 128)
                        throw new InvalidDataException("Native include request has an incompatible shape.");
                    string? parent = request.ParentResourceId == 0 ? null : resources.TryGetValue(request.ParentResourceId, out var value) ? value :
                        throw new InvalidDataException("Native include parent is not a frozen resource identity.");
                    var frozen = captureInclude(new(ProfileId(request.ProfileKind, request.Entry, request.Target), request.Kind == 1,
                        parent, checked((int)request.Depth), new(Bytes(request.RawRequest, 32767), codePage)));
                    if (!resourceIds.TryGetValue(frozen.OriginAbsolutePath, out ulong id))
                    {
                        if (resources.Count == 65536) throw new InvalidDataException("Native include identity budget exceeded.");
                        id = checked((ulong)resources.Count + 1); resourceIds.Add(frozen.OriginAbsolutePath, id); resources.Add(id, frozen.OriginAbsolutePath);
                    }
                    response.Size = (uint)Marshal.SizeOf<IncludeResponse>(); response.Version = 1;
                    response.ResourceId = id; response.SourceBytes = pins.Add(frozen.CopyBytes()); return 0;
                }
                catch (Exception error)
                {
                    callbackFailure ??= ExceptionDispatchInfo.Capture(error);
                    response.Size = (uint)Marshal.SizeOf<IncludeResponse>(); response.Version = 1;
                    response.Error = pins.Add(Encoding.UTF8.GetBytes("Managed include capture failed; the original exception is retained."));
                    return -10;
                }
            };
            var input = new InspectRequest { Size = (uint)Marshal.SizeOf<InspectRequest>(), Version = 1, RootResourceId = 1,
                RootOrigin = pins.Add(origin), RootSource = pins.Add(topFx.CopyBytes()), Resolve = Marshal.GetFunctionPointerForDelegate(resolve) };
            byte[] errorBuffer = new byte[8192]; var errorSpan = pins.Add(errorBuffer);
            var error = new NativeError { Size = (uint)Marshal.SizeOf<NativeError>(), Version = 1, Utf8 = errorSpan.Data, Capacity = errorSpan.Length };
            ulong handle = 0;
            try
            {
                int status = inspect(in input, out handle, ref error);
                callbackFailure?.Throw(); cancellationToken.ThrowIfCancellationRequested();
                Check(status, Encoding.UTF8.GetString(errorBuffer.AsSpan(0, Array.IndexOf(errorBuffer, (byte)0) is int end && end >= 0 ? end : errorBuffer.Length)));
                if (handle == 0) throw new InvalidDataException("Native inspection returned no immutable report.");
                var info = new Info { Size = (uint)Marshal.SizeOf<Info>(), Version = 1 }; Check(getInfo(handle, ref info));
                if (info.CodePage != codePage || info.PointerBits != 64 || info.RootResourceId != 1 || info.Reserved != 0 ||
                    info.ProfileCount is < 2 or > 4098 || info.ParameterCount > 65536 || info.AnnotationCount > 262144 ||
                    info.IncludeCount > 65536 || info.ReferenceCount > 262144 || info.DiagnosticCount > 65536 ||
                    !SamePath(Text(info.D3dxPath, Encoding.UTF8), d3dxPath) || !SamePath(Text(info.CompilerPath, Encoding.UTF8), compilerPath))
                    throw new InvalidDataException("Native compiler identity or report bounds differ from the frozen runtime.");
                var profiles = new List<D3D9EffectDependencyProfile>(); var profileIds = new Dictionary<ulong, string>();
                var diagnostics = new List<D3D9EffectDependencyInspectorDiagnostic>();
                for (ulong index = 0; index < info.ProfileCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var profile = new Profile { Size = (uint)Marshal.SizeOf<Profile>(), Version = 1 }; Check(getProfile(handle, index, ref profile));
                    string id = ProfileId(profile.Kind, profile.Entry, profile.Target); profileIds.Add(index, id);
                    D3D9EffectDependencyMacro[] macros = profile.Kind == 3 ? [] : profile.Kind == 1 ?
                        [new("_INDEX", "PSIZE15")] : [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")];
                    profiles.Add(new(id, profile.Flags, macros, profile.Kind == 3 ? Text(profile.Entry, acp) : null,
                        profile.Kind == 3 ? Text(profile.Target, acp) : null));
                    if (profile.Status != 0) diagnostics.Add(new("compiler-profile", "A required compiler profile failed.", id, HResult: profile.HResult));
                }
                string Id(ulong index) => profileIds.TryGetValue(index, out string? id) ? id : throw new InvalidDataException("Unknown native profile index.");
                var parameterNames = new Dictionary<ulong, string>();
                string ParameterName(ulong index, int depth = 0)
                {
                    if (index >= info.ParameterCount || depth > 128) throw new InvalidDataException("Unknown or cyclic native parameter index.");
                    if (parameterNames.TryGetValue(index, out string? name)) return name;
                    var parameter = new Parameter { Size = (uint)Marshal.SizeOf<Parameter>(), Version = 1 };
                    Check(getParameter(handle, index, ref parameter)); name = Text(parameter.Name, acp);
                    if (parameter.ParentIndex != ulong.MaxValue)
                        name = ParameterName(parameter.ParentIndex, depth + 1) + (parameter.ArrayElement == uint.MaxValue ? "." + name : "[" + parameter.ArrayElement + "]");
                    parameterNames.Add(index, name); return name;
                }
                var references = new List<D3D9EffectDependencySemanticReference>();
                for (ulong index = 0; index < info.ReferenceCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reference = new Reference { Size = (uint)Marshal.SizeOf<Reference>(), Version = 1 }; Check(getReference(handle, index, ref reference));
                    var kind = reference.Kind switch { 1 or 2 or 3 => D3D9EffectDependencyReferenceKind.Resource,
                        4 => D3D9EffectDependencyReferenceKind.Effect, 5 or 6 or 7 => D3D9EffectDependencyReferenceKind.Symbol,
                        8 => D3D9EffectDependencyReferenceKind.TextureFunction, _ => throw new InvalidDataException("Unknown native resource semantic.") };
                    D3D9EffectDependencySymbol? symbol = reference.Kind switch { 5 => D3D9EffectDependencySymbol.None,
                        6 => D3D9EffectDependencySymbol.Hide, 7 => D3D9EffectDependencySymbol.MainDefault, _ => null };
                    references.Add(new(Id(reference.ProfileIndex), kind, new(Bytes(reference.Value, 32767), codePage),
                        ParameterName(reference.ParameterIndex), Text(reference.Annotation, acp),
                        reference.RowOrdinal == uint.MaxValue ? null : checked((int)reference.RowOrdinal), Text(reference.Key, acp),
                        new(Bytes(reference.Raw, 32767), codePage), symbol,
                        reference.Kind == 8 ? Text(reference.Value, acp) : null, reference.Kind == 8 ? Text(reference.Target, acp) : null));
                }
                // 离屏目标声明：宿主（sas_interpreter）只遍历顶层参数，且跳过没有任何注解的字面量参数。
                var annotationCounts = new Dictionary<ulong, int>();
                for (ulong index = 0; index < info.AnnotationCount; index++)
                {
                    var annotation = new Annotation { Size = (uint)Marshal.SizeOf<Annotation>(), Version = 1 };
                    Check(getAnnotation(handle, index, ref annotation));
                    annotationCounts[annotation.ParameterIndex] = annotationCounts.GetValueOrDefault(annotation.ParameterIndex) + 1;
                }
                var offscreenTargets = new List<D3D9EffectOffscreenDeclaration>();
                for (ulong index = 0; index < info.ParameterCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parameter = new Parameter { Size = (uint)Marshal.SizeOf<Parameter>(), Version = 1 };
                    Check(getParameter(handle, index, ref parameter));
                    if (parameter.SemanticId != 0x2E || parameter.ParentIndex != ulong.MaxValue) continue;
                    if (annotationCounts.GetValueOrDefault(index) == 0 && (parameter.Flags & 1) != 0) continue;
                    offscreenTargets.Add(new(Id(parameter.ProfileIndex), Text(parameter.Name, acp)));
                }
                for (ulong index = 0; index < info.IncludeCount; index++)
                {
                    var include = new Include { Size = (uint)Marshal.SizeOf<Include>(), Version = 1 }; Check(getInclude(handle, index, ref include));
                    if (include.Status == 0 && include.Closed != 1) throw new InvalidDataException("Native compiler retained an unclosed include buffer.");
                    if (include.Status != 0)
                    {
                        byte[] bytes = Bytes(include.Error, 32767);
                        diagnostics.Add(new("compiler-include", bytes.Length == 0 ? "A compiler include request failed." :
                            Encoding.UTF8.GetString(bytes), Id(include.ProfileIndex), RawMessage: new(bytes, 65001)));
                    }
                }
                for (ulong index = 0; index < info.DiagnosticCount; index++)
                {
                    var diagnostic = new Diagnostic { Size = (uint)Marshal.SizeOf<Diagnostic>(), Version = 1 };
                    Check(getDiagnostic(handle, index, ref diagnostic));
                    byte[] bytes = Bytes(diagnostic.Message, 32 * 1024 * 1024); Encoding encoding = StrictEncoding(checked((int)diagnostic.CodePage));
                    diagnostics.Add(new("native-" + diagnostic.Kind, encoding.GetString(bytes).TrimEnd('\0'),
                        diagnostic.ProfileIndex == ulong.MaxValue ? null : Id(diagnostic.ProfileIndex),
                        diagnostic.ParameterIndex == ulong.MaxValue ? null : ParameterName(diagnostic.ParameterIndex),
                        Text(diagnostic.Annotation, acp), new(bytes, checked((int)diagnostic.CodePage)), diagnostic.HResult));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new(info.Status == 0 ? D3D9EffectDependencyStatus.Complete : info.Status == -6 ?
                    D3D9EffectDependencyStatus.Unsupported : D3D9EffectDependencyStatus.Incomplete,
                    new("mda.mme.resource-semantics/1", D3dxSha, 64, codePage, CompilerSha), profiles, references, diagnostics, offscreenTargets);
            }
            finally
            {
                if (handle != 0) Check(destroy(handle));
                GC.KeepAlive(resolve);
            }
        }
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static nint LoadModule(string path) => NativeLibrary.Load(path, typeof(D3D9NativeEffectDependencyInspector).Assembly,
        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
    private static Encoding StrictEncoding(int codePage) => codePage == 65001 ? new UTF8Encoding(false, true) :
        CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? throw new InvalidDataException("Native compiler code page is unsupported.");
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void VerifySelectedModule(string name, string expected)
    {
        nint module = GetModuleHandleW(name); var path = new StringBuilder(32768);
        uint length = module == 0 ? 0 : GetModuleFileNameW(module, path, path.Capacity);
        if (length == 0 || length >= path.Capacity || !SamePath(path.ToString(), expected))
            throw new InvalidDataException("A different same-named compiler module is already selected by this process.");
    }
    private static byte[] Bytes(ByteSpan span, int maximum)
    {
        if (span.Length > (ulong)maximum || span.Data == 0 && span.Length != 0) throw new InvalidDataException("Native report span exceeds its bound.");
        byte[] bytes = new byte[checked((int)span.Length)]; if (bytes.Length != 0) Marshal.Copy(span.Data, bytes, 0, bytes.Length); return bytes;
    }
    private static string Text(ByteSpan span, Encoding encoding) => encoding.GetString(Bytes(span, 32767));
    private static string ProfileId(uint kind, ByteSpan entry, ByteSpan target)
    {
        if (kind is 1 or 2) return kind == 1 ? "effect-mip-off" : "effect-mip-on";
        if (kind != 3) throw new InvalidDataException("Unknown native compiler profile.");
        byte[] first = Bytes(entry, 32767), second = Bytes(target, 32767), data = new byte[16 + first.Length + second.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(data, (ulong)first.Length); first.CopyTo(data, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8 + first.Length), (ulong)second.Length); second.CopyTo(data, 16 + first.Length);
        return "texture-function-" + Convert.ToHexStringLower(SHA256.HashData(data));
    }
    private static void Check(int status, string? message = null)
    {
        if (status == 0) return;
        throw new D3D9EffectDependencyException(status == -6 ? D3D9EffectDependencyStatus.Unsupported : D3D9EffectDependencyStatus.Incomplete,
            "native-inspection", string.IsNullOrWhiteSpace(message) ? "Native effect inspection failed with status " + status + "." : message);
    }
    private void Unload()
    {
        if (library != 0) NativeLibrary.Free(library); library = 0;
        if (d3dxModule != 0) NativeLibrary.Free(d3dxModule); d3dxModule = 0;
        if (compilerModule != 0) NativeLibrary.Free(compilerModule); compilerModule = 0;
    }
    public void Dispose() { lock (gate) Unload(); }

    private sealed class Pins : IDisposable
    {
        private readonly List<GCHandle> handles = [];
        internal ByteSpan Add(byte[] bytes)
        {
            if (bytes.Length == 0) return default;
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned); handles.Add(handle); return new() { Data = handle.AddrOfPinnedObject(), Length = (ulong)bytes.Length };
        }
        public void Dispose() { foreach (var handle in handles) handle.Free(); handles.Clear(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ByteSpan { internal nint Data; internal ulong Length; }
    [StructLayout(LayoutKind.Sequential)] private struct InspectRequest { internal uint Size, Version; internal ulong RootResourceId; internal ByteSpan RootOrigin, RootSource; internal nint Resolve, Context; internal uint Reserved0, Reserved1; }
    [StructLayout(LayoutKind.Sequential)] private struct IncludeRequest { internal uint Size, Version; internal ulong RootResourceId, ParentResourceId, ProfileIndex; internal uint Kind, Depth, ProfileKind, Reserved; internal ByteSpan RawRequest, Entry, Target; }
    [StructLayout(LayoutKind.Sequential)] private struct IncludeResponse { internal uint Size, Version; internal ulong ResourceId; internal ByteSpan SourceBytes, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { internal uint Size, Version; internal int Status; internal uint CodePage, PointerBits, Reserved; internal ulong RootResourceId, ProfileCount, ParameterCount, AnnotationCount, IncludeCount, ReferenceCount, DiagnosticCount; internal ByteSpan D3dxPath, CompilerPath; }
    [StructLayout(LayoutKind.Sequential)] private struct Profile { internal uint Size, Version, Kind, Flags; internal int Status, HResult; internal ulong SourceParameterIndex; internal ByteSpan Entry, Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Parameter { internal uint Size, Version; internal ulong ProfileIndex, ParentIndex; internal uint Ordinal, ArrayElement, Class, Type, Rows, Columns, Elements, StructMembers, Flags, SemanticId; internal ByteSpan Name, Semantic; }
    [StructLayout(LayoutKind.Sequential)] private struct Annotation { internal uint Size, Version; internal ulong ParameterIndex; internal uint Ordinal, ParameterType; internal int StringHResult; internal uint Reserved; internal ByteSpan Name, Value; }
    [StructLayout(LayoutKind.Sequential)] private struct Include { internal uint Size, Version; internal ulong ProfileIndex, RootId, ParentId, ResourceId; internal uint Kind, Depth, RootOpen, Closed; internal int Status; internal uint Reserved; internal ByteSpan Raw, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct Reference { internal uint Size, Version; internal ulong ProfileIndex, ParameterIndex; internal uint Kind, RowOrdinal; internal ulong RawOffset; internal ByteSpan Annotation, Raw, Key, Value, Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Diagnostic { internal uint Size, Version; internal ulong ProfileIndex, ParameterIndex; internal uint Kind, CodePage; internal int Status, HResult; internal ByteSpan Annotation, Message; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResolveCall(nint context, in IncludeRequest request, ref IncludeResponse response);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InspectCall(in InspectRequest request, out ulong handle, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InfoCall(ulong handle, ref Info info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProfileCall(ulong handle, ulong index, ref Profile value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ParameterCall(ulong handle, ulong index, ref Parameter value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AnnotationCall(ulong handle, ulong index, ref Annotation value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IncludeCall(ulong handle, ulong index, ref Include value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReferenceCall(ulong handle, ulong index, ref Reference value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DiagnosticCall(ulong handle, ulong index, ref Diagnostic value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DestroyCall(ulong handle);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetACP();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint GetModuleFileNameW(nint module, StringBuilder filename, int capacity);
}
