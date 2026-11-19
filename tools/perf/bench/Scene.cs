using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics.Runtime;
using Mmd.Modern.ViewOutput;

namespace PerfBench;

/// <summary>One benchmark configuration, parsed once per process.</summary>
sealed class BenchConfig
{
    public IReadOnlyList<string> Models { get; }
    public int Characters { get; }
    public string? Stage { get; }
    public IReadOnlyList<string> Vmds { get; }
    public int? VmdCodePage { get; }
    public bool Gi { get; }
    public RayGiComposition GiComposition { get; }
    public int Width { get; }
    public int Height { get; }
    public string Physics { get; }
    public int Warmup { get; }
    public int Frames { get; }
    public int SerialFrames { get; }
    public int Seeks { get; }
    public int ExportFrames { get; }
    public string? Ffmpeg { get; }
    public string? Ffprobe { get; }
    public bool KeepVideo { get; }
    public string OutPath { get; }
    public Dictionary<string, string> Tags { get; }
    public Dictionary<string, string> Switches { get; }

    public BenchConfig(Cli cli)
    {
        Models = cli.All("model").Select(Path.GetFullPath).ToArray();
        if (Models.Count == 0) throw new ArgumentException("At least one --model is required.");
        foreach (var path in Models) if (!File.Exists(path)) throw new FileNotFoundException($"Model not found: {path}", path);
        Characters = cli.Int("characters", 1);
        if (Characters < 1) throw new ArgumentOutOfRangeException(nameof(Characters));
        Stage = cli.Optional("stage") is { Length: > 0 } stage ? Path.GetFullPath(stage) : null;
        if (Stage is not null && !File.Exists(Stage)) throw new FileNotFoundException($"Stage not found: {Stage}", Stage);
        Vmds = cli.All("vmd").Where(v => v.Length > 0).Select(Path.GetFullPath).ToArray();
        foreach (var path in Vmds) if (!File.Exists(path)) throw new FileNotFoundException($"VMD not found: {path}", path);
        VmdCodePage = cli.Optional("vmd-codepage") is { Length: > 0 } cp ? int.Parse(cp, CultureInfo.InvariantCulture) : null;
        Gi = (cli.Optional("gi") ?? "off") switch { "on" => true, "off" => false, var other => throw new ArgumentException($"--gi {other}") };
        GiComposition = (cli.Optional("gi-composition") ?? "toon").ToLowerInvariant() switch
        {
            "toon" or "toonpalette" => RayGiComposition.ToonPalette,
            "additive" => RayGiComposition.Additive,
            var other => throw new ArgumentException($"--gi-composition {other}")
        };
        var size = (cli.Optional("size") ?? "960x544").Split('x');
        if (size.Length != 2) throw new ArgumentException($"Expected WxH for --size, got '{cli.Optional("size")}'.");
        Width = int.Parse(size[0], CultureInfo.InvariantCulture); Height = int.Parse(size[1], CultureInfo.InvariantCulture);
        if (Width <= 0 || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Width), "Size must be positive.");
        Physics = cli.Optional("physics") ?? "independent";
        if (Physics is not ("independent" or "shared" or "off")) throw new ArgumentException($"--physics {Physics}");
        Warmup = cli.Int("warmup", 60); Frames = cli.Int("frames", 300); SerialFrames = cli.Int("serial-frames", 60);
        Seeks = cli.Int("seeks", 3); ExportFrames = cli.Int("export-frames", 60);
        if (Warmup < 0 || Frames < 2 || SerialFrames < 0 || Seeks < 0 || ExportFrames < 2) throw new ArgumentOutOfRangeException(nameof(Frames));
        Ffmpeg = cli.Optional("ffmpeg") ?? Environment.GetEnvironmentVariable("MMD_FFMPEG_PATH");
        Ffprobe = cli.Optional("ffprobe") ?? Environment.GetEnvironmentVariable("MMD_FFPROBE_PATH");
        KeepVideo = cli.Flags.Contains("keep-video");
        OutPath = Path.GetFullPath(cli.Required("out"));
        Tags = cli.Tags();
        // Optional runtime switches let a later branch expose experiments without changing this tool.
        Switches = cli.All("switch").Select(Cli.Split).ToDictionary(p => p.Key, p => p.Value);
        foreach (var (name, value) in Switches)
        {
            if (bool.TryParse(value, out bool flag)) { AppContext.SetSwitch(name, flag); AppContext.SetData(name, flag); }
            else if (int.TryParse(value, CultureInfo.InvariantCulture, out int number)) AppContext.SetData(name, number);
            else AppContext.SetData(name, value);
        }
    }

    public string Key => $"c{Characters}{(Stage is null ? "" : "+stage")}-gi{(Gi ? "on" : "off")}-{Width}x{Height}-{Physics}";
    public RayGiOptions GiOptions => Gi ? new RayGiOptions(LightingMode.HardwareRayTracing, Composition: GiComposition) : RayGiOptions.Off;
    public ViewQualityOptions Quality => new(Gi: GiOptions);
    public object Describe() => new
    {
        key = Key, characters = Characters, models = Enumerable.Range(0, Characters).Select(i => Models[i % Models.Count]).ToArray(),
        stage = Stage, vmds = Vmds, vmd = Vmds.Count > 0 ? Vmds[0] : null, gi = Gi, giMode = GiOptions.Mode.ToString(), giStrength = GiOptions.Strength,
        giSamplesPerPixel = GiOptions.SamplesPerPixel, giComposition = GiOptions.Composition.ToString(),
        width = Width, height = Height, physics = Physics, warmup = Warmup, frames = Frames, serialFrames = SerialFrames,
        seeks = Seeks, exportFrames = ExportFrames, switches = Switches
    };
}

