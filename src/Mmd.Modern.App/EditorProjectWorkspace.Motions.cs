using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

public sealed partial class EditorProjectWorkspace
{
    public void CommitMotion(Guid objectId, VmdMotion motion) => CommitMotion(objectId, motion, null);
    internal void CommitMotion(Guid objectId, VmdMotion motion, Action<bool>? poseDraftHistory)
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
        }, null, poseDraftHistory: poseDraftHistory);
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
        var authored = source.CaptureAuthoredContent();
        var actors = authored.Motions.ToDictionary(actor => actor.ObjectId);
        var replacements = motions.ToDictionary(pair => pair.Key, pair =>
        {
            var actor = actors[pair.Key];
            var asset = CaptureMotion(pair.Value, actor.Asset?.Name ?? "motion.vmd", new()
            { AnsiCodePage = MotionCodePage(actor), ModelNameOverride = actor.Asset?.Motion.ModelName });
            return actor with { Motion = asset?.Motion ?? pair.Value, Asset = asset };
        });
        var oldScene = authored.SceneMotion;
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
        => InspectUncapturedMotions(Motions).MissingHeaders;

    public IReadOnlyList<string> GetMotionPersistenceErrors() => InspectUncapturedMotions(Motions).Errors;

    private (IReadOnlyList<SceneActorMotion> MissingHeaders, IReadOnlyList<string> Errors) InspectUncapturedMotions(
        IReadOnlyList<SceneActorMotion> actors, CancellationToken token = default)
    {
        var missingHeaders = new List<SceneActorMotion>();
        var errors = new List<string>();
        foreach (var actor in actors)
        {
            token.ThrowIfCancellationRequested();
            if (actor.Asset is not null) continue;
            try { _ = VmdWriter.ToBytes(actor.Motion, new() { AnsiCodePage = MotionCodePage(actor) }); }
            catch (ArgumentException error) when (error.ParamName == "header.modelName") { missingHeaders.Add(actor); }
            catch (ArgumentException error) { errors.Add(actor.Model.Name + ": " + error.Message); }
        }
        return (missingHeaders.AsReadOnly(), errors.AsReadOnly());
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
}
