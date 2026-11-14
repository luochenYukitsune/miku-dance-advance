using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>Explicit caller declaration: configured MME/EMM scenes cannot enter this surface experiment.</summary>
public enum D3D9SurfaceIsolation { NoConfiguredMmeOrEmm = 1 }

/// <summary>Explicit immutable request scope; scene FX requires separate session opt-in.</summary>
public enum D3D9SurfaceScope { IsolatedSurface = 1, ConfiguredObjectFx = 2, ConfiguredSceneFx = 3, ConfiguredEmm = 4 }

public enum D3D9SurfaceRenderOrder { Original = 0, OpaqueFirst = 1 }

/// <summary>Options for an isolated opaque-background capture or borrowed-window preview session.</summary>
public sealed class D3D9SurfaceSessionOptions
{
    public D3D9SurfaceSessionOptions(int width, int height, uint adapterOrdinal = 0, int queueCapacity = 2)
    {
        ValidateExtent(width, height);
        if (queueCapacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        Width = width;
        Height = height;
        AdapterOrdinal = adapterOrdinal;
        QueueCapacity = queueCapacity;
    }

    public int Width { get; }
    public int Height { get; }
    public uint AdapterOrdinal { get; }
    public int QueueCapacity { get; }

    internal static void ValidateExtent(int width, int height)
    {
        if (width is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width), "Surface extent must be 1..4096.");
        if (height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(height), "Surface extent must be 1..4096.");
    }
}

/// <summary>
/// Frozen FRM1 v1/v2/v3/v4/v5/v6 / VIS1 v2/v3/v4/v5/v6/v7 pair. Copies each caller span once, then keeps both private for its lifetime.
/// Reuse across captures or presentations is allowed. Object IDs must continue to name the same logical objects.
/// The caller must finish writing the source spans before invoking this factory.
/// </summary>
public sealed class D3D9SurfaceSnapshot
{
    internal byte[] FrameBytes { get; }
    internal byte[] VisualBytes { get; }
    internal ulong RequiredFeatures { get; }
    internal NativeEditorOverlayPrimitive[] PreviewOverlayPrimitives { get; }
    public int Width { get; }
    public int Height { get; }
    public double SourceFrame { get; }
    public D3D9SurfaceScope Scope { get; }
    public int FrameByteCount => FrameBytes.Length;
    public int VisualByteCount => VisualBytes.Length;
    public int PreviewOverlayPrimitiveCount => PreviewOverlayPrimitives.Length;