sealed record LoadedModel(string Path, PmxModel Model, OriginalFrameScene Source, Vector3 Min, Vector3 Max, double LoadMs, string Sha256);

/// <summary>Builds the scene through public product APIs: the editor's model loader, the project physics provider
/// with its pose-initialization policy, a 30 fps VMD clock and a scene camera track.</summary>
sealed class BenchScene
{
    public BenchConfig Config { get; }
    public IReadOnlyList<LoadedModel> Assets { get; }
    public IReadOnlyList<EditableSceneObject> Objects { get; }
    public IReadOnlyList<SceneActorMotion> Motions { get; }
    public SceneMotionAsset Camera { get; }
    public uint DurationFrames { get; }
    public IReadOnlyList<object> Animated { get; }
    private static readonly PropertyInfo? DeferSkinningProperty = typeof(SceneAnimationSession).GetProperty("DeferVertexSkinningToRenderer");
    private readonly IProjectPhysicsBackendProvider physics = ProjectPhysicsRuntime.CreateProjectProvider();
    private readonly ProjectPhysicsSettings settings;
    public int InitializationSubsteps => physics.PoseInitializationSubsteps;
    public bool? DeferVertexSkinningToRenderer { get; }

    public BenchScene(BenchConfig config, uint requiredFrames)
    {
        Config = config;
        if (DeferSkinningProperty is { CanWrite: true } prop && prop.PropertyType == typeof(bool))
        {
            bool desired = true;
            if (config.Switches.TryGetValue("DeferVertexSkinningToRenderer", out var raw) && !bool.TryParse(raw, out desired))
                throw new ArgumentException($"--switch DeferVertexSkinningToRenderer expects true|false, got '{raw}'.");
            DeferVertexSkinningToRenderer = desired;
        }
        var cache = new Dictionary<string, LoadedModel>(StringComparer.OrdinalIgnoreCase);
        LoadedModel Load(string path)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            long start = Stopwatch.GetTimestamp();
            var asset = EditorProjectWorkspace.LoadModelAsset(path);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var v in asset.Source.Surface.Snapshot.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
            string sha;
            using (var file = File.OpenRead(path)) sha = Convert.ToHexString(SHA256.HashData(file));
            return cache[path] = new(path, asset.Model, asset.Source, min, max, ms, sha);
        }
        var actors = Enumerable.Range(0, config.Characters).Select(i => Load(config.Models[i % config.Models.Count])).ToArray();
        var stage = config.Stage is null ? null : Load(config.Stage);
        Assets = cache.Values.ToArray();

