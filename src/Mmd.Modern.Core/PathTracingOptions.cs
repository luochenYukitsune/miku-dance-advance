using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Core;

/// <summary>Top-level rendering pipeline architecture for modern views.
/// Forward is the unchanged default; ClusteredForward accelerates multi-light forward shading while
/// preserving SMAA 1x, spatial supersampling, transparency and style plugins; PathTracing replaces raster
/// shading/shadows/GI with progressive multi-bounce Monte Carlo path tracing.</summary>
public enum RenderPipelineMode
{
    Forward = 0,
    ClusteredForward = 1,
    PathTracing = 2,
}

/// <summary>Surface shading contract used when <see cref="RenderPipelineMode.PathTracing"/> is active.</summary>
public enum PathTracingShadingMode
{
    /// <summary>Preserves MMD original palette, toon ramp and outlines while adding ray-traced soft shadows
    /// and multi-bounce diffuse indirect illumination.</summary>
    ToonHybrid = 0,

    /// <summary>Energy-conserving physically based shading with multi-bounce diffuse and microfacet specular transport.</summary>
    Physical = 1,
}

/// <summary>Configuration for full multi-bounce path tracing. All fields carry safe defaults so existing
/// scenes and presets deserialize with zero breaking changes.</summary>
public sealed record PathTracingOptions(
    PathTracingShadingMode ShadingMode = PathTracingShadingMode.ToonHybrid,
    int SamplesPerFrame = 1,
    int MaxBounces = 4,
    bool ProgressiveAccumulation = true,
    int MaxAccumulatedFrames = 256,
    int InteractiveSamplesPerFrame = 1,
    float FireflyClamp = 10f,
    RayGiDenoiser Denoiser = RayGiDenoiser.TemporalSpatial,
    bool UseHardwareRayTracing = true)
{
    /// <summary>Advance for seek, export-owner changes or explicit discontinuities. Excluded from portable presets.</summary>
    public long HistoryGeneration { get; init; }

    /// <summary>Application-provided camera-cut identity. Excluded from portable presets.</summary>
    public long CameraCutKey { get; init; }

    /// <summary>Explicitly discard accumulated progressive frames for this submission. Excluded from portable presets.</summary>
    public bool ResetHistory { get; init; }

    /// <summary>When true (during playback or interactive viewport dragging), downsamples to
    /// <see cref="InteractiveSamplesPerFrame"/> for fast responsive preview. Excluded from portable presets.</summary>
    public bool Interactive { get; init; }

    public static PathTracingOptions Default { get; } = new();

    /// <summary>Resolved sample budget for this frame after applying interactive preview downsampling.</summary>
    public int EffectiveSamplesPerFrame => Interactive ? Math.Min(SamplesPerFrame, InteractiveSamplesPerFrame) : SamplesPerFrame;

    /// <summary>Returns a portable copy with transient runtime history markers cleared.</summary>
    public PathTracingOptions StripRuntimeState() => this with
    {
        HistoryGeneration = 0,
        CameraCutKey = 0,
        ResetHistory = false,
        Interactive = false,
    };

    public void Validate()
    {
        if (!Enum.IsDefined(ShadingMode))
            throw new ArgumentOutOfRangeException(nameof(ShadingMode));
        if (SamplesPerFrame is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(SamplesPerFrame), "SamplesPerFrame must be between 1 and 64.");
        if (MaxBounces is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(MaxBounces), "MaxBounces must be between 1 and 8.");
        if (MaxAccumulatedFrames is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(MaxAccumulatedFrames), "MaxAccumulatedFrames must be between 1 and 4096.");
        if (InteractiveSamplesPerFrame is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(InteractiveSamplesPerFrame), "InteractiveSamplesPerFrame must be between 1 and 16.");
        if (!float.IsFinite(FireflyClamp) || FireflyClamp is < 1f or > 1000f)
            throw new ArgumentOutOfRangeException(nameof(FireflyClamp), "FireflyClamp must be finite and between 1 and 1000.");
        if (!Enum.IsDefined(Denoiser))
            throw new ArgumentOutOfRangeException(nameof(Denoiser));
    }
}

