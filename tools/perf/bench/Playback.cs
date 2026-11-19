using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace PerfBench;

/// <summary>Open-to-first-frame, pipelined playback, serialized submit-to-fence frames and seeks on one GPU session.
/// Rendering keeps the GPU image (no readback), like the realtime viewport minus swap-chain Present.</summary>
static class Playback
{
    public static object Run(BenchConfig config, bool openOnly, long processStart)
    {
        var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
        double runtimeStartup = Math.Max(0, (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - Stopwatch.GetElapsedTime(processStart).TotalMilliseconds);
        uint required = openOnly ? 0u : (uint)(config.Warmup + config.Frames + config.SerialFrames + 2);
        long openStart = Stopwatch.GetTimestamp();
        var scene = new BenchScene(config, required);
        double loadMs = Since(openStart);
        long tick = Stopwatch.GetTimestamp();
        using var session = scene.CreateSession();
        double sessionMs = Since(tick);
        tick = Stopwatch.GetTimestamp();
        using var gpu = new D3D12FrameSession();
        double deviceMs = Since(tick);
        if (gpu.IsWarp) throw new InvalidOperationException("Hardware GPU required; WARP timings are not comparable.");
        var gi = gpu.QueryRayGiCapabilities();
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
            ["schema"] = Output.Schema, ["kind"] = openOnly ? "open" : "play", ["key"] = config.Key, ["tags"] = config.Tags,
            ["config"] = config.Describe(), ["scene"] = scene.Describe(), ["build"] = BuildInfo.Describe(),
            ["adapter"] = gpu.AdapterName, ["giHardwareAvailable"] = gi.HardwareAvailable, ["giUnavailableReason"] = gi.UnavailableReason,
            ["dxcCache"] = caches.dxc, ["caches"] = caches, ["processors"] = Environment.ProcessorCount,
        };
        if (config.Gi && !gi.HardwareAvailable)
        {
            common["status"] = "unsupported";
            return common;
        }
        var stats = new StatReader(gpu);
        int w = config.Width, h = config.Height;
        var options = config.GiOptions;
        tick = Stopwatch.GetTimestamp();
        using (gpu.Render(scene.Prepare(session, session.Frame), w, h, options)) { }
        double submitMs = Since(tick);
        gpu.WaitForIdle();
        double firstFrameMs = Since(tick);
        metrics["open.toFirstFrameMs"] = Since(openStart);
        metrics["open.processToFirstFrameMs"] = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds + runtimeStartup;
        metrics["open.runtimeStartupMs"] = runtimeStartup;
        metrics["open.loadMs"] = loadMs; metrics["open.sessionMs"] = sessionMs; metrics["open.deviceMs"] = deviceMs;
        metrics["open.firstFrameMs"] = firstFrameMs; metrics["open.firstSubmitMs"] = submitMs;
        foreach (var (name, value) in stats.ReadAll()) if (name is "ShaderCompilations" or "PipelineCreations" or "TextureUploads" or "BufferUploads") metrics["open.stat." + name] = value;
        common["giEffective"] = stats.GiEffective();
        if (config.Gi && common["giEffective"] is string effective && effective != nameof(LightingMode.HardwareRayTracing))
            throw new InvalidOperationException($"Requested hardware GI but the frame executed {effective}.");
        if (openOnly) { common["status"] = "ok"; common["metrics"] = metrics; return common; }