        var loadedVmds = new Dictionary<(string Path, int ModelIndex), (SceneMotionAsset Asset, bool AutoFallback936)>();
        float spacing = actors.Max(a => a.Max.X - a.Min.X) * 1.15f;
        var layout = new SceneCompositionEditor();
        var motions = new List<SceneActorMotion>();
        var animated = new List<object>();
        uint duration = 0;
        for (int i = 0; i < actors.Length; i++)
        {
            var asset = actors[i];
            var item = layout.Add($"actor-{i}", asset.Path, ModelRole.Character, asset.Source);
            layout.SetTransform(item.Id, new((i - (actors.Length - 1) / 2f) * spacing, 0, 0), Quaternion.Identity, 1);
            VmdMotion motion;
            if (config.Vmds.Count > 0)
            {
                string vmdPath = config.Vmds[i % config.Vmds.Count];
                var vmdAsset = LoadVmd(vmdPath, asset.Model, config.VmdCodePage, out bool fallback936);
                loadedVmds[(vmdPath, i)] = (vmdAsset, fallback936);
                motion = LoopIfNeeded(vmdAsset.Motion, requiredFrames, out uint orig, out bool looped);
                animated.Add(new { actor = i, vmd = vmdPath, codePage = vmdAsset.AnsiCodePage, fallback936, originalDuration = orig, looped });
            }
            else
            {
                motion = Synthesize(asset.Model, requiredFrames, i, out var names);
                animated.Add(new { actor = i, synthetic = true, tracks = names });
            }
            uint last = MotionLastFrame(motion);
            duration = Math.Max(duration, last);
            motions.Add(new(item.Id, asset.Model, motion, PhysicsEnabled: config.Physics != "off"));
        }
        if (stage is not null) layout.Add("stage", stage.Path, ModelRole.Environment, stage.Source);
        if (requiredFrames > 0 && duration < requiredFrames)
            throw new InvalidOperationException($"Motion lasts {duration} frames; this configuration needs at least {requiredFrames}. Use a VMD with keyframes beyond frame 0 or fewer frames.");
        Objects = layout.Objects.ToArray(); Motions = motions; DurationFrames = duration; Animated = animated;

