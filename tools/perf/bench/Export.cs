using System.Diagnostics;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.FFmpeg;

namespace PerfBench;

/// <summary>Product export path: ExportCoordinator, D3D12ViewRenderer and a real FFmpeg pipe (HEVC software, CQ 23,
/// Balanced, no audio). Two exports per process: the first includes device/shader warm-up, the second is warm.</summary>
static class Export
{
    public static async Task<object> RunAsync(BenchConfig config, long processStart)
    {
        if (string.IsNullOrEmpty(config.Ffmpeg) || string.IsNullOrEmpty(config.Ffprobe) || !File.Exists(config.Ffmpeg) || !File.Exists(config.Ffprobe))
            throw new FileNotFoundException("Set MMD_FFMPEG_PATH/MMD_FFPROBE_PATH or pass --ffmpeg/--ffprobe.");
        var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var scene = new BenchScene(config, (uint)config.ExportFrames + 2);
        var caches = new
        {
            dxc = Environment.GetEnvironmentVariable("MMD_DXC_CACHE"),
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            shader = Environment.GetEnvironmentVariable("MMD_SHADER_CACHE"),
            fxc = Environment.GetEnvironmentVariable("MMD_FXC_CACHE"),
            pso = Environment.GetEnvironmentVariable("MMD_PSO_CACHE"),
        };
        var common = new Dictionary<string, object?>
        {
            ["schema"] = Output.Schema, ["kind"] = "export", ["key"] = config.Key, ["tags"] = config.Tags,
            ["config"] = config.Describe(), ["scene"] = scene.Describe(), ["build"] = BuildInfo.Describe(),
            ["dxcCache"] = caches.dxc, ["caches"] = caches, ["processors"] = Environment.ProcessorCount,
            ["encoder"] = "HevcSoftware CQ23 Balanced",
        };
        if (config.Gi)
        {
            // Explicit capability check; this extra device is created before any timed export.
            using var probe = new D3D12FrameSession();
            if (probe.IsWarp) throw new InvalidOperationException("Hardware GPU required; WARP timings are not comparable.");
            var gi = probe.QueryRayGiCapabilities();
            common["adapter"] = probe.AdapterName; common["giHardwareAvailable"] = gi.HardwareAvailable; common["giUnavailableReason"] = gi.UnavailableReason;
            if (!gi.HardwareAvailable) { common["status"] = "unsupported"; return common; }
        }
        var tools = new FfmpegToolchain(config.Ffmpeg, config.Ffprobe);
        var runs = new List<object>();
        for (int run = 0; run < 2; run++)
        {
            var plan = new OfflineFramePlan(config.Width, config.Height, new(30), 0, (uint)config.ExportFrames - 1);
            string video = Path.ChangeExtension(config.OutPath, $".export{run}.mp4");
            var sink = new FfmpegFrameSink(tools, new(video, VideoEncoder.HevcSoftware)
                { RateControl = VideoRateControl.ConstantQuality, Quality = 23, Speed = VideoSpeed.Balanced });
            var coordinator = new ExportCoordinator(_ => ValueTask.FromResult(scene.CreateSession()),
                (request, _) =>
                {
                    var renderer = new D3D12ViewRenderer(request);
                    if (renderer.IsWarp) { renderer.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw new InvalidOperationException("Hardware GPU required; WARP timings are not comparable."); }
                    common.TryAdd("adapter", renderer.AdapterName);
                    return ValueTask.FromResult<IViewRenderer>(renderer);
                });
            var progress = new FrameTimes();
            var gc = GcWindow.Start();
            long start = Stopwatch.GetTimestamp();
            try
            {
                var result = await coordinator.RunAsync(new ExportRequest(plan, config.Quality), sink, progress);
                double total = Playback.Since(start);
                var runMetrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
                gc.Stop(runMetrics, "gc", config.ExportFrames);
                string backend = result.BackendId;
                if (result.CompletedFrames != plan.FrameCount || sink.Validation is null) throw new InvalidOperationException("Incomplete video.");
                // Steady rate excludes the first quarter (session, renderer and encoder start-up) and the encoder flush.
                var times = progress.Times(start);
                int skip = Math.Max(1, times.Count / 4);
                double steady = times.Count - 1 > skip ? (times.Count - 1 - skip) * 1000 / (times[^1] - times[skip]) : double.NaN;
                runMetrics["totalMs"] = total; runMetrics["fps"] = config.ExportFrames * 1000 / total; runMetrics["steadyFps"] = steady;
                runMetrics["firstFrameMs"] = times.Count > 0 ? times[0] : double.NaN;
                foreach (var (name, value) in runMetrics) metrics[$"export.run{run}.{name}"] = value;
                runs.Add(new { run, backend, video = config.KeepVideo ? video : null, validation = sink.Validation, frameCompletedMs = times, metrics = runMetrics });
            }
            finally
            {
                if (!config.KeepVideo && File.Exists(video)) File.Delete(video);
            }
        }
        common["status"] = "ok";
        common["metrics"] = metrics;
        common["runs"] = runs;
        common["processMs"] = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
        return common;
    }

    /// <summary>Synchronous progress sink: records the completion time of each exported frame.</summary>
    sealed class FrameTimes : IProgress<ExportTaskProgress>
    {
        private readonly List<(long Frames, long Tick)> marks = [];
        public void Report(ExportTaskProgress value)
        {
            long now = Stopwatch.GetTimestamp();
            lock (marks) if (value.State == ExportTaskState.Running && (marks.Count == 0 || marks[^1].Frames < value.CompletedFrames)) marks.Add((value.CompletedFrames, now));
        }
        public List<double> Times(long start)
        {
            lock (marks) return marks.Select(m => Playback.Ms(start, m.Tick)).ToList();
        }
    }
}
