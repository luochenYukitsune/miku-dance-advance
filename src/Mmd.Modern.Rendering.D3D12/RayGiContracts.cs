using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Observed execution support, separate from requested mode and actual frame execution.</summary>
public sealed record D3D12RayGiCapabilities(bool IsWarp,bool Compute,int ShaderModel,int RawRaytracingTier,
    bool Device5,bool CommandList4,bool CompilerAvailable,bool SoftwareImplementationReady,bool HardwareImplementationReady,string? UnavailableReason)
{
    public bool SoftwareAvailable=>!IsWarp&&Compute&&ShaderModel>=60&&CompilerAvailable&&SoftwareImplementationReady;
    public bool HardwareAvailable=>SoftwareAvailable&&ShaderModel>=65&&RawRaytracingTier>=11&&Device5&&CommandList4&&HardwareImplementationReady;
}

/// <summary>An optional subregion of explicit GI diagnostic capture, not an additional frame duration.</summary>
public sealed record D3D12RayGiGpuStageStatistics(string Name, double GpuMilliseconds);

/// <summary>CPU recording time for a GI stage, summed across spatial samples in the frame.</summary>
public sealed record D3D12RayGiCpuStageStatistics(string Name, double CpuMilliseconds);

/// <summary>Counts reflect recorded work; GPU hit counters require explicit diagnostic capture.</summary>
public sealed record D3D12RayGiStatistics(LightingMode Requested,LightingMode Effective,int DispatchCount,int GBufferDrawCalls,
    long BufferUploads,long AccelerationBuilds,int SamplesPerPixel,uint Seed,float Strength,int Width,int Height,
    int CachedResources,bool GpuCountersCaptured=false,uint SecondaryHits=0,uint TraversalOverflows=0)
{
    /// <summary>Dispatch/result semantics, copied from the recorded immutable options.</summary>
    public RayGiComposition Composition {get;init;}=RayGiComposition.Additive;
    /// <summary>Actual surface response recorded with this frame's incident radiance.</summary>
    public RayGiToonResponse ToonResponse {get;init;}=RayGiToonResponse.Continuous;
    /// <summary>Enabled material ranges actually using palette bands after per-material overrides.</summary>
    public int PaletteBandReceiverMaterials {get;init;}
    /// <summary>Actual filtering recorded for this frame; a requested filter with Strength=0 remains Off.</summary>
    public RayGiDenoiser Denoiser {get;init;}=RayGiDenoiser.Off;
    /// <summary>Filter dispatches only. DispatchCount continues to count one-bounce integration.</summary>
    public int DenoiseDispatchCount {get;init;}
    /// <summary>GI incident history rejected for this recording; successful submission starts a fresh history.</summary>
    public bool TemporalResetHistory {get;init;}
    public string TemporalResetReason {get;init;}="None";
    /// <summary>Successful logical-frame history sequence, independent of spatial sample count. Per-pixel accumulation is capped at 32.</summary>
    public ulong TemporalHistoryFrames {get;init;}
    /// <summary>Sum of retained pre-spatial signal/moments/position/normal sets for all spatial samples;
    /// replaced generations retire through frame fences. Excludes driver alignment and in-flight generations.</summary>
    public long TemporalHistoryBytes {get;init;}
    /// <summary>Enabled native lights actually uploaded to the secondary-hit integrator.</summary>
    public int SecondaryNativeLights {get;init;}
    public string RgbSemantic=>Composition==RayGiComposition.ToonPalette?"MeanIncidentRadiance":"ReceiverAlbedoTimesMeanIncidentRadiance";
    /// <summary>Fixed art mapping scale. Additive does not use this scale.</summary>
    public float ReferenceRadiance=>RayGiToonPaletteMath.ReferenceRadiance;
    /// <summary>Actual out-of-place BLAS/TLAS update pairs; separate from full AccelerationBuilds.</summary>
    public long AccelerationUpdates {get;init;}
    public double RecordCpuMilliseconds {get;init;}
    /// <summary>Nonoverlapping CPU stages within RecordCpuMilliseconds, available with Capture + MMD_RAYGI_TIMING_STAGES=1.</summary>
    public IReadOnlyList<D3D12RayGiCpuStageStatistics> CpuStages {get;init;} = [];
    /// <summary>Distinct frame-owned receiver workspaces and their driver-aligned committed allocation bytes.
    /// Excludes temporal history, scene caches, diagnostics readbacks and descriptor heaps.</summary>
    public int ReceiverWorkspaceCount {get;init;}
    public long ReceiverWorkspaceBytes {get;init;}
    /// <summary>Explicit Capture + MMD_RAYGI_TIMING only: summed GI GBuffer/AS build or update/compute/filter GPU duration.
    /// Excludes later surface composition/postFX/presentation; not complete-frame GPU time or FPS.</summary>
    public double? GpuMilliseconds {get;init;}
    /// <summary>Optional nonoverlapping subregions inside GpuMilliseconds. Never add them to the aggregate.</summary>
    public IReadOnlyList<D3D12RayGiGpuStageStatistics> GpuStages {get;init;} = [];
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct RayGiConstants
{
    public readonly RayGiUInt4 Dispatch, Counts;
    public readonly Vector4 LightDirectionDistance, IrradianceStrength, Tuning, Reserved;
    public readonly Matrix4x4 Camera;
    public RayGiConstants(RayGiUInt4 dispatch, RayGiUInt4 counts, Vector4 lightDirectionDistance,
        Vector4 irradianceStrength, Vector4 tuning, Vector4 reserved, Matrix4x4 camera)
    {
        Dispatch = dispatch; Counts = counts; LightDirectionDistance = lightDirectionDistance;
        IrradianceStrength = irradianceStrength; Tuning = tuning; Reserved = reserved;
        Camera = camera;
    }
}

/// <summary>Future temporal consumers must invalidate on each generation before reusing history.</summary>
public sealed record RayGiFrameInputs(Guid SceneIdentity,Matrix4x4 ViewProjection,int Width,int Height,RayGiOptions Options,
    bool HasDepth=true,bool HasNormals=true,bool HasMotionVectors=false,bool HasHistory=false);
