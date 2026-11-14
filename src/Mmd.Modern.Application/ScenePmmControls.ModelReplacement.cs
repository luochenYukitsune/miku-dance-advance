using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class ScenePmmControlState
{
    public bool HasReplacementMetadata => actors.Any(RequiresBindingMetadata) || restoredBindings.Length != 0;

    private bool RequiresBindingMetadata(Actor actor)
    {
        if (actor.DetachedOrdinals is { Length: > 0 }) return true;
        try
        {
            var model = document.Models.Single(m => m.Index == actor.ModelIndex);
            var inferred = PmmNameResolver.ResolveTable(model.BoneNames,
                actor.NativeBoneNames, "control bone").Select((binding, ordinal) => binding.BoundName
                    ?? model.BoneNames[ordinal].Value);
            return !inferred.SequenceEqual(actor.BindingNames!);
        }
        catch (InvalidDataException) { return true; }
    }
    public IReadOnlyList<SceneModelDataNotice> DetachedDataNotices => DetachedDataNoticesFor(null);

    internal IReadOnlyList<SceneModelDataNotice> DetachedDataNoticesFor(Guid? objectId) => Array.AsReadOnly(actors
        .SelectMany(actor => (actor.DetachedOrdinals ?? []).Select(ordinal =>
        {
            var redirect = restoredBindings.SingleOrDefault(r => r.SourceObjectId == actor.ObjectId && r.BoneOrdinal == ordinal);
            if (redirect is { IsDetached: false }) return null;
            Guid owner = redirect?.TargetObjectId ?? actor.ObjectId;
            if (objectId.HasValue && owner != objectId.Value) return null;
            bool recoverable = CanRestoreBinding(actor, ordinal, owner,
                redirect?.TargetBoneNames ?? actor.NativeBoneNames);
            return new SceneModelDataNotice(SceneModelDataKind.PmmControl, actor.BindingNames![ordinal], recoverable,
                "无主数据：PMM控制／相机／外亲原始记录保留，恢复需确认");
        })).OfType<SceneModelDataNotice>().ToArray());

    internal ScenePmmControlState RemapModel(Guid objectId, PmxModel model, bool restore)
    {
        string[] names = model.Bones.Select(b => b.Name).ToArray();
        var updated = actors.Select(a =>
        {
            if (a.ObjectId != objectId) return a;
            var detached = Enumerable.Range(0, a.BoneIndices.Length).Where(i => restoredBindings.Any(r => r.SourceObjectId == a.ObjectId && r.BoneOrdinal == i)
                || names.Count(n => n == a.BindingNames![i]) != 1
                || a.BoneIndices[i] < 0 || a.DetachedOrdinals?.Contains(i) == true).ToArray();
            return a with { NativeBoneNames = names, DetachedOrdinals = detached };
        }).ToArray();
        var redirected = restoredBindings.Select(r => r.TargetObjectId != objectId ? r : r with
        {
            TargetBoneNames = names,
            IsDetached = names.Count(n => n == actors.Single(a => a.ObjectId == r.SourceObjectId).BindingNames![r.BoneOrdinal]) != 1
                || r.IsDetached
        }).ToArray();
        var paused = new ScenePmmControlState(document, updated, OriginSha256, redirected);
        return restore ? paused.RestoreModelBindings(objectId) : paused;
    }

    private ScenePmmControlState RestoreModelBindings(Guid objectId)
    {
        var updated = actors.Select(actor => actor.ObjectId != objectId ? actor : actor with
        {
            DetachedOrdinals = (actor.DetachedOrdinals ?? []).Where(ordinal => restoredBindings.Any(r => r.SourceObjectId == actor.ObjectId && r.BoneOrdinal == ordinal)
                || !CanRestoreBinding(actor, ordinal, objectId, actor.NativeBoneNames)).ToArray()
        }).ToArray();
        var redirected = restoredBindings.Select(redirect => redirect.TargetObjectId != objectId || !redirect.IsDetached
            ? redirect : redirect with { IsDetached = !CanRestoreBinding(actors.Single(actor => actor.ObjectId == redirect.SourceObjectId),
                redirect.BoneOrdinal, objectId, redirect.TargetBoneNames) }).ToArray();
        return new(document, updated, OriginSha256, redirected);
    }

    internal IReadOnlyList<ScenePmmOrphanRedirection> PrepareOrphanRedirect(Guid sourceId, Guid targetId, PmxModel target)
    {
        var source = actors.SingleOrDefault(a => a.ObjectId == sourceId);
        if (source is null || sourceId == targetId) return [];
        string[] names = target.Bones.Select(b => b.Name).ToArray();
        return Array.AsReadOnly((source.DetachedOrdinals ?? []).Where(i => CanRestoreBinding(source, i, targetId, names)
            && !restoredBindings.Any(r => r.SourceObjectId == sourceId && r.BoneOrdinal == i && !r.IsDetached))
            .Select(i => new ScenePmmOrphanRedirection(sourceId, i, targetId) { TargetBoneNames = names.ToArray() }).ToArray());
    }

    internal IReadOnlyList<SceneModelDataNotice> PrepareOrphanRedirectNotices(Guid sourceId, Guid targetId, PmxModel target)
    {
        var source = actors.SingleOrDefault(a => a.ObjectId == sourceId);
        if (source is null || sourceId == targetId) return [];
        string[] names = target.Bones.Select(b => b.Name).ToArray();
        return Array.AsReadOnly((source.DetachedOrdinals ?? []).Where(i => !restoredBindings.Any(r => r.SourceObjectId == sourceId && r.BoneOrdinal == i && !r.IsDetached))
            .Select(i =>
            {
                bool recoverable = CanRestoreBinding(source, i, targetId, names);
                return new SceneModelDataNotice(SceneModelDataKind.PmmControl, source.BindingNames![i], recoverable,
                    recoverable ? "确认后恢复相关相机跟随、外亲及物理开关记录" : "目标名称缺失、重名或已有PMM绑定；原数据继续保留");
            }).ToArray());
    }

    private bool CanRestoreBinding(Actor source, int ordinal, Guid targetId, string[] names)
    {
        string name = source.BindingNames![ordinal];
        if (source.BindingNames!.Count(n => n == name) != 1 || names.Count(n => n == name) != 1) return false;
        int index = Array.IndexOf(names, name);
        if (actors.Any(a => a.ObjectId == targetId && a.BoneIndices.Contains(index))) return false;
        // A paused native slot still owns its authored records. A second source cannot claim it
        // merely because that first slot has not yet been explicitly reactivated.
        if (actors.Any(actor => actor.ObjectId == targetId && (actor.DetachedOrdinals ?? []).Any(other =>
            (actor.ObjectId != source.ObjectId || other != ordinal) && actor.BindingNames![other] == name
            && !restoredBindings.Any(r => r.SourceObjectId == actor.ObjectId && r.BoneOrdinal == other)))) return false;
        return !restoredBindings.Any(r => r.TargetObjectId == targetId
            && (r.SourceObjectId != source.ObjectId || r.BoneOrdinal != ordinal)
            && Array.IndexOf(r.TargetBoneNames, actors.Single(a => a.ObjectId == r.SourceObjectId).BindingNames![r.BoneOrdinal]) == index);
    }

    internal ScenePmmControlState RedirectOrphans(IReadOnlyList<ScenePmmOrphanRedirection> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var owned = bindings.ToArray();
        ValidateRestoredBindings(ActorBindings(), owned);
        var keys = owned.Select(r => (r.SourceObjectId, r.BoneOrdinal)).ToHashSet();
        return new(document, actors, OriginSha256, restoredBindings.Where(r => !keys.Contains((r.SourceObjectId, r.BoneOrdinal)))
            .Concat(owned).ToArray());
    }

    private (Guid Id, int Bone)? ResolveMappedBone(Actor actor, int ordinal)
    {
        if ((uint)ordinal >= (uint)actor.BoneIndices.Length) return null;
        var redirection = restoredBindings.FirstOrDefault(r => r.SourceObjectId == actor.ObjectId && r.BoneOrdinal == ordinal);
        if (redirection is not null)
        {
            if (redirection.IsDetached) return null;
            int mapped = Array.IndexOf(redirection.TargetBoneNames, actor.BindingNames![ordinal]);
            return mapped >= 0 ? (redirection.TargetObjectId, mapped) : null;
        }
        return actor.BoneIndices[ordinal] >= 0 ? (actor.ObjectId, actor.BoneIndices[ordinal]) : null;
    }

    private ScenePmmActorBinding[] ActorBindings() => actors.Select(a => new ScenePmmActorBinding(a.ObjectId, a.ModelIndex, a.BoneIndices)
        { NativeBindingNames = a.BindingNames, DetachedBoneOrdinals = a.DetachedOrdinals }).ToArray();

    // This validation also runs on untrusted DTOs before copying or looking up any model asset.
    private static void ValidateRestoredBindings(ScenePmmActorBinding[] bindings, ScenePmmOrphanRedirection[] redirects)
    {
        if (redirects.Length > 100_000 || redirects.Any(r => r is null || r.SourceObjectId == Guid.Empty || r.TargetObjectId == Guid.Empty
            || r.TargetObjectId == r.SourceObjectId || r.TargetBoneNames is null || r.TargetBoneNames.Length > 100_000
            || r.TargetBoneNames.Any(n => n is null || n.Length > 4096))
            || redirects.Select(r => (r.SourceObjectId, r.BoneOrdinal)).Distinct().Count() != redirects.Length)
            throw new InvalidDataException("Invalid explicit restored PMM control bindings.");
        var byId = bindings.ToDictionary(a => a.ObjectId);
        var occupied = bindings.SelectMany(a => a.BoneIndices.Where(i => i >= 0).Select(i => (a.ObjectId, Bone: i))).ToHashSet();
        foreach (var group in redirects.GroupBy(r => r.TargetObjectId))
            if (group.Any(r => !r.TargetBoneNames.SequenceEqual(group.First().TargetBoneNames)))
                throw new InvalidDataException("Restored PMM controls require one consistent target skeleton.");
        foreach (var r in redirects)
        {
            if (!byId.TryGetValue(r.SourceObjectId, out var actor) || (uint)r.BoneOrdinal >= (uint)actor.BoneIndices.Length
                || actor.DetachedBoneOrdinals?.Contains(r.BoneOrdinal) != true || actor.NativeBindingNames is null)
                throw new InvalidDataException("Restored PMM controls require a unique explicitly detached native name.");
            if (r.IsDetached) continue;
            string name = actor.NativeBindingNames[r.BoneOrdinal];
            if (actor.NativeBindingNames.Count(n => n == name) != 1 || r.TargetBoneNames.Count(n => n == name) != 1)
                throw new InvalidDataException("Restored PMM controls require a unique explicitly detached native name.");
            if (!occupied.Add((r.TargetObjectId, Array.IndexOf(r.TargetBoneNames, name))))
                throw new InvalidDataException("Restored PMM control target is already occupied; no binding was changed.");
        }
    }
}