        // Pipelined playback: Advance then submit, no explicit wait; the renderer's own frame slots apply back-pressure.
        const double step = 1d / 30;
        for (int i = 0; i < config.Warmup; i++) using (gpu.Render(scene.Prepare(session, session.Advance(step)), w, h, options)) { }
        gpu.WaitForIdle();
        string before = BenchScene.PoseHash(session.Frame);
        double sessionFrameBefore = session.CurrentFrame;
        int n = config.Frames;
        double[] pose = new double[n], render = new double[n], frame = new double[n];
        var perFrameStats = stats.Names.ToDictionary(name => name, _ => new double[n]);
        var counters = stats.ReadAll().ToDictionary(p => p.Name, p => p.Value);
        // Parity runs write a frame-major stream of solved matrices in scene actor order.
        // Timed runs leave this option unset so reading the diagnostic pose cannot affect results.
        using var boneAudit = config.BoneAuditPath is { } auditPath ? File.Open(auditPath, FileMode.CreateNew, FileAccess.Write) : null;
        using var morphAudit = config.MorphAuditPath is { } morphPath ? new BinaryWriter(File.Open(morphPath, FileMode.CreateNew, FileAccess.Write)) : null;
        using var process = Process.GetCurrentProcess();
        using var timing = config.FrameTiming ? new FrameTiming(n) : null;
        var gc = GcWindow.Start();
        var cpuBefore = process.TotalProcessorTime;
        common["playWindowStartUtc"] = DateTimeOffset.UtcNow;
        long batchStart = Stopwatch.GetTimestamp(), previous = batchStart;
        for (int i = 0; i < n; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var next = session.Advance(step);
            long t1 = Stopwatch.GetTimestamp();
            var view = scene.Prepare(session, next);
            long prepared = timing is null ? 0 : Stopwatch.GetTimestamp();
            using (gpu.Render(view, w, h, options)) { }
            long t2 = Stopwatch.GetTimestamp();
            timing?.Record(i, Ms(t1, prepared));
            pose[i] = Ms(t0, t1); render[i] = Ms(t1, t2); frame[i] = Ms(previous, t2); previous = t2;
            stats.Read(perFrameStats, i);
            if (morphAudit is not null) WriteMorphAudit(morphAudit, next.Surface.Snapshot);
            if (boneAudit is not null)
                foreach (var actor in scene.Motions)
                    boneAudit.Write(MemoryMarshal.AsBytes(session.GetBoneWorldMatrices(actor.ObjectId).ToArray().AsSpan()));
        }
        gpu.WaitForIdle();
        double batchMs = Since(batchStart);
        common["playWindowEndUtc"] = DateTimeOffset.UtcNow;
        metrics["play.processCpuEquivalentCores"] = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / batchMs;
        gc.Stop(metrics, "play.gc", n);
        string after = BenchScene.PoseHash(session.Frame);
        double sessionFrameAfter = session.CurrentFrame;
        metrics["play.fps"] = n * 1000 / batchMs;
        metrics["play.batchMsPerFrame"] = batchMs / n;
        Percentiles(metrics, "play.frameMs", frame);
        Percentiles(metrics, "play.poseMs", pose);
        Percentiles(metrics, "play.renderCallMs", render);
        stats.ToPerFrame(perFrameStats, counters);
        foreach (var (name, values) in perFrameStats)
            if (values.Any(v => !double.IsNaN(v))) Percentiles(metrics, "play.stat." + name, values.Where(v => !double.IsNaN(v)).ToArray(), p99: false);
        bool hasGpuTimestamp = metrics.ContainsKey("play.stat.GpuFrameMilliseconds.p50") || metrics.ContainsKey("play.stat.GpuMilliseconds.p50");
        common["gpuTimingSource"] = hasGpuTimestamp ? "timestamp" : "submit-to-fence";

        // Serialized frames: each submit is followed by a full fence wait, so the wait approximates GPU execution
        // plus wake-up latency. No public full-frame GPU timestamp exists, so this is the GPU time proxy.
        double[] serialRender = new double[config.SerialFrames], fence = new double[config.SerialFrames], serial = new double[config.SerialFrames];
        for (int i = 0; i < config.SerialFrames; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var next = scene.Prepare(session, session.Advance(step));
            long t1 = Stopwatch.GetTimestamp();
            using (gpu.Render(next, w, h, options)) { }
            long t2 = Stopwatch.GetTimestamp();
            gpu.WaitForIdle();
            long t3 = Stopwatch.GetTimestamp();
            serialRender[i] = Ms(t1, t2); fence[i] = Ms(t2, t3); serial[i] = Ms(t0, t3);
        }
        if (config.SerialFrames > 0)
        {
            Percentiles(metrics, "serial.submitToFenceMs", fence);
            Percentiles(metrics, "serial.renderCallMs", serialRender);
            Percentiles(metrics, "serial.frameMs", serial);
        }

