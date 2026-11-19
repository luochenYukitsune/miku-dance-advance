using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>A detached project candidate. Ownership moves to the workspace only after preparation succeeds.</summary>
public sealed class EditorPmmCandidate : IDisposable
{
    private SceneProjectSession? session;
    public SceneProjectSession Session => session ?? throw new ObjectDisposedException(nameof(EditorPmmCandidate));
    public PmmProjectImportPlan Plan { get; }
    public IReadOnlyDictionary<int, Guid> ModelObjects { get; }
    /// <summary>Unregistered saved editor poses, kept separate from the session's authored timeline.</summary>
    public IReadOnlyDictionary<Guid, VmdMotion> SavedPoses { get; }
    public PmmCameraPose SavedCamera { get; }
    public PmmLightPose SavedLight { get; }
    public PmmSelfShadowSavedState SavedSelfShadow { get; }
    /// <summary>Models needing an explicit short VMD header before their in-memory motions can be captured for a PMA save.</summary>
    public IReadOnlyList<int> ModelHeaderOverridesRequired { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    internal IEditorProjectMetadata? Metadata { get; init; }
    internal EditorPmmCandidate(SceneProjectSession session, PmmProjectImportPlan plan,
        Dictionary<int, Guid> modelObjects, IEnumerable<string> diagnostics, IEnumerable<int> modelHeaderOverridesRequired)
    {
        this.session = session; Plan = plan;
        ModelObjects = new ReadOnlyDictionary<int, Guid>(modelObjects);
        SavedPoses = new ReadOnlyDictionary<Guid, VmdMotion>(plan.Models.ToDictionary(
            model => modelObjects[model.Source.Index], model => model.SavedPoseMotion));
        var sceneTracks = new PmmSceneTracks(plan.Document);
        SavedCamera = sceneTracks.SavedCamera; SavedLight = sceneTracks.SavedLight;
        SavedSelfShadow = sceneTracks.SavedSelfShadow;
        ModelHeaderOverridesRequired = Array.AsReadOnly(modelHeaderOverridesRequired.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
    public SceneProjectSession TakeSession()
    {
        var owned = Session; session = null; return owned;
    }
    public void Dispose() { session?.Dispose(); session = null; }
}

/// <summary>Formal editor interchange uses the existing format readers, project owner and compatibility gates.
/// No active workspace, source model, motion or pose is modified during preparation.</summary>
public static class EditorProjectInterchange
{
    public static PmmProjectImportPlan PreparePmm(string path, PmmImportOptions? options = null,
        CancellationToken token = default)
        => PmmProjectReader.Prepare(Path.GetFullPath(path), options, cancellationToken: token);

    /// <summary>Reports channels the current modern owner cannot evaluate. Retention alone is not playback support.</summary>
    public static IReadOnlyList<string> InspectPmmSupport(PmmProjectImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var source = plan.Document;
        var issues = new List<string>();
        if (source.Models.Count == 0) issues.Add(EditorLocalization.Text("此 PMM 没有模型；当前工程宿主需要至少一个模型。"));
        if (source.Version != 2) issues.Add(EditorLocalization.Text("PMM 0001 尚不能完整接入正式工程的控制与保存边界。请先在原工具另存为 PMM 0002。"));
        int ordinaryAccessories = source.Accessories.Count(accessory => !IsMmeHelper(accessory));
        if (ordinaryAccessories != 0)
            issues.Add(EditorLocalization.Format("Mda.Text.PmmOrdinaryAccessoriesUnsupported", ordinaryAccessories));
        if (source.Settings.VideoOffsetX != 0 || source.Settings.VideoOffsetY != 0
            || source.Settings.VideoScale is not (0 or 1)
            || source.Settings.ImageOffsetX != 0 || source.Settings.ImageOffsetY != 0
            || source.Settings.ImageScale is not (0 or 1))
            issues.Add(EditorLocalization.Text("此 PMM 含背景视频或图片的位移／缩放；正式工程尚不能完整表示这些媒体布局。"));
        return issues.AsReadOnly();
    }

    /// <summary>Visible information which does not prevent importing the supported authored scene.</summary>
    public static IReadOnlyList<string> InspectPmmNotices(PmmProjectImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int helpers = plan.Document.Accessories.Count(IsMmeHelper);
        return helpers == 0 ? [] : [EditorLocalization.Format(
            "Mda.Text.PmmMmeNotice", helpers)];
    }

    /// <summary>Classic imports prepare every X accessory, including effect helpers, rather than
    /// applying the modern engine's explicit helper-omission policy.</summary>
    internal static IReadOnlyList<string> InspectClassicPmmSupport(PmmProjectImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var source = plan.Document;
        var issues = new List<string>();
        if (source.Models.Count == 0) issues.Add(EditorLocalization.Text("此 PMM 没有模型；当前工程宿主需要至少一个模型。"));
        if (source.Version != 2) issues.Add(EditorLocalization.Text("PMM 0001 尚不能完整接入正式工程的控制与保存边界。请先在原工具另存为 PMM 0002。"));
        return issues.AsReadOnly();
    }

    // Exact, established helper package/file pairs only. A generic .x mesh or an arbitrary file
    // with a nearby effect is not evidence that its visible geometry can be omitted.
    private static bool IsMmeHelper(PmmAccessory accessory)
    {
        string reference = accessory.Path.Value.Replace('\\', '/').TrimEnd('/');
        string[] pairs = ["ExcellentShadow2/ExcellentShadow.x", "SvSSAO/SvSSAO.x", "Diffusion7/Diffusion.x",
            "LightBloom/LightBloom.x", "AutoLuminous4/AutoLuminous.x"];
        return pairs.Any(pair => reference.Equals(pair, StringComparison.OrdinalIgnoreCase)
            || reference.EndsWith("/" + pair, StringComparison.OrdinalIgnoreCase));
    }

    public static EditorPmmCandidate CreatePmmCandidate(PmmProjectImportPlan plan,
        Func<PreparedPmmModel, CancellationToken, SceneAnimationModelAsset> loadAsset,
        IProjectPhysicsBackendProvider physics, PmmImportOptions? importOptions = null,
        CancellationToken token = default, IReadOnlyDictionary<int, string>? modelHeaderOverrides = null,
        IReadOnlyDictionary<int, string>? displayNameOverrides = null)
        => CreatePmmCandidateCore(plan, loadAsset, physics, importOptions, token, modelHeaderOverrides,
            displayNameOverrides, classicEngine: null, emmBytes: null, absoluteEmmPath: null, accessoryReadOptions: null);

    /// <summary>The selected Classic engine prepares its existing frozen effects policy exactly once.
    /// EMM bytes and their original absolute identity must be supplied together; omitting both creates
    /// an empty EMM configuration while retaining the pinned host's implicit FX discovery.</summary>
    internal static EditorPmmCandidate CreateClassicPmmCandidate(PmmProjectImportPlan plan,
        Func<PreparedPmmModel, CancellationToken, SceneAnimationModelAsset> loadAsset,
        IProjectPhysicsBackendProvider physics, EditorClassicRenderEngine engine,
        PmmImportOptions? importOptions = null, CancellationToken token = default,
        IReadOnlyDictionary<int, string>? modelHeaderOverrides = null,
        IReadOnlyDictionary<int, string>? displayNameOverrides = null,
        ReadOnlyMemory<byte>? emmBytes = null, string? absoluteEmmPath = null,
        XTextReadOptions? accessoryReadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (emmBytes.HasValue != (absoluteEmmPath is not null))
            throw new ArgumentException("Explicit EMM bytes and their original absolute path must be provided together.");
        if (absoluteEmmPath is not null && !Path.IsPathFullyQualified(absoluteEmmPath))
            throw new ArgumentException("An explicit EMM source requires an absolute path.", nameof(absoluteEmmPath));
        return CreatePmmCandidateCore(plan, loadAsset, physics, importOptions, token, modelHeaderOverrides,
            displayNameOverrides, engine, emmBytes, absoluteEmmPath, accessoryReadOptions);
    }

    private static EditorPmmCandidate CreatePmmCandidateCore(PmmProjectImportPlan plan,
        Func<PreparedPmmModel, CancellationToken, SceneAnimationModelAsset> loadAsset,
        IProjectPhysicsBackendProvider physics, PmmImportOptions? importOptions,
        CancellationToken token, IReadOnlyDictionary<int, string>? modelHeaderOverrides,
        IReadOnlyDictionary<int, string>? displayNameOverrides, EditorClassicRenderEngine? classicEngine,
        ReadOnlyMemory<byte>? emmBytes, string? absoluteEmmPath, XTextReadOptions? accessoryReadOptions)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(loadAsset);
        ArgumentNullException.ThrowIfNull(physics); token.ThrowIfCancellationRequested();
        plan.RequireConfirmedRoles();
        if (!plan.CanCommit) throw new InvalidDataException(EditorLocalization.Text("请重新定位所有缺失或无效的模型，再导入 PMM。"));
        var unsupported = classicEngine is null ? InspectPmmSupport(plan) : InspectClassicPmmSupport(plan);
        if (unsupported.Count != 0) throw new NotSupportedException(string.Join(Environment.NewLine, unsupported));
        var composition = new SceneCompositionEditor();
        var lighting = new SceneLightingEditor();
        var motions = new List<SceneActorMotion>();
        var identities = new Dictionary<int, Guid>();
        var headerOverridesRequired = new List<int>();
        var diagnostics = new List<string>(plan.Diagnostics.Where(message =>
            !message.StartsWith("PMM camera, light, output size", StringComparison.Ordinal)));
        if (classicEngine is null) diagnostics.AddRange(InspectPmmNotices(plan));
        var workingFocus = new PmmSceneTracks(plan.Document).SavedCamera.LookAt;
        bool hasIndoorEnvironment = false;
        foreach (var prepared in plan.Models.OrderBy(item => item.Source.DrawOrderIndex))
        {
            token.ThrowIfCancellationRequested();
            var asset = loadAsset(prepared, token);
            ArgumentNullException.ThrowIfNull(asset);
            if (classicEngine is not null) asset = classicEngine.PrepareModelAsset(asset, prepared.Path, token);
            var source = asset.Source;
            // Same model-local shadow switches as the established PMM playback adapter.
            source = new(source.Surface, source.Outline, source.OutlineOptions, source.Options,
                source.Materials.Select(flags => flags with
                {
                    CastSelfShadow = flags.CastSelfShadow && prepared.Source.SelfShadowEnabled,
                    ReceiveSelfShadow = flags.ReceiveSelfShadow && prepared.Source.SelfShadowEnabled
                }), source.Lighting, source.EditorOverlay);
            var display = PmmNameResolver.ResolveDisplay(prepared.Source.Name, [prepared.Model.Name, prepared.Model.EnglishName]);
            string name = display.BoundName ?? prepared.Source.Name.Value;
            if (displayNameOverrides?.TryGetValue(prepared.Source.Index, out var selectedName) == true)
            {
                if (!display.Candidates.Any(candidate => candidate.Value == selectedName))
                    throw new ArgumentException("A display-name override must select an original PMM encoding interpretation.", nameof(displayNameOverrides));
                name = selectedName;
            }
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(prepared.Path);
            var item = composition.Add(name, prepared.Path, prepared.Purpose.Role, source);
            lighting.SetModelRole(item.Key, prepared.Purpose.Role, prepared.Purpose.EnvironmentIndoor);
            if (classicEngine is null && prepared.Purpose is { Role: ModelRole.Environment, EnvironmentIndoor: true })
            {
                hasIndoorEnvironment = true;
                // One shared, editable starter light serves this imported project. Selecting
                // several indoor instances must not consume the entire four-light budget.
                if (lighting.Lights.Count == 0)
                    lighting.Add(EditorIndoorImportLighting.Create(asset, workingFocus, lighting.Lights));
            }
            identities.Add(prepared.Source.Index, item.Id);
            SceneMotionAsset? motion = null;
            string? headerOverride = modelHeaderOverrides?.GetValueOrDefault(prepared.Source.Index);
            try
            {
                motion = SceneMotionAsset.FromMotion(prepared.Motion, $"pmm-{prepared.Source.Index}.vmd",
                    new() { AnsiCodePage = plan.Document.AnsiCodePage, ModelNameOverride = headerOverride });
            }
            catch (ArgumentException error) when (error.ParamName == "header.modelName" && headerOverride is null)
            {
                // PMM can contain a PMX display name too long for a VMD header. Loading it must not rewrite
                // that name or its registered motion. The owner asks for an explicit short header at save/export.
                headerOverridesRequired.Add(prepared.Source.Index);
                diagnostics.Add(EditorLocalization.Format("模型 #{0} 的 VMD 模型头需要一个较短且可编码的名称；已保留完整动作，保存工程或导出前请明确填写名称。", prepared.Source.Index));
            }
            catch (ArgumentException error) when (error.ParamName?.EndsWith(".name", StringComparison.Ordinal) == true)
            {
                // PMM's channel names can exceed VMD's 15-byte fields. Keep the registered
                // motion intact for editing/playback and PMM roundtrips. Do not silently truncate.
                diagnostics.Add(EditorLocalization.Format("模型 #{0} 的动作名称超出 VMD 字段限制；已保留完整轨道。此动作可继续编辑与渲染，保存 PMA／VMD 前需要处理名称兼容性。", prepared.Source.Index) + error.Message);
            }
            motions.Add(new(item.Id, prepared.Model, motion?.Motion ?? prepared.Motion,
                PhysicsEnabled: plan.EffectivePhysics.Enabled, Asset: motion));
        }
        var controls = ScenePmmControlState.Create(plan.Document, motions.Select(actor =>
            new ScenePmmActorSource(actor.ObjectId, checked((byte)identities.Single(pair => pair.Value == actor.ObjectId).Key), actor.Model)));
        var settings = SettingsFromPmm(plan);
        var sceneMotion = SceneMotionAsset.FromMotion(SceneMotionFromPmm(plan.Document), "pmm-scene.vmd",
            // This track contains only camera/light/shadow channels and the standard Japanese
            // system header. Its encoding is independent of the PMM's model/channel names.
            new() { AnsiCodePage = 932 });
        var media = LoadPmmMedia(plan, importOptions, token);
        // Validate the actual frozen payload before any candidate can be adopted. Disabled
        // tracks remain portable project data and do not claim playback/decoder support.
        EditorSceneAudioInputs.Validate(media);
        var accessoryAssets = classicEngine is null ? null
            : PrepareClassicAccessories(plan, importOptions, accessoryReadOptions, token);
        if (hasIndoorEnvironment)
            diagnostics.Add(EditorLocalization.Text("Mda.Text.Context.Room.BasicLightHelp"));
        token.ThrowIfCancellationRequested();
        var session = new SceneProjectSession(composition, lighting, motions, physics, settings,
            sceneMotion: sceneMotion, media: media, initialFrame: plan.CurrentFrame,
            cancellationToken: token, pmmControls: controls, accessories: accessoryAssets?.Accessories);
        try
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(EditorLocalization.Text("已接入全部模型动作、相机／主光／自阴影轨道、动态物理与模型外亲。PMM 控制原文随工程保留。"));
            if (media.Count != 0) diagnostics.Add(EditorLocalization.Text("媒体资源已保留并纳入工程保存；音视频解码与背景显示由媒体宿主提供。"));
            EditorClassicDocumentState? metadata = null;
            if (classicEngine is not null)
            {
                var bindings = plan.Models.Select(model => new EmmObjectBinding(identities[model.Source.Index], true,
                        model.Path, model.Source.DrawOrderIndex))
                    .Concat(accessoryAssets!.Accessories.Select(accessory => new EmmObjectBinding(accessory.ObjectId, false,
                        accessory.Asset.Path, plan.Document.Accessories.Single(value => value.Index == accessory.Asset.Index).DrawOrderIndex))).ToArray();
                var originalPaths = plan.Models.ToDictionary(model => identities[model.Source.Index], model => model.Source.Path.Value);
                foreach (var accessory in accessoryAssets.Accessories)
                    originalPaths.Add(accessory.ObjectId,
                        plan.Document.Accessories.Single(value => value.Index == accessory.Asset.Index).Path.Value);
                ReadOnlyMemory<byte> source = emmBytes ?? "[Info]\nVersion=3\n[Object]\n[Effect]\nDefault=none\n"u8.ToArray();
                // A synthetic identity supplies lexical context only. No neighbouring EMM file is opened.
                string emmPath = absoluteEmmPath ?? Path.GetFullPath(Path.ChangeExtension(plan.ProjectPath, ".emm"));
                var emm = EmmReader.Read(source.Span, new() { AnsiCodePage = plan.Document.AnsiCodePage });
                var effectsPlan = CreateClassicPmmEffectsPlan(emm, emmPath, bindings, originalPaths,
                    Path.GetDirectoryName(plan.ProjectPath)!);
                var effects = classicEngine.PrepareEffects(effectsPlan, token);
                metadata = classicEngine.CaptureDocument(session, effects, accessoryAssets, token);
                diagnostics.AddRange(effectsPlan.Diagnostics.Select(value => value.Message));
                diagnostics.Add(EditorLocalization.Text("全部 X 配件及其普通／球面贴图已准备，保留原 PMM 的顺序、显隐与父骨控制。"));
            }
            return new(session, plan, identities, diagnostics, headerOverridesRequired) { Metadata = metadata };
        }
        catch { session.Dispose(); throw; }
    }

    internal static EmmImportPlan CreateClassicPmmEffectsPlan(EmmDocument document, string emmPath,
        IEnumerable<EmmObjectBinding> currentBindings, IReadOnlyDictionary<Guid, string> originalPaths,
        string workingDirectory)
    {
        var current = currentBindings.ToArray();
        var original = current.Select(binding => new EmmObjectBinding(binding.ObjectId, binding.IsModel,
            string.IsNullOrWhiteSpace(originalPaths[binding.ObjectId]) ? binding.SourcePath : originalPaths[binding.ObjectId],
            binding.RenderOrder)).ToArray();
        // A repaired PMM path keeps the same stored object identity. Match the original PMM/EMM
        // lexical paths first, then carry those assignments to the resolved resource paths by Guid.
        var sourcePlan = EmmImportPlan.Create(document, emmPath, original, workingDirectory);
        var matchedIds = sourcePlan.Matches.Select(match => match.ObjectId).ToHashSet();
        var retained = EmmImportPlan.RebindLiveObjects(sourcePlan, original.Where(binding => matchedIds.Contains(binding.ObjectId)));
        // Only originally unmatched identities enter the existing matcher using their current paths.
        return EmmImportPlan.RebindLiveObjects(retained, current);
    }

    private static D3D9ClassicAccessoryAssetsState PrepareClassicAccessories(PmmProjectImportPlan plan,
        PmmImportOptions? importOptions, XTextReadOptions? readOptions, CancellationToken token)
    {
        var resolver = new PmmAssetPathResolver(plan.ProjectPath, importOptions);
        readOptions ??= new();
        if (readOptions.MaxInputBytes is < 16 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(readOptions));
        var accessories = new List<SceneProjectAccessory>();
        foreach (var accessory in plan.Document.Accessories.OrderBy(value => value.DrawOrderIndex))
        {
            token.ThrowIfCancellationRequested();
            var resolution = resolver.Resolve(accessory.Path);
            if (resolution.Status != PmmAssetResolutionStatus.Resolved)
                throw new InvalidDataException($"Accessory #{accessory.Index} '{accessory.Path.Value}': {resolution.Status}; candidates: {string.Join(" | ", resolution.Candidates)}");
            string path = resolution.Path!;
            if (!Path.GetExtension(path).Equals(".x", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Classic PMM accessories require their original .x files.");
            var bytes = ReadPmmImportBytes(path, readOptions.MaxInputBytes, token);
            // The existing Classic asset collector owns texture token grammar and freezes image bytes.
            // Parsing here never substitutes modern helper handling or creates a second playback owner.
            var asset = PreparedPmmAccessory.FromOriginalBytes(accessory.Index, path, bytes,
                readOptions: readOptions, diagnostics: resolution.Diagnostics, cancellationToken: token);
            accessories.Add(new(Guid.NewGuid(), asset));
        }
        return D3D9ClassicAccessoryAssets.Capture(accessories,
            (bytes, authoredToken) => EditorTextureLoader.Decode(bytes, authoredToken, token), cancellationToken: token);
    }

    internal static byte[] ReadPmmImportBytes(string path, long maximumBytes, CancellationToken token)
    {
        if (maximumBytes is < 1 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        if (input.Length > maximumBytes) throw new InvalidDataException("PMM import resource exceeds its byte limit: " + path);
        var bytes = new byte[checked((int)input.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            int count = input.Read(bytes, offset, Math.Min(16 * 1024, bytes.Length - offset));
            if (count == 0) throw new EndOfStreamException("PMM import resource changed while freezing its original bytes.");
            offset += count;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("PMM import resource changed while freezing its original bytes.");
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    /// <summary>Only layout conversion; samplers and authored curve semantics remain the existing core's responsibility.</summary>
    public static VmdMotion SceneMotionFromPmm(PmmDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        byte[] CameraCurve(IReadOnlyList<byte> raw)
        {
            if (raw.Count != 24) throw new InvalidDataException(EditorLocalization.Text("PMM 相机插值需要 24 字节。"));
            var result = raw.ToArray();
            // Inverse of PmmSceneMotionExporter.CameraInterpolation: PMM x1,y1,x2,y2 -> VMD x1,x2,y1,y2.
            for (int index = 0; index < result.Length; index += 4)
                (result[index + 1], result[index + 2]) = (result[index + 2], result[index + 1]);
            return result;
        }
        return new()
        {
            ModelName = "カメラ・照明",
            CameraKeyframes = new[] { document.Camera.InitialKeyframe }.Concat(document.Camera.Keyframes).Select(key => new VmdCameraKeyframe
            {
                Frame = checked((uint)key.Frame), Distance = key.Distance, Interest = key.LookAt,
                Rotation = key.Angle, ViewAngle = checked((uint)key.Fov), Perspective = key.Perspective,
                Interpolation = CameraCurve(key.Interpolation)
            }).ToArray(),
            LightKeyframes = new[] { document.Light.InitialKeyframe }.Concat(document.Light.Keyframes).Select(key => new VmdLightKeyframe
            { Frame = checked((uint)key.Frame), Color = key.Color, Direction = key.Direction }).ToArray(),
            SelfShadowKeyframes = new[] { document.SelfShadow.InitialKeyframe }.Concat(document.SelfShadow.Keyframes).Select(key => new VmdSelfShadowKeyframe
            { Frame = checked((uint)key.Frame), Mode = key.Mode, Distance = key.Distance }).ToArray()
        };
    }

    private static ProjectSettings SettingsFromPmm(PmmProjectImportPlan plan)
    {
        float fps = plan.Document.Settings.PreferredFps;
        if (!float.IsFinite(fps) || fps <= 0) throw new InvalidDataException(EditorLocalization.Text("PMM 的输出帧率必须大于 0。"));
        // Decimal UI timing uses the same rational conversion as the output settings editor.
        decimal rate = (decimal)fps;
        int denominator = 1;
        while (rate != decimal.Truncate(rate) && denominator <= 100_000_000)
        { rate *= 10; denominator *= 10; }
        if (rate > int.MaxValue || rate != decimal.Truncate(rate))
            throw new NotSupportedException(EditorLocalization.Text("PMM 的帧率超出当前精确输出计时范围。"));
        int numerator = checked((int)rate);
        int a = numerator, b = denominator;
        while (b != 0) (a, b) = (b, a % b);
        var gravity = plan.EffectivePhysics.Gravity;
        bool black = plan.Document.Settings.BlackBackgroundEnabled;
        return new()
        {
            Width = plan.OutputWidth, Height = plan.OutputHeight, FpsNumerator = numerator / a, FpsDenominator = denominator / a,
            BackgroundRed = black ? 0 : 1, BackgroundGreen = black ? 0 : 1, BackgroundBlue = black ? 0 : 1,
            GroundShadowEnabled = plan.Document.Settings.GroundShadowShown,
            Physics = new()
            {
                Backend = ProjectPhysicsBackend.Bullet, GroundEnabled = plan.EffectivePhysics.GroundEnabled,
                GravityX = gravity.X, GravityY = gravity.Y, GravityZ = gravity.Z
            }
        };
    }

    private static IReadOnlyList<SceneMediaTrack> LoadPmmMedia(PmmProjectImportPlan plan, PmmImportOptions? options, CancellationToken token)
    {
        var resolver = new PmmAssetPathResolver(plan.ProjectPath, options);
        var result = new List<SceneMediaTrack>();
        var source = plan.Document.Settings;
        void Add(PmmString reference, SceneMediaKind kind, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(reference.Value)) return;
            token.ThrowIfCancellationRequested();
            var path = resolver.Resolve(reference);
            if (path.Status != PmmAssetResolutionStatus.Resolved)
                throw new InvalidDataException(EditorLocalization.Format("PMM 媒体“{0}”未能定位：{1}。请保留原工程并修复资源路径。", reference.Value, path.Status));
            // The media bytes are embedded in the project and its recovery copies. Only accept files of the media type
            // the PMM slot is for, so a crafted PMM cannot pull an arbitrary local file into a shareable package.
            if (!IsAllowedMedia(kind, path.Path!))
                throw new InvalidDataException(EditorLocalization.Format("PMM 媒体“{0}”不是受支持的媒体文件类型。请保留原工程并修复资源路径。", reference.Value));
            result.Add(new(Guid.NewGuid(), kind, SceneMediaAsset.Load(path.Path!, token), 0, enabled));
        }
        Add(source.AudioPath, SceneMediaKind.Audio, source.AudioEnabled);
        Add(source.VideoPath, SceneMediaKind.Video, source.VideoEnabledRaw != 0);
        Add(source.ImagePath, SceneMediaKind.BackgroundImage, source.ImageEnabled);
        return result.AsReadOnly();
    }

    internal static bool IsAllowedMedia(SceneMediaKind kind, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return kind switch
        {
            SceneMediaKind.Audio => extension is ".wav",
            SceneMediaKind.Video => extension is ".avi" or ".mp4" or ".mov" or ".mkv" or ".webm" or ".wmv" or ".mpg" or ".mpeg",
            SceneMediaKind.BackgroundImage => extension is ".bmp" or ".png" or ".jpg" or ".jpeg" or ".tga" or ".gif",
            _ => false,
        };
    }

    public static SceneMotionAsset PrepareVmd(string path, PmxModel? targetModel = null, int ansiCodePage = 932,
        IReadOnlyList<VmdNameOverride>? nameOverrides = null, CancellationToken token = default)
        => SceneMotionAsset.Load(Path.GetFullPath(path), token, new()
        {
            AnsiCodePage = ansiCodePage, NameOverrides = nameOverrides ?? [],
            BoneNameCandidates = targetModel?.Bones.Select(bone => bone.Name).Distinct(StringComparer.Ordinal).ToArray(),
            MorphNameCandidates = targetModel?.Morphs.Select(morph => morph.Name).Distinct(StringComparer.Ordinal).ToArray()
        });

    /// <summary>Returns named records for the existing MotionEditor.PrepareRegister conflict and Undo transaction.</summary>
    public static VmdMotion PrepareVpd(string path, PmxModel model, uint frame,
        VpdReadOptions? options = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        var pose = VpdReader.Read(input, options);
        token.ThrowIfCancellationRequested();
        return VpdMotionAdapter.Convert(pose, model, frame);
    }

    /// <summary>Strict writer errors, including header.modelName, reach the dialog; only a user-supplied override changes the header.</summary>
    public static void ExportVmd(string path, VmdMotion motion, int ansiCodePage = 932,
        string? modelNameOverride = null, CancellationToken token = default)
    {
        byte[] bytes = VmdWriter.ToBytes(motion, new()
        { AnsiCodePage = ansiCodePage, ModelNameOverride = modelNameOverride, PreserveOriginalNames = true });
        SaveBytes(path, ".vmd", bytes, token);
    }

    public static void ExportVpd(string path, PmxModel model, IReadOnlyList<VpdLocalBoneTransform> localAnimation,
        IReadOnlyDictionary<int, float>? morphWeights = null, string? parentFileName = null,
        VpdTextEncoding encoding = VpdTextEncoding.Cp932, CancellationToken token = default)
    {
        var pose = VpdPoseExporter.ConvertLocalPose(model, localAnimation, morphWeights);
        var named = new VpdPose
        {
            ParentFileName = parentFileName ?? "model.pmx", Bones = pose.Bones, Morphs = pose.Morphs
        };
        using var output = new MemoryStream(); VpdWriter.Write(output, named, encoding);
        SaveBytes(path, ".vpd", output.ToArray(), token);
    }

    public static PmaPmmCompatibilityReport InspectPmmExport(SceneProjectSession session, PmmSessionExportOptions? options = null)
        => PmaPmmCompatibility.Inspect(session, options);

    public static void ExportPmm(string path, SceneProjectSession session, PmmSessionExportOptions? options = null,
        CancellationToken token = default)
    {
        var report = InspectPmmExport(session, options);
        if (!report.CanExport) throw new PmaPmmExportException(report);
        session.SavePmm(path, options, token);
    }

    private static void SaveBytes(string path, string extension, byte[] bytes, CancellationToken token)
    {
        string destination = Path.GetFullPath(path);
        if (!Path.GetExtension(destination).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(EditorLocalization.Format("请选择 {0} 文件名。", extension), nameof(path));
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (int offset = 0; offset < bytes.Length; offset += 64 * 1024)
                { token.ThrowIfCancellationRequested(); output.Write(bytes.AsSpan(offset, Math.Min(64 * 1024, bytes.Length - offset))); }
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
