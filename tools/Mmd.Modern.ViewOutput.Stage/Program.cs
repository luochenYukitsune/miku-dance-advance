using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.FFmpeg;
using Mmd.Modern.ViewOutput.Images;

bool fsr1 = args.Contains("--fsr1", StringComparer.Ordinal);
bool temporalFsr = args.Contains("--temporal-fsr", StringComparer.Ordinal);
bool temporalXeSs = args.Contains("--temporal-xess", StringComparer.Ordinal);
if ((fsr1 ? 1 : 0) + (temporalFsr ? 1 : 0) + (temporalXeSs ? 1 : 0) > 1)
    throw new ArgumentException("Choose only one of --fsr1, --temporal-fsr or --temporal-xess.");
args = args.Where(arg => arg is not ("--fsr1" or "--temporal-fsr" or "--temporal-xess")).ToArray();
string? modelPath = null;
int modelFlag = Array.IndexOf(args, "--pmx");
if (modelFlag >= 0)
{
    if (modelFlag != args.Length - 2) throw new ArgumentException("--pmx requires one final model path.");
    modelPath = args[^1]; args = args[..modelFlag];
}
if (args.Length is not (1 or 3))
    throw new ArgumentException("Usage: ViewOutput.Stage <new-output-directory> [<absolute-ffmpeg.exe> <absolute-ffprobe.exe>] [--fsr1|--temporal-fsr|--temporal-xess] [--pmx <read-only-model.pmx>]");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Choose a new stage directory to preserve prior evidence.");
Directory.CreateDirectory(output);
if (temporalFsr || temporalXeSs)
{
    if (modelPath is null || args.Length != 1)
        throw new ArgumentException("Temporal modes require --pmx and no FFmpeg arguments; they run only the temporal real-model stage.");
    var temporalResult = await RealModelTemporalCapture.RunAsync(modelPath, output,
        temporalXeSs ? D3D12TemporalUpscalingMode.XeSs : D3D12TemporalUpscalingMode.Fsr);
    string temporalJson = JsonSerializer.Serialize(temporalResult, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), temporalJson);
    Console.WriteLine(temporalJson);
    return;
}
var results = new Dictionary<string, object?>();
var quality = fsr1 ? new ViewQualityOptions(Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }) : null;
var request = new ViewRenderRequest(192, 128, quality);
using (var sceneSession = await StageScene.CreateSession(CancellationToken.None))
{
    var plan = new OfflineFramePlan(request.Width, request.Height, new(30), 0, 0);
    var frame = new OfflineFrame(plan.GetSample(0), request.Width, request.Height, sceneSession.Frame, null, null, null);
    await using var adapter = new D3D12ViewRenderer(request);
    using var captured = await adapter.CaptureAsync(frame);
    using var baselineRenderer = new D3D12FrameSession();
    var baseline = baselineRenderer.Capture(frame.Scene, request.Width, request.Height, rayGiOptions: RayGiOptions.Off,
        upscaling: fsr1 ? new() { Mode = D3D12SpatialUpscalingMode.Fsr1 } : null);
    Require(captured.Pixels.Span.SequenceEqual(baseline.Rgba), "Opaque D3D12 adapter pixels differ from direct Capture.");
    Require(adapter.Statistics?.ReadbackCount == 1, "Explicit capture must record exactly one final readback.");
    if (fsr1)
    {
        Require(!adapter.IsWarp, "The FSR1 stage requires a real hardware adapter.");
        Require(adapter.Statistics is { SpatialUpscaling: D3D12SpatialUpscalingMode.Fsr1,
            UpscalingInputWidth: 128, UpscalingInputHeight: 85, UpscalingOutputWidth: 192, UpscalingOutputHeight: 128 },
            "FSR1 adapter did not use separate render and output dimensions.");
    }
    results["capture"] = new { adapter.AdapterName, adapter.IsWarp, adapter.Capabilities, exactDirectCapture = true,
        rgbaSha256 = Convert.ToHexString(SHA256.HashData(captured.Pixels.Span)), adapter.Statistics };
    await using var single = new PngFrameSink(Path.Combine(output, "capture.png"));
    await single.BeginAsync(new(plan)); await single.WriteAsync(plan.GetSample(0), captured); await single.CompleteAsync();
    var empty = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(frame.Scene.Surface.Snapshot.Camera, frame.Scene.Surface.Snapshot.LightDirection), []));
    using var emptyPixels = await adapter.CaptureAsync(frame with { Scene = empty });
    Require(emptyPixels.Pixels.Length == request.Width * request.Height * 4, "Empty capture length differs.");
    results["emptyCapture"] = true;
    var transparent = new OriginalFrameScene(frame.Scene.Surface, frame.Scene.Outline, frame.Scene.OutlineOptions,
        new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero), frame.Scene.Materials);
    if (fsr1)
    {
        bool rejected = false;
        try { using var invalid = await adapter.CaptureAsync(frame with { Scene = transparent }); }
        catch (NotSupportedException) { rejected = true; }
        Require(rejected, "FSR1 transparent capture must remain explicitly unsupported.");
        results["transparentCapture"] = new { supported = false, explicitlyRejected = true,
            scope = "FSR1 spatial upscaling does not support transparent backgrounds." };
    }
    else
    {
        using var transparentPixels = await adapter.CaptureAsync(frame with { Scene = transparent });
        Require(transparentPixels.Alpha == FrameAlphaMode.Straight, "Transparent capture must declare straight alpha.");
        var coverage = CheckTransparentCoverage(transparentPixels);
        Require(coverage.TransparentPixels > 0, "Transparent capture contains no alpha-zero background pixels.");
        Require(coverage.VisibleColorPixels > 0, "Transparent capture contains no visible colored geometry.");
        Require(adapter.Statistics?.ReadbackCount == 1, "Transparent capture must record exactly one final readback.");
        const string file = "transparent.png";
        await using var transparentPng = new PngFrameSink(Path.Combine(output, file));
        await transparentPng.BeginAsync(new(plan));
        await transparentPng.WriteAsync(plan.GetSample(0), transparentPixels);
        await transparentPng.CompleteAsync();
        results["transparentCapture"] = new { supported = true, file,
            alphaMode = transparentPixels.Alpha.ToString(), coverage.TransparentPixels, coverage.VisibleColorPixels,
            rgbaSha256 = Convert.ToHexString(SHA256.HashData(transparentPixels.Pixels.Span)), adapter.Statistics,
            scope = "Native D3D12 straight-alpha PNG capture of the self-authored triangle; no upscaling, temporal reconstruction or MME coverage claim." };
    }
}

