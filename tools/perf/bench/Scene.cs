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
    public string? PropModel { get; }
    public int Props { get; }
    public bool PmmControls { get; }
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
    public string? BoneAuditPath { get; }
    public string? MorphAuditPath { get; }
    public bool FrameTiming { get; }
    public Dictionary<string, string> Tags { get; }
    public Dictionary<string, string> Switches { get; }

    public BenchConfig(Cli cli)
    {
        Models = cli.All("model").Select(Path.GetFullPath).ToArray();
        if (Models.Count == 0) throw new ArgumentException("At least one --model is required.");
        foreach (var path in Models) if (!File.Exists(path)) throw new FileNotFoundException($"Model not found: {path}", path);
        Characters = cli.Int("characters", 1);
        if (Characters < 1) throw new ArgumentOutOfRangeException(nameof(Characters));
        PropModel = cli.Optional("prop-model") is { Length: > 0 } prop ? Path.GetFullPath(prop) : null;
        if (PropModel is not null && !File.Exists(PropModel)) throw new FileNotFoundException($"Prop model not found: {PropModel}", PropModel);
        Props = cli.Int("props", 0);
        if (Props < 0) throw new ArgumentOutOfRangeException(nameof(Props));
        PmmControls = cli.Flags.Contains("pmm-controls");
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
        BoneAuditPath = cli.Optional("bone-audit");
        MorphAuditPath = cli.Optional("morph-audit");
        FrameTiming = cli.Flags.Contains("frame-timing");
        if (FrameTiming && (cli.Mode != "play" || (Physics != "shared" && !(PmmControls && Physics == "independent"))))
            throw new ArgumentException("--frame-timing requires play with --physics shared or --pmm-controls with --physics independent.");
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

    public string Key => $"c{Characters}{(Props > 0 ? $"+p{Props}" : "")}{(PmmControls ? "+pmm" : "")}{(Stage is null ? "" : "+stage")}-gi{(Gi ? "on" : "off")}-{Width}x{Height}-{Physics}";
    public RayGiOptions GiOptions => Gi ? new RayGiOptions(LightingMode.HardwareRayTracing, Composition: GiComposition) : RayGiOptions.Off;
    public ViewQualityOptions Quality => new(Gi: GiOptions);
    public object Describe() => new
    {
        key = Key, characters = Characters, models = Enumerable.Range(0, Characters).Select(i => Models[i % Models.Count]).ToArray(),
        propModel = PropModel, props = Props, pmmControls = PmmControls,
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
    private static readonly string[] PreferredTargetBones = ["右手首", "左手首", "頭", "上半身", "センター", "左腕", "右腕", "下半身"];
    private static readonly byte[] LinearBoneCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray();
    private static readonly byte[] CameraCurve = Enumerable.Repeat(new byte[] { 20, 107, 20, 107 }, 6).SelectMany(b => b).ToArray();

    public BenchConfig Config { get; }
    public IReadOnlyList<LoadedModel> Assets { get; }
    public IReadOnlyList<EditableSceneObject> Objects { get; }
    public IReadOnlyList<SceneActorMotion> Motions { get; }
    public IReadOnlyList<SceneOutsideParentBinding> OutsideParents { get; }
    public ScenePmmControlState? PmmControls { get; }
    public SceneMotionAsset Camera { get; }
    public uint DurationFrames { get; }
    public IReadOnlyList<object> Animated { get; }
    private static readonly PropertyInfo? DeferSkinningProperty = typeof(SceneAnimationSession).GetProperty("DeferVertexSkinningToRenderer");
    private readonly IProjectPhysicsBackendProvider physics = ProjectPhysicsRuntime.CreateProjectProvider();
    private readonly ProjectPhysicsSettings settings;
    private readonly int directPropBindings, chainedPropBindings, groundPropBindings;
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
        var propAsset = config.Props > 0 ? Load(config.PropModel ?? config.Models[0]) : null;
        if (propAsset is not null && propAsset.Model.Bones.Count == 0)
            throw new InvalidOperationException("Prop model requires at least one bone.");
        var stage = config.Stage is null ? null : Load(config.Stage);
        Assets = cache.Values.ToArray();

        var loadedVmds = new Dictionary<(string Path, int ModelIndex), (SceneMotionAsset Asset, bool AutoFallback936)>();
        float spacing = actors.Max(a => a.Max.X - a.Min.X) * 1.15f;
        var layout = new SceneCompositionEditor();
        var motions = new List<SceneActorMotion>();
        var animated = new List<object>();
        var actorItems = new EditableSceneObject[actors.Length];
        uint duration = 0;
        for (int i = 0; i < actors.Length; i++)
        {
            var asset = actors[i];
            var item = layout.Add($"actor-{i}", asset.Path, ModelRole.Character, asset.Source);
            actorItems[i] = item;
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
        var propItems = new EditableSceneObject[config.Props];
        if (propAsset is not null)
        {
            string propRootBone = propAsset.Model.Bones[0].Name;
            for (int p = 0; p < config.Props; p++)
            {
                var propItem = layout.Add($"prop-{p}", propAsset.Path, ModelRole.Prop, propAsset.Source);
                propItems[p] = propItem;
                motions.Add(new(propItem.Id, propAsset.Model, new VmdMotion
                {
                    BoneKeyframes = [new() { BoneName = propRootBone, Frame = 0 }]
                }, PhysicsEnabled: false));
            }
        }
        EditableSceneObject? stageItem = null;
        if (stage is not null)
        {
            stageItem = layout.Add("stage", stage.Path, ModelRole.Environment, stage.Source);
            if (config.PmmControls)
                motions.Add(new(stageItem.Id, stage.Model, new VmdMotion(), PhysicsEnabled: false));
        }
        if (requiredFrames > 0 && duration < requiredFrames)
            throw new InvalidOperationException($"Motion lasts {duration} frames; this configuration needs at least {requiredFrames}. Use a VMD with keyframes beyond frame 0 or fewer frames.");

        if (config.PmmControls)
        {
            OutsideParents = [];
            PmmControls = BuildPmmControls(config, actors, actorItems, propAsset, propItems, stage, stageItem,
                out directPropBindings, out chainedPropBindings, out groundPropBindings);
        }
        else
        {
            PmmControls = null;
            OutsideParents = BuildStaticOutsideParents(actors, actorItems, propAsset, propItems,
                out directPropBindings, out chainedPropBindings);
        }
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
            sharedWorldGravity: settings.SharedWorld ? settings.Gravity : null,
            outsideParents: OutsideParents.Count > 0 ? OutsideParents : null,
            pmmControls: PmmControls);
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
            bones = a.Model.Bones.Count, materials = a.Model.Materials.Count, bodies = a.Model.RigidBodies.Count, joints = a.Model.Joints.Count,
            softBodies = a.Model.SoftBodies.Count, textures = a.Source.Surface.Snapshot.Textures.Count, loadMs = a.LoadMs }),
        sceneVertices = Objects.Sum(o => o.Source.Surface.Snapshot.VertexCount),
        props = Config.Props,
        directPropBindings,
        chainedPropBindings,
        groundPropBindings,
        pmmControls = PmmControls is not null,
        pmmBoundaryFrames = PmmControls?.BoundaryFrames,
        durationFrames = DurationFrames, animated = Animated, initializationSubsteps = InitializationSubsteps,
        deferVertexSkinningToRenderer = DeferVertexSkinningToRenderer
    };

    private static IReadOnlyList<SceneOutsideParentBinding> BuildStaticOutsideParents(
        LoadedModel[] actors, EditableSceneObject[] actorItems,
        LoadedModel? propAsset, EditableSceneObject[] propItems,
        out int directCount, out int chainedCount)
    {
        directCount = 0; chainedCount = 0;
        if (propItems.Length == 0 || propAsset is null) return [];
        var charTargets = actors.Select(a => SelectTargetBones(a.Model, encoding: null, desiredCount: 4)).ToArray();
        int propBoneIdx = SelectPropTargetBone(propAsset.Model, encoding: null);
        var bindings = new List<SceneOutsideParentBinding>(propItems.Length);
        for (int p = 0; p < propItems.Length; p++)
        {
            if (p >= 2 && p % 4 == 2)
            {
                // 2-level chain: prop[p] -> prop[p - 1] (which attaches directly to a character bone).
                bindings.Add(new(propItems[p].Id, -1, propItems[p - 1].Id, propBoneIdx, Matrix4x4.Identity));
                chainedCount++;
            }
            else
            {
                int charIdx = p % actorItems.Length;
                int[] targets = charTargets[charIdx];
                int boneIdx = targets[(p / actorItems.Length) % targets.Length];
                bindings.Add(new(propItems[p].Id, -1, actorItems[charIdx].Id, boneIdx, Matrix4x4.Identity));
                directCount++;
            }
        }
        return bindings.AsReadOnly();
    }

    private static ScenePmmControlState BuildPmmControls(
        BenchConfig config,
        LoadedModel[] actors, EditableSceneObject[] actorItems,
        LoadedModel? propAsset, EditableSceneObject[] propItems,
        LoadedModel? stage, EditableSceneObject? stageItem,
        out int directCount, out int chainedCount, out int groundCount)
    {
        directCount = 0; chainedCount = 0; groundCount = 0;
        int totalModels = actorItems.Length + propItems.Length + (stageItem is not null ? 1 : 0);
        if (totalModels > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(config), $"PMM controls support at most {byte.MaxValue} scene objects, got {totalModels}.");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var candidateModels = actors.Select(a => a.Model).Concat(propAsset is not null ? [propAsset.Model] : Array.Empty<PmxModel>()).ToArray();
        var (codePage, encoding) = ChoosePmmCodePage(candidateModels);

        int w1 = Math.Max(1, config.Warmup / 2);
        int m1 = Math.Max(w1 + 1, config.Warmup + Math.Max(1, config.Frames / 4));
        int m2 = Math.Max(m1 + 1, config.Warmup + Math.Max(2, config.Frames / 2));
        int m3 = Math.Max(m2 + 1, config.Warmup + Math.Max(3, (config.Frames * 3) / 4));

        var pmmModels = new List<PmmModel>(totalModels);
        var sources = new List<ScenePmmActorSource>(totalModels);
        var charTargetOrdinals = new int[actors.Length][];

        for (int c = 0; c < actors.Length; c++)
        {
            var pmx = actors[c].Model;
            int[] targets = SelectTargetBones(pmx, encoding, desiredCount: 4);
            int switchBone = SelectPhysicsSwitchBone(pmx, encoding);
            var selectedPmxBones = new List<int>(targets.Length + 1);
            var ordinalByPmx = new Dictionary<int, int>();
            void AddBone(int pmxIdx)
            {
                if (!ordinalByPmx.ContainsKey(pmxIdx))
                {
                    ordinalByPmx[pmxIdx] = selectedPmxBones.Count;
                    selectedPmxBones.Add(pmxIdx);
                }
            }
            foreach (int t in targets) AddBone(t);
            AddBone(switchBone);
            charTargetOrdinals[c] = targets.Select(t => ordinalByPmx[t]).ToArray();

            string[] boneNames = selectedPmxBones.Select(idx => pmx.Bones[idx].Name).ToArray();
            int switchOrdinal = ordinalByPmx[switchBone];
            var switchTimeline = new (int Frame, bool Disabled)[]
            {
                (0, false), (w1, true), (m1, false), (m2, true), (m3, false),
            };
            var (initBones, extraBones, boneStates) = BuildPmmBoneTracks(boneNames.Length, switchOrdinal, switchTimeline);
            pmmModels.Add(new()
            {
                Index = (byte)c, DrawOrderIndex = (byte)c, TransformOrderIndex = (byte)c,
                Visible = true, LastFrame = m3,
                Name = PmmText($"actor-{c}"), Path = PmmText($"actor-{c}.pmx"),
                BoneNames = boneNames.Select(PmmText).ToArray(),
                SelectedMorphIndices = [-1, -1, -1, -1],
                InitialBoneKeyframes = initBones, BoneKeyframes = extraBones, BoneStates = boneStates,
                OutsideParentSubjectBoneIndices = [],
                InitialModelKeyframe = new() { RecordIndex = 0, ObjectIndex = 0, Visible = true, OutsideParents = [] },
                ModelKeyframes = [],
                OutsideParentStates = [],
            });
            sources.Add(new(actorItems[c].Id, (byte)c, pmx));
        }

        if (propAsset is not null)
        {
            int propPmxBone = SelectPropTargetBone(propAsset.Model, encoding);
            string propBoneName = propAsset.Model.Bones[propPmxBone].Name;
            int charCount = actorItems.Length;
            PmmOutsideParent CharRef(int charIdx, int slot)
            {
                int[] ords = charTargetOrdinals[charIdx];
                return new(0, charIdx, ords[slot % ords.Length]);
            }
            PmmOutsideParent PropRef(int propIdx) => new(0, charCount + propIdx, 0);
            var ground = new PmmOutsideParent(0, -2, -1);

            for (int p = 0; p < propItems.Length; p++)
            {
                byte modelIdx = (byte)(charCount + p);
                int subjectOrdinal;
                (int Frame, PmmOutsideParent Parent)[] schedule;
                if (p >= 2 && p % 4 == 2)
                {
                    subjectOrdinal = -1;
                    var carrier = PropRef(p - 1);
                    var cA = CharRef(p % charCount, 0);
                    var cB = CharRef((p + 1) % charCount, 1);
                    schedule = [(0, carrier), (w1, cA), (m1, carrier), (m2, cB), (m3, carrier)];
                    chainedCount++;
                }
                else if (p % 4 == 3 || (propItems.Length < 4 && p == propItems.Length - 1))
                {
                    // Ground outside-parent (-2) requires a nonnegative subject bone ordinal.
                    subjectOrdinal = 0;
                    var cA = CharRef(p % charCount, 0);
                    var cB = CharRef((p + 1) % charCount, 1);
                    var cC = CharRef(p % charCount, 2);
                    schedule = propItems.Length < 4
                        ? [(0, cA), (w1, cB), (m1, ground), (m2, cC), (m3, ground)]
                        : [(0, ground), (w1, cA), (m1, cB), (m2, ground), (m3, cC)];
                    groundCount++;
                }
                else if (p % 4 == 0)
                {
                    subjectOrdinal = -1;
                    var cA = CharRef(p % charCount, p / charCount);
                    var cB = CharRef((p + 1) % charCount, p / charCount + 1);
                    schedule = p % 8 == 0 ? [(0, cA), (m1, cB), (m2, cA)] : [(0, cA)];
                    directCount++;
                }
                else
                {
                    // Level-1 carrier for 2-level chains: never depends on another prop to preserve acyclic DAG.
                    subjectOrdinal = -1;
                    var cA = CharRef(p % charCount, p / charCount);
                    var cB = CharRef(p % charCount, p / charCount + 1);
                    schedule = p == 1 ? [(0, cA), (m1, cB), (m2, cA)] : [(0, cA)];
                    directCount++;
                }

                var (initBones, extraBones, boneStates) = BuildPmmBoneTracks(1, switchOrdinal: -1, []);
                var initialModel = new PmmModelKeyframe
                {
                    RecordIndex = 0, ObjectIndex = 0, PreviousIndex = 0, NextIndex = schedule.Length > 1 ? 1 : 0,
                    Frame = 0, Visible = true, OutsideParents = [schedule[0].Parent],
                };
                var extraModels = new PmmModelKeyframe[schedule.Length - 1];
                for (int k = 1; k < schedule.Length; k++)
                {
                    extraModels[k - 1] = new()
                    {
                        RecordIndex = k, ObjectIndex = k, PreviousIndex = k - 1,
                        NextIndex = k + 1 < schedule.Length ? k + 1 : 0,
                        Frame = schedule[k].Frame, Visible = true, OutsideParents = [schedule[k].Parent],
                    };
                }
                pmmModels.Add(new()
                {
                    Index = modelIdx, DrawOrderIndex = modelIdx, TransformOrderIndex = modelIdx,
                    Visible = true, LastFrame = m3,
                    Name = PmmText($"prop-{p}"), Path = PmmText($"prop-{p}.pmx"),
                    BoneNames = [PmmText(propBoneName)],
                    SelectedMorphIndices = [-1, -1, -1, -1],
                    InitialBoneKeyframes = initBones, BoneKeyframes = extraBones, BoneStates = boneStates,
                    OutsideParentSubjectBoneIndices = [subjectOrdinal],
                    InitialModelKeyframe = initialModel, ModelKeyframes = extraModels,
                    OutsideParentStates = [new(0, 0, int.MaxValue, schedule[0].Parent)],
                });
                sources.Add(new(propItems[p].Id, modelIdx, propAsset.Model));
            }
        }

        if (stage is not null && stageItem is not null)
        {
            byte stageIdx = (byte)(actorItems.Length + propItems.Length);
            pmmModels.Add(new()
            {
                Index = stageIdx, DrawOrderIndex = stageIdx, TransformOrderIndex = stageIdx,
                Visible = true, LastFrame = m3,
                Name = PmmText("stage"), Path = PmmText("stage.pmx"),
                BoneNames = [], SelectedMorphIndices = [-1, -1, -1, -1],
                InitialBoneKeyframes = [], BoneKeyframes = [], BoneStates = [],
                OutsideParentSubjectBoneIndices = [],
                InitialModelKeyframe = new() { RecordIndex = 0, ObjectIndex = 0, Visible = true, OutsideParents = [] },
                ModelKeyframes = [], OutsideParentStates = [],
            });
            sources.Add(new(stageItem.Id, stageIdx, stage.Model));
        }

        Vector3 angledDir = Vector3.Normalize(new(0.15f, -1f, 0f));
        var gravity = new PmmGravity
        {
            Acceleration = 9.8f, Direction = -Vector3.UnitY, NoiseEnabled = false, Noise = 0,
            InitialKeyframe = new() { RecordIndex = 0, ObjectIndex = 0, PreviousIndex = 0, NextIndex = 1, Frame = 0, Acceleration = 9.8f, Direction = -Vector3.UnitY, NoiseEnabled = false, Noise = 0 },
            Keyframes =
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = w1, Acceleration = 9.8f, Direction = -Vector3.UnitY, NoiseEnabled = false, Noise = 0 },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 3, Frame = m1, Acceleration = 10.5f, Direction = angledDir, NoiseEnabled = true, Noise = 1 },
                new() { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 2, NextIndex = 4, Frame = m2, Acceleration = 9.8f, Direction = -Vector3.UnitY, NoiseEnabled = false, Noise = 0 },
                new() { RecordIndex = 4, ObjectIndex = 4, PreviousIndex = 3, NextIndex = 0, Frame = m3, Acceleration = 9.8f, Direction = -Vector3.UnitY, NoiseEnabled = false, Noise = 0 },
            ],
        };

        var doc = new PmmDocument
        {
            AnsiCodePage = codePage,
            PanelStates = new byte[7],
            Models = pmmModels,
            OutputWidth = config.Width,
            OutputHeight = config.Height,
            CameraFov = 30,
            Camera = new() { InitialKeyframe = new() { Interpolation = CameraCurve, Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PreferredFps = 30, VideoScale = 1, ImageScale = 1, PhysicsSimulationMode = 1, PhysicsGroundEnabled = true, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1 },
            Gravity = gravity,
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } },
        };

        byte[] pmmBytes = PmmWriter.ToBytes(doc);
        var roundTripped = PmmReader.Read(new MemoryStream(pmmBytes, writable: false), new() { AnsiCodePage = codePage });
        return ScenePmmControlState.Create(roundTripped, sources);
    }

    private static PmmString PmmText(string value) => new(0, value, []);

    private static (int CodePage, Encoding Encoding) ChoosePmmCodePage(PmxModel[] models)
    {
        Encoding CreateStrict(int page) => Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var enc932 = CreateStrict(932);
        if (models.All(m => m.Bones.Any(b => CanEncodePmm(enc932, b.Name))))
        {
            bool allPreferred = models.All(m => PreferredTargetBones.Where(n => m.Bones.Any(b => b.Name == n)).All(n => CanEncodePmm(enc932, n)));
            if (allPreferred) return (932, enc932);
        }
        var enc936 = CreateStrict(936);
        if (models.All(m => m.Bones.Any(b => CanEncodePmm(enc936, b.Name)))) return (936, enc936);
        if (models.All(m => m.Bones.Any(b => CanEncodePmm(enc932, b.Name)))) return (932, enc932);
        throw new InvalidOperationException("Models do not contain PMM-encodable bone names in code page 932 or 936.");
    }

    private static bool CanEncodePmm(Encoding? encoding, string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0')) return false;
        if (encoding is null) return true;
        try { return encoding.GetByteCount(name) <= 255; }
        catch (EncoderFallbackException) { return false; }
    }

    private static int[] SelectTargetBones(PmxModel model, Encoding? encoding, int desiredCount)
    {
        var chosen = new List<int>(desiredCount);
        void TryAdd(int idx)
        {
            if (idx >= 0 && idx < model.Bones.Count && !chosen.Contains(idx) && CanEncodePmm(encoding, model.Bones[idx].Name))
                chosen.Add(idx);
        }
        foreach (string name in PreferredTargetBones)
        {
            for (int i = 0; i < model.Bones.Count; i++)
                if (model.Bones[i].Name == name) { TryAdd(i); break; }
            if (chosen.Count >= desiredCount) break;
        }
        if (chosen.Count < desiredCount)
        {
            for (int i = 0; i < model.Bones.Count && chosen.Count < desiredCount; i++)
                if ((model.Bones[i].Flags & PmxBoneFlags.DeformAfterPhysics) != 0) TryAdd(i);
        }
        for (int i = 0; i < model.Bones.Count && chosen.Count < desiredCount; i++) TryAdd(i);
        if (chosen.Count == 0) throw new InvalidOperationException($"Model '{model.Name}' has no selectable target bone.");
        return chosen.ToArray();
    }

    private static int SelectPhysicsSwitchBone(PmxModel model, Encoding encoding)
    {
        foreach (var body in model.RigidBodies)
            if (body.Operation != PmxRigidBodyOperation.Static && body.BoneIndex >= 0 && body.BoneIndex < model.Bones.Count
                && CanEncodePmm(encoding, model.Bones[body.BoneIndex].Name))
                return body.BoneIndex;
        return SelectTargetBones(model, encoding, 1)[0];
    }

    private static int SelectPropTargetBone(PmxModel propModel, Encoding? encoding)
    {
        for (int i = 0; i < propModel.Bones.Count; i++)
            if (propModel.Bones[i].Name == "センター" && CanEncodePmm(encoding, propModel.Bones[i].Name))
                return i;
        return SelectTargetBones(propModel, encoding, 1)[0];
    }

    private static (PmmBoneKeyframe[] Initial, PmmBoneKeyframe[] Extra, PmmBoneState[] States) BuildPmmBoneTracks(
        int boneCount, int switchOrdinal, (int Frame, bool Disabled)[] switchTimeline)
    {
        var initial = new PmmBoneKeyframe[boneCount];
        var extra = new List<PmmBoneKeyframe>();
        var states = new PmmBoneState[boneCount];
        for (int b = 0; b < boneCount; b++)
        {
            bool hasSwitch = b == switchOrdinal && switchTimeline.Length > 0;
            bool initDisabled = hasSwitch && switchTimeline[0].Disabled;
            initial[b] = new()
            {
                RecordIndex = b, ObjectIndex = b, PreviousIndex = 0, NextIndex = 0,
                Frame = 0, Translation = Vector3.Zero, Rotation = Quaternion.Identity,
                Interpolation = LinearBoneCurve, PhysicsDisabled = initDisabled,
            };
            if (hasSwitch && switchTimeline.Length > 1)
            {
                int prevId = b;
                for (int k = 1; k < switchTimeline.Length; k++)
                {
                    int id = boneCount + extra.Count;
                    if (prevId < boneCount) initial[prevId].NextIndex = id;
                    else extra[prevId - boneCount].NextIndex = id;
                    extra.Add(new()
                    {
                        RecordIndex = id, ObjectIndex = id, PreviousIndex = prevId, NextIndex = 0,
                        Frame = switchTimeline[k].Frame, Translation = Vector3.Zero, Rotation = Quaternion.Identity,
                        Interpolation = LinearBoneCurve, PhysicsDisabled = switchTimeline[k].Disabled,
                    });
                    prevId = id;
                }
            }
            states[b] = new(0, Vector3.Zero, Quaternion.Identity, false, false, 0);
        }
        return (initial, extra.ToArray(), states);
    }
}
