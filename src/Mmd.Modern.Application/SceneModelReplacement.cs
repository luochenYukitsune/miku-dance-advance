using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public enum SceneModelDataKind { Bone, Morph, Ik, OutsideParent, PmmControl }
public sealed record SceneModelNameMapping(SceneModelDataKind Kind, string Name, int SourceIndex, int TargetIndex);
public sealed record SceneModelDataNotice(SceneModelDataKind Kind, string Name, bool CanRestore, string Detail);
/// <summary>A static parent record with original native names. Null subject means the whole object.</summary>
public sealed record SceneDetachedOutsideParent(Guid ObjectId, string? BoneName, Guid ParentObjectId,
    string ParentBoneName, [property: JsonConverter(typeof(SceneDetachedMatrixConverter))] Matrix4x4 Offset);

public sealed class SceneDetachedMatrixConverter : JsonConverter<Matrix4x4>
{
    public override Matrix4x4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a detached parent matrix.");
        var values = new float[16];
        for (int i = 0; i < values.Length; i++)
        { if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out values[i]) || !float.IsFinite(values[i])) throw new JsonException("Invalid detached parent matrix."); }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Invalid detached parent matrix size.");
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
    }
    public override void Write(Utf8JsonWriter writer, Matrix4x4 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (float component in new[] { value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44 }) writer.WriteNumberValue(component);
        writer.WriteEndArray();
    }
}

/// <summary>Authored records stay in the original VMD; these explicit masks prevent silent rebinding.</summary>
public sealed class SceneMotionDetachment
{
    public IReadOnlyList<string> BoneNames { get; }
    public IReadOnlyList<string> MorphNames { get; }
    public IReadOnlyList<string> IkNames { get; }
    public IReadOnlyList<SceneDetachedOutsideParent> OutsideParents { get; }
    [JsonConstructor]
    public SceneMotionDetachment(IReadOnlyList<string> boneNames, IReadOnlyList<string> morphNames,
        IReadOnlyList<string> ikNames, IReadOnlyList<SceneDetachedOutsideParent> outsideParents)
    {
        BoneNames = Names(boneNames); MorphNames = Names(morphNames); IkNames = Names(ikNames);
        ArgumentNullException.ThrowIfNull(outsideParents);
        if (outsideParents.Count > 100_000 || outsideParents.Any(p => p is null || p.ObjectId == Guid.Empty || p.ParentObjectId == Guid.Empty
            || p.ObjectId == p.ParentObjectId || string.IsNullOrEmpty(p.ParentBoneName) || p.ParentBoneName.Length > 4096
            || p.BoneName?.Length > 4096 || !ValidOffset(p.Offset)))
            throw new InvalidDataException("Invalid detached outside-parent metadata.");
        OutsideParents = Array.AsReadOnly(outsideParents.ToArray());
    }
    private static bool ValidOffset(Matrix4x4 value) => value.M14 == 0 && value.M24 == 0 && value.M34 == 0 && value.M44 == 1
        && new[] { value.M11, value.M12, value.M13, value.M21, value.M22, value.M23, value.M31, value.M32, value.M33,
            value.M41, value.M42, value.M43 }.All(float.IsFinite) && Matrix4x4.Invert(value, out _);
    private static IReadOnlyList<string> Names(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count > 100_000 || names.Any(n => n is null || n.Length > 4096) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new InvalidDataException("Invalid detached native-name table.");
        return Array.AsReadOnly(names.ToArray());
    }
    public bool IsEmpty => BoneNames.Count == 0 && MorphNames.Count == 0 && IkNames.Count == 0 && OutsideParents.Count == 0;
    public VmdMotion Filter(VmdMotion motion) => new()
    {
        ModelName = motion.ModelName, OriginalModelName = motion.OriginalModelName,
        BoneKeyframes = motion.BoneKeyframes.Where(k => !BoneNames.Contains(k.BoneName, StringComparer.Ordinal)).ToArray(),
        MorphKeyframes = motion.MorphKeyframes.Where(k => !MorphNames.Contains(k.MorphName, StringComparer.Ordinal)).ToArray(),
        IkKeyframes = motion.IkKeyframes.Select(k => new VmdIkKeyframe { Frame = k.Frame, Visible = k.Visible,
            Switches = k.Switches.Where(s => !IkNames.Contains(s.BoneName, StringComparer.Ordinal)).ToArray() }).ToArray(),
        CameraKeyframes = motion.CameraKeyframes, LightKeyframes = motion.LightKeyframes,
        SelfShadowKeyframes = motion.SelfShadowKeyframes, Diagnostics = motion.Diagnostics,
    };
}

