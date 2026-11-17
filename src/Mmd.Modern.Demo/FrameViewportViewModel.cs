using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using SkiaSharp;

namespace Mmd.Modern.Demo;

public sealed record FrameViewportSource(string Path, long Bytes, string Sha256);
public sealed record FrameViewportFraming(double Zoom, double Yaw, double Pitch, float ModelSpan, float OrthographicHeight,
    float[] Eye, float[] Target, float[] MinimumNdc, float[] MaximumNdc, int ClippedReferencedVertices, int ReferencedVertices);

/// <summary>Immutable scene/options only. Native handles and GPU resources are owned by the viewport host.</summary>
public sealed partial class FrameViewportViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly FrameViewportOptions options;
    private LightingCapabilities lightingCapabilities = LightingCapabilities.None;
    private LightingExecutionCapabilities lightingExecution = new(BackendAvailable: false);
    private bool lightingObserved, compatMode, startupSelectionApplied;
    private RayGiOptions rayGi = new(Strength: .5f, SamplesPerPixel: 16, OcclusionStrength: 0);
    private string giExecutionDiagnostics = "实际 GI：Off · 尚未提交 GI · GPU 时间未测量";
    private OriginalStyleScene? surface;
    private OriginalOutlineData? outline;
    private OriginalFrameMaterialFlags[] flags = [];
    private FrameViewportCamera? camera;
    private OriginalFrameScene? demoRoom;
    private FrameViewportCamera? roomCamera;
    private bool roomEnabled;
    private float roomEmissionStrength;
    private bool shadowFitEnabled;
    private FrameAntialiasing antialiasing;
    private Matrix4x4? fittedShadow, fittedRoomShadow;
    private volatile bool sceneReady;
    private OriginalFrameScene? cachedFrame;
    private int cachedWidth, cachedHeight;
    private double yaw, pitch, zoom = 1;
    private bool outlineEnabled = true, selfShadowEnabled = true, groundShadowEnabled = true, transparencyDiagnostic;
    private string status = "请选择 PMX 模型。", modelSummary = "D3D12 实时完整帧 · 静态 PMX";
    private string renderDiagnostics = "D3D12 · 等待模型 · 每帧 CPU 读回 0";
    private readonly Dictionary<string, FrameViewportSource> sources = new(StringComparer.OrdinalIgnoreCase);
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<FrameViewportSource> Sources => sources.Values.ToArray();
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];
    public string? ModelPath { get; private set; }
    public bool HasScene => sceneReady;
    public bool RoomEnabled
    {
        get => roomEnabled;
        set
        {
            if(value&&HasMotion){Status="动作预览当前使用角色场景，请先卸载动作再切演示房间。";Changed();Changed(nameof(ScenePresetIndex));return;}
            if (roomEnabled == value) return;
            roomEnabled = value; ResetCamera(); Changed(); Changed(nameof(ScenePresetIndex));
        }
    }
    public int ScenePresetIndex
    {
        get => roomEnabled ? 1 : 0;
        set { if (value is 0 or 1) RoomEnabled = value == 1; }
    }
    public double RoomEmissionStrength
    {
        get => roomEmissionStrength;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            if (roomEmissionStrength == (float)value) return;
            roomEmissionStrength = (float)value;
            demoRoom = null; roomCamera = null; fittedRoomShadow = null;
            Invalidate();
        }
    }
    public bool GiEnabled => rayGi.Enabled;
    public FrameAntialiasing Antialiasing
    {
        get => antialiasing;
        set
        {
            if (value is not (FrameAntialiasing.Off or FrameAntialiasing.Smaa1X))
            {
                Status = "抗锯齿只支持 Off 或 SMAA1x；保留当前选择。";
                Changed(); Changed(nameof(AntialiasingIndex)); return;
            }
            if (antialiasing == value) return;
            antialiasing = value;
            Invalidate(); Changed(nameof(AntialiasingIndex));
        }
    }
    public int AntialiasingIndex
    {
        get => antialiasing == FrameAntialiasing.Smaa1X ? 1 : 0;
        set => Antialiasing = value switch
        { 0 => FrameAntialiasing.Off, 1 => FrameAntialiasing.Smaa1X, _ => (FrameAntialiasing)(-1) };
    }
    public bool RayTracingEnabled => GiEnabled;
    public RayGiOptions RayGiOptions => rayGi;
    public int GiCompositionIndex
    {
        get => rayGi.Composition == RayGiComposition.ToonPalette ? 1 : 0;
        set
        {
            if (value is < 0 or > 1 || value == GiCompositionIndex) return;
            rayGi = rayGi with { Composition = value == 1 ? RayGiComposition.ToonPalette : RayGiComposition.Additive,
                Denoiser = value == 1 ? rayGi.Denoiser : RayGiDenoiser.Off };
            Invalidate(); Changed(nameof(RayGiOptions));
            Changed(nameof(GiDenoiseEnabled)); Changed(nameof(GiDenoiseAvailable));
        }
    }
    public bool GiDenoiseAvailable => rayGi.Composition == RayGiComposition.ToonPalette;
    public bool GiDenoiseEnabled
    {
        get => rayGi.Denoiser == RayGiDenoiser.Spatial;
        set
        {
            if (value && !GiDenoiseAvailable)
            {
                Status = "补光降噪需要先手动选择三渲二受限反弹；GI 与合成模式保持当前选择。";
                Changed(); return;
            }
            if (value == GiDenoiseEnabled) return;
            rayGi = rayGi with { Denoiser = value ? RayGiDenoiser.Spatial : RayGiDenoiser.Off };
            Invalidate(); Changed(nameof(RayGiOptions));
        }
    }
    public bool NearOcclusionEnabled
    {
        get => rayGi.OcclusionStrength > 0;
        set
        {
            if (value == NearOcclusionEnabled) return;
            rayGi = rayGi with { OcclusionStrength = value ? 1 : 0 };
            Invalidate(); Changed(nameof(RayGiOptions));
        }
    }
    public LightingMode GiMode { get => rayGi.Mode; set => TrySelectGiMode(value); }
    public double GiStrength
    {
        get => rayGi.Strength;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
            rayGi = rayGi with { Strength = (float)value }; Invalidate(); Changed(nameof(RayGiOptions));
        }
    }
    public int GiQualityIndex
    {
        get => rayGi.SamplesPerPixel switch { 4 => 0, 16 => 1, _ => 2 };
        set
        {
            if (value is < 0 or > 2) return;
            rayGi = rayGi with { SamplesPerPixel = value switch { 0 => 4, 1 => 16, _ => 64 } };
            Invalidate(); Changed(nameof(RayGiOptions));
        }
    }
    private LightingExecutionCapabilities CurrentExecution => lightingExecution with { TransportSupported = !transparencyDiagnostic && materialOpacity==1 && !HasMotion && lightingExecution.TransportSupported };
    public IReadOnlyList<LightingMenuOption> GiMenu => LightingPlanner.Menu(lightingCapabilities, CurrentExecution, compatMode);
    public bool SoftwareGiAvailable => GiMenu.Single(o => o.Mode == LightingMode.SoftwareRayTracing).Available;
    public bool HardwareGiAvailable => GiMenu.Single(o => o.Mode == LightingMode.HardwareRayTracing).Available;
    public string GiAvailabilityDetails => materialOpacity<1 ? "整体透明度小于 1：当前光追不支持部分透光，GI 关闭；原版光照和直接光仍可用。"
        : HasMotion ? "动作预览当前关闭 GI；动态加速结构后续接入。"
        : transparencyDiagnostic ? "透明诊断（alpha=0.5）与 GI 互斥；先关闭透明诊断。"
        : !lightingObserved ? "等待实际 D3D12 设备与 GI 实现能力探测。"
        : string.Join("\n", GiMenu.Where(o => !o.Available).Select(o => $"{o.DisplayName}：{o.UnavailableReason}"));
    public string GiExecutionDiagnostics { get => giExecutionDiagnostics; set { giExecutionDiagnostics = value; Changed(); } }
    public string Status { get => status; set { status = value; Changed(); } }
    public string RenderDiagnostics { get => renderDiagnostics; set { renderDiagnostics = value; Changed(); } }
    public string ModelSummary { get => modelSummary; private set { modelSummary = value; Changed(); } }
    public double Yaw { get => yaw; set { yaw = value; ProjectCameraEnabled=false; Invalidate(); } }
    public double Pitch { get => pitch; set { pitch = Math.Clamp(value, -85, 85); ProjectCameraEnabled=false; Invalidate(); } }
    public double Zoom { get => zoom; set { zoom = Math.Clamp(value, .15, 8); ProjectCameraEnabled=false; Invalidate(); } }
    public bool OutlineEnabled { get => outlineEnabled; set { outlineEnabled = value; Invalidate(); } }
    public bool SelfShadowEnabled { get => selfShadowEnabled; set { selfShadowEnabled = value; Invalidate(); } }
    public bool ShadowFitEnabled
    {
        get => shadowFitEnabled;
        set
        {
            if (shadowFitEnabled == value) return;
            shadowFitEnabled = value; Invalidate(); Changed(nameof(ShadowProjectionPolicy));
        }
    }
    public int ShadowProjectionFitCount { get; private set; }
    public string ShadowProjectionPolicy => shadowFitEnabled
        ? "modern scene-fit toward scene.LightDirection,World included once,all referenced geometry; original host parity unverified"
        : "selected mode1 LH warp C=model center,D=(0,-1,0),S=1/(span*1.6)";
    public bool GroundShadowEnabled { get => groundShadowEnabled; set { groundShadowEnabled = value; Invalidate(); } }
    public bool TransparencyDiagnostic
    {
        get => transparencyDiagnostic;
        set
        {
            if (value && GiEnabled) { Status = "透明诊断与 GI 互斥；先选择 GI Off。普通半透明材质仍按表面绘制顺序处理。"; Changed(); return; }
            transparencyDiagnostic = value; Invalidate(); NotifyGiMenu();
        }
    }
    public FrameViewportViewModel(FrameViewportOptions options)
    {
        this.options = options;
        if (!float.IsFinite(options.RoomEmission) || options.RoomEmission is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options.RoomEmission));
        if (options.Antialiasing is not (FrameAntialiasing.Off or FrameAntialiasing.Smaa1X))
            throw new ArgumentOutOfRangeException(nameof(options.Antialiasing));
        antialiasing = options.Antialiasing;
        roomEmissionStrength = options.RoomEmission;
        shadowFitEnabled = options.FitShadow;
        roomEnabled = options.DemoRoom; yaw = roomEnabled ? 22 : 0; pitch = roomEnabled ? 16 : 0;
        if (options.RayGi is { } initial) { initial.Validate(); rayGi = initial with { Mode = LightingMode.Legacy }; }
    }

    public void UpdateLightingCapabilities(LightingCapabilities capabilities, LightingExecutionCapabilities execution, bool compatMode = false)
    {
        lightingCapabilities = capabilities; lightingExecution = execution; this.compatMode = compatMode; lightingObserved = true;
        if (GiEnabled)
        {
            var current = LightingPlanner.Select(new(GiMode, AllowFallback: false, compatMode), capabilities, CurrentExecution);
            if (!current.Success || !current.IsRayTraced) TrySelectGiMode(LightingMode.Legacy);
        }
        NotifyGiMenu();
        ApplyDemoLightingSelection();
        if (!startupSelectionApplied)
        {
            startupSelectionApplied = true;
            if (options.RayGi?.Enabled == true) TrySelectGiMode(options.RayGi.Mode);
        }
    }
    public bool TrySelectGiMode(LightingMode mode)
    {
        var selection = LightingPlanner.Select(new(mode, AllowFallback: false, compatMode), lightingCapabilities, CurrentExecution);
        if (!selection.Success || selection.Effective != mode || (mode != LightingMode.Legacy && !lightingObserved))
        {
            Status = transparencyDiagnostic ? GiAvailabilityDetails : string.Join("; ", selection.Diagnostics.Select(d => d.Message));
            Changed(nameof(GiMode)); return false;
        }
        rayGi = rayGi with { Mode = mode }; cachedFrame = null;
        Changed(nameof(GiMode)); Changed(nameof(GiEnabled)); Changed(nameof(RayTracingEnabled)); Changed(nameof(RayGiOptions));
        GiExecutionDiagnostics = mode == LightingMode.Legacy ? "实际 GI：等待下一帧确认 Off" : "GI 请求已更新；等待实际 dispatch 确认。";
        Status = mode == LightingMode.Legacy ? "GI 已关闭；保留原版表面光照。" : $"请求 {mode} · {rayGi.SamplesPerPixel} SPP · 强度 {rayGi.Strength:F1}；以实际帧统计为准。";
        return true;
    }
    private void NotifyGiMenu()
    {
        Changed(nameof(GiMenu)); Changed(nameof(SoftwareGiAvailable)); Changed(nameof(HardwareGiAvailable)); Changed(nameof(GiAvailabilityDetails));
    }

    public void LoadModel(string path)
    {
        using var candidate=PrepareModel(path);CommitModel(candidate);
        if(options.MotionPath is not null){LoadMotion(options.MotionPath);if(options.AutoPlayMotion)PlayMotion();}
    }

    public OriginalFrameScene GetFrame(int width, int height)
    {
        if (surface is null || outline is null || camera is null) throw new InvalidOperationException("Load a PMX model before rendering.");
        if (cachedFrame is not null && width == cachedWidth && height == cachedHeight) return cachedFrame;
        if (roomEnabled && demoRoom is null)
        {
            var initialEdge = new OriginalOutlineOptions(new(camera.Center, 1), Matrix4x4.Identity, camera.CreateCamera(1, 1).Eye, 30, 1, outlineEnabled);
            demoRoom = RayGiDemoScene.AddRoom(new(surface, outline, initialEdge, materials: flags), roomEmissionStrength);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (uint index in demoRoom.Surface.Snapshot.Indices)
            {
                var position = Vector3.Transform(demoRoom.Surface.Snapshot.Vertices[(int)index].Position, demoRoom.Surface.Snapshot.World);
                min = Vector3.Min(min, position); max = Vector3.Max(max, position);
            }
            roomCamera = new(min, max);
        }
        var activeCamera = roomEnabled ? roomCamera! : camera;
        activeCamera.YawDegrees = yaw; activeCamera.PitchDegrees = pitch; activeCamera.Zoom = zoom;
        activeCamera.Pan=new((float)panX,(float)panY);
        var selectedCamera = ProjectCameraEnabled&&projectPlayback is not null?projectPlayback.Camera:activeCamera.CreateCamera(width, height);
        var rotation = selectedCamera.LeftHanded?Matrix4x4.CreateLookAtLeftHanded(selectedCamera.Eye,selectedCamera.Target,selectedCamera.Up):Matrix4x4.CreateLookAt(selectedCamera.Eye, selectedCamera.Target, selectedCamera.Up);
        rotation.M41 = rotation.M42 = rotation.M43 = 0;
        var edge = new OriginalOutlineOptions(new(camera.Center, 1), Matrix4x4.Identity, selectedCamera.Eye, selectedCamera.VerticalFovDegrees??30, (float)outlineWidthScale, outlineEnabled);
        var activeSurface = ApplyAppearance(roomEnabled ? demoRoom!.Surface : surface);
        // The selected legacy warp stays unchanged; modern scene coverage is an explicit independent option.
        var shadow = selfShadowEnabled ? new OriginalSelfShadowOptions(shadowFitEnabled
            ? FittedShadowProjection(activeSurface.Snapshot) : SelectedLightWarp(activeCamera.Center, activeCamera.Span), 1024, 1024,
            true, false, OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual) : null;
        var groundDirection=projectPlayback is null?new Vector3(.35f,-1,.2f):-activeSurface.Snapshot.LightDirection;
        var ground = groundShadowEnabled && !roomEnabled && MathF.Abs(groundDirection.Y)>1e-6f ? new OriginalGroundShadowOptions(groundDirection, new(.2f, .2f, .2f, .5f), true, true) : null;
        cachedFrame = new(activeSurface, ApplyOutlineAppearance(roomEnabled ? demoRoom!.Outline! : outline), edge,
            new(OriginalAlphaBlendMode.Normal, true, transparencyDiagnostic, shadow, ground, selectedCamera, rotation,
                RayGi: rayGi, Antialiasing: antialiasing,BackgroundColor:projectPlayback is null?null:projectPlayback.Plan.Document.Settings.BlackBackgroundEnabled?new Vector4(0,0,0,1):Vector4.One),
            roomEnabled ? demoRoom!.Materials : flags,LightEditor.Snapshot,LightOverlay(selectedCamera,width,height));
        cachedWidth = width; cachedHeight = height;
        return cachedFrame;
    }

    private Matrix4x4 FittedShadowProjection(SceneSnapshot snapshot)
    {
        // Static geometry/light inputs do not change with the orbit camera, viewport size or GI options.
        // Keep only pure matrices; no device resources or geometry scan on ordinary frame invalidation.
        if ((roomEnabled ? fittedRoomShadow : fittedShadow) is { } cached) return cached;
        var projection = ModernDirectionalShadowProjection.Fit(snapshot);
        if (roomEnabled) fittedRoomShadow = projection; else fittedShadow = projection;
        ShadowProjectionFitCount++;
        return projection;
    }

    public FrameViewportFraming GetFramingDiagnostics(int width, int height)
    {
        var frame = GetFrame(width, height); var selected = frame.Options!.CameraOverride!;
        var matrix = frame.Surface.Snapshot.World * selected.ViewProjection(width, height);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue); int clipped = 0, referenced = 0;
        foreach (uint index in frame.Surface.Snapshot.Indices)
        {
            var clip = Vector4.Transform(new Vector4(frame.Surface.Snapshot.Vertices[(int)index].Position, 1), matrix);
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            min = Vector3.Min(min, ndc); max = Vector3.Max(max, ndc); referenced++;
            if (MathF.Abs(ndc.X) > 1 || MathF.Abs(ndc.Y) > 1 || ndc.Z is < 0 or > 1) clipped++;
        }
        static float[] V(Vector3 value) => [value.X, value.Y, value.Z];
        return new(zoom, yaw, pitch, (roomEnabled ? roomCamera! : camera!).Span, selected.OrthographicHeight, V(selected.Eye), V(selected.Target), V(min), V(max), clipped, referenced);
    }

    public void ResetCamera()
    {
        yaw = roomEnabled ? 22 : 0; pitch = roomEnabled ? 16 : 0; zoom = 1; panX=panY=0; cachedFrame = null;
        Changed(nameof(Yaw)); Changed(nameof(Pitch)); Changed(nameof(Zoom));
        Changed(nameof(PanX));Changed(nameof(PanY));
    }

    private static Matrix4x4 SelectedLightWarp(Vector3 center, float span)
    {
        Vector3 eye = center + new Vector3(0, 50, 0), z = -Vector3.UnitY;
        var x = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, z)); var y = Vector3.Cross(z, x);
        var view = new Matrix4x4(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0,
            -Vector3.Dot(x, eye), -Vector3.Dot(y, eye), -Vector3.Dot(z, eye), 1);
        float scale = 1 / (span * 1.6f);
        var projection = new Matrix4x4(2 * scale, 0, 0, 0, 0, 2 * scale, 0, 0, 0, 0, .15f * scale, 0, 0, 0, 0, 1);
        var warp = Matrix4x4.Identity; warp.M24 = .5f; warp.M42 = -1;
        return view * projection * Matrix4x4.CreateScale(1, 1, 2) * Matrix4x4.CreateTranslation(0, 0, -1)
            * warp * Matrix4x4.CreateTranslation(0, 0, 1) * Matrix4x4.CreateScale(1, 1, .5f);
    }

    private SceneTexture? LoadTexture(string root,string relative)=>ReadTexture(root,relative,Remember);
    private static SceneTexture? ReadTexture(string root, string relative,Action<string> remember)
    {
        string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(normalized) || normalized.Contains(':')) throw new InvalidDataException("Texture path must be relative.");
        string file = Path.GetFullPath(Path.Combine(folder, normalized));
        if (!file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Texture path leaves the model directory.");
        if (!File.Exists(file) || Path.GetExtension(file).ToLowerInvariant() is not (".png" or ".bmp" or ".jpg" or ".jpeg")) return null;
        using var input = File.OpenRead(file); using var codec = SKCodec.Create(input);
        if (codec is null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096) throw new InvalidDataException("Invalid texture dimensions or encoding.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("Incomplete texture.");
        byte[] pixels = new byte[checked(info.Width * info.Height * 4)];
        for (int row = 0; row < info.Height; row++) Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), row * bitmap.RowBytes), pixels, row * info.Width * 4, info.Width * 4);
        remember(file); return new(relative, info.Width, info.Height, pixels);
    }
    private void Remember(string file)
    {
        var info = new FileInfo(file);
        sources[file] = new(info.FullName, info.Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant());
    }
    private void Invalidate([CallerMemberName] string? property = null) { cachedFrame = null; Changed(property); }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public void Dispose() { ResetSceneState(); animationSourceModel=null; sceneReady = false; cachedFrame = null; demoRoom = null; roomCamera = null; camera = null; surface = null; outline = null; fittedShadow = fittedRoomShadow = null; }
}