    private D3D9SurfaceSnapshot(byte[] frame, byte[] visual, D3D9SurfaceScope scope)
    {
        FrameBytes = frame;
        VisualBytes = visual;
        Scope = scope;
        PreviewOverlayPrimitives = [];
        uint frameVersion = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8));
        uint typedFlags = frameVersion is 3 or 4 or 5 or 6 ? BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(208)) : 0;
        RequiredFeatures = 1UL | (frameVersion == 2 || (typedFlags & 1) != 0 ? 2UL : 0UL) |
            ((typedFlags & 2) != 0 ? 16UL : 0UL) | ((typedFlags & 4) != 0 ? 2048UL : 0UL) |
            (frameVersion is 5 or 6 ? 12UL : 0UL) | ((typedFlags & 16) != 0 ? 8192UL : 0UL) |
            (scope switch { D3D9SurfaceScope.ConfiguredObjectFx => 256UL, D3D9SurfaceScope.ConfiguredSceneFx => 1280UL,
                D3D9SurfaceScope.ConfiguredEmm => 5376UL, _ => 0UL });
        Width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)));
        D3D9SurfaceSessionOptions.ValidateExtent(Width, Height);
        SourceFrame = BinaryPrimitives.ReadDoubleLittleEndian(frame.AsSpan(40));
        if (!double.IsFinite(SourceFrame)) throw new ArgumentException("FRM1 source frame must be finite.", nameof(frame));
        var alpha = BinaryPrimitives.ReadSingleLittleEndian(frame.AsSpan(204));
        if (!float.IsFinite(alpha) || alpha < 1)
            throw new NotSupportedException("The isolated surface experiment requires an opaque FRM1 background.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(12)) != BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(32)) ||
            BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(16)) != BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)))
            throw new ArgumentException("VIS1 material/vertex counts do not match FRM1.", nameof(visual));
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(frame, digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, visual.AsSpan(20, 32)))
            throw new ArgumentException("VIS1 is not bound to these exact FRM1 bytes.", nameof(visual));
        // Full packet topology, IDs, floats, material coverage and normal validation remain native preflight.
    }

    /// <summary>Freezes editor primitives separately from the unchanged FRM/VIS scene packets.
    /// Only preview sessions accept this snapshot; captures never include editing helpers.</summary>
    public D3D9SurfaceSnapshot WithPreviewOverlay(IEnumerable<D3D9EditorOverlayPrimitive> primitives)
        => new(this, NativeEditorOverlayPrimitive.Freeze(primitives));

    private D3D9SurfaceSnapshot(D3D9SurfaceSnapshot source, NativeEditorOverlayPrimitive[] previewOverlay)
    {
        // The original arrays have already been copied, hashed and kept private. Share that
        // immutable storage instead of hashing/copying model geometry again for editor lines.
        FrameBytes = source.FrameBytes; VisualBytes = source.VisualBytes; Scope = source.Scope;
        Width = source.Width; Height = source.Height; SourceFrame = source.SourceFrame;
        PreviewOverlayPrimitives = previewOverlay;
        RequiredFeatures = (source.RequiredFeatures & ~16384UL) | (previewOverlay.Length == 0 ? 0UL : 16384UL);
    }

    public static D3D9SurfaceSnapshot CopyIsolatedSurface(ReadOnlySpan<byte> framePacket, ReadOnlySpan<byte> visualPacket,
        D3D9SurfaceIsolation isolation)
    {
        if (isolation != D3D9SurfaceIsolation.NoConfiguredMmeOrEmm)
            throw new ArgumentOutOfRangeException(nameof(isolation), "Declare a scene without configured MME/EMM effects explicitly.");
        return Copy(framePacket, visualPacket, D3D9SurfaceScope.IsolatedSurface);
    }

    /// <summary>
    /// Copies packets for a session explicitly created with object/subset FX configuration. The scene must contain
    /// no EMM, scene effects or effects beyond the supplied limited object FX configuration. This factory does not
    /// freeze any files; the configuration's complete resource-tree lifetime requirement still applies.
    /// </summary>
    public static D3D9SurfaceSnapshot CopyConfiguredObjectFx(ReadOnlySpan<byte> framePacket, ReadOnlySpan<byte> visualPacket) =>
        Copy(framePacket, visualPacket, D3D9SurfaceScope.ConfiguredObjectFx);

    /// <summary>
    /// Copies one evaluated sample for all native scene FX render turns. The session must have been created
    /// explicitly with scene FX configuration, and its complete dependency lease must remain alive.
    /// </summary>
    public static D3D9SurfaceSnapshot CopyConfiguredSceneFx(ReadOnlySpan<byte> framePacket, ReadOnlySpan<byte> visualPacket) =>
        Copy(framePacket, visualPacket, D3D9SurfaceScope.ConfiguredSceneFx);

    /// <summary>Copies one evaluated sample for a session explicitly configured with frozen EMM tables and leased resources.</summary>
    public static D3D9SurfaceSnapshot CopyConfiguredEmm(ReadOnlySpan<byte> framePacket, ReadOnlySpan<byte> visualPacket) =>
        Copy(framePacket, visualPacket, D3D9SurfaceScope.ConfiguredEmm);

    private static D3D9SurfaceSnapshot Copy(ReadOnlySpan<byte> framePacket, ReadOnlySpan<byte> visualPacket, D3D9SurfaceScope scope)
    {
        if (framePacket.Length is < 208 or > 1_073_741_824) throw new ArgumentException("Invalid FRM1 packet size.", nameof(framePacket));
        if (visualPacket.Length is < 128 or > 16_777_216) throw new ArgumentException("Invalid VIS1 packet size.", nameof(visualPacket));
        var frame = framePacket.ToArray();
        var visual = visualPacket.ToArray();
        uint frameVersion = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8));
        uint visualVersion = BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(8));
        if (!frame.AsSpan(0, 8).SequenceEqual("MDA9FRM1"u8) || frameVersion is not (1 or 2 or 3 or 4 or 5 or 6))
            throw new ArgumentException("Expected MDA9FRM1 version 1, 2, 3, 4, 5 or 6.", nameof(framePacket));
        if (!visual.AsSpan(0, 8).SequenceEqual("MDA9VIS1"u8) || visualVersion is not (2 or 3 or 4 or 5 or 6 or 7))
            throw new ArgumentException("Expected MDA9VIS1 version 2, 3, 4, 5, 6 or 7.", nameof(visualPacket));
        if ((frameVersion is 3 or 4 or 5 or 6) && (frame.Length < 216
            || (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(208)) & ~(frameVersion == 6 ? 31U : frameVersion == 5 ? 15U : frameVersion == 4 ? 7U : 3U)) != 0))
            throw new ArgumentException("Invalid typed FRM1 header.", nameof(framePacket));
        if (frameVersion == 4 && ((BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(208)) & 4U) == 0
            || scope != D3D9SurfaceScope.ConfiguredEmm))
            throw new ArgumentException("FRM1 version 4 requires explicit EMM scope and complete model material metadata.", nameof(framePacket));
        if ((frameVersion == 3) != (visualVersion == 4))
            throw new ArgumentException("Typed FRM1 version 3 requires VIS1 version 4 exclusively.", nameof(visualPacket));
        if ((frameVersion == 4) != (visualVersion == 5))
            throw new ArgumentException("Material metadata FRM1 version 4 requires VIS1 version 5 exclusively.", nameof(visualPacket));
        if ((frameVersion == 5) != (visualVersion == 6))
            throw new ArgumentException("Classic pass FRM1 version 5 requires VIS1 version 6 exclusively.", nameof(visualPacket));
        if ((frameVersion == 6) != (visualVersion == 7))
            throw new ArgumentException("Media FRM1 version 6 requires VIS1 version 7 exclusively.", nameof(visualPacket));
        if ((frameVersion is 5 or 6) && (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(208)) & 8U) == 0)
            throw new ArgumentException("Classic pass FRM1 version 5/6 requires its explicit Classic pass block.", nameof(framePacket));
        if (frameVersion == 6 && ((BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(208)) & 28U) != 28U
            || scope != D3D9SurfaceScope.ConfiguredEmm))
            throw new ArgumentException("FRM1 version 6 requires explicit EMM scope, original pass inputs and media.", nameof(framePacket));
        if ((visualVersion is 4 or 5 or 6 or 7) && visual.Length < 164)
            throw new ArgumentException("Typed VIS1 requires its accessory light header.", nameof(visualPacket));
        if (visualVersion == 3 && frameVersion != 2)
            throw new ArgumentException("VIS1 version 3 requires a FRM1 version 2 additional UV stream.", nameof(visualPacket));
        return new(frame, visual, scope);
    }
}