/// <summary>Detached preview. Publication requires the same owner/content revision and an explicit Apply call.</summary>
public sealed class SceneModelReplacementPlan
{
    public Guid ObjectId { get; }
    public string SourcePath { get; }
    public PmxModel PreviousModel { get; }
    public PmxModel ReplacementModel { get; }
    public IReadOnlyList<SceneModelNameMapping> Mappings { get; }
    public IReadOnlyList<SceneModelDataNotice> Notices { get; }
    public int MatchedBoneCount => Mappings.Count(m => m.Kind == SceneModelDataKind.Bone && m.TargetIndex >= 0);
    public int MatchedMorphCount => Mappings.Count(m => m.Kind == SceneModelDataKind.Morph && m.TargetIndex >= 0);
    public int RecoverableCount => Notices.Count(n => n.CanRestore);
    internal object Revision { get; }
    internal SceneProjectSession Owner { get; }
    internal SceneAnimationModelAsset Asset { get; }
    internal SceneModelReplacementPlan(SceneProjectSession owner, object revision, Guid objectId, string path,
        PmxModel previous, SceneAnimationModelAsset asset, IEnumerable<SceneModelNameMapping> mappings, IEnumerable<SceneModelDataNotice> notices)
    {
        Owner = owner; Revision = revision; ObjectId = objectId; SourcePath = path; PreviousModel = previous;
        Asset = asset; ReplacementModel = asset.Model; Mappings = Array.AsReadOnly(mappings.ToArray()); Notices = Array.AsReadOnly(notices.ToArray());
    }
}

public sealed partial class SceneCompositionEditor
{
    internal void ReplaceSource(Guid id, string path, PmxModel previousModel, SceneAnimationModelAsset asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(asset);
        int index = Index(id);
        if (asset.Source?.Outline is null || asset.Source.Surface.Snapshot.HasDynamicVertices)
            throw new ArgumentException("Replacement requires a static source frame with outline geometry.");
        var previous = objects[index];
        // Old documents did not retain name provenance. Only exact source names migrate
        // automatically; a distinct PMM instance name remains an authored alias.
        bool follows = previous.NameFollowsSource ?? (previous.Name == previousModel.Name
            || !string.IsNullOrWhiteSpace(previousModel.EnglishName) && previous.Name == previousModel.EnglishName
            || previous.Name == Path.GetFileNameWithoutExtension(previous.SourcePath));
        string name = follows
            ? string.IsNullOrWhiteSpace(asset.Model.Name) ? Path.GetFileNameWithoutExtension(path) : asset.Model.Name
            : previous.Name;
        Save(); objects[index] = previous with { SourcePath = path, Source = asset.Source,
            Name = name, NameFollowsSource = follows }; cached = null;
    }
}

public sealed partial class SceneProjectSession
{
    public SceneModelReplacementPlan PrepareModelReplacement(Guid objectId, string sourcePath, SceneAnimationModelAsset asset,
        CancellationToken cancellationToken = default)
    {
        RequireIdle(); cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(asset.Model); ArgumentNullException.ThrowIfNull(asset.Source);
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.GetExtension(sourcePath).Equals(".pmx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an explicit fully qualified PMX file.", nameof(sourcePath));
        var previous = content.Motions.SingleOrDefault(m => m.ObjectId == objectId)
            ?? throw new NotSupportedException("Replacement requires an explicitly bound PMX skeleton.");
        var mappings = previous.Model.Bones.Select((b, i) => new SceneModelNameMapping(SceneModelDataKind.Bone, b.Name, i,
                UniqueBone(previous.Model, b.Name) == i ? UniqueBone(asset.Model, b.Name) : -1))
            .Concat(previous.Model.Morphs.Select((m, i) => new SceneModelNameMapping(SceneModelDataKind.Morph, m.Name, i,
                UniqueMorph(previous.Model, m.Name) == i ? UniqueMorph(asset.Model, m.Name) : -1))).ToArray();
        var candidate = ToEdit(content); var notices = new List<SceneModelDataNotice>();
        ReplaceModel(candidate, objectId, Path.GetFullPath(sourcePath), asset, false, notices);
        cancellationToken.ThrowIfCancellationRequested();
        // Distinct native slots can have the same display name. Keep every retained claim
        // visible in the confirmation list instead of collapsing conflicts by their label.
        return new(this, content, objectId, Path.GetFullPath(sourcePath), previous.Model, asset, mappings, notices);
    }

    public void ApplyModelReplacement(SceneModelReplacementPlan plan, bool confirmRestoreOrphans = false, CancellationToken cancellationToken = default)
        => ApplyModelReplacement(plan, confirmRestoreOrphans, cancellationToken, null);