/// <summary>Deterministic snapshot of every scene, camera and parameter input that governs whether
/// progressive path-tracing accumulation may continue or must reset.</summary>
public sealed record PathTracingHistoryKey(
    int Width,
    int Height,
    SceneCamera Camera,
    Matrix4x4 World,
    Vector3 LightDirection,
    Vector3 LightAmbient,
    Vector3 LightSpecular,
    Matrix4x4 SphereRotation,
    ulong GeometryFingerprint,
    ulong MaterialFingerprint,
    ulong LightingFingerprint,
    Vector4 BackgroundColor,
    FrameAlphaOutput AlphaOutput,
    PathTracingShadingMode ShadingMode,
    int MaxBounces,
    float FireflyClamp,
    bool UseHardwareRayTracing,
    bool ProgressiveAccumulation,
    long HistoryGeneration,
    long CameraCutKey)
{
    public static PathTracingHistoryKey Create(
        OriginalFrameScene frame,
        int width,
        int height,
        PathTracingOptions options,
        ulong? geometryFingerprintOverride = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        var scene = frame.Surface.Snapshot;
        var profile = frame.Surface.Profile;
        var camera = frame.Options?.CameraOverride ?? scene.Camera;
        var sphereRotation = frame.Options?.SphereRotationOverride ?? profile.SphereRotation;
        var background = frame.Options?.BackgroundColor ?? new Vector4(20f / 255f, 24f / 255f, 32f / 255f, 1f);
        var alphaOutput = frame.Options?.AlphaOutput ?? FrameAlphaOutput.LegacyMax;
        ulong geometryFp = geometryFingerprintOverride ?? ComputeGeometryFingerprint(scene);
        ulong materialFp = ComputeMaterialFingerprint(frame);
        ulong lightingFp = ComputeLightingFingerprint(frame.Lighting);

        return new PathTracingHistoryKey(
            Width: width,
            Height: height,
            Camera: camera,
            World: scene.World,
            LightDirection: scene.LightDirection,
            LightAmbient: profile.LightAmbient,
            LightSpecular: profile.LightSpecular,
            SphereRotation: sphereRotation,
            GeometryFingerprint: geometryFp,
            MaterialFingerprint: materialFp,
            LightingFingerprint: lightingFp,
            BackgroundColor: background,
            AlphaOutput: alphaOutput,
            ShadingMode: options.ShadingMode,
            MaxBounces: options.MaxBounces,
            FireflyClamp: options.FireflyClamp,
            UseHardwareRayTracing: options.UseHardwareRayTracing,
            ProgressiveAccumulation: options.ProgressiveAccumulation,
            HistoryGeneration: options.HistoryGeneration,
            CameraCutKey: options.CameraCutKey)
        {
            VertexRevision = scene.VertexRevision,
        };
    }

    internal SceneVertexRevision? VertexRevision { get; init; }

    /// <summary>Evaluates whether progressive accumulation must reset and returns the reason,
    /// or <see langword="null"/> when history can safely accumulate another sample.</summary>
    public static string? EvaluateResetReason(
        PathTracingHistoryKey? previous,
        PathTracingHistoryKey current,
        bool explicitReset)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.ProgressiveAccumulation) return "ProgressiveDisabled";
        if (explicitReset) return "ExplicitReset";
        if (previous is null) return "FirstFrame";
        if (previous.Width != current.Width || previous.Height != current.Height) return "Resized";
        if (previous.HistoryGeneration != current.HistoryGeneration) return "GenerationChanged";
        if (previous.CameraCutKey != current.CameraCutKey) return "CameraCut";
        if (previous.ShadingMode != current.ShadingMode
            || previous.MaxBounces != current.MaxBounces
            || BitConverter.SingleToInt32Bits(previous.FireflyClamp) != BitConverter.SingleToInt32Bits(current.FireflyClamp)
            || previous.UseHardwareRayTracing != current.UseHardwareRayTracing
            || previous.ProgressiveAccumulation != current.ProgressiveAccumulation)
            return "SettingsChanged";
        if (!SameCamera(previous.Camera, current.Camera)) return "CameraChanged";
        if (!SameMatrix(previous.World, current.World) || previous.GeometryFingerprint != current.GeometryFingerprint
            || !SameVertexRevision(previous.VertexRevision, current.VertexRevision))
            return "SceneGeometryChanged";
        if (previous.MaterialFingerprint != current.MaterialFingerprint || !SameMatrix(previous.SphereRotation, current.SphereRotation))
            return "SceneMaterialChanged";
        if (!SameVec3(previous.LightDirection, current.LightDirection)
            || !SameVec3(previous.LightAmbient, current.LightAmbient)
            || !SameVec3(previous.LightSpecular, current.LightSpecular)
            || previous.LightingFingerprint != current.LightingFingerprint)
            return "LightingChanged";
        if (!SameVec4(previous.BackgroundColor, current.BackgroundColor) || previous.AlphaOutput != current.AlphaOutput)
            return "BackgroundChanged";
        return null;
    }

    private static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static bool SameNullableBits(float? a, float? b) => a is { } av ? b is { } bv && SameBits(av, bv) : b is null;
    private static bool SameVec2(Vector2 a, Vector2 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y);
    private static bool SameVec3(Vector3 a, Vector3 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);
    private static bool SameVec4(Vector4 a, Vector4 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z) && SameBits(a.W, b.W);
    private static bool SameMatrix(Matrix4x4 a, Matrix4x4 b) =>
        SameBits(a.M11, b.M11) && SameBits(a.M12, b.M12) && SameBits(a.M13, b.M13) && SameBits(a.M14, b.M14)
        && SameBits(a.M21, b.M21) && SameBits(a.M22, b.M22) && SameBits(a.M23, b.M23) && SameBits(a.M24, b.M24)
        && SameBits(a.M31, b.M31) && SameBits(a.M32, b.M32) && SameBits(a.M33, b.M33) && SameBits(a.M34, b.M34)
        && SameBits(a.M41, b.M41) && SameBits(a.M42, b.M42) && SameBits(a.M43, b.M43) && SameBits(a.M44, b.M44);

    private static bool SameCamera(SceneCamera a, SceneCamera b) =>
        SameVec3(a.Eye, b.Eye) && SameVec3(a.Target, b.Target) && SameVec3(a.Up, b.Up)
        && SameBits(a.OrthographicHeight, b.OrthographicHeight) && SameBits(a.Near, b.Near) && SameBits(a.Far, b.Far)
        && SameNullableBits(a.VerticalFovDegrees, b.VerticalFovDegrees)
        && a.LeftHanded == b.LeftHanded
        && SameVec2(a.ProjectionJitterPixels, b.ProjectionJitterPixels);

    private static ulong FnvMix(ulong hash, ulong value)
    {
        hash ^= value;
        return unchecked(hash * 1099511628211UL);
    }

    private static ulong FnvFloat(ulong hash, float value) => FnvMix(hash, (uint)BitConverter.SingleToInt32Bits(value));
    private static ulong FnvVec3(ulong hash, Vector3 v) => FnvFloat(FnvFloat(FnvFloat(hash, v.X), v.Y), v.Z);
    private static ulong FnvVec4(ulong hash, Vector4 v) => FnvFloat(FnvVec3(hash, new(v.X, v.Y, v.Z)), v.W);
    private static ulong FnvGuid(ulong hash, Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        ulong lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]);
        ulong hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        return FnvMix(FnvMix(hash, lo), hi);
    }

    private static bool SameVertexRevision(SceneVertexRevision? a, SceneVertexRevision? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.GetChangedRanges(b).Count == 0;
    }

    private static ulong ComputeGeometryFingerprint(SceneSnapshot scene)
    {
        ulong h = 14695981039346656037UL;
        h = FnvMix(h, (ulong)scene.VertexCount);
        h = FnvMix(h, (ulong)scene.Indices.Length);
        h = FnvMix(h, (ulong)scene.Materials.Count);
        h = FnvMix(h, (ulong)(scene.AdditionalUvs is not null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(scene.AdditionalUvs) : 0));
        if (scene.HasDeferredDeformation)
        {
            foreach (var range in scene.DeformationRanges)
            {
                h = FnvMix(h, (ulong)range.Start);
                h = FnvMix(h, (ulong)range.Count);
                h = FnvMix(h, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(range.Deformation));
            }
        }
        return h;
    }

    private static ulong ComputeMaterialFingerprint(OriginalFrameScene frame)
    {
        ulong h = 14695981039346656037UL;
        var scene = frame.Surface.Snapshot;
        h = FnvMix(h, (ulong)frame.Surface.Profile.TextureSampling);
        h = FnvMix(h, (ulong)scene.Textures.Count);
        for (int t = 0; t < scene.Textures.Count; t++)
            h = FnvMix(h, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(scene.Textures[t]));
        for (int i = 0; i < scene.Materials.Count; i++)
        {
            var m = scene.Materials[i];
            var b = frame.Surface.Materials[i];
            var f = frame.Materials[i];
            h = FnvMix(h, (ulong)m.StartIndex);
            h = FnvMix(h, (ulong)m.IndexCount);
            h = FnvVec4(h, m.Diffuse);
            h = FnvVec3(h, m.Specular);
            h = FnvVec3(h, m.Ambient);
            h = FnvVec3(h, m.Emission);
            h = FnvFloat(h, m.Shininess);
            h = FnvFloat(h, m.AlphaCutoff);
            h = FnvMix(h, (ulong)(uint)m.DiffuseTexture);
            h = FnvMix(h, (ulong)(uint)b.SphereTexture);
            h = FnvMix(h, (ulong)b.SphereMode);
            h = FnvMix(h, b.DoubleSided ? 1UL : 0UL);
            h = FnvMix(h, b.SubTextureAdditive ? 1UL : 0UL);
            h = FnvVec3(h, b.ToonShadowColor);
            h = FnvVec4(h, b.TextureFactors.Diffuse.Multiply);
            h = FnvVec4(h, b.TextureFactors.Diffuse.Add);
            h = FnvVec4(h, b.TextureFactors.Sphere.Multiply);
            h = FnvVec4(h, b.TextureFactors.Sphere.Add);
            h = FnvVec4(h, b.TextureFactors.Toon.Multiply);
            h = FnvVec4(h, b.TextureFactors.Toon.Add);
            h = FnvMix(h, (f.SurfaceEnabled ? 1UL : 0UL) | (f.ReceiveSelfShadow ? 2UL : 0UL) | (f.UseSceneLighting ? 4UL : 0UL) | (f.CullTransparentSurface ? 8UL : 0UL));
            h = FnvFloat(h, f.OutlineWidthScale);
            if (f.LightReceiverObjectId is { } receiverId)
            {
                h = FnvMix(h, 1UL);
                h = FnvGuid(h, receiverId);
            }
            else
            {
                h = FnvMix(h, 0UL);
            }
        }
        return h;
    }

    private static ulong ComputeLightingFingerprint(SceneLightingSnapshot? lighting)
    {
        if (lighting is null) return 0UL;
        ulong h = 14695981039346656037UL;
        h = FnvFloat(h, lighting.Settings.EnvironmentStrength);
        h = FnvFloat(h, lighting.Settings.ExposureEv);
        h = FnvMix(h, (ulong)lighting.EffectiveLights.Count);
        foreach (var l in lighting.EffectiveLights)
        {
            if (!l.Enabled || l.Intensity <= 0 || l.Color == Vector3.Zero) continue;
            h = FnvMix(h, (ulong)l.Kind);
            h = FnvVec3(h, l.Position);
            h = FnvVec4(h, new(l.Rotation.X, l.Rotation.Y, l.Rotation.Z, l.Rotation.W));
            h = FnvVec3(h, l.Color);
            h = FnvFloat(h, l.Intensity);
            h = FnvFloat(h, l.Range);
            h = FnvFloat(h, l.InnerConeRadians);
            h = FnvFloat(h, l.OuterConeRadians);
            h = FnvMix(h, l.CastShadows ? 1UL : 0UL);
            if (l.AreaSize is { } area)
            {
                h = FnvFloat(h, area.X);
                h = FnvFloat(h, area.Y);
            }
            if (l.AimDistance is { } aim) h = FnvFloat(h, aim);
            if (l.Receivers is { } receivers)
            {
                h = FnvMix(h, (ulong)receivers.Mode + 1UL);
                h = FnvMix(h, (ulong)receivers.ObjectIds.Count);
                foreach (var id in receivers.ObjectIds)
                    h = FnvGuid(h, id);
            }
            else
            {
                h = FnvMix(h, 0UL);
            }
        }
        return h;
    }
}
