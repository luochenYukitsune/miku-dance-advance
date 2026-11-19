using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics.Bullet;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.Smoke;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.FFmpeg;

if (args.Length < 3 || args[0] is not ("cpu" or "phases" or "gpu" or "export") ||
    args.Length != (args[0] == "export" ? 5 : 3))
    throw new ArgumentException("FrameStudy cpu|phases|gpu|export model.pmx new-directory [ffmpeg.exe ffprobe.exe]");
var mode = args[0];
var path = Path.GetFullPath(args[1]);
var directory = Path.GetFullPath(args[2]);
if (Directory.Exists(directory)) throw new IOException("Choose a new results directory.");
Directory.CreateDirectory(directory);
var json = new JsonSerializerOptions { WriteIndented = true };
void Save(string name, object data) => File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(data, json));

var model = PmxReader.Read(path);
var style = PmxOriginalStyleAdapter.FromRestPose(model, relative => SceneTextureLoader.Load(Path.GetDirectoryName(path)!, relative));
var mesh = style.Scene.Snapshot;
Vector3 min = new(float.MaxValue), max = new(float.MinValue);
foreach (var v in mesh.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
var center = (min + max) / 2;
var span = Math.Max(.1f, (max - min).Length());
var camera = new SceneCamera(center - Vector3.UnitZ * span * 2, center, Vector3.UnitY, span * 1.2f, .01f, span * 5);
var rest = new OriginalFrameScene(new(mesh.WithCamera(camera), style.Scene.Materials, style.Scene.Profile),
    PmxOriginalOutlineAdapter.FromRestPose(model), new(new(center, 1), Matrix4x4.Identity, camera.Eye, 30, 1, false),
    materials: PmxOriginalFrameAdapter.FromMaterials(model));
// Some PMX files start with an editor control that influences no vertices.
var weights = new double[model.Bones.Count];
foreach (var vertex in model.Vertices)
    for (int i = 0; i < vertex.BoneIndices.Count; i++)
        if (vertex.BoneIndices[i] >= 0) weights[vertex.BoneIndices[i]] += vertex.BoneWeights[i];
var bone = model.Bones[Array.IndexOf(weights, weights.Max())];
var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = bone.Name },
    new() { BoneName = bone.Name, Frame = 90, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .65f) }] };
using (var file = File.OpenRead(path))
    Save("fixture", new { mode, model.Name, sha256 = Convert.ToHexString(SHA256.HashData(file)),
        vertices = model.Vertices.Count, materials = model.Materials.Count, bodies = model.RigidBodies.Count,
        joints = model.Joints.Count, softBodies = model.SoftBodies.Count, textures = mesh.Textures.Count,
        animatedBone = bone.Name, runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount });

SceneAnimationSession Create(int actors, bool physics)
{
    var layout = new SceneCompositionEditor();
    var bindings = new List<SceneActorMotion>();
    for (int i = 0; i < actors; i++)
    {
        var actor = layout.Add($"actor-{i}", path, ModelRole.Character, rest);
        layout.SetTransform(actor.Id, Vector3.UnitX * i * span, Quaternion.Identity, 1);
        bindings.Add(new(actor.Id, model, motion, PhysicsEnabled: physics));
    }
    return new(layout.Objects, bindings, _ => new BulletCharacterPhysicsBackend(),
        sharedPhysicsFactory: physics ? () => new BulletCharacterPhysicsBackend() : null);
}

const int warm = 8, frames = 32;
static double Milliseconds(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
static string PoseHash(OriginalFrameScene frame) =>
    Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(frame.Surface.Snapshot.Vertices)));

