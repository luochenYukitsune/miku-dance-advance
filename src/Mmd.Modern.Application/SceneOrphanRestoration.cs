using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed class SceneOrphanRestorationPlan
{
    public Guid SourceObjectId { get; }
    public Guid TargetObjectId { get; }
    public IReadOnlyList<SceneModelDataNotice> Notices { get; }
    public int RecoverableCount => Notices.Count(n => n.CanRestore);
    public int ConflictCount => Notices.Count(n => !n.CanRestore);
    internal SceneProjectSession Owner { get; }
    internal object Revision { get; }
    internal string[] Bones { get; }
    internal string[] Morphs { get; }
    internal string[] Ik { get; }
    internal SceneDetachedOutsideParent[] Parents { get; }
    internal ScenePmmOrphanRedirection[] Controls { get; }
    internal SceneOrphanRestorationPlan(SceneProjectSession owner, object revision, Guid source, Guid target,
        string[] bones, string[] morphs, string[] ik, SceneDetachedOutsideParent[] parents,
        ScenePmmOrphanRedirection[] controls, IEnumerable<SceneModelDataNotice> notices)
    {
        Owner = owner; Revision = revision; SourceObjectId = source; TargetObjectId = target;
        Bones = bones; Morphs = morphs; Ik = ik; Parents = parents; Controls = controls;
        Notices = Array.AsReadOnly(notices.ToArray());
    }
}

public sealed partial class SceneProjectSession
{
    /// <summary>Only explicitly detached data is proposed. Target-authored channels are never overwritten.</summary>
    public SceneOrphanRestorationPlan PrepareOrphanRestoration(Guid sourceObjectId, Guid targetObjectId)
    {
        RequireIdle();
        var source = content.Motions.Single(m => m.ObjectId == sourceObjectId);
        var target = content.Motions.Single(m => m.ObjectId == targetObjectId);
        if (sourceObjectId == targetObjectId)
        {
            if (source.Detachment is null && content.PmmControls?.DetachedDataNoticesFor(sourceObjectId).Count is not > 0)
                return new(this, content, sourceObjectId, targetObjectId, [], [], [], [], [], []);
            var obj = content.Composition.Objects.Single(o => o.Id == sourceObjectId);
            if (!Path.GetExtension(obj.SourcePath).Equals(".pmx", StringComparison.OrdinalIgnoreCase))
                return new(this, content, sourceObjectId, targetObjectId, [], [], [], [], [], []);
            var replacement = PrepareModelReplacement(sourceObjectId, obj.SourcePath, new(source.Model, obj.Source));
            return new(this, content, sourceObjectId, targetObjectId, [], [], [], [], [], replacement.Notices.Where(n => n.CanRestore));
        }
        var notices = new List<SceneModelDataNotice>();
        string[] Select(IReadOnlyList<string> names, SceneModelDataKind kind, Func<string, int> find, Func<string, bool> conflict)
        {
            var result = new List<string>();
            foreach (string name in names)
            {
                if (find(name) < 0)
                {
                    notices.Add(new(kind, name, false, "目标PMX无唯一同名项，原数据继续保留"));
                    continue;
                }
                bool occupied = conflict(name);
                notices.Add(new(kind, name, !occupied, occupied ? "目标模型已有同名轨道，未覆盖；原数据继续保留" : "确认后将无主数据绑定到所选模型"));
                if (!occupied) result.Add(name);
            }
            return result.ToArray();
        }
        var bones = Select(source.Detachment?.BoneNames ?? [], SceneModelDataKind.Bone,
            n => UniqueBone(target.Model, n), n => target.Motion.BoneKeyframes.Any(k => k.BoneName == n)
                || target.Detachment?.BoneNames.Contains(n, StringComparer.Ordinal) == true);
        var morphs = Select(source.Detachment?.MorphNames ?? [], SceneModelDataKind.Morph,
            n => UniqueMorph(target.Model, n), n => target.Motion.MorphKeyframes.Any(k => k.MorphName == n)
                || target.Detachment?.MorphNames.Contains(n, StringComparer.Ordinal) == true);
        var ik = Select(source.Detachment?.IkNames ?? [], SceneModelDataKind.Ik,
            n => { int i = UniqueBone(target.Model, n); return i >= 0 && target.Model.Bones[i].Flags.HasFlag(PmxBoneFlags.Ik) ? i : -1; },
            n => target.Motion.IkKeyframes.Any(k => k.Switches.Any(s => s.BoneName == n))
                || target.Detachment?.IkNames.Contains(n, StringComparer.Ordinal) == true);
        var models = content.Motions.ToDictionary(m => m.ObjectId, m => m.Model);
        var parents = new List<SceneDetachedOutsideParent>();
        var reservedParents = content.Parents.ToList();
        foreach (var parent in content.Motions.SelectMany(m => m.Detachment?.OutsideParents ?? []).Distinct())
        {
            if (parent.ObjectId != sourceObjectId && parent.ParentObjectId != sourceObjectId) continue;
            var moved = parent with { ObjectId = parent.ObjectId == sourceObjectId ? targetObjectId : parent.ObjectId,
                ParentObjectId = parent.ParentObjectId == sourceObjectId ? targetObjectId : parent.ParentObjectId };
            bool conflict = !TryParent(moved, models, out var mapped) || !CanAddParent(mapped!, reservedParents);
            notices.Add(new(SceneModelDataKind.OutsideParent, parent.BoneName ?? parent.ParentBoneName, !conflict,
                conflict ? "名称不唯一、目标已有绑定或会产生依赖循环，原数据继续保留" : "确认后恢复外亲绑定"));
            if (!conflict) { parents.Add(parent); reservedParents.Add(mapped!); }
        }
        var controls = content.PmmControls?.PrepareOrphanRedirect(sourceObjectId, targetObjectId, target.Model).ToArray() ?? [];
        notices.AddRange(content.PmmControls?.PrepareOrphanRedirectNotices(sourceObjectId, targetObjectId, target.Model) ?? []);
        return new(this, content, sourceObjectId, targetObjectId, bones, morphs, ik, parents.ToArray(), controls, notices);
    }