        // Frame the whole line-up with a fixed 16:9 budget so both output sizes see the same scene.
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < actors.Length; i++)
        {
            var offset = new Vector3((i - (actors.Length - 1) / 2f) * spacing, 0, 0);
            min = Vector3.Min(min, actors[i].Min + offset); max = Vector3.Max(max, actors[i].Max + offset);
        }
        float tan = MathF.Tan(15 * MathF.PI / 180);
        float distance = 1.1f * MathF.Max((max.Y - min.Y) / 2 / tan, (max.X - min.X) / 2 / (tan * 16 / 9f)) + (max.Z - min.Z) / 2;
        var interest = (min + max) / 2;
        Camera = SceneMotionAsset.FromMotion(new VmdMotion { CameraKeyframes = [new() { Distance = -distance, Interest = interest, ViewAngle = 30 }] }, "bench-camera.vmd");
        settings = new ProjectPhysicsSettings { SharedWorld = config.Physics == "shared" };
    }

    /// <summary>Deterministic dance-like loop on standard MMD bones (plus the most-weighted bone and two common morphs),
    /// so every model and branch evaluates the same tracks. Keys every 15 frames keep physics continuously excited.</summary>
    static VmdMotion Synthesize(PmxModel model, uint frames, int phase, out string[] animated)
    {
        var names = model.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var weights = new double[model.Bones.Count];
        foreach (var vertex in model.Vertices)
            for (int i = 0; i < vertex.BoneIndices.Count; i++)
                if (vertex.BoneIndices[i] >= 0) weights[vertex.BoneIndices[i]] += vertex.BoneWeights[i];
        string heaviest = model.Bones[Array.IndexOf(weights, weights.Max())].Name;
        var tracks = new List<(string Bone, Func<float, (Vector3, Quaternion)> Pose)>
        {
            ("センター", s => (new Vector3(1.5f * s, .4f * MathF.Abs(s), 0), Quaternion.Identity)),
            ("上半身", s => (default, Quaternion.CreateFromYawPitchRoll(.35f * s, .1f * s, 0))),
            ("下半身", s => (default, Quaternion.CreateFromYawPitchRoll(-.2f * s, 0, 0))),
            ("頭", s => (default, Quaternion.CreateFromYawPitchRoll(.3f * s, .25f * s, 0))),
            ("左腕", s => (default, Quaternion.CreateFromYawPitchRoll(0, 0, .6f * s))),
            ("右腕", s => (default, Quaternion.CreateFromYawPitchRoll(0, 0, .6f * s))),
            ("左ひじ", s => (default, Quaternion.CreateFromYawPitchRoll(.5f * MathF.Abs(s), 0, 0))),
            ("右ひじ", s => (default, Quaternion.CreateFromYawPitchRoll(-.5f * MathF.Abs(s), 0, 0))),
        };
        tracks.RemoveAll(t => !names.Contains(t.Bone));
        if (tracks.All(t => t.Bone != heaviest)) tracks.Add((heaviest, s => (default, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f * s))));
        var morphNames = new[] { "まばたき", "あ" }.Where(n => model.Morphs.Any(m => m.Name == n)).ToArray();
        var bones = new List<VmdBoneKeyframe>();
        var morphs = new List<VmdMorphKeyframe>();
        uint last = (frames / 15 + 2) * 15;
        for (uint frame = 0, step = 0; frame <= last; frame += 15, step++)
        {
            float s = step == 0 ? 0 : (step + phase) % 2 == 0 ? 1 : -1;
            foreach (var (bone, pose) in tracks)
            {
                var (translation, rotation) = pose(s);
                bones.Add(new() { BoneName = bone, Frame = frame, Translation = translation, Rotation = rotation });
            }
            foreach (var morph in morphNames) morphs.Add(new() { MorphName = morph, Frame = frame, Weight = step % 2 == 0 ? 0 : 1 });
        }
        animated = tracks.Select(t => t.Bone).Concat(morphNames).ToArray();
        return new VmdMotion { BoneKeyframes = bones, MorphKeyframes = morphs };
    }

    static SceneMotionAsset LoadVmd(string path, PmxModel model, int? explicitCodePage, out bool fallback936)
    {
        fallback936 = false;
        if (explicitCodePage is { } cp) return EditorProjectInterchange.PrepareVmd(path, model, ansiCodePage: cp);
        try { return EditorProjectInterchange.PrepareVmd(path, model, ansiCodePage: 932); }
        catch (VmdFormatException error) when (error.InnerException is DecoderFallbackException)
        {
            fallback936 = true;
            return EditorProjectInterchange.PrepareVmd(path, model, ansiCodePage: 936);
        }
    }

    static uint MotionLastFrame(VmdMotion motion) => Math.Max(
        motion.BoneKeyframes.Select(k => k.Frame).DefaultIfEmpty().Max(),
        Math.Max(motion.MorphKeyframes.Select(k => k.Frame).DefaultIfEmpty().Max(),
            motion.IkKeyframes.Select(k => k.Frame).DefaultIfEmpty().Max()));

    static VmdMotion LoopIfNeeded(VmdMotion source, uint requiredFrames, out uint originalDuration, out bool looped)
    {
        originalDuration = MotionLastFrame(source);
        looped = false;
        if (requiredFrames == 0 || originalDuration == 0 || originalDuration >= requiredFrames) return source;
        looped = true;
        uint period = originalDuration + 1;
        var bones = new List<VmdBoneKeyframe>(source.BoneKeyframes.Count * 2);
        var morphs = new List<VmdMorphKeyframe>(source.MorphKeyframes.Count * 2);
        var iks = new List<VmdIkKeyframe>(source.IkKeyframes.Count * 2);
        for (uint offset = 0; offset <= requiredFrames; offset += period)
        {
            foreach (var k in source.BoneKeyframes)
                bones.Add(new() { BoneName = k.BoneName, OriginalName = k.OriginalName, Frame = k.Frame + offset,
                    Translation = k.Translation, Rotation = k.Rotation, Interpolation = k.Interpolation });
            foreach (var k in source.MorphKeyframes)
                morphs.Add(new() { MorphName = k.MorphName, OriginalName = k.OriginalName, Frame = k.Frame + offset, Weight = k.Weight });
            foreach (var k in source.IkKeyframes)
                iks.Add(new() { Frame = k.Frame + offset, Visible = k.Visible, Switches = k.Switches });
        }
        return new VmdMotion
        {
            ModelName = source.ModelName, OriginalModelName = source.OriginalModelName,
            BoneKeyframes = bones, MorphKeyframes = morphs, IkKeyframes = iks,
            CameraKeyframes = source.CameraKeyframes, LightKeyframes = source.LightKeyframes,
            SelfShadowKeyframes = source.SelfShadowKeyframes, Diagnostics = source.Diagnostics
        };
    }

    /// <summary>Mirrors SceneProjectSession.Prepare: independent or shared worlds from project settings, then the
    /// host's fixed-pose physics initialization.</summary>
    public SceneAnimationSession CreateSession()
    {
        var session = new SceneAnimationSession(Objects, Motions, item => physics.CreateIndependent(settings, item),
            sceneMotion: Camera, sharedPhysicsFactory: settings.SharedWorld ? () => physics.CreateShared(settings) : null,
            softPhysicsFactory: item => physics.CreateSoftBody(settings, item),
            sharedWorldGravity: settings.SharedWorld ? settings.Gravity : null);
        try
        {
            if (DeferVertexSkinningToRenderer is { } defer && DeferSkinningProperty is { CanWrite: true } prop)
                prop.SetValue(session, defer);
            session.StabilizePhysicsAtCurrentFrame(physics.PoseInitializationSubsteps);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    /// <summary>The per-view camera/quality resolution that the D3D12 view outputs perform before submitting a frame.</summary>
    public OriginalFrameScene Prepare(SceneAnimationSession session, OriginalFrameScene frame)
    {
        var options = Config.Quality.ApplyTo(frame.Options);
        var outline = frame.OutlineOptions;
        if (session.Camera is { } camera)
        {
            var rotation = camera.LeftHanded ? Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up) : Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up);
            rotation.M41 = rotation.M42 = rotation.M43 = 0;
            options = options with { CameraOverride = camera, SphereRotationOverride = rotation };
            if (outline is not null) outline = outline with { CameraPosition = camera.Eye, FovDegrees = camera.VerticalFovDegrees ?? outline.FovDegrees };
        }
        return new(frame.Surface, frame.Outline, outline, options, frame.Materials, frame.Lighting, frame.EditorOverlay);
    }

    public static string PoseHash(OriginalFrameScene frame) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(frame.Surface.Snapshot.Vertices)))[..16];

    public object Describe() => new
    {
        assets = Assets.Select(a => new { path = a.Path, name = a.Model.Name, sha256 = a.Sha256, vertices = a.Model.Vertices.Count,
            materials = a.Model.Materials.Count, bodies = a.Model.RigidBodies.Count, joints = a.Model.Joints.Count,
            softBodies = a.Model.SoftBodies.Count, textures = a.Source.Surface.Snapshot.Textures.Count, loadMs = a.LoadMs }),
        sceneVertices = Objects.Sum(o => o.Source.Surface.Snapshot.VertexCount),
        durationFrames = DurationFrames, animated = Animated, initializationSubsteps = InitializationSubsteps,
        deferVertexSkinningToRenderer = DeferVertexSkinningToRenderer
    };
}
