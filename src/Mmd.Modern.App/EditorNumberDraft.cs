namespace Mmd.Modern.App;

/// <summary>One numeric menu edit targets one existing draft channel. Its history keeps no control references.</summary>
internal sealed record EditorNumberDraft(Func<bool> CanWrite, Action<Action> Write)
{
    internal static readonly EditorNumberDraft Unavailable = new(() => false, _ => { });

    internal static EditorNumberDraft Capture<T>(EditorProjectWorkspace workspace, Func<bool> selected,
        Func<bool> valid, Func<T> read, Action<T> restore, Func<T, T, bool>? sameField = null)
    {
        var document = workspace.DocumentId;
        var session = workspace.Session;
        bool TargetExists() => workspace.DocumentId == document && ReferenceEquals(workspace.Session, session) && valid();
        var before = read();
        bool Current() => TargetExists() && selected() && EqualityComparer<T>.Default.Equals(read(), before);
        return new(Current, write =>
        {
            if (!Current()) return;
            write();
            var after = read();
            if (EqualityComparer<T>.Default.Equals(before, after)) return;
            workspace.RecordNumberEdit(new(
                undo => TargetExists() && (sameField ?? EqualityComparer<T>.Default.Equals)(read(), undo ? after : before),
                undo => restore(undo ? before : after)));
        });
    }
}

internal sealed record EditorNumberHistory(Func<bool, bool> CanReplay, Action<bool> Replay);

public sealed partial class EditorAnimationViewModel
{
    internal EditorNumberDraft CaptureMorphMenu(EditorProjectWorkspace workspace, int index, Func<bool> selected)
    {
        if (objectId is not { } actor || model is not { } pmx || (uint)index >= (uint)pmx.Morphs.Count) return EditorNumberDraft.Unavailable;
        var key = (Actor: actor, Frame: CurrentFrame);
        var motionSource = sourceMotion;

        (bool Exists, decimal? Weight) Read() =>
            actorDrafts.TryGetValue(key, out var pending) && pending.Morphs.TryGetValue(index, out var weight) ? (true, weight) : default;

        bool ValidSource() => !actorDrafts.TryGetValue(key, out var pending) || ReferenceEquals(pending.Model, pmx);
        bool CurrentTarget() => !playing && !IsProjectBusy && animationContextActive && CurrentFrame == key.Frame
            && objectId == actor && ReferenceEquals(model, pmx) && ReferenceEquals(sourceMotion, motionSource) && selected();

        return EditorNumberDraft.Capture(workspace, CurrentTarget, ValidSource, Read, state =>
        {
            if (state.Exists)
            {
                if (!actorDrafts.TryGetValue(key, out var pending))
                    actorDrafts.Add(key, pending = new(pmx, motionSource));
                pending.Morphs[index] = state.Weight;
            }
            else if (actorDrafts.TryGetValue(key, out var pending))
            {
                pending.Morphs.Remove(index);
                if (pending.IsEmpty) actorDrafts.Remove(key);
            }
            if (objectId == actor && CurrentFrame == key.Frame && morphIndex == index) LoadPose();
            Changed(nameof(HasInvalidDrafts)); Changed(nameof(HasAnyDraft)); NotifyDraftContentEdited();
            PoseDraftChanged?.Invoke(this, EventArgs.Empty);
        });
    }
}

public sealed partial class EditorSceneInspectorViewModel
{
    // Scene drafts are discarded when their channel is reloaded (including changing frame).
    private long cameraDraftGeneration, lightDraftGeneration;

    internal EditorNumberDraft CaptureFovMenu(EditorProjectWorkspace workspace)
    {
        long generation = cameraDraftGeneration;
        var otherFields = () => (Pose: cameraPose with { Fov = 0 }, centerX, centerY, centerZ, rotationX, rotationY, rotationZ, distance, perspective);
        return EditorNumberDraft.Capture(workspace, () => IsCameraInspector && !IsBusy && CanMutateProject?.Invoke() != false,
            () => cameraDraftGeneration == generation,
            () => (Fov: fov, ExactFov: cameraPose.Fov, Draft: cameraDraft, Other: otherFields()),
            state =>
            {
                cameraDraft = state.Draft || state.Other != otherFields();
                fov = state.Fov; cameraPose = cameraPose with { Fov = state.ExactFov };
                contextRevision++; Changed(nameof(CameraFov)); DraftChanged(); PublishCamera();
            }, (current, expected) => current.Fov == expected.Fov && current.ExactFov == expected.ExactFov);
    }

    internal EditorNumberDraft CaptureIntensityMenu(EditorProjectWorkspace workspace)
    {
        long generation = lightDraftGeneration;
        var otherFields = () => (lightPose.Direction, directionX, directionY, directionZ, colorR, colorG, colorB);
        return EditorNumberDraft.Capture(workspace, () => ShowsPrimaryLightTrack && !IsBusy && CanMutateProject?.Invoke() != false,
            () => lightDraftGeneration == generation,
            () => (Intensity: intensity, Radiance: lightPose.Color, Draft: lightDraft, Other: otherFields()),
            state =>
            {
                lightDraft = state.Draft || state.Other != otherFields();
                intensity = state.Intensity;
                if ((colorR, colorG, colorB) == (state.Other.colorR, state.Other.colorG, state.Other.colorB))
                    lightPose = lightPose with { Color = state.Radiance };
                else if (TryLightRadiance(out var radiance))
                    lightPose = lightPose with { Color = radiance };
                contextRevision++; Changed(nameof(LightIntensity)); DraftChanged(); PublishLight();
            }, (current, expected) => current.Intensity == expected.Intensity);
    }
}
