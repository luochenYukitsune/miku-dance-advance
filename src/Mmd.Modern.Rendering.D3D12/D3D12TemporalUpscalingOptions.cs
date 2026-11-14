using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

public enum D3D12TemporalUpscalingMode { Off = 0, Fsr = 1, XeSs = 2, Dlss = 3 }
public enum D3D12FsrQualityPreset { NativeAa, Quality, Balanced, Performance, UltraPerformance }

/// <summary>Optional vendor temporal reconstruction. The loader is an explicitly installed SDK component.
/// Advance HistoryGeneration after seeks/restarts and CameraCutKey after discontinuous camera edits.</summary>
public sealed record D3D12TemporalUpscalingOptions
{
    public D3D12TemporalUpscalingMode Mode { get; init; }
    public D3D12FsrQualityPreset QualityPreset { get; init; } = D3D12FsrQualityPreset.NativeAa;
    public D3D12XeSsQuality XeSsQuality { get; init; } = D3D12XeSsQuality.AntiAliasing;
    public D3D12DlssQuality DlssQuality { get; init; } = D3D12DlssQuality.NativeAa;
    public string? LoaderPath { get; init; }
    /// <summary>Directory containing the optional official nvngx_dlss.dll. Otherwise MMD_DLSS_SDK_DIRECTORY is used.</summary>
    public string? DlssSdkDirectory { get; init; }
    public float FrameTimeMilliseconds { get; init; } = 1000f / 60;
    public bool ResetHistory { get; init; }
    public long HistoryGeneration { get; init; }
    public long CameraCutKey { get; init; }
    public static D3D12TemporalUpscalingOptions Off { get; } = new();

    /// <summary>FSR ratios are fixed. XeSS and DLSS dimensions require their initialized native device query.</summary>
    public D3D12SpatialUpscalingDimensions GetDimensions(int displayWidth, int displayHeight)
    {
        Validate(displayWidth, displayHeight);
        if (Mode is D3D12TemporalUpscalingMode.XeSs or D3D12TemporalUpscalingMode.Dlss)
            throw new InvalidOperationException($"{Mode} input dimensions require its native SDK device query; use submitted frame statistics.");
        return FsrDimensions(displayWidth, displayHeight);
    }

    internal void Validate(int displayWidth, int displayHeight)
    {
        if (displayWidth is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayWidth));
        if (displayHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayHeight));
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!Enum.IsDefined(QualityPreset)) throw new ArgumentOutOfRangeException(nameof(QualityPreset));
        if (!float.IsFinite(FrameTimeMilliseconds) || FrameTimeMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(FrameTimeMilliseconds));
        if (!Enum.IsDefined(XeSsQuality)) throw new ArgumentOutOfRangeException(nameof(XeSsQuality));
        if (!Enum.IsDefined(DlssQuality)) throw new ArgumentOutOfRangeException(nameof(DlssQuality));
    }

    internal (string Bridge, string SdkDirectory) ResolveDlssPaths()
        => (LoaderPath ?? Environment.GetEnvironmentVariable("MMD_DLSS_BRIDGE_PATH")
                ?? throw new InvalidOperationException("Set LoaderPath or MMD_DLSS_BRIDGE_PATH to the separately compiled mmd_dlss.dll before enabling DLSS."),
            DlssSdkDirectory ?? Environment.GetEnvironmentVariable("MMD_DLSS_SDK_DIRECTORY")
                ?? throw new InvalidOperationException("Set DlssSdkDirectory or MMD_DLSS_SDK_DIRECTORY to the optional official nvngx_dlss.dll directory before enabling DLSS."));

    private D3D12SpatialUpscalingDimensions FsrDimensions(int displayWidth, int displayHeight)
    {
        double scale = Mode == D3D12TemporalUpscalingMode.Off ? 1 : QualityPreset switch
        {
            D3D12FsrQualityPreset.NativeAa => 1, D3D12FsrQualityPreset.Quality => 1.5,
            D3D12FsrQualityPreset.Balanced => 1.7, D3D12FsrQualityPreset.Performance => 2,
            D3D12FsrQualityPreset.UltraPerformance => 3, _ => throw new ArgumentOutOfRangeException(nameof(QualityPreset))
        };
        return new(Math.Max(1, (int)Math.Floor(displayWidth / scale)), Math.Max(1, (int)Math.Floor(displayHeight / scale)), displayWidth, displayHeight);
    }
}

public sealed record D3D12TemporalUpscalingStatistics(string ProviderName, ulong ProviderId, bool ResetHistory,
    long SubmittedFrames, int JitterPhase, Vector2 JitterPixels, int RenderWidth, int RenderHeight, int DisplayWidth, int DisplayHeight)
{
    public string ResetReason { get; init; } = "None";
    public D3D12TemporalUpscalingMode Mode { get; init; } = D3D12TemporalUpscalingMode.Fsr;
    public D3D12XeSsDeviceQuery? XeSsDeviceQuery { get; init; }
    public D3D12DlssDeviceQuery? DlssDeviceQuery { get; init; }
}