if (mode == "cpu")
{
    var cases = (from actors in new[] { 1, 3 } from physics in new[] { false, true }
                 from degree in new[] { 1, 4, 8 } select (actors, physics, degree)).ToArray();
    var hashes = new Dictionary<(int, bool), string>();
    for (int run = 0; run < 2; run++)
    foreach (var (actors, physics, degree) in run == 0 ? cases : cases.Reverse())
    {
        AppContext.SetData("Mmd.Modern.Animation.ParallelSkinning.Degree", degree);
        using var session = Create(actors, physics);
        if (session.UsesSharedPhysicsWorld != physics) throw new InvalidOperationException("Unexpected physics route.");
        string initial = PoseHash(session.Frame);
        var rows = new List<object>();
        for (int i = 0; i < warm + frames; i++)
        {
            long bytes = GC.GetTotalAllocatedBytes(true), start = Stopwatch.GetTimestamp();
            session.Advance(1d / 30);
            double ms = Milliseconds(start);
            if (i >= warm) rows.Add(new { ms, bytes = GC.GetTotalAllocatedBytes(true) - bytes });
        }
        string hash = PoseHash(session.Frame);
        if (hash == initial || (hashes.TryGetValue((actors, physics), out string? other) && other != hash))
            throw new InvalidOperationException("Pose did not advance or differs across worker counts.");
        hashes[(actors, physics)] = hash;
        Save($"cpu-{actors}-{physics}-{degree}-{run}", new { actors, physics, degree, run, hash,
            steps = session.SharedPhysicsStepCount, session.CurrentFrame, rows });
    }
}
else if (mode == "phases")
{
    foreach (int actors in new[] { 1, 3 })
    {
        using var session = Create(actors, true);
        for (int i = 0; i < warm; i++) session.Advance(1d / 30);
        using var events = new PosePhases();
        var rows = new List<object>();
        for (int i = 0; i < frames; i++)
        {
            events.Ticks.Clear();
            long start = Stopwatch.GetTimestamp();
            session.Advance(1d / 30);
            rows.Add(new { ms = Milliseconds(start), phases = events.Ticks.ToDictionary(p => p.Key,
                p => p.Value * 1000d / Stopwatch.Frequency) });
        }
        Save($"phases-{actors}", new { actors, rows });
    }
}
else if (mode == "gpu")
{
    foreach (int width in new[] { 960, 1920 })
    foreach (bool capture in new[] { false, true, true, false })
    {
        int height = width == 960 ? 544 : 1080;
        using var gpu = new D3D12FrameSession();
        if (gpu.IsWarp) throw new InvalidOperationException("Hardware GPU required.");
        using var session = Create(1, false);
        var rows = new List<object>();
        var before = gpu.Capture(session.Frame, width, height).Rgba;
        for (int i = 0; i < warm; i++) Draw(session.Advance(1d / 30));
        gpu.WaitForIdle();
        long batchStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < frames; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            var frame = session.Advance(1d / 30);
            double pose = Milliseconds(tick);
            tick = Stopwatch.GetTimestamp();
            Draw(frame);
            var draw = Milliseconds(tick);
            var stats = gpu.Statistics!;
            if (stats.ReadbackCount != (capture ? 1 : 0)) throw new InvalidOperationException("Unexpected readback count.");
            rows.Add(new { pose, draw, stats.FrameTargetMilliseconds, stats.RecordCpuMilliseconds,
                stats.TextureBindingMilliseconds, stats.SceneRecordMilliseconds, stats.GeometryCacheMilliseconds,
                stats.LastSlotWaitMilliseconds, stats.FrameOwnedAllocations, stats.SceneDrawCalls });
        }
        gpu.WaitForIdle();
        double elapsed = Milliseconds(batchStart);
        var after = gpu.Capture(session.Frame, width, height).Rgba;
        if (before.AsSpan().SequenceEqual(after)) throw new InvalidOperationException("Expected visible motion.");
        string prefix = $"gpu-{width}-{capture}";
        int run = Directory.GetFiles(directory, prefix + "-*.json").Length;
        Save($"{prefix}-{run}", new { width, height, capture, run, elapsed, frames, gpu.AdapterName,
            hash = Convert.ToHexString(SHA256.HashData(after)), rows });
        void Draw(OriginalFrameScene frame)
        {
            if (capture) { _ = gpu.Capture(frame, width, height); }
            else { using var output = gpu.Render(frame, width, height); }
        }
    }
}
else if (mode == "export")
{
    var tools = new FfmpegToolchain(args[3], args[4]);
    for (int run = 0; run < 2; run++)
    {
        var captures = new List<double>();
        var writes = new List<double>();
        var plan = new OfflineFramePlan(960, 544, new(30), 0, 39);
        var ffmpeg = new FfmpegFrameSink(tools, new(Path.Combine(directory, $"export-{run}.mp4"), VideoEncoder.HevcSoftware)
            { RateControl = VideoRateControl.ConstantQuality, Quality = 23, Speed = VideoSpeed.Balanced });
        var coordinator = new ExportCoordinator(token => ValueTask.FromResult(Create(1, false)),
            (request, token) => ValueTask.FromResult<IViewRenderer>(new TimedRenderer(request, captures)));
        var sink = new TimedSink(ffmpeg, writes);
        long start = Stopwatch.GetTimestamp();
        var result = await coordinator.RunAsync(new(plan), sink);
        if (result.CompletedFrames != plan.FrameCount || ffmpeg.Validation is null) throw new InvalidOperationException("Incomplete video.");
        Save($"export-{run}", new { totalMs = Milliseconds(start), result, ffmpeg.Validation, captures, writes });
    }
}

sealed class PosePhases : EventListener
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> Ticks { get; } = new();
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name is "MDA-PoseEvaluation" or "FrameStudy-NativeBullet") EnableEvents(source, EventLevel.Informational);
    }
    protected override void OnEventWritten(EventWrittenEventArgs args)
    {
        if (args.EventSource.Name == "FrameStudy-NativeBullet" && args.EventId == 1 && args.Payload?[0] is long elapsed)
            Ticks.AddOrUpdate("native.bullet.step", elapsed, (_, previous) => previous + elapsed);
        else if (args.EventId == 1 && args.Payload is { Count: >= 3 } values &&
            values[0] is string phase && values[2] is long ticks)
            Ticks.AddOrUpdate(phase, ticks, (_, previous) => previous + ticks);
    }
}

sealed class TimedRenderer(ViewRenderRequest request, List<double> timings) : IViewRenderer
{
    private readonly D3D12ViewRenderer inner = new(request);
    public ViewRendererCapabilities Capabilities => inner.Capabilities;
    public async ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        var result = await inner.CaptureAsync(frame, cancellationToken);
        timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return result;
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

sealed class TimedSink(IFrameSink inner, List<double> timings) : IFrameSink
{
    public ValueTask BeginAsync(ExportRequest request, CancellationToken token) => inner.BeginAsync(request, token);
    public async ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        await inner.WriteAsync(sample, frame, token);
        timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    public ValueTask CompleteAsync(CancellationToken token) => inner.CompleteAsync(token);
    public ValueTask AbortAsync(Exception? error, CancellationToken token) => inner.AbortAsync(error, token);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