/// <summary>Independent managed, tightly packed, top-down RGBA. No COM/native resources or disposal affinity.</summary>
public sealed class D3D9SurfaceCapture
{
    internal D3D9SurfaceCapture(int width, int height, byte[] rgba, NativeCaptureResult result)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        SourceFrame = result.SourceFrame;
        CompletedCaptures = result.CompletedCaptures;
        GeometryGeneration = result.GeometryGeneration;
        DrawCalls = result.DrawCalls;
        IsolatedHostMetadata = (result.Flags & 1) != 0;
        ConfiguredObjectFxMetadata = (result.Flags & 8) != 0;
        ConfiguredSceneFxMetadata = (result.Flags & 16) != 0;
        ConfiguredEmmMetadata = (result.Flags & 32) != 0;
        SoftwareVertexProcessing = (result.Flags & 2) != 0;
        Diagnostics = new(result.EffectDispatches, result.EffectPassDraws, result.EffectPassFailures, result.RawFallbackDraws, result.NoUiDiagnostics);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public byte[] Rgba { get; }
    public double SourceFrame { get; }
    public ulong CompletedCaptures { get; }
    public ulong GeometryGeneration { get; }
    public uint DrawCalls { get; }
    public bool IsolatedHostMetadata { get; }
    public bool ConfiguredObjectFxMetadata { get; }
    public bool ConfiguredSceneFxMetadata { get; }
    public bool ConfiguredEmmMetadata { get; }
    public bool SoftwareVertexProcessing { get; }
    public D3D9SurfaceCodePathDiagnostics Diagnostics { get; }
}

