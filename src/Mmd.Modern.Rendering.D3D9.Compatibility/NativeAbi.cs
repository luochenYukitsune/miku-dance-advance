using System.Runtime.InteropServices;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

// Native alignment, Windows x64. Keep in lockstep with native/.../mda_d3d9_session.h.
[StructLayout(LayoutKind.Sequential)]
internal struct NativeError
{
    internal uint Size, Version;
    internal nint Utf8;
    internal ulong Capacity, RequiredBytes;
    internal int Status;
    internal uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAbi
{
    internal uint Size, Version, AbiVersion, PointerBits, MaxWidth, MaxHeight;
    internal ulong SupportedFeatures;
    internal uint Reserved0, Reserved1;

    internal static NativeAbi Valid() => new()
    {
        Size = 40, Version = 1, AbiVersion = 1, PointerBits = 64, MaxWidth = 4096, MaxHeight = 4096, SupportedFeatures = 1
    };

    internal readonly void Validate()
    {
        if (Size < 40 || Version != 1 || AbiVersion != 1 || PointerBits != 64)
            throw new NotSupportedException($"D3D9 surface ABI mismatch: size={Size}, version={Version}, ABI={AbiVersion}, pointerBits={PointerBits}.");
        if (MaxWidth is < 1 or > 4096 || MaxHeight is < 1 or > 4096 || (SupportedFeatures & 1UL) == 0 ||
            (SupportedFeatures & ~32767UL) != 0 || Reserved0 != 0 || Reserved1 != 0)
            throw new NotSupportedException($"D3D9 surface capabilities are incompatible: extent={MaxWidth}x{MaxHeight}, features=0x{SupportedFeatures:X}, reserved={Reserved0}/{Reserved1}.");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCreate
{
    internal uint Size, Version, Width, Height, AdapterOrdinal, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResize
{
    internal uint Size, Version, Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCapture
{
    internal uint Size, Version;
    internal nint FrameBytes;
    internal ulong FrameLength;
    internal nint VisualBytes;
    internal ulong VisualLength, RequiredFeatures;
    internal uint Flags, RenderOrder, Width, Height;
    internal nint Rgba;
    internal ulong RgbaCapacity, RgbaStride;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCaptureResult
{
    internal uint Size, Version, Width, Height;
    internal double SourceFrame;
    internal ulong CompletedCaptures, GeometryGeneration, RgbaBytesRequired;
    internal uint DrawCalls, Flags, EffectDispatches, EffectPassDraws, EffectPassFailures, RawFallbackDraws, NoUiDiagnostics, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCreatePreview
{
    internal uint Size, Version, Width, Height, AdapterOrdinal, Flags;
    internal ulong TargetHwnd;
    internal uint Reserved0, Reserved1, Reserved2, Reserved3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePresent
{
    internal uint Size, Version;
    internal nint FrameBytes;
    internal ulong FrameLength;
    internal nint VisualBytes;
    internal ulong VisualLength, RequiredFeatures;
    internal uint Flags, RenderOrder, Width, Height, Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePresentResult
{
    internal uint Size, Version, Width, Height;
    internal double SourceFrame;
    internal ulong CompletedPresents, GeometryGeneration, ReadbackBytes;
    internal uint DrawCalls, Flags, EffectDispatches, EffectPassDraws, EffectPassFailures, RawFallbackDraws, NoUiDiagnostics, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePresentWithOverlay
{
    internal NativePresent Present;
    internal nint Primitives;
    internal ulong PrimitiveCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeObjectFxAssignment
{
    internal uint Size, Version;
    internal ulong ObjectId;
    internal int Subset;
    internal uint Assignment;
    internal nint Utf8Path;
    internal ulong PathLength;
    internal uint Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeObjectFxConfiguration
{
    internal uint Size, Version;
    internal nint Assignments;
    internal ulong AssignmentCount;
    internal uint Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEffectResourceBinding
{
    internal uint Size, Version;
    internal nint OriginUtf8;
    internal ulong OriginLength;
    internal nint MaterializedUtf8;
    internal ulong MaterializedLength;
    internal uint Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeObjectFxConfigurationV2
{
    internal uint Size, Version;
    internal nint Assignments;
    internal ulong AssignmentCount;
    internal nint Resources;
    internal ulong ResourceCount;
    internal uint Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeUtf8Span
{
    internal nint Data;
    internal ulong Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEmmBinding
{
    internal uint Size, Version;
    internal NativeUtf8Span Key;
    internal ulong ObjectId;
    internal uint Kind, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEmmRootSeed
{
    internal uint Size, Version;
    internal ulong ObjectId;
    internal uint Kind, Reserved;
    internal NativeUtf8Span EffectPath;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEmmEntry
{
    internal uint Size, Version;
    internal NativeUtf8Span Key;
    internal int Subset;
    internal uint Flags;
    internal NativeUtf8Span Path;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEmmSection
{
    internal uint Size, Version;
    internal NativeUtf8Span Name, BaseName, OwnerKey, ParentTarget, DefaultPath;
    internal int OwnerSubset;
    internal uint Flags;
    internal nint Entries;
    internal ulong EntryCount;
    internal uint Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEmmConfiguration
{
    internal uint Size, Version;
    internal nint Sections;
    internal ulong SectionCount;
    internal nint Bindings;
    internal ulong BindingCount;
    internal nint RootSeeds;
    internal ulong RootSeedCount;
    internal nint Resources;
    internal ulong ResourceCount;
    internal uint Reserved0, Reserved1;
}

internal interface ID3D9SurfaceNativeApi : IDisposable
{
    NativeAbi QueryAbi();
    ulong Create(D3D9SurfaceSessionOptions options);
    D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order);
    // Optional contract: existing capture-only implementations need neither new entry point.
    bool SupportsPreview => false;
    ulong CreatePreview(nint targetHwnd, D3D9SurfaceSessionOptions options) =>
        throw new NotSupportedException("Native surface preview exports are unavailable.");
    D3D9SurfacePresentation Present(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order) =>
        throw new NotSupportedException("Native surface preview exports are unavailable.");
    bool SupportsObjectFx => false;
    bool SupportsObjectFxResources => false;
    void ConfigureObjectFx(ulong handle, D3D9ObjectFxConfiguration configuration) =>
        throw new NotSupportedException("Native object FX configuration export is unavailable.");
    bool SupportsSceneFx => false;
    void ConfigureSceneFx(ulong handle, D3D9SceneFxConfiguration configuration) =>
        throw new NotSupportedException("Native scene FX configuration export is unavailable.");
    bool SupportsEmm => false;
    void ConfigureEmm(ulong handle, D3D9EmmSessionConfiguration configuration) =>
        throw new NotSupportedException("Native EMM configuration export is unavailable.");
    void Resize(ulong handle, int width, int height);
    void Destroy(ulong handle);
}

internal sealed class NativeSurfaceExports
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int QueryAbiCall(ref NativeAbi abi, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CreateCall(in NativeCreate create, out ulong handle, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CaptureCall(ulong handle, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CreatePreviewCall(in NativeCreatePreview create, out ulong handle, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PresentCall(ulong handle, in NativePresent present, ref NativePresentResult result, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PresentWithOverlayCall(ulong handle, in NativePresentWithOverlay present, ref NativePresentResult result, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ConfigureObjectFxCall(ulong handle, in NativeObjectFxConfiguration configuration, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ConfigureObjectFxV2Call(ulong handle, in NativeObjectFxConfigurationV2 configuration, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ConfigureEmmCall(ulong handle, in NativeEmmConfiguration configuration, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ResizeCall(ulong handle, in NativeResize resize, ref NativeError error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DestroyCall(ulong handle, ref NativeError error);

    internal required QueryAbiCall QueryAbi { get; init; }
    internal required CreateCall Create { get; init; }
    internal required CaptureCall Capture { get; init; }
    internal required ResizeCall Resize { get; init; }
    internal required DestroyCall Destroy { get; init; }
    internal CreatePreviewCall? CreatePreview { get; init; }
    internal PresentCall? Present { get; init; }
    internal PresentWithOverlayCall? PresentWithOverlay { get; init; }
    internal ConfigureObjectFxCall? ConfigureObjectFx { get; init; }
    internal ConfigureObjectFxV2Call? ConfigureObjectFxV2 { get; init; }
    internal ConfigureObjectFxV2Call? ConfigureSceneFx { get; init; }
    internal ConfigureEmmCall? ConfigureEmm { get; init; }

    internal static NativeSurfaceExports Load(nint library) => new()
    {
        QueryAbi = Export<QueryAbiCall>(library, "MdaD3D9QueryAbiV1"),
        Create = Export<CreateCall>(library, "MdaD3D9CreateV1"),
        Capture = Export<CaptureCall>(library, "MdaD3D9CaptureV1"),
        Resize = Export<ResizeCall>(library, "MdaD3D9ResizeV1"),
        Destroy = Export<DestroyCall>(library, "MdaD3D9DestroyV1"),
        CreatePreview = OptionalExport<CreatePreviewCall>(library, "MdaD3D9CreatePreviewV1"),
        Present = OptionalExport<PresentCall>(library, "MdaD3D9PresentV1"),
        PresentWithOverlay = OptionalExport<PresentWithOverlayCall>(library, "MdaD3D9PresentV2"),
        ConfigureObjectFx = OptionalExport<ConfigureObjectFxCall>(library, "MdaD3D9ConfigureObjectFxV1"),
        ConfigureObjectFxV2 = OptionalExport<ConfigureObjectFxV2Call>(library, "MdaD3D9ConfigureObjectFxV2"),
        ConfigureSceneFx = OptionalExport<ConfigureObjectFxV2Call>(library, "MdaD3D9ConfigureSceneFxV1"),
        ConfigureEmm = OptionalExport<ConfigureEmmCall>(library, "MdaD3D9ConfigureEmmV1")
    };

    private static T Export<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static T? OptionalExport<T>(nint library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var entryPoint) ? Marshal.GetDelegateForFunctionPointer<T>(entryPoint) : null;
}
