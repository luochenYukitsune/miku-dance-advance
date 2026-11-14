using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed class PmmSessionExportOptions
{
    public PmmDocument? Template { get; init; }
    /// <summary>Actor identity to the template model's stored Index, never its list position. Must cover both sets exactly.</summary>
    public IReadOnlyDictionary<Guid, byte> TemplateModelIndices { get; init; } = new Dictionary<Guid, byte>();
    /// <summary>Explicit skeletons for static actors with no bound motion. Animated actors already carry their PMX model.</summary>
    public IReadOnlyDictionary<Guid, PmxModel> StaticModels { get; init; } = new Dictionary<Guid, PmxModel>();
    public int AnsiCodePage { get; init; } = 932;
    public int OutputWidth { get; init; } = 1920;
    public int OutputHeight { get; init; } = 1080;
    public float PreferredFps { get; init; } = 30;
    /// <summary>Replace a retained template's output dimensions/FPS with these explicit values.
    /// SceneProjectSession uses this to export its current typed output settings.</summary>
    public bool OverrideTemplateOutput { get; init; }
    /// <summary>Explicit native background switch. Null preserves a retained template, or uses PMM's white default.
    /// SceneProjectSession supplies its current opaque black/white background independently of output overrides.</summary>
    public bool? BlackBackgroundEnabled { get; init; }
}