/// <summary>
/// Metadata from an accepted native Present call. Contains no CPU image, scan-out acknowledgement or GPU completion fence.
/// </summary>
public sealed class D3D9SurfacePresentation
{
    internal D3D9SurfacePresentation(NativePresentResult result)
    {
        Width = checked((int)result.Width);
        Height = checked((int)result.Height);
        SourceFrame = result.SourceFrame;
        CompletedPresents = result.CompletedPresents;
        GeometryGeneration = result.GeometryGeneration;
        DrawCalls = result.DrawCalls;
        IsolatedHostMetadata = (result.Flags & 1) != 0;
        ConfiguredObjectFxMetadata = (result.Flags & 8) != 0;
        ConfiguredSceneFxMetadata = (result.Flags & 16) != 0;
        ConfiguredEmmMetadata = (result.Flags & 32) != 0;
        SoftwareVertexProcessing = (result.Flags & 2) != 0;
        Diagnostics = new(result.EffectDispatches, result.EffectPassDraws, result.EffectPassFailures, result.RawFallbackDraws, result.NoUiDiagnostics);
    }

    public int Width { get; }
    public int Height { get; }
    public double SourceFrame { get; }
    public ulong CompletedPresents { get; }
    public ulong GeometryGeneration { get; }
    public uint DrawCalls { get; }
    public bool IsolatedHostMetadata { get; }
    public bool ConfiguredObjectFxMetadata { get; }
    public bool ConfiguredSceneFxMetadata { get; }
    public bool ConfiguredEmmMetadata { get; }
    public bool SoftwareVertexProcessing { get; }
    public D3D9SurfaceCodePathDiagnostics Diagnostics { get; }
}

/// <summary>Host code-path counters only; these do not measure GPU utilization, timing, or playback FPS.</summary>
public sealed record D3D9SurfaceCodePathDiagnostics(uint EffectDispatches, uint EffectPassDraws,
    uint EffectPassFailures, uint RawFallbackDraws, uint NoUiDiagnostics);

/// <summary>Native failure including its original status, bounded UTF-8 diagnostic and reported required size.</summary>
public sealed class D3D9SurfaceNativeException : Exception
{
    internal D3D9SurfaceNativeException(string operation, int status, string diagnostic, ulong requiredBytes, bool truncated)
        : base($"D3D9 surface {operation} failed ({status}): {diagnostic}" +
            (truncated ? $" [diagnostic truncated; native requires {requiredBytes} bytes]" : string.Empty))
    {
        Operation = operation;
        Status = status;
        Diagnostic = diagnostic;
        RequiredDiagnosticBytes = requiredBytes;
        DiagnosticTruncated = truncated;
    }
    public string Operation { get; }
    public int Status { get; }
    public string Diagnostic { get; }
    public ulong RequiredDiagnosticBytes { get; }
    public bool DiagnosticTruncated { get; }
}
