namespace Mmd.Modern.Application;

public sealed partial class SceneProjectSession
{
    /// <summary>Whether the existing animation owner requires recovery before further evaluation or rendering.</summary>
    public bool IsFaulted { get { RequireAlive(); return animation.IsFaulted; } }
    /// <summary>The original failure that requires an explicit recovery of this owner.</summary>
    public Exception? FaultCause { get { RequireAlive(); return animation.FaultCause; } }

    /// <summary>Captures the frame, completing deferred visual leaves without seeking or advancing physics.
    /// Unsupported future style schemas remain loadable, but cannot silently produce a typed rendering selection.</summary>
    public SceneProjectRenderSnapshot CaptureRenderSnapshot(IEnumerable<SceneObjectRenderRequest>? sceneInputRequests = null)
        => CaptureRenderSnapshotWithCancellation(sceneInputRequests, default);

    internal SceneProjectRenderSnapshot CaptureRenderSnapshotWithCancellation(IEnumerable<SceneObjectRenderRequest>? sceneInputRequests,
        CancellationToken cancellationToken)
    {
        RequireIdle();
        cancellationToken.ThrowIfCancellationRequested();
        var styles = content.RenderingStyles.Known;
        if (animation.IsFaulted)
            throw new InvalidOperationException("Recover or reload a faulted project session before rendering.", animation.FaultCause);
        var objects = content.Composition.Objects;
        var requests = SceneObjectRenderRequest.Freeze(sceneInputRequests);
        foreach (var request in requests)
            if (!objects.Any(item => item.Id == request.ObjectId))
                throw new ArgumentException("A render input request refers to an unknown scene object.", nameof(sceneInputRequests));
        var scene = animation.GetFrame(cancellationToken);
        var actors = animation.Actors;
        // No-request callers retain the old path without scanning source texture tables or solved skeletons.
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs = SceneObjectRenderInputs.Empty;
        IReadOnlyDictionary<Guid, SceneObjectMaterialInputs> materialInputs = SceneObjectMaterialInputs.Empty;
        if (requests.Count != 0)
        {
            var selected = new Dictionary<Guid, SceneObjectRenderInputs>(requests.Count);
            Dictionary<Guid, SceneObjectMaterialInputs>? selectedMaterials = null;
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int objectIndex = -1;
                for (int index = 0; index < objects.Count; index++)
                    if (objects[index].Id == request.ObjectId) { objectIndex = index; break; }
                if (objectIndex < 0) throw new ArgumentException("A render input request refers to an unknown scene object.", nameof(sceneInputRequests));
                if (objectIndex >= actors.Count || actors[objectIndex].ObjectId != request.ObjectId)
                    throw new InvalidOperationException("Project render inputs require matching playback identities.");
                var textures = new Dictionary<int, int>(request.LocalTextureIndices.Count);
                var bones = new Dictionary<int, System.Numerics.Matrix4x4>();
                bool visible = actors[objectIndex].Visible;
                if (visible)
                {
                    bones = animation.CaptureSelectedBoneWorldMatrices(request.ObjectId, request.BoneIndices);
                    var source = objects[objectIndex].Source.Surface.Snapshot.Textures;
                    var combined = scene.Surface.Snapshot.Textures;
                    foreach (int localIndex in request.LocalTextureIndices.Concat(request.OptionalLocalTextureIndices).Distinct())
                    {
                        if ((uint)localIndex >= (uint)source.Count && request.OptionalLocalTextureIndices.Contains(localIndex)
                            && !request.LocalTextureIndices.Contains(localIndex)) continue;
                        if ((uint)localIndex >= (uint)source.Count)
                            throw new ArgumentOutOfRangeException(nameof(sceneInputRequests), "A selected local texture index is outside the source model.");
                        int combinedIndex = -1;
                        for (int index = 0; index < combined.Count; index++)
                            if (ReferenceEquals(source[localIndex], combined[index])) { combinedIndex = index; break; }
                        if (combinedIndex < 0 && request.OptionalLocalTextureIndices.Contains(localIndex)
                            && !request.LocalTextureIndices.Contains(localIndex)) continue;
                        if (combinedIndex < 0)
                            throw new InvalidOperationException("A requested source texture is absent from the current composed frame.");
                        textures.Add(localIndex, combinedIndex);
                    }
                }
                var sourceFrame = visible && request.IncludeSourceGeometry ? animation.CaptureSourceFrame(request.ObjectId) : default;
                selected.Add(request.ObjectId, new(request.ObjectId, visible, bones, textures,
                    objects[objectIndex].Source.Surface.Snapshot.RawRadialSeeds is not null,
                    sourceFrame.Geometry, sourceFrame.Outline, sourceFrame.OutlineOptions));
                if (request.IncludeMaterialInputs)
                    (selectedMaterials ??= []).Add(request.ObjectId, animation.CaptureMaterialInputs(request.ObjectId, cancellationToken));
            }
            sceneInputs = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, SceneObjectRenderInputs>(selected);
            if (selectedMaterials is not null)
                materialInputs = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, SceneObjectMaterialInputs>(selectedMaterials);
        }
        if (actors.Count != objects.Count)
            throw new InvalidOperationException("Project rendering requires one playback state per composition instance.");
        var bindings = new SceneRenderingStyleBinding[objects.Count];
        var channels = new Dictionary<Guid, CharacterMorphChannels>();
        var ids = new HashSet<Guid>();
        int cursor = 0;
        for (int index = 0; index < objects.Count; index++)
        {
            var item = objects[index]; var actor = actors[index];
            if (item.Id == Guid.Empty || !ids.Add(item.Id) || actor.ObjectId != item.Id)
                throw new InvalidOperationException("Project playback identities must match composition order exactly.");
            int count = actor.Visible ? item.Source.Materials.Count : 0;
            bindings[index] = new(item.Id, styles.ModelOverrides.GetValueOrDefault(item.Id, styles.DefaultStyle),
                SceneModelShadingPolicy.ResolveRole(item, content.Lighting.ModelRoles), cursor, count, actor.Visible);
            cursor = checked(cursor + count);
            if (actor.HasMotion)
            {
                var value = animation.GetMorphChannels(item.Id);
                // Controller-owned samples contain read-only collections of value-only
                // UV, texture-factor and impulse structs. Later samples replace the
                // entire snapshot, so captures can retain it without copying every vertex.
                channels.Add(item.Id, value);
            }
        }
        if (cursor != scene.Materials.Count || cursor != scene.Surface.Materials.Count || cursor != scene.Surface.Snapshot.Materials.Count)
            throw new InvalidOperationException("Project rendering material ranges do not cover the current combined frame exactly.");
        var camera = animation.CameraPose;
        var accessories = CaptureAccessories(requests, actors);
        return new(animation.CurrentFrame, scene, camera, animation.LightPose, animation.SelfShadowPose,
            animation.Camera ?? scene.Options?.CameraOverride,
            new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, CharacterMorphChannels>(channels), Array.AsReadOnly(bindings), sceneInputs,
            animation.CameraParentTransform, accessories.Accessories, accessories.DrawOrder, accessories.Split,
            accessories.Diagnostics, UnpreparedAccessoryIndices, accessories.ParentBones, materialInputs);
    }

    /// <summary>Whether this reader understands the retained selection schema. Custom references remain supported
    /// metadata regardless of renderer/preset installation; renderer capability resolution is a separate responsibility.</summary>
    public bool HasSupportedRenderingStyles { get { RequireAlive(); return content.RenderingStyles.IsSupported; } }
    public IReadOnlyList<string> RenderingStyleDiagnostics { get { RequireAlive(); return content.RenderingStyles.Diagnostics; } }
    /// <summary>Reference-only selection metadata. Preset definitions and parameters belong to the renderer.
    /// Unsupported future schemas throw instead of silently replacing their selection with MMD.</summary>
    public SceneRenderingStyles RenderingStyles { get { RequireAlive(); return content.RenderingStyles.Known; } }
    public RenderingPresetReference GetRenderingStyle(Guid objectId)
    {
        RequireAlive(); RequireRenderingObject(objectId);
        return RenderingStyles.ModelOverrides.GetValueOrDefault(objectId, RenderingStyles.DefaultStyle);
    }
    public void SetDefaultRenderingStyle(RenderingPresetReference style, CancellationToken cancellationToken = default)
        => EditRenderingStyles(edit => edit.DefaultStyle = style, cancellationToken);
    /// <summary>Null removes the explicit override and restores inheritance.</summary>
    public void SetModelRenderingStyle(Guid objectId, RenderingPresetReference? style, CancellationToken cancellationToken = default)
    {
        RequireIdle(); RequireRenderingObject(objectId);
        EditRenderingStyles(edit => { if (style.HasValue) edit.ModelOverrides[objectId] = style.Value; else edit.ModelOverrides.Remove(objectId); }, cancellationToken);
    }
    /// <summary>Atomic metadata-only transaction. It preserves the current animation frame and live solver histories,
    /// and shares the owner's ordered Undo/Redo history with ordinary content edits.</summary>
    public void EditRenderingStyles(Action<SceneRenderingStylesEdit> edit, CancellationToken cancellationToken = default)
    {
        RequireIdle(); ArgumentNullException.ThrowIfNull(edit); cancellationToken.ThrowIfCancellationRequested();
        var previous = content.RenderingStyles.Known;
        editing = true;
        try
        {
            var detached = new SceneRenderingStylesEdit(previous); edit(detached);
            var next = detached.Freeze(content.Composition.Objects.Select(o => o.Id).ToHashSet());
            cancellationToken.ThrowIfCancellationRequested();
            if (previous.SameAs(next)) return;
            undo.Push(content); redo.Clear(); content = content with { RenderingStyles = new(next) };
        }
        finally { editing = false; }
    }
    private void RequireRenderingObject(Guid id)
    { if (!content.Composition.Objects.Any(o => o.Id == id)) throw new ArgumentException("Unknown scene object.", nameof(id)); }
    internal void AdoptPmaRenderingStyles(PmaRenderingStyleState styles)
    { RequireIdle(); content = content with { RenderingStyles = styles }; }
    internal PmaRenderingStyleState PmaRenderingStyles { get { RequireIdle(); return content.RenderingStyles; } }
}