        // Seek: absolute integer-frame seek, the host's physics initialization, then one completed frame.
        double[] seeks = new double[config.Seeks];
        for (int i = 0; i < config.Seeks; i++)
        {
            double target = (i * 97 + 31) % Math.Max(1, (int)scene.DurationFrames - 1);
            long t0 = Stopwatch.GetTimestamp();
            session.Seek(target);
            var settled = session.StabilizePhysicsAtCurrentFrame(scene.InitializationSubsteps);
            using (gpu.Render(scene.Prepare(session, settled), w, h, options)) { }
            gpu.WaitForIdle();
            seeks[i] = Since(t0);
        }
        if (config.Seeks > 0) Percentiles(metrics, "seek.ms", seeks, p99: false);

        // Out-of-band check: if CPU vertices match (e.g. GPU skinning branches), verify GPU-rendered output changed.
        bool cpuPoseChanged = before != after;
        bool? pixelAdvanced = null;
        if (!cpuPoseChanged)
        {
            session.Seek(0);
            var f0 = gpu.Capture(scene.Prepare(session, session.StabilizePhysicsAtCurrentFrame(scene.InitializationSubsteps)), w, h, options);
            session.Seek(Math.Min(scene.DurationFrames, Math.Max(1u, (uint)Math.Floor(sessionFrameAfter))));
            var f1 = gpu.Capture(scene.Prepare(session, session.StabilizePhysicsAtCurrentFrame(scene.InitializationSubsteps)), w, h, options);
            pixelAdvanced = !f0.Rgba.AsSpan().SequenceEqual(f1.Rgba);
        }

        common["status"] = "ok";
        common["poseAdvanced"] = cpuPoseChanged || (pixelAdvanced == true);
        common["cpuPoseChanged"] = cpuPoseChanged;
        common["pixelAdvanced"] = pixelAdvanced;
        common["sessionFrameBefore"] = sessionFrameBefore;
        common["sessionFrameAfter"] = sessionFrameAfter;
        common["poseHashBefore"] = before; common["poseHashAfter"] = after;
        common["metrics"] = metrics;
        if (timing is not null) common["frameTiming"] = timing.Rows;
        common["frames"] = new { frameMs = frame, poseMs = pose, renderCallMs = render, stats = perFrameStats.Where(p => p.Value.Any(v => !double.IsNaN(v))).ToDictionary(p => p.Key, p => p.Value),
            serialSubmitToFenceMs = fence, serialFrameMs = serial, seekMs = seeks };
        return common;
    }

    private static void WriteMorphAudit(BinaryWriter writer, SceneSnapshot snapshot)
    {
        // Per-frame CPU reference vertices (including UV), followed by the ordered
        // sparse inputs actually published to the GPU. Keep both out of timed runs.
        writer.Write(SHA256.HashData(MemoryMarshal.AsBytes(snapshot.Vertices)));
        writer.Write(snapshot.DeformationRanges.Count);
        foreach (var range in snapshot.DeformationRanges)
        {
            writer.Write(range.Start);
            writer.Write(range.Count);
            var morph = range.Deformation.Morphs;
            writer.Write(morph?.Indices.Length ?? -1);
            if (morph is null) continue;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(MemoryMarshal.AsBytes(morph.Indices));
            hash.AppendData(MemoryMarshal.AsBytes(morph.Positions));
            hash.AppendData(MemoryMarshal.AsBytes(morph.Uvs));
            writer.Write(hash.GetHashAndReset());
        }
    }