    public void ApplyOrphanRestoration(SceneOrphanRestorationPlan plan, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        RequireIdle(); ArgumentNullException.ThrowIfNull(plan); cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed) throw new InvalidOperationException("Restoring unowned data requires explicit confirmation.");
        if (!ReferenceEquals(plan.Owner, this) || !ReferenceEquals(plan.Revision, content))
            throw new InvalidOperationException("The restoration preview is stale. Prepare it again.");
        if (plan.RecoverableCount == 0) return;
        Edit(edit =>
        {
            if (plan.SourceObjectId == plan.TargetObjectId)
            {
                var actor = edit.Motions.Single(m => m.ObjectId == plan.SourceObjectId);
                var obj = edit.Composition.Objects.Single(o => o.Id == plan.SourceObjectId);
                ReplaceModel(edit, plan.SourceObjectId, obj.SourcePath, new(actor.Model, obj.Source), true, []);
                return;
            }
            int sourceIndex = edit.Motions.FindIndex(m => m.ObjectId == plan.SourceObjectId);
            int targetIndex = edit.Motions.FindIndex(m => m.ObjectId == plan.TargetObjectId);
            var source = edit.Motions[sourceIndex]; var target = edit.Motions[targetIndex];
            var sourceMotion = CopyReplacementMotion(source.Motion,
                source.Motion.BoneKeyframes.Where(k => !plan.Bones.Contains(k.BoneName)),
                source.Motion.MorphKeyframes.Where(k => !plan.Morphs.Contains(k.MorphName)),
                source.Motion.IkKeyframes.Select(k => new VmdIkKeyframe { Frame = k.Frame, Visible = k.Visible,
                    Switches = k.Switches.Where(s => !plan.Ik.Contains(s.BoneName)).ToArray() }));
            var targetVisibility = new MmdModelVisibilityTrack(target.Motion);
            var transferredIk = source.Motion.IkKeyframes.Select(k => new VmdIkKeyframe { Frame = k.Frame, Visible = targetVisibility.Evaluate(k.Frame),
                Switches = k.Switches.Where(s => plan.Ik.Contains(s.BoneName)).ToArray() }).Where(k => k.Switches.Count > 0);
            // Retain existing records and their same-frame ordering. Independent switch channels
            // can share a frame; inserted records use the target's existing visibility value.
            var targetIk = target.Motion.IkKeyframes.Concat(transferredIk);
            var targetMotion = CopyReplacementMotion(target.Motion,
                target.Motion.BoneKeyframes.Concat(source.Motion.BoneKeyframes.Where(k => plan.Bones.Contains(k.BoneName))),
                target.Motion.MorphKeyframes.Concat(source.Motion.MorphKeyframes.Where(k => plan.Morphs.Contains(k.MorphName))), targetIk);
            edit.Motions[sourceIndex] = WithCapturedMotion(source, sourceMotion);
            edit.Motions[targetIndex] = WithCapturedMotion(target, targetMotion);
            for (int i = 0; i < edit.Motions.Count; i++)
            {
                var actor = edit.Motions[i]; var state = actor.Detachment;
                if (state is null) continue;
                var next = new SceneMotionDetachment(
                    actor.ObjectId == plan.SourceObjectId ? state.BoneNames.Except(plan.Bones).ToArray() : state.BoneNames,
                    actor.ObjectId == plan.SourceObjectId ? state.MorphNames.Except(plan.Morphs).ToArray() : state.MorphNames,
                    actor.ObjectId == plan.SourceObjectId ? state.IkNames.Except(plan.Ik).ToArray() : state.IkNames,
                    state.OutsideParents.Except(plan.Parents).ToArray());
                edit.Motions[i] = actor with { Detachment = next.IsEmpty ? null : next };
            }
            var models = edit.Motions.ToDictionary(m => m.ObjectId, m => m.Model);
            foreach (var parent in plan.Parents)
            {
                var moved = parent with { ObjectId = parent.ObjectId == plan.SourceObjectId ? plan.TargetObjectId : parent.ObjectId,
                    ParentObjectId = parent.ParentObjectId == plan.SourceObjectId ? plan.TargetObjectId : parent.ParentObjectId };
                if (!TryParent(moved, models, out var mapped) || !CanAddParent(mapped!, edit.OutsideParents))
                    throw new InvalidOperationException("Outside-parent restoration changed.");
                edit.OutsideParents.Add(mapped!);
            }
            if (edit.PmmControls is not null) edit.PmmControls = edit.PmmControls.RedirectOrphans(plan.Controls);
        }, cancellationToken);
    }

    private static VmdMotion CopyReplacementMotion(VmdMotion basis, IEnumerable<VmdBoneKeyframe> bones,
        IEnumerable<VmdMorphKeyframe> morphs, IEnumerable<VmdIkKeyframe> ik) => new()
    {
        ModelName = basis.ModelName, OriginalModelName = basis.OriginalModelName,
        BoneKeyframes = bones.OrderBy(k => k.Frame).ToArray(), MorphKeyframes = morphs.OrderBy(k => k.Frame).ToArray(),
        IkKeyframes = ik.OrderBy(k => k.Frame).ToArray(), CameraKeyframes = basis.CameraKeyframes,
        LightKeyframes = basis.LightKeyframes, SelfShadowKeyframes = basis.SelfShadowKeyframes, Diagnostics = basis.Diagnostics
    };
    private static SceneActorMotion WithCapturedMotion(SceneActorMotion actor, VmdMotion motion)
    {
        // Capture both resulting streams before publication. Encoding failures must leave the
        // original project intact rather than publishing a motion that can no longer be saved.
        var asset = SceneMotionAsset.FromMotion(motion, actor.Asset?.Name ?? "motion.vmd", new()
            { AnsiCodePage = actor.Asset?.AnsiCodePage ?? 932 });
        return actor with { Motion = asset.Motion, Asset = asset };
    }
}