/// <summary>Maps an immutable session save snapshot into an independently validated PMM 0002 candidate.
/// Does not bake IK/physics world matrices into authored local animation, open model files, or modify the session/template.
/// Unsupported state is rejected rather than silently omitted.</summary>
public static class PmmSessionExporter
{
    public static PmmDocument Export(SceneAnimationSession session, PmmSessionExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session); options ??= new();
        if (options.TemplateModelIndices is null || options.StaticModels is null) throw new ArgumentException("Export model mappings cannot be null.", nameof(options));
        var state = session.CaptureSaveSnapshot();
        if (state.PmmControls is { } controls)
        {
            if (options.Template is not null && !PmmWriter.ToBytes(options.Template).SequenceEqual(PmmWriter.ToBytes(controls.ExportDocument())))
                throw new InvalidDataException("An explicit PMM template conflicts with the session's retained control provenance.");
            if (options.TemplateModelIndices.Count != 0 && (options.TemplateModelIndices.Count != controls.ModelIndices.Count
                || options.TemplateModelIndices.Any(p => !controls.ModelIndices.TryGetValue(p.Key, out var id) || id != p.Value)))
                throw new InvalidDataException("Explicit PMM model mappings conflict with retained stable actor identities.");
            options = new() { Template = controls.ExportDocument(), TemplateModelIndices = controls.ModelIndices,
                StaticModels = options.StaticModels, AnsiCodePage = options.AnsiCodePage, OutputWidth = options.OutputWidth,
                OutputHeight = options.OutputHeight, PreferredFps = options.PreferredFps, OverrideTemplateOutput = options.OverrideTemplateOutput,
                BlackBackgroundEnabled = options.BlackBackgroundEnabled };
        }
        if (state.CurrentFrame != Math.Floor(state.CurrentFrame) || state.CurrentFrame > int.MaxValue)
            throw new NotSupportedException("PMM export requires an integer current frame within Int32 range; seek before exporting.");
        if (state.Lighting.Lights.Count != 0)
            throw new NotSupportedException("PMM cannot represent native point/spot/directional light objects; remove them or save the modern project.");
        if (state.Objects.Count > 255) throw new InvalidDataException("PMM supports at most 255 model records.");
        if (options.TemplateModelIndices is null || options.StaticModels is null) throw new ArgumentException("Export model mappings cannot be null.", nameof(options));
        var actors = state.Objects.Select(o => o.Id).ToHashSet();
        if (options.StaticModels.Keys.Any(id => !actors.Contains(id))) throw new InvalidDataException("Static model metadata refers to an unknown actor.");
        var motionById = state.Motions.ToDictionary(m => m.ObjectId);
        var modelById = state.Objects.ToDictionary(o => o.Id, o => motionById.TryGetValue(o.Id, out var motion) ? motion.Model
            : options.StaticModels.TryGetValue(o.Id, out var model) ? model : throw new InvalidDataException($"Actor {o.Id} needs explicit StaticModels PMX metadata before PMM export."));
        state.PmmControls?.ValidateActors(modelById);
        foreach (var item in state.Objects)
        {
            var model = modelById[item.Id]; ValidateNames(model);
            var snapshot = item.Source.Surface.Snapshot;
            if (model.Vertices.Count != snapshot.Vertices.Length || model.Faces.Count * 3L != snapshot.Indices.Length || model.Materials.Count != snapshot.Materials.Count
                || model.Vertices.Where((v, i) => v.Position != snapshot.Vertices[i].Position).Any()
                || model.Faces.Where((f, i) => f.A != snapshot.Indices[3 * i] || f.B != snapshot.Indices[3 * i + 1] || f.C != snapshot.Indices[3 * i + 2]).Any())
                throw new InvalidDataException($"Actor {item.Id} PMX metadata must match its static source topology and rest vertices.");
            if (options.StaticModels.TryGetValue(item.Id, out var supplied) && motionById.ContainsKey(item.Id) && !ReferenceEquals(supplied, model))
                throw new InvalidDataException("StaticModels cannot override an animated actor's explicitly bound PMX model.");
        }
        var ids = ModelIds(state.Objects, options);
        var placementParents = state.PmmControls is null ? state.OutsideParents
            : state.OutsideParents.Concat(state.PmmControls.BoundaryFrames.SelectMany(frame => state.PmmControls.Sample(frame).OutsideParents)).ToArray();
        foreach (var item in state.Objects) ValidatePlacement(item, modelById[item.Id], placementParents);
        foreach (var binding in state.OutsideParents)
            if (binding.Offset != Matrix4x4.Identity) throw new NotSupportedException("PMM outside-parent records cannot represent a separate offset matrix.");
        int current = (int)state.CurrentFrame;
        var original = options.Template;
        if (original is not null)
        {
            if (original.Version != 2) throw new NotSupportedException("Session export currently writes PMM 0002, not PMM 0001.");
            if (original.Settings.PhysicsSimulationMode == 0 && state.Motions.Any(m => m.PhysicsEnabled && m.Model.RigidBodies.Count != 0))
                throw new NotSupportedException("The template disables all physics but the session has an active physical actor; change the template setting or export a new project.");
            _ = PmmWriter.ToBytes(original);
        }
        var candidate = original is null ? NewDocument(options, current) : CopyDocument(original, original.Models, current);
        // Preserve template list order and stored IDs so unedited camera/accessory references retain their meaning.
        var ordered = original is null ? state.Objects.ToArray()
            : original.Models.Select(source => state.Objects.Single(o => ids[o.Id] == source.Index)).ToArray();
        var templateById = original is null ? null : state.Objects.ToDictionary(o => o.Id, o => original.Models.Single(m => m.Index == ids[o.Id]));
        var drawOrders = state.Objects.Select((item, i) => (item.Id, Order: (byte)i)).ToDictionary(p => p.Id, p => p.Order);
        var models = ordered.Select(item => InitialModel(item, modelById[item.Id], ids[item.Id], drawOrders[item.Id],
            templateById?.GetValueOrDefault(item.Id), state.OutsideParents, ids, modelById, templateById,
            motionById.TryGetValue(item.Id, out var motion) && motion.PhysicsEnabled, state.PmmControls is not null, current)).ToArray();
        candidate = CopyDocument(candidate, models, current);
        _ = PmmWriter.ToBytes(candidate);
        for (int i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i]; var model = modelById[item.Id]; motionById.TryGetValue(item.Id, out var binding);
            var motion = CompleteMotion(model, binding, item.Position);
            candidate = PmmEditedMotionExporter.ReplaceModelMotion(candidate, i, model, motion);
        }
        if (state.SceneMotion is { } scene)
            candidate = PmmSceneMotionExporter.ReplaceSceneMotion(candidate, new VmdMotion
            { CameraKeyframes = scene.Motion.CameraKeyframes, LightKeyframes = scene.Motion.LightKeyframes, SelfShadowKeyframes = scene.Motion.SelfShadowKeyframes },
                preserveCameraParents: state.PmmControls is not null);
        if (options.OverrideTemplateOutput)
        {
            if (options.OutputWidth <= 0 || options.OutputHeight <= 0 || !float.IsFinite(options.PreferredFps) || options.PreferredFps <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Positive dimensions and finite positive FPS are required.");
        }
        if (options.OverrideTemplateOutput || options.BlackBackgroundEnabled.HasValue)
            candidate = CopyDocument(candidate, candidate.Models, current, options.OverrideTemplateOutput ? options : null, options.BlackBackgroundEnabled);
        _ = PmmWriter.ToBytes(candidate);
        return candidate;
    }

    /// <summary>Validates completely, then publishes a same-directory temporary file using the existing project-store pattern.</summary>
    public static void Save(string path, SceneAnimationSession session, PmmSessionExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(path);
        if (!Path.GetExtension(destination).Equals(".pmm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PMM export destination must have a .pmm extension; model and motion assets cannot be overwritten.", nameof(path));
        byte[] bytes = PmmWriter.ToBytes(Export(session, options)); cancellationToken.ThrowIfCancellationRequested();
        string parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (int offset = 0; offset < bytes.Length; offset += 64 * 1024)
                { cancellationToken.ThrowIfCancellationRequested(); output.Write(bytes.AsSpan(offset, Math.Min(64 * 1024, bytes.Length - offset))); }
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static Dictionary<Guid, byte> ModelIds(IReadOnlyList<EditableSceneObject> objects, PmmSessionExportOptions options)
    {
        if (options.Template is null)
        {
            if (options.TemplateModelIndices.Count != 0) throw new InvalidDataException("Template model mappings require a template.");
            return objects.Select((item, i) => (item.Id, Index: (byte)i)).ToDictionary(p => p.Id, p => p.Index);
        }
        var source = options.Template.Models.Select(m => m.Index).ToHashSet();
        if (options.TemplateModelIndices.Count != objects.Count || source.Count != objects.Count
            || options.TemplateModelIndices.Values.Distinct().Count() != objects.Count
            || objects.Any(o => !options.TemplateModelIndices.TryGetValue(o.Id, out byte id) || !source.Contains(id)))
            throw new InvalidDataException("Explicit TemplateModelIndices must map every session actor uniquely to every stored template model Index.");
        return options.TemplateModelIndices.ToDictionary(p => p.Key, p => p.Value);
    }
    static void ValidateNames(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Bones.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != model.Bones.Count
            || model.Morphs.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != model.Morphs.Count)
            throw new InvalidDataException("PMM session export requires unambiguous unique bone and morph names.");
    }
    static void ValidatePlacement(EditableSceneObject item, PmxModel model, IReadOnlyList<SceneOutsideParentBinding> parents)
    {
        if (item.Source.Surface.Snapshot.World != Matrix4x4.Identity)
            throw new NotSupportedException("The source asset has an additional world transform which PMM model records cannot represent.");
        var rotation = Matrix4x4.CreateFromQuaternion(item.Rotation);
        if (item.Scale != 1 || rotation != Matrix4x4.Identity)
            throw new NotSupportedException($"Actor {item.Id} has scale or rotation which PMM model records cannot represent without changing authored animation.");
        if (item.Position == Vector3.Zero) return;
        if (model.RigidBodies.Count != 0 || model.Bones.Any(b => (b.Flags & (PmxBoneFlags.AppendTranslate | PmxBoneFlags.AppendRotate)) != 0)
            || parents.Any(p => p.ObjectId == item.Id || p.ParentObjectId == item.Id))
            throw new NotSupportedException("Constant placement translation requires a non-physical skeleton without append or outside-parent dependencies.");
        if (model.Bones.Count == 0 || model.Bones.All(b => b.ParentIndex >= 0))
            throw new NotSupportedException("Placement translation requires parentless bones.");
    }

    static PmmModel InitialModel(EditableSceneObject item, PmxModel model, byte id, byte order, PmmModel? source,
        IReadOnlyList<SceneOutsideParentBinding> allParents, IReadOnlyDictionary<Guid, byte> ids, IReadOnlyDictionary<Guid, PmxModel> models,
        IReadOnlyDictionary<Guid, PmmModel>? templates, bool physicsEnabled, bool preserveControls, int currentFrame)
    {
        string extension = Path.GetExtension(item.SourcePath);
        if (!extension.Equals(".pmx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".pmd", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("PMM session actors must refer to a PMX or PMD model asset.");
        if (!physicsEnabled && model.RigidBodies.Any(b => b.Operation != PmxRigidBodyOperation.Static && b.BoneIndex < 0))
            throw new NotSupportedException("PMM per-bone switches cannot disable an unbound dynamic rigid body for a single actor.");
        // The existing serializer validates fixed 256-byte paths and variable 255-byte names using strict encoding.
        var boneNames = OrderedNames(source?.BoneNames, model.Bones.Select(b => b.Name));
        var morphNames = OrderedNames(source?.MorphNames, model.Morphs.Select(m => m.Name));
        var boneIndices = boneNames.Select((n, i) => (Name: ResolveName(n, model.Bones.Select(b => b.Name)), Index: i)).ToDictionary(p => p.Name, p => p.Index, StringComparer.Ordinal);
        var ik = model.Bones.Where(b => b.Flags.HasFlag(PmxBoneFlags.Ik)).Select(b => boneIndices[b.Name]).ToArray();
        var bindings = allParents.Where(p => p.ObjectId == item.Id).OrderBy(p => p.BoneIndex).ToArray();
        var subjects = bindings.Select(p => p.BoneIndex < 0 ? -1 : boneIndices[model.Bones[p.BoneIndex].Name]).ToArray();
        var parents = bindings.Select(p =>
        {
            var targetModel = models[p.ParentObjectId];
            string targetBone = targetModel.Bones[p.ParentBoneIndex].Name;
            var targetNames = OrderedNames(templates?.GetValueOrDefault(p.ParentObjectId)?.BoneNames, targetModel.Bones.Select(b => b.Name));
            int targetIndex = Array.FindIndex(targetNames, n => ResolveName(n, targetModel.Bones.Select(b => b.Name)) == targetBone);
            return new PmmOutsideParent(0, ids[p.ParentObjectId], targetIndex);
        }).ToArray();
        if (source is not null && !preserveControls)
        {
            var oldParents = source.InitialModelKeyframe.OutsideParents;
            if (source.ModelKeyframes.Any(k => !SameParents(oldParents, k.OutsideParents)))
                throw new NotSupportedException("A session static outside-parent graph cannot preserve a template's animated outside-parent transitions.");
            // Per-bone physics switches have no independent modern session track yet. Retain source metadata;
            // the existing model exporter rejects edited animation which would erase a switch frame.
        }
        PmmModelKeyframe? retainedInitial = null; IReadOnlyList<PmmModelKeyframe> retainedKeys = [];
        IReadOnlyList<PmmOutsideParentState>? retainedStates = null;
        if (preserveControls && source is not null)
        {
            if (!source.ConstraintBoneIndices.ToHashSet().SetEquals(ik))
                throw new InvalidDataException("The PMM control template and current PMX disagree on the native IK constraint bones.");
            ik = source.ConstraintBoneIndices.ToArray();
            var subjectList = source.OutsideParentSubjectBoneIndices.ToList();
            foreach (int subject in subjects) if (!subjectList.Contains(subject)) subjectList.Add(subject);
            PmmModelKeyframe Merge(PmmModelKeyframe key)
            {
                var mapped = subjectList.Select(subject =>
                {
                    int originalSlot = source.OutsideParentSubjectBoneIndices.ToList().IndexOf(subject);
                    var value = originalSlot < 0 ? new PmmOutsideParent(0, -1, -1) : key.OutsideParents[originalSlot];
                    int staticSlot = Array.IndexOf(subjects, subject);
                    if (staticSlot >= 0)
                    {
                        if (value.ModelIndex >= 0) throw new NotSupportedException("Static and dynamic PMM parent tracks cannot author the same active subject.");
                        value = parents[staticSlot];
                    }
                    return value;
                }).ToArray();
                return new() { RecordIndex = key.RecordIndex, ObjectIndex = key.ObjectIndex, Frame = key.Frame,
                    PreviousIndex = key.PreviousIndex, NextIndex = key.NextIndex, Selected = key.Selected,
                    Visible = key.Visible, ConstraintEnabled = key.ConstraintEnabled, OutsideParents = mapped };
            }
            retainedInitial = Merge(source.InitialModelKeyframe); retainedKeys = source.ModelKeyframes.Select(Merge).ToArray();
            var timeline = new[] { retainedInitial! }.Concat(retainedKeys).OrderBy(k => k.Frame).ToArray();
            parents = MotionEditing.PmmTrackEditorRecords.At(timeline, currentFrame).OutsideParents.ToArray();
            subjects = subjectList.ToArray();
            retainedStates = parents.Select((parent, slot) =>
            {
                bool Same(PmmOutsideParent other) => other.ModelIndex == parent.ModelIndex && other.BoneIndex == parent.BoneIndex;
                int begin = timeline.Where(k => k.Frame <= currentFrame).Reverse().TakeWhile(k => Same(k.OutsideParents[slot])).LastOrDefault()?.Frame ?? 0;
                int end = timeline.FirstOrDefault(k => k.Frame > currentFrame && !Same(k.OutsideParents[slot]))?.Frame - 1 ?? int.MaxValue;
                return new PmmOutsideParentState(0, begin, end, parent);
            }).ToArray();
        }
        var curves = LinearBoneCurve();
        PmmBoneKeyframe Physics(PmmBoneKeyframe k) => physicsEnabled ? k : new()
        { RecordIndex = k.RecordIndex, ObjectIndex = k.ObjectIndex, Frame = k.Frame, PreviousIndex = k.PreviousIndex, NextIndex = k.NextIndex,
            Selected = k.Selected, Translation = k.Translation, Rotation = k.Rotation, Interpolation = k.Interpolation, PhysicsDisabled = true };
        var initialBones = boneNames.Select((_, i) => Physics(source is not null && i < source.InitialBoneKeyframes.Count ? source.InitialBoneKeyframes[i]
            : new PmmBoneKeyframe { RecordIndex = i, ObjectIndex = i, Rotation = Quaternion.Identity, Interpolation = curves })).ToArray();
        var initialMorphs = morphNames.Select((_, i) => new PmmMorphKeyframe { RecordIndex = i, ObjectIndex = i }).ToArray();
        return new()
        {
            Index = id, Name = source is not null && (source.Name.Value == item.Name
                || PmmNameResolver.DecodeCandidates(source.Name).Any(c => c.Value == item.Name)) ? source.Name : S(item.Name),
            EnglishName = source?.EnglishName ?? S(model.EnglishName), Path = ReusePath(source?.Path, item.SourcePath),
            FixedTrackCount = source?.FixedTrackCount ?? 0, BoneNames = boneNames, MorphNames = morphNames, ConstraintBoneIndices = ik,
            OutsideParentSubjectBoneIndices = subjects, DrawOrderIndex = order, Visible = true, SelectedBoneIndex = source?.SelectedBoneIndex ?? -1,
            SelectedMorphIndices = source?.SelectedMorphIndices ?? [-1, -1, -1, -1], ExpansionStates = source?.ExpansionStates ?? [], VerticalScroll = source?.VerticalScroll ?? 0,
            InitialBoneKeyframes = initialBones, BoneKeyframes = source?.BoneKeyframes.Select(Physics).ToArray() ?? [], InitialMorphKeyframes = initialMorphs,
            InitialModelKeyframe = retainedInitial ?? new() { Visible = true, ConstraintEnabled = ik.Select(_ => true).ToArray(), OutsideParents = parents },
            ModelKeyframes = retainedKeys,
            BoneStates = boneNames.Select((_, i) => new PmmBoneState(0, Vector3.Zero, Quaternion.Identity, false, source is not null && i < source.BoneStates.Count && source.BoneStates[i].PhysicsDisabled, 0)).ToArray(),
            MorphStates = morphNames.Select(_ => new PmmMorphState(0, 0)).ToArray(), ConstraintStates = ik.Select(_ => new PmmConstraintState(0, true)).ToArray(),
            OutsideParentStates = retainedStates ?? parents.Select(p => new PmmOutsideParentState(0, 0, int.MaxValue, p)).ToArray(),
            BlendEnabled = source?.BlendEnabled ?? false, EdgeWidth = source?.EdgeWidth ?? 1, SelfShadowEnabled = source?.SelfShadowEnabled ?? true,
            TransformOrderIndex = source?.TransformOrderIndex ?? order,
        };
    }

    static VmdMotion CompleteMotion(PmxModel model, SceneActorMotion? binding, Vector3 placement)
    {
        var motion = binding?.Motion ?? new VmdMotion();
        // Character tracks have a separate owner; retained scene records in an actor VMD must never be silently exported as project edits.
        if (motion.CameraKeyframes.Count != 0 || motion.LightKeyframes.Count != 0 || motion.SelfShadowKeyframes.Count != 0)
            throw new NotSupportedException("Actor motion contains scene sections; bind its camera/light/shadow as the session scene motion explicitly.");
        var bones = motion.BoneKeyframes.ToList();
        foreach (var bone in model.Bones) if (!bones.Any(k => k.BoneName == bone.Name)) bones.Add(new() { BoneName = bone.Name });
        if (placement != Vector3.Zero)
        {
            var roots = model.Bones.Where(b => b.ParentIndex < 0).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
            // Seed rest+offset at zero when the first authored root key occurs later, retaining the original destination curves.
            foreach (string root in roots) if (!bones.Any(k => k.BoneName == root && k.Frame == 0)) bones.Add(new() { BoneName = root });
            bones = bones.Select(k => roots.Contains(k.BoneName) ? new VmdBoneKeyframe { BoneName = k.BoneName, OriginalName = k.OriginalName,
                Frame = k.Frame, Translation = k.Translation + placement, Rotation = k.Rotation, Interpolation = k.Interpolation } : k).ToList();
        }
        var morphs = motion.MorphKeyframes.ToList();
        // Retain zero-valued keys at their authored times: disabled morphs still extend the actor clock,
        // which determines when its physical pose freezes in a multi-actor project.
        if (binding?.MorphsEnabled == false) morphs = morphs.Select(k => new VmdMorphKeyframe
            { MorphName = k.MorphName, OriginalName = k.OriginalName, Frame = k.Frame, Weight = 0 }).ToList();
        foreach (var morph in model.Morphs) if (!morphs.Any(k => k.MorphName == morph.Name)) morphs.Add(new() { MorphName = morph.Name });
        var ik = model.Bones.Where(b => b.Flags.HasFlag(PmxBoneFlags.Ik)).Select(b => b.Name).ToArray();
        var switches = motion.IkKeyframes.ToList();
        // Explicit frame zero clears stale template visibility/IK defaults when no imported switches were authored.
        if (!switches.Any(k => k.Frame == 0)) switches.Insert(0, new()
        {
            Visible = switches.GroupBy(k => k.Frame).OrderBy(g => g.Key).FirstOrDefault()?.Last().Visible ?? true,
            Switches = ik.Select(name => new VmdIkSwitch(name, true)).ToArray()
        });
        if (binding?.IkEnabled == false) switches = switches.Select(k => new VmdIkKeyframe { Frame = k.Frame, Visible = k.Visible,
            Switches = ik.Select(name => new VmdIkSwitch(name, false)).ToArray() }).ToList();
        return new() { ModelName = motion.ModelName, BoneKeyframes = bones, MorphKeyframes = morphs, IkKeyframes = switches };
    }

    static PmmString[] OrderedNames(IReadOnlyList<PmmString>? source, IEnumerable<string> modelNames)
    {
        var names = modelNames.ToArray();
        if (source is null) return names.Select(S).ToArray();
        var existing = source.Select(n => ResolveName(n, names)).ToArray();
        if (existing.Distinct(StringComparer.Ordinal).Count() != source.Count || names.Length != existing.Length)
            throw new InvalidDataException("Template model names require the same unambiguous skeleton/morph topology; changed topology needs a new PMM document.");
        return source.ToArray();
    }
    static string ResolveName(PmmString name, IEnumerable<string> names)
    {
        var known = names.ToHashSet(StringComparer.Ordinal);
        var matches = PmmAssetPathResolver.DecodeCandidates(name).Where(known.Contains).Distinct(StringComparer.Ordinal).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Template name '{name.Value}' has no unique exact model binding.");
    }
    static bool SameParents(IReadOnlyList<PmmOutsideParent> a, IReadOnlyList<PmmOutsideParent> b)
        => a.Count == b.Count && a.Zip(b).All(p => p.First.ModelIndex == p.Second.ModelIndex && p.First.BoneIndex == p.Second.BoneIndex);
    static PmmString ReusePath(PmmString? source, string path)
    {
        if (source is null) return S(path);
        if (source.Value == path) return source;
        // A resolved absolute source can use a different separator spelling or the alternate explicit
        // ANSI decoding. Keep the original fixed buffer only when it denotes this exact source path.
        // Relative references need an explicit source base; never infer one from process CWD here.
        string Normalize(string value) => value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string target = Normalize(path);
        if (!Path.IsPathFullyQualified(target)) return S(path);
        target = Path.GetFullPath(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string candidate in PmmAssetPathResolver.DecodeCandidates(source))
        {
            try
            {
                string normalized = Normalize(candidate);
                if (Path.IsPathFullyQualified(normalized) && string.Equals(Path.GetFullPath(normalized), target, comparison)) return source;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return S(path);
    }
    static PmmString S(string value) => new(0, value, []);
    static byte[] LinearBoneCurve() => Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(c => c).ToArray();
    static PmmDocument NewDocument(PmmSessionExportOptions options, int frame)
    {
        if (options.AnsiCodePage is not (932 or 936) || options.OutputWidth <= 0 || options.OutputHeight <= 0
            || !float.IsFinite(options.PreferredFps) || options.PreferredFps <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        var light = new Vector3(.6f); var direction = new Vector3(-.5f, -1, -.5f);
        var cameraCurve = Enumerable.Repeat(new byte[] { 20, 107, 20, 107 }, 6).SelectMany(c => c).ToArray();
        return new()
        {
            AnsiCodePage = options.AnsiCodePage, OutputWidth = options.OutputWidth, OutputHeight = options.OutputHeight, TimelineWidth = 250,
            CameraFov = 30, PanelStates = new byte[7], SelectedModelIndex = 0, CurrentFrame = frame,
            Camera = new() { InitialKeyframe = new() { Distance = -45, LookAt = new(0, 10, 0), Fov = 30, Perspective = true, Interpolation = cameraCurve,
                ParentModelIndex = -1, ParentBoneIndex = -1 }, LookAt = new(0, 10, 0), Position = new(0, 0, -45), Perspective = true },
            Light = new() { InitialKeyframe = new() { Color = light, Direction = direction }, Color = light, Direction = direction },
            Gravity = new() { Acceleration = 9.8f, Direction = -Vector3.UnitY, InitialKeyframe = new() { Acceleration = 9.8f, Direction = -Vector3.UnitY } },
            SelfShadow = new() { Enabled = true, Distance = .011f, InitialKeyframe = new() { Mode = 1, Distance = .011f } },
            Settings = new() { PreferredFps = options.PreferredFps, VideoScale = 1, ImageScale = 1, GroundShadowShown = true, PhysicsGroundEnabled = true,
                PhysicsSimulationMode = 2, InformationShown = true, GridAndAxisShown = true, GroundShadowBrightness = .5f,
                EdgeColorRgb = [0, 0, 0], BlackBackgroundEnabled = options.BlackBackgroundEnabled ?? false,
                CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1, UnknownMatrix = new byte[64], CurrentFrameText = frame, SelectionTrailerFlag = 0 },
        };
    }
    static PmmDocument CopyDocument(PmmDocument source, IReadOnlyList<PmmModel> models, int frame,
        PmmSessionExportOptions? output = null, bool? blackBackgroundEnabled = null) => new()
    {
        Version = source.Version, AnsiCodePage = source.AnsiCodePage, HeaderBytes = source.HeaderBytes,
        OutputWidth = output?.OutputWidth ?? source.OutputWidth, OutputHeight = output?.OutputHeight ?? source.OutputHeight, TimelineWidth = source.TimelineWidth, CameraFov = source.CameraFov,
        PanelStates = source.PanelStates, SelectedModelIndex = source.SelectedModelIndex, Models = models, Camera = source.Camera, Light = source.Light,
        SelectedAccessoryIndex = source.SelectedAccessoryIndex, AccessoryHorizontalScroll = source.AccessoryHorizontalScroll,
        AccessoryNames = source.AccessoryNames, Accessories = source.Accessories, CurrentFrame = frame,
        Settings = output is null && blackBackgroundEnabled is null ? source.Settings
            : WithOutputSettings(source.Settings, output?.PreferredFps, blackBackgroundEnabled),
        Gravity = source.Gravity, SelfShadow = source.SelfShadow, ParsedLength = source.ParsedLength, BytesConsumed = source.BytesConsumed,
        UnknownTail = source.UnknownTail, Diagnostics = source.Diagnostics,
    };

    static PmmSettings WithOutputSettings(PmmSettings s, float? fps, bool? blackBackgroundEnabled) => new()
    {
        Offset = s.Offset, HorizontalScroll = s.HorizontalScroll, HorizontalScrollThumb = s.HorizontalScrollThumb,
        EditingMode = s.EditingMode, CameraLookMode = s.CameraLookMode, LoopEnabled = s.LoopEnabled,
        BeginFrameEnabled = s.BeginFrameEnabled, EndFrameEnabled = s.EndFrameEnabled, BeginFrame = s.BeginFrame, EndFrame = s.EndFrame,
        AudioEnabled = s.AudioEnabled, AudioPath = s.AudioPath, VideoOffsetX = s.VideoOffsetX, VideoOffsetY = s.VideoOffsetY,
        VideoScale = s.VideoScale, VideoPath = s.VideoPath, VideoEnabledRaw = s.VideoEnabledRaw,
        ImageOffsetX = s.ImageOffsetX, ImageOffsetY = s.ImageOffsetY, ImageScale = s.ImageScale, ImagePath = s.ImagePath, ImageEnabled = s.ImageEnabled,
        InformationShown = s.InformationShown, GridAndAxisShown = s.GridAndAxisShown, GroundShadowShown = s.GroundShadowShown,
        PreferredFps = fps ?? s.PreferredFps, ScreenCaptureMode = s.ScreenCaptureMode, AccessoryIndexAfterModels = s.AccessoryIndexAfterModels,
        GroundShadowBrightness = s.GroundShadowBrightness, TranslucentGroundShadowEnabled = s.TranslucentGroundShadowEnabled,
        PhysicsSimulationMode = s.PhysicsSimulationMode, EdgeColorRgb = s.EdgeColorRgb, BlackBackgroundEnabled = blackBackgroundEnabled ?? s.BlackBackgroundEnabled,
        CameraLookAtModelIndex = s.CameraLookAtModelIndex, CameraLookAtBoneIndex = s.CameraLookAtBoneIndex, UnknownMatrix = s.UnknownMatrix,
        FollowingLookAtRaw = s.FollowingLookAtRaw, UnknownBooleanRaw = s.UnknownBooleanRaw,
        PhysicsGroundEnabled = s.PhysicsGroundEnabled, CurrentFrameText = s.CurrentFrameText,
        SelectionTrailerFlag = s.SelectionTrailerFlag, ModelSelections = s.ModelSelections,
    };
}