    /// <summary>Allows an owning adapter to prepare detached metadata before the same core transaction
    /// validates and publishes its candidate. A callback failure leaves live content and history intact.</summary>
    public void ApplyModelReplacement(SceneModelReplacementPlan plan, bool confirmRestoreOrphans,
        CancellationToken cancellationToken, Action<SceneProjectEdit>? prepareCandidate)
    {
        RequireIdle(); ArgumentNullException.ThrowIfNull(plan); cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(plan.Owner, this) || !ReferenceEquals(plan.Revision, content))
            throw new InvalidOperationException("The replacement preview is stale; prepare it again against the current project.");
        Edit(e =>
        {
            ReplaceModel(e, plan.ObjectId, plan.SourcePath, plan.Asset, confirmRestoreOrphans, []);
            prepareCandidate?.Invoke(e);
        }, cancellationToken);
    }

    private static void ReplaceModel(SceneProjectEdit edit, Guid id, string path, SceneAnimationModelAsset asset, bool restore,
        List<SceneModelDataNotice> notices)
    {
        var old = edit.Motions.Single(m => m.ObjectId == id);
        var previousModels = edit.Motions.ToDictionary(m => m.ObjectId, m => m.Model);
        var models = previousModels.ToDictionary(p => p.Key, p => p.Key == id ? asset.Model : p.Value);
        var detached = old.Detachment;
        string[] Mask(IEnumerable<string> trackNames, IReadOnlyList<string>? retained, SceneModelDataKind kind,
            Func<string, int> findSource, Func<string, int> findTarget)
        {
            var result = new List<string>();
            foreach (string name in trackNames.Concat(retained ?? []).Distinct(StringComparer.Ordinal))
            {
                bool wasDetached = retained?.Contains(name, StringComparer.Ordinal) == true;
                // A unique target alone cannot disambiguate a duplicated or missing source name.
                // Newly detached records require a later explicit restoration preview.
                bool matches = findTarget(name) >= 0 && (wasDetached || findSource(name) >= 0);
                if (wasDetached) notices.Add(new(kind, name, matches, matches ? "无主数据：可恢复，需确认" : "无主数据：当前PMX无唯一同名项"));
                else if (!matches) notices.Add(new(kind, name, false, "无主数据：保留原始记录，不绑定"));
                if (!matches || wasDetached && !restore) result.Add(name);
            }
            return result.ToArray();
        }
        var bones = Mask(old.Motion.BoneKeyframes.Select(k => k.BoneName), detached?.BoneNames, SceneModelDataKind.Bone,
            n => UniqueBone(old.Model, n), n => UniqueBone(asset.Model, n));
        var morphs = Mask(old.Motion.MorphKeyframes.Select(k => k.MorphName), detached?.MorphNames, SceneModelDataKind.Morph,
            n => UniqueMorph(old.Model, n), n => UniqueMorph(asset.Model, n));
        var ik = Mask(old.Motion.IkKeyframes.SelectMany(k => k.Switches).Select(s => s.BoneName), detached?.IkNames, SceneModelDataKind.Ik,
            n => { int i = UniqueBone(old.Model, n); return i >= 0 && old.Model.Bones[i].Flags.HasFlag(PmxBoneFlags.Ik) ? i : -1; },
            n => { int i = UniqueBone(asset.Model, n); return i >= 0 && asset.Model.Bones[i].Flags.HasFlag(PmxBoneFlags.Ik) ? i : -1; });
        var orphans = edit.Motions.SelectMany(m => (m.Detachment?.OutsideParents ?? [])
            .Select(parent => (Owner: m.ObjectId, Parent: parent))).DistinctBy(entry => entry.Parent).ToArray();
        var remaining = edit.Motions.ToDictionary(m => m.ObjectId, _ => new List<SceneDetachedOutsideParent>());
        var activeParents = edit.OutsideParents.Where(parent => parent.ObjectId == id || parent.ParentObjectId == id).ToArray();
        edit.OutsideParents.RemoveAll(parent => parent.ObjectId == id || parent.ParentObjectId == id);
        foreach (var parent in activeParents)
        {
            string? subject = parent.BoneIndex < 0 ? null : previousModels[parent.ObjectId].Bones[parent.BoneIndex].Name;
            string target = previousModels[parent.ParentObjectId].Bones[parent.ParentBoneIndex].Name;
            var named = new SceneDetachedOutsideParent(parent.ObjectId, subject, parent.ParentObjectId, target, parent.Offset);
            bool sourceUnique = (parent.ObjectId != id || subject is null || UniqueBone(old.Model, subject) == parent.BoneIndex)
                && (parent.ParentObjectId != id || UniqueBone(old.Model, target) == parent.ParentBoneIndex);
            int bone = parent.ObjectId == id && subject is not null ? UniqueBone(asset.Model, subject) : parent.BoneIndex;
            int parentBone = parent.ParentObjectId == id ? UniqueBone(asset.Model, target) : parent.ParentBoneIndex;
            var mapped = parent with { BoneIndex = bone, ParentBoneIndex = parentBone };
            if (sourceUnique && bone >= -1 && (subject is null || bone >= 0) && parentBone >= 0 && CanAddParent(mapped, edit.OutsideParents))
                edit.OutsideParents.Add(mapped);
            else
            {
                remaining[id].Add(named);
                notices.Add(new(SceneModelDataKind.OutsideParent, subject ?? "对象", false, "无主数据：外亲保留并暂停"));
            }
        }
        var reservedParents = edit.OutsideParents.ToList();
        foreach (var entry in orphans)
        {
            var orphan = entry.Parent;
            if (orphan.ObjectId != id && orphan.ParentObjectId != id) { remaining[entry.Owner].Add(orphan); continue; }
            bool matches = TryParent(orphan, models, out var mapped);
            bool available = matches && CanAddParent(mapped!, reservedParents);
            notices.Add(new(SceneModelDataKind.OutsideParent, orphan.BoneName ?? "对象", available,
                available ? "无主数据：外亲恢复需要确认" : "无主数据：名称不唯一、目标已有绑定或会产生依赖循环，继续保留"));
            if (available) reservedParents.Add(mapped!);
            if (available && restore) edit.OutsideParents.Add(mapped!); else remaining[entry.Owner].Add(orphan);
        }
        for (int i = 0; i < edit.Motions.Count; i++)
        {
            var item = edit.Motions[i]; var state = item.Detachment;
            // Keep the original owner of each detached graph record while either endpoint can discover it.
            var next = item.ObjectId == id ? new SceneMotionDetachment(bones, morphs, ik, remaining[item.ObjectId].Distinct().ToArray())
                : new SceneMotionDetachment(state?.BoneNames ?? [], state?.MorphNames ?? [], state?.IkNames ?? [], remaining[item.ObjectId]);
            edit.Motions[i] = item with { Model = item.ObjectId == id ? asset.Model : item.Model, Detachment = next.IsEmpty ? null : next };
        }
        if (edit.PmmControls is { } controls)
        {
            edit.PmmControls = controls.RemapModel(id, asset.Model, restore);
            notices.AddRange(edit.PmmControls.DetachedDataNoticesFor(id));
        }
        edit.Composition.ReplaceSource(id, path, old.Model, asset);
    }
    internal static int UniqueBone(PmxModel model, string name) => UniqueName(model.Bones.Select(b => b.Name), name);
    internal static int UniqueMorph(PmxModel model, string name) => UniqueName(model.Morphs.Select(m => m.Name), name);
    private static int UniqueName(IEnumerable<string> names, string name)
    {
        int found = -1, index = 0;
        foreach (string candidate in names) { if (candidate == name) { if (found >= 0) return -1; found = index; } index++; }
        return found;
    }
    private static bool TryParent(SceneDetachedOutsideParent named, IReadOnlyDictionary<Guid, PmxModel> models, out SceneOutsideParentBinding? binding)
    {
        binding = null;
        if (named.ObjectId == named.ParentObjectId) return false;
        if (!models.TryGetValue(named.ObjectId, out var subject) || !models.TryGetValue(named.ParentObjectId, out var target)) return false;
        int bone = named.BoneName is null ? -1 : UniqueBone(subject, named.BoneName), parent = UniqueBone(target, named.ParentBoneName);
        if (named.BoneName is not null && bone < 0 || parent < 0) return false;
        binding = new(named.ObjectId, bone, named.ParentObjectId, parent, named.Offset); return true;
    }
    private static bool CanAddParent(SceneOutsideParentBinding candidate, IEnumerable<SceneOutsideParentBinding> parents)
    {
        var existing = parents.ToArray();
        if (candidate.ObjectId == candidate.ParentObjectId
            || existing.Any(parent => parent.ObjectId == candidate.ObjectId && parent.BoneIndex == candidate.BoneIndex)) return false;
        var visited = new HashSet<Guid>(); var pending = new Stack<Guid>(); pending.Push(candidate.ParentObjectId);
        while (pending.TryPop(out var id))
        {
            if (id == candidate.ObjectId) return false;
            if (!visited.Add(id)) continue;
            foreach (var parent in existing.Where(parent => parent.ObjectId == id)) pending.Push(parent.ParentObjectId);
        }
        return true;
    }
}