/// <summary>CPU history commits only after the owning command list reaches Submit.</summary>
internal sealed class D3D12TemporalHistory
{
    internal OriginalFrameScene? Previous { get; private set; }
    internal long SubmittedFrames { get; private set; }
    internal int Phase { get; private set; }
    private long generation, cameraCut;
    private RayGiOptions previousGi = RayGiOptions.Off;
    internal string InvalidationReason { get; private set; } = "FirstFrame";
    internal bool ShouldReset(OriginalFrameScene current, D3D12TemporalUpscalingOptions options, RayGiOptions? rayGiOptions = null)
        => ResetReason(current, options, rayGiOptions) != "None";
    internal string ResetReason(OriginalFrameScene current, D3D12TemporalUpscalingOptions options, RayGiOptions? rayGiOptions = null)
        => Previous is null ? InvalidationReason : options.ResetHistory ? "ExplicitRequest" : generation != options.HistoryGeneration ? "SeekOrRestart"
            : cameraCut != options.CameraCutKey ? "CameraCut"
            : RequestsGiReset(current, rayGiOptions) || previousGi != EffectiveGi(current, rayGiOptions) ? "GiConfigurationChanged"
            : !SameProjection(current, Previous) ? "ProjectionChanged"
            : !Compatible(current, Previous) ? "SceneStructureChanged" : "None";

    internal void Commit(OriginalFrameScene frame, D3D12TemporalUpscalingOptions options, int phase, int phaseCount, RayGiOptions? rayGiOptions = null)
    {
        Previous = frame; previousGi = EffectiveGi(frame, rayGiOptions);
        generation = options.HistoryGeneration; cameraCut = options.CameraCutKey;
        Phase = (phase + 1) % phaseCount; SubmittedFrames++;
    }
    internal void Invalidate(string reason = "ConfigurationChanged") { Previous = null; Phase = 0; InvalidationReason = reason; }
    internal static Vector2 Jitter(int phase) => new(Halton(phase + 1, 2) - .5f, Halton(phase + 1, 3) - .5f);
    private static float Halton(int index, int radix)
    { float result = 0, factor = 1; while (index > 0) { factor /= radix; result += factor * (index % radix); index /= radix; } return result; }
    private static bool SameProjection(OriginalFrameScene a, OriginalFrameScene b)
    {
        var x = a.Options?.CameraOverride ?? a.Surface.Snapshot.Camera;
        var y = b.Options?.CameraOverride ?? b.Surface.Snapshot.Camera;
        // View motion and owned subpixel jitter are represented by motion vectors. Projection
        // family, depth mapping and zoom edits discard history rather than reuse stale samples.
        return x.LeftHanded == y.LeftHanded && x.Near == y.Near && x.Far == y.Far
            && x.VerticalFovDegrees == y.VerticalFovDegrees
            && (x.VerticalFovDegrees is not null || x.OrthographicHeight == y.OrthographicHeight);
    }
    private static bool Compatible(OriginalFrameScene a, OriginalFrameScene b)
    {
        var x = a.Surface.Snapshot; var y = b.Surface.Snapshot;
        return x.VertexCount == y.VertexCount && x.Indices.SequenceEqual(y.Indices)
            && SameUvs(x, y) && x.LightDirection == y.LightDirection
            && (a.Lighting ?? SceneLightingSnapshot.Empty).Lights.SequenceEqual((b.Lighting ?? SceneLightingSnapshot.Empty).Lights)
            && x.Materials.SequenceEqual(y.Materials) && x.Textures.SequenceEqual(y.Textures)
            && a.Surface.Materials.SequenceEqual(b.Surface.Materials) && a.Surface.Profile == b.Surface.Profile
            && a.Materials.SequenceEqual(b.Materials) && (a.Outline is null) == (b.Outline is null)
            && (a.Outline is null || a.Outline.Materials.SequenceEqual(b.Outline!.Materials))
            && Normalize(a.OutlineOptions) == Normalize(b.OutlineOptions)
            && Normalize(a.Options) == Normalize(b.Options);
    }
    private static OriginalFrameOptions? Normalize(OriginalFrameOptions? options)
        // Both matrices may vary continuously during camera motion. Sphere rotation changes shading,
        // not topology or history ownership; the host explicitly marks discontinuities with CameraCutKey.
        => options is null ? null : options with { CameraOverride = null, SphereRotationOverride = null, RayGi = null };
    private static OriginalOutlineOptions? Normalize(OriginalOutlineOptions? options)
        => options is null ? null : options with {CameraPosition=Vector3.Zero,FovDegrees=1,DistanceTransform=Matrix4x4.Identity};

    private static RayGiOptions EffectiveGi(OriginalFrameScene frame, RayGiOptions? explicitOptions)
    {
        var options = explicitOptions ?? frame.Options?.RayGi ?? RayGiOptions.Off;
        // Disabled artist settings have no effect on the image and should not discard useful history.
        // A reset is a request for this frame, not a persistent image configuration. Storing
        // it would reset AA again when the host clears the one-frame request after Submit.
        return options.Enabled ? options with { ResetTemporalHistory = false } : RayGiOptions.Off;
    }

    private static bool RequestsGiReset(OriginalFrameScene frame, RayGiOptions? explicitOptions)
    {
        var options = explicitOptions ?? frame.Options?.RayGi;
        return options is { Enabled: true, ResetTemporalHistory: true };
    }

    private static bool SameUvs(SceneSnapshot a, SceneSnapshot b)
    {
        for (int i = 0; i < a.VertexCount; i++)
            if (a.GetVertex(i).Uv != b.GetVertex(i).Uv) return false;
        if (ReferenceEquals(a.AdditionalUvs, b.AdditionalUvs)) return true;
        return a.AdditionalUvs is { } x && b.AdditionalUvs is { } y
            && x.ChannelCount == y.ChannelCount && x.Vertices.SequenceEqual(y.Vertices);
    }
}
