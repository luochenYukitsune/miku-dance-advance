using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;

namespace Mmd.Modern.App;

/// <summary>Editor-only pose drafts travel as a neutral optional extension in both native formats.</summary>
internal sealed record EditorPoseDraftPackage(int Version, ImmutableArray<EditorRecoveryActorState> Actors)
{
    internal const string Extension = "org.mda.editor-pose-drafts";
    internal static readonly EditorPoseDraftPackage Empty = new(1, []);
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32
    };

    internal static EditorPoseDraftPackage Read(PmaLoadedProject package)
    {
        if (!package.OptionalExtensions.TryGetValue(Extension, out var value)) return Empty;
        var state = value.Deserialize<EditorPoseDraftPackage>(Json);
        if (state is not { Version: 1 } || state.Actors.IsDefault || state.Actors.Length > EditorRecoverySessionState.MaximumActors)
            throw new InvalidDataException("Unsupported project pose draft metadata.");
        var models = package.Session.CaptureAuthoredContent().Motions.ToDictionary(actor => actor.ObjectId);
        var seen = new HashSet<Guid>();
        foreach (var saved in state.Actors)
        {
            if (saved is null || !seen.Add(saved.ObjectId) || !models.TryGetValue(saved.ObjectId, out var actor))
                throw new InvalidDataException("Pose drafts reference a duplicate or missing actor.");
            saved.Validate();
            if (saved.Bones.Any(bone => bone.Frame is null) || saved.Morphs.Any(morph => morph.Frame is null)
                || saved.BoneCount != actor.Model.Bones.Count || saved.MorphCount != actor.Model.Morphs.Count
                || saved.StructureSignature != EditorRecoverySessionState.ModelStructureSignature(actor.Model))
                throw new InvalidDataException("Pose drafts do not match their authored frames or model.");
        }
        return state;
    }

    internal EditorPoseDraftPackage ForModels(IReadOnlyList<SceneActorMotion> actors)
    {
        if (Actors.Length == 0) return this;
        var models = actors.ToDictionary(actor => actor.ObjectId);
        return this with { Actors = Actors.Where(saved => models.TryGetValue(saved.ObjectId, out var actor)
            && saved.StructureSignature == EditorRecoverySessionState.ModelStructureSignature(actor.Model)).ToImmutableArray() };
    }

    internal JsonElement Encode() => JsonSerializer.SerializeToElement(this, Json);
}

public sealed partial class EditorAnimationViewModel
{
    internal EditorPoseDraftPackage CaptureNativeDrafts(IReadOnlyList<SceneActorMotion> actors)
    {
        var dirtyIds = actorDrafts.Keys.Select(key => key.Actor).ToHashSet();
        return dirtyIds.Count == 0 ? EditorPoseDraftPackage.Empty
            : new(1, actors.Where(actor => dirtyIds.Contains(actor.ObjectId)).Select(CaptureDraftActor).ToImmutableArray());
    }
}
