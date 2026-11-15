using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Runtime;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

/// <summary>A native-light transaction changes no authored pose source, so its refresh and Undo/Redo
/// retain unregistered animation input. All ordinary project changes keep their invalidation policy.</summary>
internal sealed class EditorWorkspaceChangedEventArgs(bool preservePoseDrafts) : EventArgs
{
    public bool PreservePoseDrafts { get; } = preservePoseDrafts;
}

/// <summary>Single UI-thread owner of the editable project. Preview borrows snapshots; export opens
/// a separately owned solver snapshot. Original models and motions are never written.</summary>
public sealed partial class EditorProjectWorkspace : IDisposable
{
    public EditorProjectWorkspace() : this(EditorModernRenderEngine.Instance) { }

    internal EditorProjectWorkspace(IEditorRenderEngine renderEngine)
    {
        ArgumentNullException.ThrowIfNull(renderEngine);
        if (string.IsNullOrWhiteSpace(renderEngine.Id)) throw new ArgumentException("A render engine requires a stable identity.", nameof(renderEngine));
        RenderEngine = renderEngine;
        persistence = EditorProjectPersistence.Require(renderEngine, renderEngine.Persistence);
    }

    internal IEditorRenderEngine RenderEngine { get; }
    private readonly IEditorProjectPersistence persistence;
    public EditorProjectFormat ProjectFormat => persistence.Format;
    internal IEditorProjectMetadata? ProjectMetadata { get; private set; }
    internal EditorRenderCleanupLedger RenderCleanup { get; } = new();
    private string packageCacheDirectory = CacheDirectory;
    /// <summary>The package extraction owner can be isolated before a document is opened.
    /// Changing it while content is owned would split resource ownership across caches.</summary>
    internal string PackageCacheDirectory
    {
        get => packageCacheDirectory;
        set
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("A package cache requires an absolute path.", nameof(value));
            if (Session is not null || package is not null) throw new InvalidOperationException("The package cache cannot change while a document is open.");
            packageCacheDirectory = Path.GetFullPath(value);
        }
    }
    internal IEditorRenderBinding CaptureRenderBinding()
        => EditorRenderEngineBinding.Require(RenderEngine, RenderEngine.Bind(this));

    private PmaLoadedProject? package;
    private bool disposed;
    private sealed record HistoryStep(SceneProjectSession? Before, SceneProjectSession? After, bool SessionEdit,
        Dictionary<Guid, D3D12MaterialBindingProfile> BeforeMappings, Dictionary<Guid, D3D12MaterialBindingProfile> AfterMappings,
        IEditorProjectMetadata? BeforeMetadata, IEditorProjectMetadata? AfterMetadata,
        bool PreservePoseDrafts = false);
    private readonly Stack<HistoryStep> undo = [], redo = [];
    private readonly HashSet<SceneProjectSession> standaloneSessions = [];
    private readonly Dictionary<Guid, int> importedMotionCodePages = [];
    public SceneProjectSession? Session { get; private set; }
    internal Guid DocumentId { get; private set; } = Guid.NewGuid();
    public string? ProjectPath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public Dictionary<Guid, D3D12MaterialBindingProfile> MaterialBindings { get; private set; } = [];
    public EditorPreferences Preferences { get; set; } = new();
    public PmaLoadOptions PackageLimits => new()
    {
        MaxPackageBytes = Preferences.PmaPackageMaxBytes,
        MaxExpandedBytes = Preferences.PmaExpandedMaxBytes,
        MaxEntryBytes = Preferences.PmaEntryMaxBytes
    };
    public IReadOnlyList<SceneActorMotion> Motions => Session?.CaptureAuthoredContent().Motions ?? [];
    public event EventHandler? Changed;
    public static IProjectPhysicsBackendProvider CreatePhysicsProvider() => ProjectPhysicsRuntime.CreateProjectProvider();

    public static SceneAnimationModelAsset LoadModelAsset(string path, CancellationToken token = default)
        => LoadModelAssetWithLimits(path, 10 * EditorPreferences.Gibibyte, token);

    public static SceneAnimationModelAsset LoadModelAssetWithLimits(string path, long maxBytes, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes), EditorLocalization.Text("模型输入容量上限必须大于 0。"));
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        if (input.Length > maxBytes) throw new InvalidDataException(EditorLocalization.Text("模型文件超过配置的输入容量上限。"));
        var model = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pmx" => PmxReader.Read(input, new PmxReadOptions { MaxInputBytes = maxBytes }),
            // The existing bridge retains PMD display groups, animation and physics semantics.
            // Its reader still enforces the separate 512 MiB PMD parsing limit.
            ".pmd" => PmdPmxAdapter.Convert(PmdReader.Read(input)),
            _ => throw new NotSupportedException(EditorLocalization.Text("模型载入支持 PMX 或 PMD 文件。"))
        };
        return CreateModelAsset(model, path, token);
    }

    /// <summary>Builds a render source for the exact model already prepared by the PMM/core reader.
    /// The source path is used only to resolve read-only model-local textures.</summary>
    public static SceneAnimationModelAsset CreateModelAsset(PmxModel model, string path, CancellationToken token = default)
        => CreateModelAsset(model, path, relative => LoadTexture(Path.GetDirectoryName(Path.GetFullPath(path))!, relative, token), token);

    internal static SceneAnimationModelAsset CreateModelAsset(PmxModel model, string path,
        Func<string, SceneTexture?> loadTexture, CancellationToken token = default,
        bool retainToonTextures = false, Func<int, SceneTexture?>? loadCommonToon = null,
        Func<SceneTexture?>? loadDefaultToon = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();
        var adapted = PmxOriginalStyleAdapter.FromRestPose(model, loadTexture, loadCommonToon,
            retainToonTextures: retainToonTextures, loadDefaultToon: loadDefaultToon);
        var outline = PmxOriginalOutlineAdapter.FromRestPose(model);
        var camera = new PmmCameraPose(new(0, 10, 0), Vector3.Zero, -45, 30, true).CreateCamera();
        var frame = new OriginalFrameScene(adapted.Scene, outline,
            new(Vector4.One, Matrix4x4.Identity, camera.Eye, 30, 1),
            materials: PmxOriginalFrameAdapter.FromMaterials(model));
        token.ThrowIfCancellationRequested();
        return new(model, frame);
    }

    private static SceneTexture? LoadTexture(string root, string relative, CancellationToken token)
        => EditorTextureLoader.Load(root, relative, token);

    public Guid AddModel(string path, SceneAnimationModelAsset asset, ModelRole role)
        => AddModel(path, asset, new EditorModelImportOptions(new ModelPurpose(role)));

    /// <summary>Commits one new model, its purpose and optional local starter light as one Undo step.</summary>
    public Guid AddModel(string path, SceneAnimationModelAsset asset, EditorModelImportOptions options, Vector3? workingFocus = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Purpose);
        var role = options.Purpose.Role;
        if (role is not (ModelRole.Character or ModelRole.Environment or ModelRole.Prop))
            throw new ArgumentException(EditorLocalization.Text("请明确选择角色、场景或道具。"), nameof(role));
        if ((role != ModelRole.Environment && options.Purpose.EnvironmentIndoor.HasValue)
            || (options.AddIndoorBasicLight && options.Purpose is not { Role: ModelRole.Environment, EnvironmentIndoor: true }))
            throw new ArgumentException(EditorLocalization.Text("Mda.Text.Context.Room.InvalidImportOptions"), nameof(options));
        if (options.AddIndoorBasicLight && (Session?.Lighting.Lights.Count ?? 0) >= SceneLightingSnapshot.MaxLights)
            throw new InvalidOperationException(EditorLocalization.Text("Mda.Text.Context.Room.LightsChanged"));
        string full = Path.GetFullPath(path);
        if (RenderEngine is EditorClassicRenderEngine classic)
            asset = classic.PrepareModelAsset(asset, full, CancellationToken.None);
        Guid id = Guid.Empty;
        void Add(SceneCompositionEditor composition, SceneLightingEditor lighting, IList<SceneActorMotion> motions)
        {
            var item = composition.Add(string.IsNullOrWhiteSpace(asset.Model.Name) ? Path.GetFileNameWithoutExtension(full) : asset.Model.Name,
                full, role, asset.Source, nameFollowsSource: true);
            id = item.Id;
            lighting.SetModelRole(item.Key, role, options.Purpose.EnvironmentIndoor);
            if (options.AddIndoorBasicLight)
                lighting.Add(EditorIndoorImportLighting.Create(asset, workingFocus, lighting.Lights));
            var motion = new VmdMotion { ModelName = asset.Model.Name };
            var captured = CaptureMotion(motion, "motion.vmd", new());
            motions.Add(new(id, asset.Model, captured?.Motion ?? motion, Asset: captured));
        }
        if (Session is null)
        {
            var composition = new SceneCompositionEditor();
            var lighting = new SceneLightingEditor();
            var motions = new List<SceneActorMotion>();
            Add(composition, lighting, motions);
            var sceneMotion = SceneMotionAsset.FromMotion(new VmdMotion
            {
                CameraKeyframes = [new() { Frame = 0, Interest = new(0, 10, 0), Distance = -45, ViewAngle = 30 }]
            }, "scene.vmd");
            var candidate = new SceneProjectSession(composition, lighting, motions, CreatePhysicsProvider(),
                new ProjectSettings { BackgroundRed = 1, BackgroundGreen = 1, BackgroundBlue = 1 }, sceneMotion: sceneMotion);
            IEditorProjectMetadata? metadata;
            try { metadata = PrepareInitialModelMetadata(candidate); }
            catch { candidate.Dispose(); throw; }
            var beforeMetadata = ProjectMetadata;
            Session = candidate; ProjectMetadata = metadata;
            standaloneSessions.Add(Session);
            undo.Push(new(null, Session, false, [], SnapshotMappings(), beforeMetadata, metadata)); redo.Clear();
        }
        else { Edit(edit => Add(edit.Composition, edit.Lighting, edit.Motions)); return id; }
        MarkChanged();
        return id;
    }

    public void CommitMotion(Guid objectId, VmdMotion motion)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        var old = Motions.Single(actor => actor.ObjectId == objectId);
        var writeOptions = new VmdWriteOptions
        {
            AnsiCodePage = MotionCodePage(old),
            // A user-confirmed short header belongs to this binding and survives subsequent edits.
            ModelNameOverride = old.Asset is { } previous && motion.ModelName != previous.Motion.ModelName
                ? previous.Motion.ModelName : null
        };
        var asset = CaptureMotion(motion, old.Asset?.Name ?? "motion.vmd", writeOptions);
        Edit(edit =>
        {
            int index = edit.Motions.FindIndex(actor => actor.ObjectId == objectId);
            if (index < 0) throw new InvalidOperationException(EditorLocalization.Text("选中的模型已被移除。"));
            edit.Motions[index] = edit.Motions[index] with { Motion = asset?.Motion ?? motion, Asset = asset };
        });
    }

    private static SceneMotionAsset? CaptureMotion(VmdMotion motion, string name, VmdWriteOptions options)
    {
        try { return SceneMotionAsset.FromMotion(motion, name, options); }
        catch (ArgumentException error) when (error.ParamName == "header.modelName" && options.ModelNameOverride is null)
        {
            // PMX/PMM display names have no VMD's twenty-byte header restriction. Keep all authored
            // tracks intact; the save/export host must ask for an explicit encodable header.
            return null;
        }
        catch (ArgumentException error) when (error.ParamName?.EndsWith(".name", StringComparison.Ordinal) == true)
        {
            // Keep PMM's full channel names editable. PMA/VMD serialization reports its own
            // format restriction before writing; ordinary pose edits must not truncate them.
            return null;
        }
    }

    /// <summary>Registers all prepared actor/scene drafts as one undoable project transaction.</summary>
    public void CommitDraftMotions(IReadOnlyDictionary<Guid, VmdMotion> motions, VmdMotion? sceneMotion,
        uint cameraFrame = 0, PmmCameraPose? cameraPose = null)
    {
        var source = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        var replacements = motions.ToDictionary(pair => pair.Key, pair =>
        {
            var actor = Motions.Single(item => item.ObjectId == pair.Key);
            var asset = CaptureMotion(pair.Value, actor.Asset?.Name ?? "motion.vmd", new()
            { AnsiCodePage = MotionCodePage(actor), ModelNameOverride = actor.Asset?.Motion.ModelName });
            return actor with { Motion = asset?.Motion ?? pair.Value, Asset = asset };
        });
        var oldScene = source.CaptureAuthoredContent().SceneMotion;
        var sceneAsset = sceneMotion is null ? null : SceneMotionAsset.FromMotion(sceneMotion, oldScene?.Name ?? "scene.vmd",
            new() { AnsiCodePage = oldScene?.AnsiCodePage ?? 932 });
        if (replacements.Count == 0 && sceneAsset is null) return;
        Edit(edit =>
        {
            for (int index = 0; index < edit.Motions.Count; index++)
                if (replacements.TryGetValue(edit.Motions[index].ObjectId, out var actor)) edit.Motions[index] = actor;
            if (sceneAsset is not null) edit.SceneMotion = sceneAsset;
            if (cameraPose is { } camera && edit.PmmControls is { } controls)
                edit.PmmControls = controls.WithCameraParent(cameraFrame,
                    RegisteredCamera(sceneAsset?.Motion, cameraFrame, camera));
        });
    }

    /// <summary>Bindings whose complete authored motion needs an explicit VMD model header before
    /// PMA save/export. This read has no edit or Undo side effect.</summary>
    public IReadOnlyList<SceneActorMotion> GetMotionHeaderRequirements()
        => Array.AsReadOnly(Motions.Where(actor => actor.Asset is null && IsHeaderFailure(actor)).ToArray());

    private bool IsHeaderFailure(SceneActorMotion actor)
    {
        try { _ = VmdWriter.ToBytes(actor.Motion, new() { AnsiCodePage = MotionCodePage(actor) }); return false; }
        catch (ArgumentException error) when (error.ParamName == "header.modelName") { return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Channel names on one actor that the VMD-based project storage cannot write losslessly
    /// (field width or code page). Empty when the motion is persistable.</summary>
    public IReadOnlyList<string> GetUnpersistableMotionNames(Guid objectId)
    {
        var actor = Motions.SingleOrDefault(item => item.ObjectId == objectId);
        if (actor is null || actor.Asset is not null) return [];
        return UnpersistableNames(actor.Motion, MotionCodePage(actor));
    }

    /// <summary>Channels whose names the VMD-based storage cannot write, judged per channel kind against that kind's
    /// own field width (bone/morph 15 bytes, IK 20 bytes). A name dropped on one kind does not affect another.</summary>
    private sealed record DroppedChannels(IReadOnlySet<string> Bones, IReadOnlySet<string> Morphs, IReadOnlySet<string> IkBones)
    {
        internal bool IsEmpty => Bones.Count == 0 && Morphs.Count == 0 && IkBones.Count == 0;
        internal IReadOnlyList<string> Names => Bones.Concat(Morphs).Concat(IkBones).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> UnpersistableNames(VmdMotion motion, int codePage) => DroppedChannelsOf(motion, codePage).Names;

    private static DroppedChannels DroppedChannelsOf(VmdMotion motion, int codePage)
    {
        var encoding = LegacyAnsiEncoding.Create(codePage);
        var bones = new HashSet<string>(StringComparer.Ordinal);
        var morphs = new HashSet<string>(StringComparer.Ordinal);
        var ik = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in motion.BoneKeyframes) if (!Fits(key.BoneName, key.OriginalName, 15)) bones.Add(key.BoneName);
        foreach (var key in motion.MorphKeyframes) if (!Fits(key.MorphName, key.OriginalName, 15)) morphs.Add(key.MorphName);
        foreach (var key in motion.IkKeyframes)
            foreach (var item in key.Switches) if (!Fits(item.BoneName, item.OriginalName, 20)) ik.Add(item.BoneName);
        return new(bones, morphs, ik);

        bool Fits(string name, VmdEncodedName? original, int width)
        {
            if (original is not null && string.Equals(original.Value, name, StringComparison.Ordinal)) return original.Bytes.Count <= width;
            try { return encoding.GetByteCount(name) <= width; }
            catch (System.Text.EncoderFallbackException) { return false; }
        }
    }

    /// <summary>A VMD-representable copy of an unpersistable motion: keyframes on channels whose names cannot be
    /// written are left out, every other track is kept. Used only for recovery copies; null if still unwritable.</summary>
    private static SceneMotionAsset? RecoveryProjection(SceneActorMotion actor, int codePage, DroppedChannels dropped)
    {
        var motion = actor.Motion;
        var projected = new VmdMotion
        {
            ModelName = motion.ModelName, OriginalModelName = motion.OriginalModelName,
            BoneKeyframes = motion.BoneKeyframes.Where(key => !dropped.Bones.Contains(key.BoneName)).ToArray(),
            MorphKeyframes = motion.MorphKeyframes.Where(key => !dropped.Morphs.Contains(key.MorphName)).ToArray(),
            CameraKeyframes = motion.CameraKeyframes, LightKeyframes = motion.LightKeyframes,
            SelfShadowKeyframes = motion.SelfShadowKeyframes,
            IkKeyframes = motion.IkKeyframes.Select(key => new VmdIkKeyframe
            {
                Frame = key.Frame, Visible = key.Visible,
                Switches = key.Switches.Where(item => !dropped.IkBones.Contains(item.BoneName)).ToArray()
            }).ToArray(),
            Diagnostics = motion.Diagnostics,
        };
        try { return SceneMotionAsset.FromMotion(projected, "motion.vmd", new() { AnsiCodePage = codePage }); }
        catch (ArgumentException) { return null; }
    }

    public IReadOnlyList<string> GetMotionPersistenceErrors() => GetMotionPersistenceErrors(null);

    private IReadOnlyList<string> GetMotionPersistenceErrors(IReadOnlySet<Guid>? projectedActors)
    {
        var errors = new List<string>();
        foreach (var actor in Motions.Where(actor => actor.Asset is null && projectedActors?.Contains(actor.ObjectId) != true))
        {
            try { _ = VmdWriter.ToBytes(actor.Motion, new() { AnsiCodePage = MotionCodePage(actor) }); }
            catch (ArgumentException error) when (error.ParamName != "header.modelName")
            { errors.Add(actor.Model.Name + ": " + error.Message); }
        }
        return errors.AsReadOnly();
    }

    private int MotionCodePage(SceneActorMotion actor)
        => actor.Asset?.AnsiCodePage ?? importedMotionCodePages.GetValueOrDefault(actor.ObjectId, 932);

    /// <summary>Captures a user-supplied header as one undoable motion edit. Model display names and
    /// source files are unchanged; an invalid header fails before publication.</summary>
    public void SetMotionHeader(Guid objectId, string modelNameOverride)
    {
        ArgumentNullException.ThrowIfNull(modelNameOverride);
        var old = Motions.Single(actor => actor.ObjectId == objectId);
        var asset = SceneMotionAsset.FromMotion(old.Motion, old.Asset?.Name ?? "motion.vmd", new()
        { AnsiCodePage = MotionCodePage(old), ModelNameOverride = modelNameOverride });
        Edit(edit =>
        {
            int index = edit.Motions.FindIndex(actor => actor.ObjectId == objectId);
            if (index < 0) throw new InvalidOperationException(EditorLocalization.Text("选中的模型已被移除。"));
            edit.Motions[index] = edit.Motions[index] with { Motion = asset.Motion, Asset = asset };
        });
    }

    public void BindMotion(Guid objectId, SceneMotionAsset asset)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        Edit(edit =>
        {
            int index = edit.Motions.FindIndex(actor => actor.ObjectId == objectId);
            if (index < 0) throw new InvalidOperationException(EditorLocalization.Text("请选择动作所属的模型。"));
            edit.Motions[index] = edit.Motions[index] with { Motion = asset.Motion, Asset = asset };
        });
    }

    public void CommitSceneMotion(VmdMotion motion) => CommitSceneMotion(motion, 0, null);

    public void CommitSceneMotion(VmdMotion motion, uint cameraFrame, PmmCameraPose? cameraPose)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        var old = session.CaptureAuthoredContent().SceneMotion;
        var asset = SceneMotionAsset.FromMotion(motion, old?.Name ?? "scene.vmd",
            new VmdWriteOptions { AnsiCodePage = old?.AnsiCodePage ?? 932 });
        Edit(edit =>
        {
            edit.SceneMotion = asset;
            if (cameraPose is { } camera && edit.PmmControls is { } controls)
                edit.PmmControls = controls.WithCameraParent(cameraFrame,
                    RegisteredCamera(asset.Motion, cameraFrame, camera));
        });
    }

    private static PmmCameraPose RegisteredCamera(VmdMotion? motion, uint frame, PmmCameraPose parent)
    {
        var key = motion?.CameraKeyframes.LastOrDefault(value => value.Frame == frame)
            ?? throw new InvalidOperationException("A registered camera parent requires its matching authored camera key.");
        // The numeric editor accepts fractional FOV; VMD registration rounds it to its integer
        // field. Retained PMM local values must use that same actual key, including this rounding.
        return parent with { LookAt = key.Interest, Angle = key.Rotation, Distance = key.Distance,
            Fov = key.ViewAngle, Perspective = key.Perspective, ViewOffset = Vector2.Zero };
    }

    public void Edit(Action<SceneProjectEdit> edit) => Edit(edit, null);

    /// <summary>Edits only additional lights. Pending bone, morph, camera and main-light poses are
    /// independent of these properties and remain unregistered through this transaction and its history.</summary>
    public void EditLighting(EditorLightingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        if (session.Lighting.Lights.SequenceEqual(draft.Snapshot.Lights)) return;
        Edit(edit => draft.ApplyTo(edit.Lighting), null, preservePoseDrafts: true);
    }

    private void Edit(Action<SceneProjectEdit> edit, Action? commitMappings, IReadOnlyList<(Guid ObjectId, int BoneIndex)>? preserveWorld = null,
        bool preservePoseDrafts = false)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        var before = SnapshotMappings();
        var beforeMetadata = ProjectMetadata;
        var preparedMetadata = beforeMetadata;
        void Prepare(SceneProjectEdit candidate)
        {
            edit(candidate);
            preparedMetadata = PrepareObjectEditMetadata(candidate);
        }
        if (preserveWorld is { Count: > 0 }) session.EditWithWorldPreservation(Prepare, preserveWorld);
        else session.Edit(Prepare);
        commitMappings?.Invoke();
        ProjectMetadata = preparedMetadata;
        undo.Push(new(session, session, true, before, SnapshotMappings(), beforeMetadata, preparedMetadata, preservePoseDrafts)); redo.Clear();
        MarkChanged(preservePoseDrafts);
    }

    public void ApplyRigSettings(EditorRigSettingsDraft draft)
    {
        if (draft.OutsideParents.Count == 0 && draft.Motion is null) return;
        var actor = Motions.Single(item => item.ObjectId == draft.ObjectId);
        var asset = draft.Motion is null ? actor.Asset : CaptureMotion(draft.Motion, actor.Asset?.Name ?? "motion.vmd", new()
        { AnsiCodePage = MotionCodePage(actor), ModelNameOverride = actor.Asset?.Motion.ModelName });
        Edit(edit =>
        {
            foreach (var change in draft.OutsideParents)
            {
                edit.OutsideParents.RemoveAll(binding => binding.ObjectId == draft.ObjectId && binding.BoneIndex == change.BoneIndex);
                if (change.Binding is { } binding) edit.OutsideParents.Add(binding);
            }
            if (draft.Motion is not null)
            {
                int index = edit.Motions.FindIndex(item => item.ObjectId == draft.ObjectId);
                edit.Motions[index] = actor with { Motion = asset?.Motion ?? draft.Motion, Asset = asset };
            }
        }, null, draft.OutsideParents.Where(change => change.Binding is not null && change.KeepWorld)
            .Select(change => (draft.ObjectId, change.BoneIndex)).ToArray());
    }

    public void ApplyModelProperties(EditorModelPropertiesDraft draft)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        var item = session.Objects.Single(obj => obj.Id == draft.ObjectId);
        var actor = Motions.Single(value => value.ObjectId == draft.ObjectId);
        var previousPurpose = session.GetModelPurpose(item.Id);
        bool? environmentIndoor = draft.Role == ModelRole.Environment
            ? draft.EnvironmentIndoor ?? previousPurpose.EnvironmentIndoor : null;
        var oldStyle = session.RenderingStyles.ModelOverrides.TryGetValue(item.Id, out var style) ? style : (RenderingPresetReference?)null;
        MaterialBindings.TryGetValue(item.Id, out var oldProfile);
        if (item.Name == draft.Name && item.Role == draft.Role && item.Position == draft.Position && item.Rotation == draft.Rotation && item.Scale == draft.Scale
            && actor.IkEnabled == draft.IkEnabled && actor.MorphsEnabled == draft.MorphsEnabled && actor.PhysicsEnabled == draft.PhysicsEnabled
            && oldStyle == draft.StyleOverride && Equals(oldProfile, draft.MaterialProfile)
            && previousPurpose.EnvironmentIndoor == environmentIndoor) return;
        if (draft.MaterialProfile is { } profile)
        {
            if (profile.OutlineWidths.Count != 0 && !EditorRenderEngineOptions.UsesModernQualityPreferences(RenderEngine))
                throw new NotSupportedException(EditorLocalization.Text("Mda.Text.OutlineScale.ModernOnly"));
            var catalog = Mmd.Modern.Rendering.D3D12.D3D12StylePresetCatalog.BuiltIn;
            var selected = catalog.Find((draft.StyleOverride ?? session.RenderingStyles.DefaultStyle).StableId);
            if (!selected.IsAvailable || selected.Definition is null) throw new NotSupportedException(selected.Diagnostic);
            _ = D3D12MaterialBindingProfileResolver.Resolve(profile, item.Source, item.Id, selected.Definition.PipelineId);
        }
        Edit(edit =>
        {
            edit.Composition.SetObjectMetadata(item.Id, draft.Name, draft.Role);
            edit.Composition.SetTransform(item.Id, draft.Position, draft.Rotation, draft.Scale);
            edit.Lighting.SetModelRole(item.Key, draft.Role, environmentIndoor);
            edit.SetModelRenderingStyle(item.Id, draft.StyleOverride);
            int index = edit.Motions.FindIndex(value => value.ObjectId == item.Id);
            edit.Motions[index] = edit.Motions[index] with { IkEnabled = draft.IkEnabled, MorphsEnabled = draft.MorphsEnabled, PhysicsEnabled = draft.PhysicsEnabled };
        }, () => { if (draft.MaterialProfile is { } mapping) MaterialBindings[item.Id] = mapping; else MaterialBindings.Remove(item.Id); });
    }

    /// <summary>Global default style is reference-only project metadata. The candidate is validated against
    /// every inheriting model's explicit material mapping before one workspace Undo step; a failed candidate
    /// leaves the selection, mappings, dirty flag and redo history untouched.</summary>
    public bool SetDefaultRenderingStyle(RenderingPresetReference style)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        if (!session.HasSupportedRenderingStyles)
            throw new NotSupportedException(string.Join(" ", session.RenderingStyleDiagnostics));
        var previous = session.RenderingStyles.DefaultStyle;
        if (previous == style) return false;
        var catalog = Mmd.Modern.Rendering.D3D12.D3D12StylePresetCatalog.BuiltIn;
        var selected = catalog.Find(style.StableId);
        if (!selected.IsAvailable || selected.Definition is null)
            throw new NotSupportedException(EditorLocalization.Format("画风预设“{0}”不可用。{1}", style.StableId, selected.Diagnostic));
        foreach (var item in session.Objects)
        {
            // Only models that inherit the new default change effective pipeline; explicit overrides are untouched.
            if (session.RenderingStyles.ModelOverrides.ContainsKey(item.Id)) continue;
            if (!MaterialBindings.TryGetValue(item.Id, out var profile)) continue;
            try { _ = D3D12MaterialBindingProfileResolver.Resolve(profile, item.Source, item.Id, selected.Definition.PipelineId); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or InvalidDataException)
            { throw new InvalidOperationException(EditorLocalization.Format("Mda.Text.Context.Inspector.Style.MappingConflict", item.Name, error.Message), error); }
        }
        var mappings = SnapshotMappings();
        session.SetDefaultRenderingStyle(style);
        // The metadata-only session transaction shares the owner's ordered history; bridge it like an ordinary edit.
        undo.Push(new(session, session, true, mappings, SnapshotMappings(), ProjectMetadata, ProjectMetadata)); redo.Clear();
        MarkChanged();
        return true;
    }

    public void RemoveModel(Guid id)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        if (!session.Objects.Any(item => item.Id == id)) throw new ArgumentException(EditorLocalization.Text("选中的模型已不存在。"));
        if (session.Objects.Count > 1)
        {
            Edit(edit => edit.RemoveObject(id), () => MaterialBindings.Remove(id)); return;
        }
        var before = SnapshotMappings();
        var beforeMetadata = ProjectMetadata;
        Session = null; MaterialBindings.Clear(); ProjectMetadata = null;
        undo.Push(new(session, null, false, before, [], beforeMetadata, null)); redo.Clear(); MarkChanged();
    }

    public void Save(string path, CancellationToken token = default)
    {
        string destination = ProjectFormat.RequirePath(path);
        try { SaveSnapshot(destination, token); }
        catch (PmaSaveCommittedException error)
        {
            // The neutral writer proves this operation committed; existence alone is not proof.
            ProjectPath = error.DestinationPath; IsDirty = false;
            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception notificationError) { error.Data["EditorSaveNotificationError"] = notificationError; }
            throw;
        }
        ProjectPath = destination; IsDirty = false; Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveSnapshot(string path, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        persistence.Save(ProjectFormat.RequirePath(path), this, CreateSaveOptions(token), token);
    }

    /// <summary>Capture on the document owner thread; only the detached package is written in the background.</summary>
    internal PmaSaveSnapshot CaptureRecoverySnapshot(CancellationToken token = default)
        => CaptureRecoverySnapshot(out _, token);

    /// <summary>The actor motions exactly as the next recovery copy stores them: actors with unwritable channel
    /// names get the projected motion (those channels left out), every other actor its live motion. Recovery
    /// editor state must be validated against this, not the live motion, or the copy cannot be reopened.</summary>
    internal IReadOnlyList<SceneActorMotion> RecoveryMotions()
        => Motions.Select(actor =>
        {
            if (actor.Asset is not null || IsHeaderFailure(actor)) return actor;
            int codePage = MotionCodePage(actor);
            var dropped = DroppedChannelsOf(actor.Motion, codePage);
            if (dropped.IsEmpty || RecoveryProjection(actor, codePage, dropped) is not { } asset) return actor;
            return actor with { Motion = asset.Motion, Asset = asset };
        }).ToArray();

    /// <summary>
    /// Unlike a manual save, a recovery copy must not be blocked by channel names that exceed the VMD-based
    /// storage limits; otherwise a crash loses all work. Such motions are stored without the unwritable channels
    /// and <paramref name="omitted"/> lists what was left out so the host can say so.
    /// </summary>
    internal PmaSaveSnapshot CaptureRecoverySnapshot(out IReadOnlyList<string> omitted, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var overrides = new Dictionary<Guid, SceneMotionAsset>();
        var notes = new List<string>();
        foreach (var actor in Motions.Where(actor => actor.Asset is null && !IsHeaderFailure(actor)))
        {
            int codePage = MotionCodePage(actor);
            var dropped = DroppedChannelsOf(actor.Motion, codePage);
            if (dropped.IsEmpty) continue;
            if (RecoveryProjection(actor, codePage, dropped) is not { } asset) continue;
            overrides.Add(actor.ObjectId, asset);
            notes.Add(actor.Model.Name + ": " + string.Join(", ", dropped.Names));
        }
        omitted = notes.AsReadOnly();
        var options = CreateSaveOptions(token, overrides.Keys.ToHashSet());
        if (overrides.Count != 0) options = options with { MotionOverrides = overrides };
        return persistence.CaptureSnapshot(this, options, token);
    }

    internal EditorRecoverySessionState ProjectRecoveryState(EditorRecoverySessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return persistence.ProjectRecoveryState(state);
    }

    private PmaSaveOptions CreateSaveOptions(CancellationToken token, IReadOnlySet<Guid>? projectedActors = null)
    {
        var session = Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
        token.ThrowIfCancellationRequested();
        var missingHeaders = GetMotionHeaderRequirements();
        var unsupportedNames = GetMotionPersistenceErrors(projectedActors);
        if (unsupportedNames.Count != 0) throw new NotSupportedException(EditorLocalization.Text("当前 PMA 动作存储遵守 VMD 的名称字段限制。请保留 PMM 原工程；以下名称不能无损写入，未改名或截断：\n") + string.Join("\n", unsupportedNames));
        if (missingHeaders.Count != 0)
            throw new InvalidOperationException(EditorLocalization.Text("以下模型的动作需要明确填写可编码且不超过 20 字节的 VMD 模型头，才可保存或导出：")
                + string.Join("、", missingHeaders.Select(actor => session.Objects.Single(item => item.Id == actor.ObjectId).Name)));
        return new PmaSaveOptions { ValidationOptions = PackageLimits, CacheDirectory = PackageCacheDirectory };
    }

    public void Open(string path, CancellationToken token = default) => OpenCore(path, null, false, token);

    /// <summary>Recovery is a new, dirty document. Its first manual save must choose a destination.</summary>
    internal EditorRecoverySessionState OpenRecovery(EditorRecoveryCheckpoint checkpoint, CancellationToken token = default, bool notifyChanged = true)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.ProjectFormat != ProjectFormat)
            throw new InvalidDataException("The recovery checkpoint belongs to another rendering engine.");
        var state = EditorRecoverySessionState.FromJsonBytes(System.Text.Encoding.UTF8.GetBytes(checkpoint.EditorState.GetRawText()));
        void ValidateCandidate(SceneProjectSession candidate)
        {
            using var file = File.OpenRead(checkpoint.PackagePath);
            if (file.Length != checkpoint.PackageSizeBytes
                || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)), checkpoint.Sha256))
                throw new InvalidDataException("Recovery package no longer matches its checkpoint.");
            var actors = candidate.CaptureAuthoredContent().Motions;
            state.ValidateForActors(actors);
            state.ValidateNativeLighting(candidate.Lighting);
            var inspector = new EditorSceneInspectorViewModel();
            inspector.SetPmmCameraContext(candidate);
            inspector.ValidateRecoveryContext(state.Scene, actors);
            if (Math.Abs(candidate.CurrentFrame - (state.ProjectFrame ?? state.Animation.CurrentFrame)) > 0.000001)
                throw new InvalidDataException("Recovery project and editor frames do not match.");
        }
        OpenCore(checkpoint.PackagePath, ValidateCandidate, true, token, notifyChanged);
        return state;
    }

    private void OpenCore(string path, Action<SceneProjectSession>? validateCandidate, bool recovered, CancellationToken token, bool notifyChanged = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string fullPath = ProjectFormat.RequirePath(path);
        long modelMaxBytes = Preferences.PmxMaxBytes;
        var limits = PackageLimits;
        var loaded = persistence.Load(fullPath, PackageCacheDirectory,
            (file, cancellation) => LoadModelAssetWithLimits(file, modelMaxBytes, cancellation),
            CreatePhysicsProvider(), limits, token);
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateProjectMetadata(loaded.Metadata, loaded.Package.Session);
            validateCandidate?.Invoke(loaded.Package.Session);
            token.ThrowIfCancellationRequested(); CloseContent();
        }
        catch (Exception error)
        {
            EditorProjectPersistence.DisposeFailedCandidate(loaded.Package, error);
            throw;
        }
        // All schemas, renderer metadata and mapping dictionaries were prepared before old owners
        // were released. Publication only transfers complete references and does no renderer binding.
        package = loaded.Package; Session = loaded.Package.Session; ProjectPath = recovered ? null : fullPath;
        MaterialBindings = loaded.MaterialBindings; ProjectMetadata = loaded.Metadata;
        IsDirty = recovered;
        if (notifyChanged) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Publishes a completely prepared PMM candidate and transfers its session ownership.
    /// A PMM import is an unsaved native project; its first save uses the selected engine's format.</summary>
    public void Adopt(EditorPmmCandidate candidate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(candidate);
        _ = candidate.Session.CaptureAuthoredContent(); // Validate the candidate before releasing active content.
        ValidateProjectMetadata(candidate.Metadata, candidate.Session);
        CloseContent();
        Session = candidate.TakeSession();
        ProjectMetadata = candidate.Metadata;
        standaloneSessions.Add(Session);
        foreach (var id in candidate.ModelObjects.Values)
            importedMotionCodePages.Add(id, candidate.Plan.Document.AnsiCodePage);
        ProjectPath = null; IsDirty = true; Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (!undo.TryPeek(out var step)) return false;
        if (step.SessionEdit && step.After?.Undo() != true) return false;
        undo.Pop(); redo.Push(step); Session = step.Before; ProjectMetadata = step.BeforeMetadata;
        RestoreMappings(step.BeforeMappings); MarkChanged(step.PreservePoseDrafts); return true;
    }
    public bool Redo()
    {
        if (!redo.TryPeek(out var step)) return false;
        if (step.SessionEdit && step.After?.Redo() != true) return false;
        redo.Pop(); undo.Push(step); Session = step.After; ProjectMetadata = step.AfterMetadata;
        RestoreMappings(step.AfterMappings); MarkChanged(step.PreservePoseDrafts); return true;
    }
    private void ValidateProjectMetadata(IEditorProjectMetadata? metadata, SceneProjectSession session)
    {
        if (metadata is not IEditorPreparedProjectMetadata prepared) return;
        if (!ReferenceEquals(prepared.Format, ProjectFormat))
            throw new InvalidDataException("Prepared project metadata belongs to another engine's native format.");
        prepared.ValidateSession(session);
    }
    private Dictionary<Guid, D3D12MaterialBindingProfile> SnapshotMappings() => new(MaterialBindings);
    private void RestoreMappings(Dictionary<Guid, D3D12MaterialBindingProfile> values)
    { MaterialBindings.Clear(); foreach (var value in values) MaterialBindings.Add(value.Key, value.Value); }
    public void MarkChanged() => MarkChanged(false);
    private void MarkChanged(bool preservePoseDrafts)
    { IsDirty = true; Changed?.Invoke(this, new EditorWorkspaceChangedEventArgs(preservePoseDrafts)); }
    public void New()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CloseContent(); ProjectPath = null; IsDirty = false; Changed?.Invoke(this, EventArgs.Empty);
    }
    private void CloseContent()
    {
        foreach (var retained in standaloneSessions)
            if (!ReferenceEquals(retained, package?.Session)) retained.Dispose();
        if (package is not null) package.Dispose();
        else if (Session is { } current && !standaloneSessions.Contains(current)) current.Dispose();
        // Keep the ownership set until the current-session check, so the active standalone owner
        // (also retained by workspace Undo) is disposed exactly once.
        standaloneSessions.Clear();
        undo.Clear(); redo.Clear();
        package = null; Session = null; MaterialBindings.Clear(); ProjectMetadata = null; importedMotionCodePages.Clear();
        DocumentId = Guid.NewGuid();
    }
    public static string CacheDirectory => EditorUserDataPaths.Current.ProjectCacheDirectory;
    public void Dispose()
    {
        if (disposed) return;
        // A failed native cleanup remains owned here. Keep the workspace retryable and its
        // content alive until no detached renderer still depends on the export lifetime.
        RenderCleanup.DrainAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        CloseContent(); disposed = true;
    }
}