ExportCoordinator Coordinator() => new(StageScene.CreateSession, (render, token) =>
{ token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IViewRenderer>(new D3D12ViewRenderer(render)); });
var clip = new ExportRequest(new(request.Width, request.Height, new(30000,1001), 0, 4), quality);
results["sequence"] = await Coordinator().RunAsync(clip, new PngSequenceSink(Path.Combine(output, "sequence")));
results["presentation"] = CheckPresentation(request);
if (modelPath is not null) results["realPmx"] = await RealModelCapture.RunAsync(modelPath, output,
    args.Length == 3 ? new FfmpegToolchain(args[1], args[2]) : null, fsr1);
if (args.Length == 3)
{
    var tools = new FfmpegToolchain(args[1], args[2]);
    foreach (var encoder in new[] { VideoEncoder.RawVideo, VideoEncoder.H264Software, VideoEncoder.HevcSoftware, VideoEncoder.Av1AomSoftware })
    {
        string extension = encoder == VideoEncoder.RawVideo ? ".avi" : ".mp4";
        var sink = new FfmpegFrameSink(tools, new(Path.Combine(output, encoder + extension), encoder));
        var result = await Coordinator().RunAsync(clip, sink);
        Require(sink.Validation is { Decoded: true }, "FFmpeg output was not decoded and validated.");
        results[encoder.ToString()] = new { result, sink.Validation, audio = "none" };
    }
}
await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

static (int TransparentPixels, int VisibleColorPixels) CheckTransparentCoverage(FrameBufferLease frame)
{
    int transparentPixels = 0, visibleColorPixels = 0;
    var pixels = frame.Pixels.Span;
    for (int y = 0; y < frame.Height; y++)
    for (int x = 0; x < frame.Width; x++)
    {
        int offset = y * frame.Stride + x * 4;
        if (pixels[offset + 3] == 0) transparentPixels++;
        else if (pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0) visibleColorPixels++;
    }
    return (transparentPixels, visibleColorPixels);
}

static object CheckPresentation(ViewRenderRequest request)
{
    nint hwnd = Native.CreateWindowExW(0, "STATIC", "ViewOutput stage hidden viewport", 0x80000000u, 0, 0, request.Width, request.Height, 0, 0, 0, 0);
    if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    try
    {
        var session = StageScene.CreateSession(CancellationToken.None).Result;
        using (session)
        {
            var adapter = new D3D12RealtimeViewRenderer(hwnd, request);
            try
            {
                var frame = new PreviewFrame(request.Width, request.Height, 0, session.Frame, null, null, null);
                adapter.PresentAsync(frame).GetAwaiter().GetResult();
                adapter.ResizeAsync(128, 96).GetAwaiter().GetResult();
                adapter.PresentAsync(frame with { Width = 128, Height = 96 }).GetAwaiter().GetResult();
                var statistics = adapter.Statistics;
                Require(statistics.PresentCount == 2 && statistics.ExplicitCaptureCount == 0 && statistics.Frame?.ReadbackCount == 0, "Preview performed CPU readback.");
                Require(statistics.ResizeCount == 1 && statistics.Width == 128 && statistics.Height == 96, "Native swapchain did not resize.");
                if (request.EffectiveQuality.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1)
                {
                    Require(!adapter.IsWarp, "The FSR1 presentation stage requires hardware.");
                    Require(statistics.Frame is { SpatialUpscaling: D3D12SpatialUpscalingMode.Fsr1,
                        UpscalingInputWidth: 85, UpscalingInputHeight: 64, UpscalingOutputWidth: 128, UpscalingOutputHeight: 96 },
                        "FSR1 presentation resize did not update both input and output dimensions.");
                }
                return new { adapter.AdapterName, adapter.IsWarp, statistics };
            }
            finally { adapter.DisposeAsync().GetAwaiter().GetResult(); }
        }
    }
    finally { Native.DestroyWindow(hwnd); }
}

internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
}