    public static double Since(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    public static double Ms(long start, long end) => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    public static void Percentiles(IDictionary<string, double> metrics, string name, double[] values, bool p99 = true)
    {
        if (values.Length == 0) return;
        var sorted = values.Order().ToArray();
        metrics[name + ".p50"] = Percentile(sorted, .50);
        metrics[name + ".p95"] = Percentile(sorted, .95);
        if (p99) metrics[name + ".p99"] = Percentile(sorted, .99);
        metrics[name + ".mean"] = sorted.Average();
        metrics[name + ".max"] = sorted[^1];
    }

    /// <summary>Linear interpolation between closest ranks (Hyndman-Fan type 7).</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        double rank = p * (sorted.Length - 1);
        int low = (int)Math.Floor(rank), high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
}

/// <summary>Process-wide managed allocation, collection counts and pause time over a measured window.
/// Includes every thread (renderer, physics and harness), so read it as pressure, not as resident memory.</summary>
sealed class GcWindow
{
    private long bytes; private int gen0, gen1, gen2; private TimeSpan pause;
    public static GcWindow Start() => new()
    {
        bytes = GC.GetTotalAllocatedBytes(true), gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1),
        gen2 = GC.CollectionCount(2), pause = GC.GetTotalPauseDuration()
    };
    public void Stop(IDictionary<string, double> metrics, string prefix, int frames)
    {
        metrics[prefix + ".allocBytesPerFrame"] = (GC.GetTotalAllocatedBytes(true) - bytes) / (double)frames;
        metrics[prefix + ".gen0Per100Frames"] = (GC.CollectionCount(0) - gen0) * 100d / frames;
        metrics[prefix + ".gen1Per100Frames"] = (GC.CollectionCount(1) - gen1) * 100d / frames;
        metrics[prefix + ".gen2Per100Frames"] = (GC.CollectionCount(2) - gen2) * 100d / frames;
        metrics[prefix + ".pauseMsPerFrame"] = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds / frames;
    }
}

/// <summary>Reads optional renderer statistics by name. Fields that a branch renames or removes are simply absent,
/// so the harness compiles and runs unchanged across branches.</summary>
sealed class StatReader(D3D12FrameSession session)
{
    public readonly string[] Names = ["RecordCpuMilliseconds", "SubmitCpuMilliseconds", "LastSlotWaitMilliseconds", "FrameTargetMilliseconds",
        "SceneRecordMilliseconds", "TextureBindingMilliseconds", "GeometryCacheMilliseconds", "GpuFrameMilliseconds", "GpuMilliseconds",
        "RecordAllocatedBytes", "FrameOwnedAllocations", "SceneDrawCalls", "ShaderCompilations", "PipelineCreations", "TextureUploads", "BufferUploads"];
    // Monotonic totals in the current statistics; reported as per-frame increments.
    private static readonly string[] Cumulative = ["ShaderCompilations", "PipelineCreations", "TextureUploads", "BufferUploads"];
    private static readonly PropertyInfo? Source = typeof(D3D12FrameSession).GetProperty("Statistics");
    private static readonly PropertyInfo? GiSource = typeof(D3D12FrameSession).GetProperty("RayGiStatistics");
    private Type? type;
    private PropertyInfo?[] properties = [];

    private object? Current()
    {
        object? value;
        try { value = Source?.GetValue(session); } catch (TargetInvocationException) { return null; }
        if (value is null) return null;
        if (value.GetType() != type) { type = value.GetType(); properties = Names.Select(n => type.GetProperty(n)).ToArray(); }
        return value;
    }

    public void Read(Dictionary<string, double[]> target, int index)
    {
        var value = Current();
        for (int i = 0; i < Names.Length; i++)
            target[Names[i]][index] = value is not null && properties[i]?.GetValue(value) is { } raw && IsNumber(raw) ? Convert.ToDouble(raw) : double.NaN;
    }

    public void ToPerFrame(Dictionary<string, double[]> target, IReadOnlyDictionary<string, double> before)
    {
        foreach (string name in Cumulative)
        {
            if (!target.TryGetValue(name, out var values) || !before.TryGetValue(name, out double previous)) continue;
            for (int i = 0; i < values.Length; i++) { double total = values[i]; values[i] = total - previous; previous = total; }
        }
    }

    public IEnumerable<(string Name, double Value)> ReadAll()
    {
        var value = Current();
        if (value is null) yield break;
        for (int i = 0; i < Names.Length; i++)
            if (properties[i]?.GetValue(value) is { } raw && IsNumber(raw)) yield return (Names[i], Convert.ToDouble(raw));
    }

    public string? GiEffective()
    {
        try { var gi = GiSource?.GetValue(session); return gi?.GetType().GetProperty("Effective")?.GetValue(gi)?.ToString(); }
        catch (TargetInvocationException) { return null; }
    }

    private static bool IsNumber(object value) => value is int or long or double or float or uint or ulong;
}
